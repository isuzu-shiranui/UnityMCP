import { EventEmitter } from 'events';
import { JObject } from '../types/index.js';
import { McpErrorCode } from "../types/ErrorCodes.js";
import {
    retryableFetch,
    RetryableFetchError,
    RetryableFetchInit,
    Idempotency,
} from './retryableFetch.js';

/**
 * Builds the idempotency-cache lookup key for a request, matching the format
 * emitted by the Unity Editor `/health.handlers[].name` field exactly.
 *
 * Rules (must stay in sync with McpHttpServer.BuildHealthResponse):
 *  - `/command` with body `{command:"console.clear"}` → `/command:console.clear`
 *  - `/play_mode` with body `{action:"step"}`        → `/play_mode:step`
 *  - `/inspect`   with body `{mode:"write"}`          → `/inspect:write`
 *  - Bare endpoint (no disambiguator) returns the endpoint as-is.
 *
 * The `endpoint` argument always starts with "/" (e.g. "/command").
 */
export function buildIdempotencyKey(
    endpoint: string,
    body?: Record<string, unknown> | null
): string {
    if (!body || typeof body !== 'object') return endpoint;

    switch (endpoint) {
        case '/command': {
            const cmd = typeof body.command === 'string' ? body.command : '';
            return cmd !== '' ? `${endpoint}:${cmd}` : endpoint;
        }
        case '/play_mode': {
            const action = typeof body.action === 'string' ? body.action : '';
            return action !== '' ? `${endpoint}:${action}` : endpoint;
        }
        case '/inspect': {
            const mode = typeof body.mode === 'string' ? body.mode : '';
            return mode !== '' ? `${endpoint}:${mode}` : endpoint;
        }
        default:
            return endpoint;
    }
}

export type UnityInstanceState = 'healthy' | 'reloading' | 'unhealthy';

/**
 * Information about a discovered Unity instance.
 */
export interface UnityInstance {
    id: string;
    projectName: string;
    projectPath: string;
    port: number;
    unityVersion: string;
    endpoint: string;
    version: string;
    /**
     * Bearer token read from the instance's descriptor file.
     * Every request must carry it: loopback binding is not access control, and
     * `/execute_code` runs arbitrary C# with full Editor privileges.
     */
    token: string;
    /** state machine (design §3.4). */
    state: UnityInstanceState;
    /** Last time a success observation was made (/health 200 or UDP announce). */
    lastSeen: number;
    /** Last time ANY contact happened — used for unhealthyCooldown decisions. */
    lastContact: number;
    /** Consecutive poll failures (for debounce). */
    consecutiveFailures: number;
}

/**
 * Error codes used by resolveInstance.
 */
export const enum ResolveErrorCode {
    NoInstance = 'no_instance',
    TargetRequired = 'target_required',
    TargetNotFound = 'target_not_found',
    Unhealthy = 'unhealthy',
}

/**
 * Error thrown when no Unity instance can be resolved.
 */
export class ResolveInstanceError extends Error {
    public readonly code: string;
    constructor(code: string, message: string) {
        super(message);
        this.name = 'ResolveInstanceError';
        this.code = code;
    }
}

/**
 * Resolves a target name against a list of instances per design §3.2 step 1.
 *   clientId exact > projectName exact (CI) > projectName substring (CI)
 *
 * Returns all matches. Callers decide single-match vs. ambiguous handling.
 */
export function matchInstancesByTarget(
    instances: UnityInstance[],
    target: string
): UnityInstance[] {
    // 1. clientId exact match.
    const exactId = instances.filter(i => i.id === target);
    if (exactId.length > 0) return exactId;

    // 2. projectName exact, case-insensitive.
    const lower = target.toLowerCase();
    const exactName = instances.filter(
        i => (i.projectName || '').toLowerCase() === lower
    );
    if (exactName.length > 0) return exactName;

    // 3. projectName substring, case-insensitive.
    return instances.filter(
        i => (i.projectName || '').toLowerCase().includes(lower)
    );
}

