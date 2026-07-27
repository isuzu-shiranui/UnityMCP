/**
 * Shared retry logic for outbound HTTP requests to Unity Editor instances.
 *
 * Classification (design §3.1):
 *   - Unsafe retryable allowlist (pre-handshake only):
 *       ECONNREFUSED, ENOTFOUND, UND_ERR_CONNECT_TIMEOUT
 *   - Safe retryable / Unsafe fatal:
 *       ECONNRESET, EPIPE, ETIMEDOUT, UND_ERR_SOCKET, UND_ERR_CLOSED,
 *       UND_ERR_HEADERS_TIMEOUT, AbortError (name==='AbortError'), response.status >= 500
 *   - Both fatal: 4xx
 *   - Unknown codes: Safe retryable, Unsafe fatal (conservative default)
 */

export type Idempotency = 'safe' | 'unsafe';

export type Classification = 'success' | 'retryable' | 'fatal';

/**
 * Error code extraction per spec:
 *   err.cause.code ?? err.code ?? (err instanceof Error ? err.name : undefined)
 */
export function extractErrorCode(err: unknown): string | undefined {
    if (err == null) return undefined;
    const anyErr = err as any;
    if (anyErr?.cause?.code) return String(anyErr.cause.code);
    if (anyErr?.code) return String(anyErr.code);
    if (err instanceof Error) return err.name;
    return undefined;
}

/**
 * Set of error codes that indicate the failure happened BEFORE the TCP
 * handshake completed. These are safe to retry even for Unsafe endpoints
 * because the request payload could not have been delivered.
 */
export const PRE_HANDSHAKE_CODES = new Set<string>([
    'ECONNREFUSED',
    'ENOTFOUND',
    'UND_ERR_CONNECT_TIMEOUT',
]);

/**
 * Set of error codes that are recognized as post-handshake transient failures.
 * These are retryable for Safe endpoints only.
 */
export const POST_HANDSHAKE_TRANSIENT_CODES = new Set<string>([
    'ECONNRESET',
    'EPIPE',
    'ETIMEDOUT',
    'UND_ERR_SOCKET',
    'UND_ERR_CLOSED',
    'UND_ERR_HEADERS_TIMEOUT',
    'AbortError',
    'ABORT_ERR',
]);

/**
 * Classifies a thrown error based on idempotency mode per design §3.1.
 *
 * @param err The error thrown by fetch()
 * @param idempotency 'safe' or 'unsafe'
 * @returns 'retryable' or 'fatal'
 */
export function classifyError(err: unknown, idempotency: Idempotency): 'retryable' | 'fatal' {
    const code = extractErrorCode(err);

    // Pre-handshake failures are always retryable (even for Unsafe).
    if (code && PRE_HANDSHAKE_CODES.has(code)) {
        return 'retryable';
    }

    // Post-handshake known transient failures: Safe retry, Unsafe fatal.
    if (code && POST_HANDSHAKE_TRANSIENT_CODES.has(code)) {
        return idempotency === 'safe' ? 'retryable' : 'fatal';
    }

    // Unknown codes: conservative default — Safe retryable, Unsafe fatal.
    return idempotency === 'safe' ? 'retryable' : 'fatal';
}

/**
 * Classifies an HTTP response status code per design §3.1.
 */
export function classifyResponseStatus(status: number, idempotency: Idempotency): Classification {
    if (status >= 200 && status < 300) return 'success';
    if (status >= 500) return idempotency === 'safe' ? 'retryable' : 'fatal';
    // 3xx / 4xx — fatal for both. Fetch doesn't auto-follow redirects in
    // all environments; we don't support redirect for Unity/local proxy.
    return 'fatal';
}

/**
 * Retry error thrown when the retry budget is exhausted or the failure is fatal.
 */
export class RetryableFetchError extends Error {
    public readonly code: string;
    public readonly httpStatus?: number;
    public readonly cause?: unknown;
    public readonly attempts: number;

    constructor(
        message: string,
        code: string,
        attempts: number,
        options?: { cause?: unknown; httpStatus?: number }
    ) {
        super(message);
        this.name = 'RetryableFetchError';
        this.code = code;
        this.attempts = attempts;
        this.cause = options?.cause;
        this.httpStatus = options?.httpStatus;
    }
}

/**
 * Pulls `{code, message}` out of Unity's error envelope.
 *
 * Returns undefined for a body that is not an envelope — a proxy error page, an empty
 * response — so the caller can fall back to the status line.
 */
export function parseEnvelopeError(body: string): { code?: string; message: string } | undefined {
    if (!body) {
        return undefined;
    }

    try {
        const parsed = JSON.parse(body);
        const error = parsed?.error;

        if (error && typeof error.message === 'string' && error.message !== '') {
            return {
                code: typeof error.code === 'string' ? error.code : undefined,
                message: error.message,
            };
        }
    } catch {
        // Not JSON.
    }

    return undefined;
}

export interface RetryableFetchOptions {
    idempotency: Idempotency;
    retryMaxMs?: number;
    /** Per-attempt abort timeout (default 30000 ms). */
    perAttemptTimeoutMs?: number;
    /** Override initial backoff (default 100 ms). */
    initialBackoffMs?: number;
    /** Override maximum backoff (default 1000 ms). */
    maxBackoffMs?: number;
    /** Optional caller-supplied AbortSignal. */
    signal?: AbortSignal;
    /** Optional hook that is called before every fetch attempt (for tests). */
    onAttempt?: (attemptNumber: number) => void;
    /** Injected fetch (for tests). Defaults to global fetch. */
    fetchImpl?: typeof fetch;
    /** Injected sleep (for tests). */
    sleepImpl?: (ms: number) => Promise<void>;
    /** Injected now() (for tests). */
    nowImpl?: () => number;
}