/**
 * HTTP client for communicating with Unity Editor instances.
 * Replaces the former TCP server architecture with fetch-based HTTP calls.
 */
export class UnityConnection extends EventEmitter {
    private static instance: UnityConnection | null = null;

    private instances: Map<string, UnityInstance> = new Map();
    private activeInstanceId: string | null = null;
    private requestTimeoutMs: number = 30000;
    /** Cache of command-name → idempotency class, populated from /health. */
    private handlerIdempotencyCache: Map<string, Idempotency> = new Map();

    public static getInstance(): UnityConnection {
        if (!UnityConnection.instance) {
            UnityConnection.instance = new UnityConnection();
        }
        return UnityConnection.instance;
    }

    /** Test-only: resets the singleton. DO NOT USE in production code. */
    public static resetInstanceForTesting(): void {
        UnityConnection.instance = null;
    }

    private constructor() {
        super();
        this.on('error', (err) => {
            console.error(`[DEBUG] Error event caught: ${err.message}`);
        });
    }

    // ──────────────────────────────────────────────
    //  Instance Registry (called by ProjectRegistry)
    // ──────────────────────────────────────────────

    /**
     * Registers or updates a Unity instance.
     *
     * NOTE: We DO NOT auto-select the first instance as active (design §3.2).
     * `activeInstanceId` is only set when the user explicitly calls
     * `unity_set_active_client`.
     */
    public registerInstance(instance: UnityInstance): void {
        const existing = this.instances.get(instance.id);
        if (existing) {
            // Merge: preserve state/lastSeen if the caller didn't supply new values.
            this.instances.set(instance.id, { ...existing, ...instance });
            return;
        }

        this.instances.set(instance.id, instance);
        console.error(
            `[INFO] Unity instance registered: ${instance.id} ` +
            `(${instance.projectName} on :${instance.port})`
        );
        this.emit('clientRegistered', {
            clientId: instance.id,
            info: {
                productName: instance.projectName,
                unityVersion: instance.unityVersion,
                isEditor: true,
                projectPathHash: instance.projectPath,
            },
        });
    }

    /**
     * Removes a Unity instance from the registry.
     */
    public removeInstance(id: string): void {
        if (!this.instances.has(id)) return;

        this.instances.delete(id);
        console.error(`[INFO] Unity instance removed: ${id}`);

        if (this.activeInstanceId === id) {
            // Do NOT auto-pick another instance. Leave active unset (user must choose).
            this.activeInstanceId = null;
            this.emit('activeClientChanged', { clientId: null });
        }

        this.emit('clientDisconnected', { clientId: id });
    }

    /**
     * Updates the health state of an instance (used by ProjectRegistry).
     */
    public updateInstanceState(id: string, patch: Partial<UnityInstance>): void {
        const instance = this.instances.get(id);
        if (instance) {
            Object.assign(instance, patch);
        }
    }

    /**
     * Returns a direct reference to a registered instance (or undefined).
     * For internal use by ProjectRegistry / ProjectApi.
     */
    public getInstanceById(id: string): UnityInstance | undefined {
        return this.instances.get(id);
    }

    /**
     * Returns all registered instances regardless of state.
     */
    public getAllInstances(): UnityInstance[] {
        return Array.from(this.instances.values());
    }

    /**
     * Cache of handler → idempotency, populated from /health `handlers[]`.
     */
    public setHandlerIdempotency(cache: Map<string, Idempotency>): void {
        this.handlerIdempotencyCache = cache;
    }

    public mergeHandlerIdempotency(entries: Iterable<[string, Idempotency]>): void {
        for (const [name, idem] of entries) {
            this.handlerIdempotencyCache.set(name, idem);
        }
    }

    /**
     * Looks up a cache entry by the canonical key (as produced by
     * {@link buildIdempotencyKey}, matching Editor `/health.handlers[].name`).
     * Unknown keys fall back to `unsafe` (conservative default).
     */
    public getHandlerIdempotency(cacheKey: string): Idempotency {
        return this.handlerIdempotencyCache.get(cacheKey) ?? 'unsafe';
    }

    // ──────────────────────────────────────────────
    //  target resolution (design §3.2)
    // ──────────────────────────────────────────────

    /**
     * Resolves a target per design §3.2:
     *
     *   1. target explicit → clientId exact > projectName exact (CI) > substring (CI)
     *      Failure → ResolveInstanceError(target_not_found)
     *   2. target omitted + activeInstanceId set → return active if state ∈ {healthy,reloading}
     *   3. target omitted + active not set + exactly 1 instance → use it
     *   4. target omitted + active not set + 0 instances → ResolveInstanceError(no_instance)
     *   5. target omitted + active not set + multiple instances →
     *        ResolveInstanceError(target_required, hint about unity_list_clients)
     */
    public resolveInstance(target?: string): UnityInstance {
        const all = Array.from(this.instances.values());

        if (target !== undefined && target !== null && target !== '') {
            const matches = matchInstancesByTarget(all, target);
            if (matches.length === 0) {
                throw new ResolveInstanceError(
                    ResolveErrorCode.TargetNotFound,
                    `No Unity instance matches target "${target}"`
                );
            }
            if (matches.length > 1) {
                // Previously the first match won. With "MyGame" and "MyGame Sandbox" both
                // open, asking for "MyGame" then resolved by registration order — and the
                // call succeeded against whichever it happened to pick, so a write could land
                // in the wrong project with nothing to indicate it. Exact matches are already
                // preferred above; only an ambiguous substring reaches here.
                throw new ResolveInstanceError(
                    ResolveErrorCode.TargetRequired,
                    `"${target}" matches more than one Unity instance: ` +
                    matches.map(m => m.projectName).join(', ') +
                    '. Use the full project name or the clientId.'
                );
            }

            return matches[0];
        }

        // target omitted
        if (this.activeInstanceId) {
            const active = this.instances.get(this.activeInstanceId);
            if (active && (active.state === 'healthy' || active.state === 'reloading')) {
                return active;
            }
            // active was set but instance went away / unhealthy — fall through
            // to the 0/1/multiple logic below.
        }

        const usable = all.filter(
            i => i.state === 'healthy' || i.state === 'reloading'
        );
        if (usable.length === 0) {
            throw new ResolveInstanceError(
                ResolveErrorCode.NoInstance,
                'No Unity instances are currently registered'
            );
        }
        if (usable.length === 1) {
            return usable[0];
        }
        throw new ResolveInstanceError(
            ResolveErrorCode.TargetRequired,
            'Multiple Unity instances are registered but no target was specified. ' +
            'Call unity_list_clients to see them, then either pass `target` on this call or ' +
            'call unity_set_active_client to fix the destination.'
        );
    }

    // ──────────────────────────────────────────────
    //  sendRequest / sendToEndpoint with retry (design §3.1)
    // ──────────────────────────────────────────────