export interface RetryableFetchInit {
    method?: string;
    headers?: Record<string, string>;
    body?: string | Buffer | Uint8Array | null;
}

export interface RetryableFetchResult {
    response: Response;
    attempts: number;
}

function sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * Wraps fetch with retry + classification per design §3.1.
 *
 * The caller provides `idempotency`. On fatal classification the error is
 * re-thrown immediately (post-handshake, Unsafe). On retryable classification
 * we back off (100ms → 1000ms cap) and retry until retryMaxMs elapses.
 *
 * Returns the final Response on success, or throws RetryableFetchError on
 * exhaustion / fatal. For HTTP >= 500 the response is consumed via .text()
 * to free the connection before the retry, and the final exhausted response
 * is also re-created as an error (with status code in err.httpStatus).
 */
export async function retryableFetch(
    url: string,
    init: RetryableFetchInit,
    opts: RetryableFetchOptions
): Promise<RetryableFetchResult> {
    const retryMaxMs = opts.retryMaxMs ?? 15000;
    const perAttemptTimeoutMs = opts.perAttemptTimeoutMs ?? 30000;
    const fetchFn = opts.fetchImpl ?? fetch;
    const sleepFn = opts.sleepImpl ?? sleep;
    const nowFn = opts.nowImpl ?? Date.now;

    const start = nowFn();
    let backoff = opts.initialBackoffMs ?? 100;
    const maxBackoff = opts.maxBackoffMs ?? 1000;
    let attempts = 0;
    let lastError: unknown = null;
    let lastStatus: number | undefined;

    // Every exit is a return or a throw: success, a fatal classification, or the retry
    // budget running out. A loop condition would have to duplicate that logic.
    // eslint-disable-next-line no-constant-condition
    while (true) {
        attempts++;
        if (opts.onAttempt) opts.onAttempt(attempts);

        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), perAttemptTimeoutMs);
        // Forward external abort.
        const externalAbort = () => controller.abort();
        if (opts.signal) {
            if (opts.signal.aborted) controller.abort();
            else opts.signal.addEventListener('abort', externalAbort);
        }

        try {
            // Prepare body. Fetch requires Uint8Array/BodyInit; Buffer is acceptable in Node.
            const body = init.body == null
                ? undefined
                : (init.body as BodyInit);

            const res = await fetchFn(url, {
                method: init.method ?? 'GET',
                headers: init.headers,
                body: body,
                signal: controller.signal,
            });

            const classification = classifyResponseStatus(res.status, opts.idempotency);
            if (classification === 'success') {
                return { response: res, attempts };
            }

            // Read rather than discard the body: Unity puts the actionable part of a failure
            // in the error envelope, and reporting a bare "HTTP 400" throws away the one
            // thing that tells the caller how to fix the call. Consuming it also frees the
            // socket before a retry, which is why the body was being drained here before.
            let responseBody = '';
            try { responseBody = await res.text(); } catch { /* ignore */ }
            lastStatus = res.status;

            if (classification === 'fatal') {
                const envelope = parseEnvelopeError(responseBody);

                throw new RetryableFetchError(
                    envelope?.message ?? `HTTP ${res.status}`,
                    envelope?.code ?? (res.status >= 500 ? 'server_error' : 'client_error'),
                    attempts,
                    { httpStatus: res.status }
                );
            }
            // retryable 5xx: fall through to backoff
            lastError = new RetryableFetchError(
                `HTTP ${res.status}`,
                'server_error',
                attempts,
                { httpStatus: res.status }
            );
        } catch (err) {
            // AbortError from our own timer → treat as UND_ERR_HEADERS_TIMEOUT-like.
            if ((err as any)?.name === 'RetryableFetchError') {
                throw err; // already classified as fatal above
            }

            const classification = classifyError(err, opts.idempotency);
            lastError = err;

            if (classification === 'fatal') {
                const code = extractErrorCode(err) ?? 'unknown';
                throw new RetryableFetchError(
                    `Fetch failed: ${code}`,
                    code,
                    attempts,
                    { cause: err }
                );
            }
            // retryable: fall through
        } finally {
            clearTimeout(timer);
            if (opts.signal) opts.signal.removeEventListener('abort', externalAbort);
        }

        // Budget check BEFORE sleep so we don't sleep past the deadline.
        const elapsed = nowFn() - start;
        if (elapsed >= retryMaxMs) {
            const code = lastStatus !== undefined
                ? 'server_error'
                : (extractErrorCode(lastError) ?? 'unknown');
            throw new RetryableFetchError(
                `Retry budget exhausted after ${attempts} attempt(s) (${elapsed}ms): ${code}`,
                code,
                attempts,
                { cause: lastError, httpStatus: lastStatus }
            );
        }

        // Exponential backoff, capped. Also cap at remaining budget.
        const remaining = retryMaxMs - elapsed;
        const wait = Math.min(backoff, maxBackoff, Math.max(1, remaining));
        await sleepFn(wait);
        backoff = Math.min(backoff * 2, maxBackoff);
    }
}