    /**
     * Shared core transport used by both `sendRequest` (/command wrapper) and
     * `sendToEndpoint` (direct endpoint POST). Handles target resolution,
     * idempotency cache lookup, retry, and envelope unwrap.
     *
     * @param path           absolute path on the Unity instance (e.g. "/command", "/execute_code")
     * @param bodyForKey     body object used to derive the idempotency cache key via buildIdempotencyKey
     * @param payload        the actual JSON body to POST (already shaped per endpoint contract)
     * @param opts           optional overrides (target, retry budget, idempotency)
     */
    private async _sendCore(
        path: string,
        bodyForKey: Record<string, unknown> | null,
        payload: unknown,
        opts?: {
            target?: string;
            retryMaxMs?: number;
            idempotency?: Idempotency;
            method?: 'GET' | 'POST';
        }
    ): Promise<JObject> {
        const retryMaxMs =
            opts?.retryMaxMs
            ?? parseInt(process.env.MCP_RELOAD_RETRY_MAX_MS ?? '15000', 10)
            ?? 15000;

        const instance = this.resolveInstance(opts?.target);

        // Idempotency cache key must match Editor `/health.handlers[].name`
        // format exactly (e.g. `/command:<command.action>`, `/execute_code`).
        const cacheKey = buildIdempotencyKey(path, bodyForKey ?? undefined);
        const idempotency: Idempotency =
            opts?.idempotency
            ?? this.getHandlerIdempotency(cacheKey);

        const method = opts?.method ?? 'POST';

        // A GET carrying a body is rejected by Unity's HttpListener, so the payload is
        // dropped for reads (the catalog fetch has nothing to send anyway).
        const authHeaders: Record<string, string> = instance.token
            ? { Authorization: `Bearer ${instance.token}` }
            : {};

        const init: RetryableFetchInit =
            method === 'GET'
                ? { method, headers: authHeaders }
                : {
                    method,
                    headers: { 'Content-Type': 'application/json', ...authHeaders },
                    body: JSON.stringify(payload),
                };

        try {
            const { response } = await retryableFetch(
                `${instance.endpoint}${path}`,
                init,
                {
                    idempotency,
                    retryMaxMs,
                    perAttemptTimeoutMs: this.requestTimeoutMs,
                }
            );

            let parsed: any;
            try {
                parsed = await response.json();
            } catch {
                throw new Error(
                    `Unity returned invalid JSON on ${path} (${response.status})`
                );
            }

            // Unified envelope: { status: 'success'|'error', result, error }
            if (parsed && typeof parsed === 'object') {
                if (parsed.status === 'success' && parsed.result !== undefined) {
                    return parsed.result as JObject;
                }
                if (parsed.status === 'error') {
                    const err = parsed.error ?? {};
                    const msg = err.message ?? err.m ?? 'Unity returned error';
                    const e = new Error(msg);
                    (e as any).code = err.code ?? McpErrorCode.InternalError;
                    throw e;
                }
            }

            // Legacy / fall-through — return as-is.
            return parsed as JObject;
        } catch (err) {
            if (err instanceof RetryableFetchError) {
                const e = new Error(err.message);
                (e as any).code = err.code;
                (e as any).cause = err.cause;
                throw e;
            }
            throw err;
        }
    }

    /**
     * Sends a command request to Unity via HTTP, with retry per design §3.1.
     *
     * @param request  JObject with at least `command`; may include `type`, `params`.
     * @param opts     Optional target override, retry budget, idempotency override.
     */
    public async sendRequest(
        request: JObject,
        opts?: {
            target?: string;
            retryMaxMs?: number;
            idempotency?: Idempotency;
        }
    ): Promise<JObject> {
        const command = (request.command as string | undefined) ?? '';

        const payload = {
            command,
            type: request.type ?? '',
            params: request.params,
        };

        return this._sendCore('/command', { command }, payload, opts);
    }

    /**
     * Sends a direct POST to an absolute endpoint on a Unity instance
     * (e.g. `/execute_code`, `/capture_screenshot`). Unlike `sendRequest`,
     * the body is forwarded verbatim — no `{command, type, params}` wrapping.
     *
     * Idempotency cache lookup key uses {@link buildIdempotencyKey}(endpoint, body)
     * so handlers registered under `/health.handlers[].name` with matching
     * keys resolve correctly. Retry + error classification are identical to
     * `sendRequest` (both share `_sendCore`).
     *
     * @param endpoint Absolute path starting with "/" (e.g. "/execute_code").
     * @param body     JSON body to POST (forwarded as-is).
     * @param opts     Optional target override, retry budget, idempotency override.
     */
    public async sendToEndpoint(
        endpoint: string,
        body: JObject,
        opts?: {
            target?: string;
            retryMaxMs?: number;
            idempotency?: Idempotency;
        }
    ): Promise<JObject> {
        return this._sendCore(endpoint, body as Record<string, unknown>, body, opts);
    }

    /**
     * GETs an absolute endpoint on a Unity instance and unwraps the envelope.
     * Used for `/tools`, which is a read and therefore always safe to retry.
     */
    public async getFromEndpoint(
        endpoint: string,
        opts?: {
            target?: string;
            retryMaxMs?: number;
        }
    ): Promise<JObject> {
        return this._sendCore(endpoint, null, null, {
            ...opts,
            method: 'GET',
            idempotency: 'safe',
        });
    }

    // ──────────────────────────────────────────────
    //  Public API (preserved for handler compatibility)
    // ──────────────────────────────────────────────

    /**
     * Checks if any Unity instances are connected and healthy-or-reloading.
     */
    public isUnityConnected(): boolean {
        return this.getUsableInstances().length > 0;
    }

    /**
     * Ensures there is at least one usable (healthy or reloading) instance.
     */
    public async ensureConnected(): Promise<void> {
        if (this.getUsableInstances().length === 0) {
            const error = new Error('No Unity clients connected');
            (error as any).code = McpErrorCode.ConnectionError;
            throw error;
        }
    }

    /**
     * Checks if there are any registered instances.
     */
    public hasConnectedClients(): boolean {
        return this.instances.size > 0;
    }

    /**
     * Lists all usable Unity instances (healthy + reloading).
     * Unhealthy instances are excluded.
     */
    public getConnectedClients(): Array<{
        id: string;
        isActive: boolean;
        state: UnityInstanceState;
        info: any;
    }> {
        return Array.from(this.instances.values())
            .filter(i => i.state === 'healthy' || i.state === 'reloading')
            .map(instance => ({
                id: instance.id,
                isActive: instance.id === this.activeInstanceId,
                state: instance.state,
                info: {
                    productName: instance.projectName,
                    unityVersion: instance.unityVersion,
                    isEditor: true,
                    projectPathHash: instance.projectPath,
                    port: instance.port,
                    endpoint: instance.endpoint,
                    state: instance.state,
                },
            }));
    }

    /**
     * Clears all registered instances.
     */
    public clearClients(): void {
        this.instances.clear();
        this.activeInstanceId = null;
    }

    /**
     * Sets the active Unity instance by id. Returns false if the id is unknown.
     */
    public setActiveClient(clientId: string): boolean {
        if (!this.instances.has(clientId)) {
            return false;
        }
        this.activeInstanceId = clientId;
        console.error(`[INFO] Active instance set to: ${clientId}`);
        this.emit('activeClientChanged', { clientId });
        return true;
    }

    /**
     * Sets the active client by looking up a target (clientId or projectName).
     * Returns the resolved instance on success, null on failure.
     */
    public setActiveClientByTarget(target: string): UnityInstance | null {
        const matches = matchInstancesByTarget(Array.from(this.instances.values()), target);
        if (matches.length === 0) return null;
        const picked = matches[0];
        this.activeInstanceId = picked.id;
        console.error(`[INFO] Active instance set to: ${picked.id} (target="${target}")`);
        this.emit('activeClientChanged', { clientId: picked.id });
        return picked;
    }

    /**
     * Gets the active instance ID.
     */
    public getActiveClientId(): string | null {
        return this.activeInstanceId;
    }

    /**
     * Stops the connection (no-op for HTTP client, kept for API compatibility).
     */
    public stop(): void {
        this.clearClients();
        this.emit('serverStopped');
    }

    // ──────────────────────────────────────────────
    //  Internal Helpers
    // ──────────────────────────────────────────────

    private getUsableInstances(): UnityInstance[] {
        return Array.from(this.instances.values())
            .filter(i => i.state === 'healthy' || i.state === 'reloading');
    }
}
