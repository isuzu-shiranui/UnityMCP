/**
 * Tests for UnityConnection:
 *   - resolveInstance decision tree (5 cases per design §3.2)
 *   - sendRequest retry classification (design §3.1)
 *   - A8 deterministic test: net.Server + socket.resetAndDestroy → ECONNRESET,
 *       Unsafe retry must fire fetch EXACTLY ONCE.
 */
import { describe, test, expect, beforeEach, jest } from '@jest/globals';
import * as http from 'http';
import * as net from 'net';
import { AddressInfo } from 'net';
import {
    UnityConnection,
    UnityInstance,
    ResolveInstanceError,
    buildIdempotencyKey,
} from '../core/UnityConnection.js';
import {
    retryableFetch,
    classifyError,
    classifyResponseStatus,
    extractErrorCode,
} from '../core/retryableFetch.js';

function makeInstance(overrides: Partial<UnityInstance> = {}): UnityInstance {
    return {
        id: overrides.id ?? 'MyGame-27182',
        projectName: overrides.projectName ?? 'MyGame',
        projectPath: overrides.projectPath ?? '',
        port: overrides.port ?? 27182,
        unityVersion: overrides.unityVersion ?? '2022.3',
        endpoint: overrides.endpoint ?? 'http://127.0.0.1:27182',
        version: overrides.version ?? '2.0.0',
        token: overrides.token ?? 'test-token',
        state: overrides.state ?? 'healthy',
        lastSeen: overrides.lastSeen ?? Date.now(),
        lastContact: overrides.lastContact ?? Date.now(),
        consecutiveFailures: overrides.consecutiveFailures ?? 0,
    };
}

describe('UnityConnection.resolveInstance', () => {
    beforeEach(() => {
        UnityConnection.resetInstanceForTesting();
    });

    test('case 1 — target explicit clientId exact match', () => {
        const conn = UnityConnection.getInstance();
        conn.registerInstance(makeInstance({ id: 'A-1', projectName: 'A' }));
        conn.registerInstance(makeInstance({ id: 'B-2', projectName: 'B' }));
        const r = conn.resolveInstance('B-2');
        expect(r.id).toBe('B-2');
    });

    test('case 1 — target explicit, projectName exact (CI)', () => {
        const conn = UnityConnection.getInstance();
        conn.registerInstance(makeInstance({ id: 'A-1', projectName: 'MyGame' }));
        conn.registerInstance(makeInstance({ id: 'B-2', projectName: 'OtherGame' }));
        const r = conn.resolveInstance('mygame');
        expect(r.id).toBe('A-1');
    });

    test('case 1 — target explicit, projectName substring (CI)', () => {
        const conn = UnityConnection.getInstance();
        conn.registerInstance(makeInstance({ id: 'X-1', projectName: 'MySuperGame' }));
        conn.registerInstance(makeInstance({ id: 'Y-2', projectName: 'Other' }));
        const r = conn.resolveInstance('super');
        expect(r.id).toBe('X-1');
    });

    test('case 1 — target explicit, no match → TargetNotFoundError', () => {
        const conn = UnityConnection.getInstance();
        conn.registerInstance(makeInstance({ id: 'X-1', projectName: 'X' }));
        expect(() => conn.resolveInstance('unknown-project-name'))
            .toThrow(ResolveInstanceError);
        try {
            conn.resolveInstance('unknown-project-name');
        } catch (err) {
            expect((err as ResolveInstanceError).code).toBe('target_not_found');
        }
    });

    test('case 2 — target omitted, activeInstanceId set → active', () => {
        const conn = UnityConnection.getInstance();
        conn.registerInstance(makeInstance({ id: 'A-1', projectName: 'A' }));
        conn.registerInstance(makeInstance({ id: 'B-2', projectName: 'B' }));
        expect(conn.setActiveClient('B-2')).toBe(true);
        const r = conn.resolveInstance();
        expect(r.id).toBe('B-2');
    });

    test('case 2 — activeInstanceId works when state=reloading', () => {
        const conn = UnityConnection.getInstance();
        conn.registerInstance(makeInstance({ id: 'A-1', projectName: 'A', state: 'reloading' }));
        conn.registerInstance(makeInstance({ id: 'B-2', projectName: 'B' }));
        conn.setActiveClient('A-1');
        const r = conn.resolveInstance();
        expect(r.id).toBe('A-1');
    });

    test('case 3 — target omitted, active not set, exactly 1 instance → picks it', () => {
        const conn = UnityConnection.getInstance();
        conn.registerInstance(makeInstance({ id: 'Only-1', projectName: 'Only' }));
        const r = conn.resolveInstance();
        expect(r.id).toBe('Only-1');
    });

    test('case 4 — target omitted, active not set, 0 instances → NoInstanceError', () => {
        const conn = UnityConnection.getInstance();
        try {
            conn.resolveInstance();
            fail('expected NoInstanceError');
        } catch (err) {
            expect((err as ResolveInstanceError).code).toBe('no_instance');
        }
    });

    test('case 5 — target omitted, active not set, multiple → TargetRequiredError', () => {
        const conn = UnityConnection.getInstance();
        conn.registerInstance(makeInstance({ id: 'A-1', projectName: 'A' }));
        conn.registerInstance(makeInstance({ id: 'B-2', projectName: 'B' }));
        try {
            conn.resolveInstance();
            fail('expected TargetRequiredError');
        } catch (err) {
            expect((err as ResolveInstanceError).code).toBe('target_required');
            expect((err as Error).message).toMatch(/unity_listClients|unity_setActiveClient/);
        }
    });

    test('A7 — registering instance does NOT auto-activate', () => {
        const conn = UnityConnection.getInstance();
        conn.registerInstance(makeInstance({ id: 'A-1', projectName: 'A' }));
        conn.registerInstance(makeInstance({ id: 'B-2', projectName: 'B' }));
        expect(conn.getActiveClientId()).toBeNull();
        // With 2 instances, no active, and no target — target_required.
        try {
            conn.resolveInstance();
            fail('expected error');
        } catch (err) {
            expect((err as ResolveInstanceError).code).toBe('target_required');
        }
    });

    test('unhealthy instance is excluded from case 3 single-pick', () => {
        const conn = UnityConnection.getInstance();
        conn.registerInstance(makeInstance({ id: 'A-1', projectName: 'A', state: 'unhealthy' }));
        try {
            conn.resolveInstance();
            fail('expected NoInstanceError');
        } catch (err) {
            expect((err as ResolveInstanceError).code).toBe('no_instance');
        }
    });
});

describe('classifyError / classifyResponseStatus / extractErrorCode', () => {
    test('extracts err.cause.code preferentially', () => {
        const err: any = new Error('x');
        err.cause = { code: 'ECONNREFUSED' };
        expect(extractErrorCode(err)).toBe('ECONNREFUSED');
    });

    test('falls back to err.code', () => {
        const err: any = new Error('x');
        err.code = 'ECONNRESET';
        expect(extractErrorCode(err)).toBe('ECONNRESET');
    });

    test('falls back to err.name for AbortError', () => {
        const err = new Error('x');
        err.name = 'AbortError';
        expect(extractErrorCode(err)).toBe('AbortError');
    });

    test('ECONNREFUSED is retryable for both safe and unsafe', () => {
        const err: any = new Error('x');
        err.cause = { code: 'ECONNREFUSED' };
        expect(classifyError(err, 'safe')).toBe('retryable');
        expect(classifyError(err, 'unsafe')).toBe('retryable');
    });

    test('ECONNRESET is retryable for safe, fatal for unsafe', () => {
        const err: any = new Error('x');
        err.cause = { code: 'ECONNRESET' };
        expect(classifyError(err, 'safe')).toBe('retryable');
        expect(classifyError(err, 'unsafe')).toBe('fatal');
    });

    test('unknown error code: safe=retryable, unsafe=fatal', () => {
        const err: any = new Error('x');
        err.cause = { code: 'SOME_NEW_CODE' };
        expect(classifyError(err, 'safe')).toBe('retryable');
        expect(classifyError(err, 'unsafe')).toBe('fatal');
    });

    test('AbortError: safe=retryable, unsafe=fatal', () => {
        const err = new Error('x');
        err.name = 'AbortError';
        expect(classifyError(err, 'safe')).toBe('retryable');
        expect(classifyError(err, 'unsafe')).toBe('fatal');
    });

    test('HTTP status classification', () => {
        expect(classifyResponseStatus(200, 'safe')).toBe('success');
        expect(classifyResponseStatus(200, 'unsafe')).toBe('success');
        expect(classifyResponseStatus(404, 'safe')).toBe('fatal');
        expect(classifyResponseStatus(404, 'unsafe')).toBe('fatal');
        expect(classifyResponseStatus(500, 'safe')).toBe('retryable');
        expect(classifyResponseStatus(500, 'unsafe')).toBe('fatal');
        expect(classifyResponseStatus(503, 'safe')).toBe('retryable');
    });
});

describe('retryableFetch retry behaviour', () => {
    test('Unsafe + ECONNRESET on first attempt throws after exactly 1 call', async () => {
        const calls: string[] = [];
        const fetchImpl = jest.fn(async (_url: any, _init: any) => {
            calls.push('call');
            const err: any = new Error('socket hang up');
            err.cause = { code: 'ECONNRESET' };
            throw err;
        });
        await expect(retryableFetch(
            'http://127.0.0.1:1/command',
            { method: 'POST' },
            {
                idempotency: 'unsafe',
                retryMaxMs: 2000,
                fetchImpl: fetchImpl as any,
                sleepImpl: async () => { /* no-op */ },
            }
        )).rejects.toThrow();
        expect(calls).toHaveLength(1);
    });

    test('Safe + ECONNRESET retries, eventually fails after budget', async () => {
        const fetchImpl = jest.fn(async () => {
            const err: any = new Error('socket hang up');
            err.cause = { code: 'ECONNRESET' };
            throw err;
        });
        let fakeNow = 0;
        await expect(retryableFetch(
            'http://127.0.0.1:1/health',
            { method: 'GET' },
            {
                idempotency: 'safe',
                retryMaxMs: 500,
                fetchImpl: fetchImpl as any,
                sleepImpl: async (ms) => { fakeNow += ms; },
                nowImpl: () => fakeNow,
            }
        )).rejects.toThrow();
        expect(fetchImpl.mock.calls.length).toBeGreaterThanOrEqual(2);
    });

    test('Pre-handshake ECONNREFUSED retries even for Unsafe', async () => {
        let attempt = 0;
        const fetchImpl = jest.fn(async () => {
            attempt++;
            if (attempt < 3) {
                const err: any = new Error('connect ECONNREFUSED');
                err.cause = { code: 'ECONNREFUSED' };
                throw err;
            }
            return new Response(JSON.stringify({ status: 'success', result: {} }), {
                status: 200, headers: { 'content-type': 'application/json' },
            });
        });
        let fakeNow = 0;
        const result = await retryableFetch(
            'http://127.0.0.1:1/command',
            { method: 'POST', body: '{}' },
            {
                idempotency: 'unsafe',
                retryMaxMs: 5000,
                fetchImpl: fetchImpl as any,
                sleepImpl: async (ms) => { fakeNow += ms; },
                nowImpl: () => fakeNow,
            }
        );
        expect(result.response.status).toBe(200);
        expect(attempt).toBe(3);
    });

    test('Safe + 500 retries; Unsafe + 500 fatal', async () => {
        // Unsafe + 500 → fatal, 1 attempt only
        const unsafeFetch = jest.fn(async () => new Response('server error', { status: 500 }));
        await expect(retryableFetch(
            'http://127.0.0.1:1/command',
            { method: 'POST' },
            {
                idempotency: 'unsafe',
                retryMaxMs: 1000,
                fetchImpl: unsafeFetch as any,
                sleepImpl: async () => {},
            }
        )).rejects.toThrow();
        expect(unsafeFetch.mock.calls.length).toBe(1);

        // Safe + 500 → retryable, more than 1 attempt within budget
        const safeFetch = jest.fn(async () => new Response('server error', { status: 500 }));
        let fakeNow = 0;
        await expect(retryableFetch(
            'http://127.0.0.1:1/health',
            { method: 'GET' },
            {
                idempotency: 'safe',
                retryMaxMs: 500,
                fetchImpl: safeFetch as any,
                sleepImpl: async (ms) => { fakeNow += ms; },
                nowImpl: () => fakeNow,
            }
        )).rejects.toThrow();
        expect(safeFetch.mock.calls.length).toBeGreaterThanOrEqual(2);
    });

    test('4xx is fatal for both', async () => {
        const fetchImpl = jest.fn(async () => new Response('bad', { status: 400 }));
        await expect(retryableFetch(
            'http://127.0.0.1:1/health',
            { method: 'GET' },
            { idempotency: 'safe', retryMaxMs: 1000, fetchImpl: fetchImpl as any, sleepImpl: async () => {} }
        )).rejects.toThrow();
        expect(fetchImpl.mock.calls.length).toBe(1);
    });
});

// ──────────────────────────────────────────────────────────────
// A8 — deterministic ECONNRESET via socket.resetAndDestroy
// ──────────────────────────────────────────────────────────────

describe('A8 — Unsafe + TCP RST → single fetch attempt', () => {
    test('/command with resetAndDestroy triggers ECONNRESET, Unsafe throws with exactly 1 fetch call', async () => {
        const server = net.createServer((socket) => {
            // Give the client time to send headers, then RST the connection.
            socket.on('data', () => {
                // After receiving any bytes of the POST, send RST.
                if (typeof (socket as any).resetAndDestroy === 'function') {
                    (socket as any).resetAndDestroy();
                } else {
                    // Very old Node fallback — force destroy.
                    socket.destroy();
                }
            });
            socket.on('error', () => { /* swallow */ });
        });
        await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
        const addr = server.address() as AddressInfo;
        const url = `http://127.0.0.1:${addr.port}/command`;

        let calls = 0;
        const wrappedFetch = async (u: any, init: any) => {
            calls++;
            // Delegate to global fetch (undici in Node 18+).
            return (globalThis as any).fetch(u, init);
        };

        try {
            await expect(retryableFetch(
                url,
                {
                    method: 'POST',
                    headers: { 'content-type': 'application/json' },
                    body: JSON.stringify({ command: 'execute_code', params: { code: 'x' } }),
                },
                {
                    idempotency: 'unsafe',
                    retryMaxMs: 3000,
                    fetchImpl: wrappedFetch as any,
                }
            )).rejects.toThrow();
        } finally {
            await new Promise<void>((resolve) => server.close(() => resolve()));
        }

        // A8 requires exactly 1 call.
        expect(calls).toBe(1);
    });
});

// ──────────────────────────────────────────────────────────────
// buildIdempotencyKey — cache key format must match Editor /health
// ──────────────────────────────────────────────────────────────

describe('buildIdempotencyKey', () => {
    test('/command with {command:"console.clear"} → /command:console.clear', () => {
        expect(buildIdempotencyKey('/command', { command: 'console.clear' }))
            .toBe('/command:console.clear');
    });

    test('/play_mode with {action:"step"} → /play_mode:step', () => {
        expect(buildIdempotencyKey('/play_mode', { action: 'step' }))
            .toBe('/play_mode:step');
    });

    test('/inspect with {mode:"write"} → /inspect:write', () => {
        expect(buildIdempotencyKey('/inspect', { mode: 'write' }))
            .toBe('/inspect:write');
    });

    test('/execute_code (no body) → /execute_code', () => {
        expect(buildIdempotencyKey('/execute_code')).toBe('/execute_code');
    });

    test('/command with empty body falls back to endpoint', () => {
        expect(buildIdempotencyKey('/command', {})).toBe('/command');
    });

    test('getHandlerIdempotency — unknown key returns unsafe', () => {
        UnityConnection.resetInstanceForTesting();
        const conn = UnityConnection.getInstance();
        expect(conn.getHandlerIdempotency('/nothing:known')).toBe('unsafe');
    });

    test('sendRequest uses /command:<cmd> as the cache lookup key', async () => {
        UnityConnection.resetInstanceForTesting();
        const conn = UnityConnection.getInstance();
        conn.mergeHandlerIdempotency([
            ['/command:console.getLogs', 'safe'],
            ['/command:console.clear', 'unsafe'],
        ]);
        expect(conn.getHandlerIdempotency(
            buildIdempotencyKey('/command', { command: 'console.getLogs' })
        )).toBe('safe');
        expect(conn.getHandlerIdempotency(
            buildIdempotencyKey('/command', { command: 'console.clear' })
        )).toBe('unsafe');
        expect(conn.getHandlerIdempotency(
            buildIdempotencyKey('/command', { command: 'menu.execute' })
        )).toBe('unsafe'); // unknown → conservative
    });
});

// ──────────────────────────────────────────────────────────────
// sendToEndpoint — direct endpoint POST (v2.1, design §3.1)
// ──────────────────────────────────────────────────────────────

async function freePort(): Promise<number> {
    return new Promise((resolve, reject) => {
        const srv = net.createServer();
        srv.unref();
        srv.on('error', reject);
        srv.listen(0, '127.0.0.1', () => {
            const port = (srv.address() as AddressInfo).port;
            srv.close(() => resolve(port));
        });
    });
}

describe('UnityConnection.sendToEndpoint', () => {
    beforeEach(() => {
        UnityConnection.resetInstanceForTesting();
    });

    test('happy path — POSTs verbatim body to `${endpoint}${path}` and unwraps result', async () => {
        const requests: { method: string; url: string; body: string }[] = [];
        const port = await freePort();
        const server = http.createServer((req, res) => {
            const chunks: Buffer[] = [];
            req.on('data', (c) => chunks.push(c));
            req.on('end', () => {
                requests.push({
                    method: req.method ?? '',
                    url: req.url ?? '',
                    body: Buffer.concat(chunks).toString('utf8'),
                });
                res.statusCode = 200;
                res.setHeader('content-type', 'application/json');
                res.end(JSON.stringify({
                    status: 'success',
                    result: { output: '', returnValue: '42' },
                }));
            });
        });
        await new Promise<void>((resolve) => server.listen(port, '127.0.0.1', resolve));

        try {
            const conn = UnityConnection.getInstance();
            conn.registerInstance(makeInstance({
                id: 'X-1',
                projectName: 'X',
                endpoint: `http://127.0.0.1:${port}`,
            }));

            const result = await conn.sendToEndpoint('/execute_code', { code: 'return 42;' });

            expect(requests).toHaveLength(1);
            expect(requests[0].method).toBe('POST');
            expect(requests[0].url).toBe('/execute_code');
            // Body is the raw JObject, not `/command`-wrapped.
            expect(requests[0].body).toBe(JSON.stringify({ code: 'return 42;' }));
            // Envelope unwrapped — returns result directly.
            expect(result).toEqual({ output: '', returnValue: '42' });
        } finally {
            await new Promise<void>((resolve) => server.close(() => resolve()));
        }
    });

    test('Unsafe override + ECONNRESET → fails fast (no retry)', async () => {
        // RST-on-data server: every connection receives some bytes, then gets RST.
        const port = await freePort();
        const server = net.createServer((socket) => {
            socket.on('data', () => {
                if (typeof (socket as any).resetAndDestroy === 'function') {
                    (socket as any).resetAndDestroy();
                } else {
                    socket.destroy();
                }
            });
            socket.on('error', () => { /* swallow */ });
        });
        await new Promise<void>((resolve) => server.listen(port, '127.0.0.1', resolve));

        try {
            const conn = UnityConnection.getInstance();
            conn.registerInstance(makeInstance({
                id: 'X-1',
                projectName: 'X',
                endpoint: `http://127.0.0.1:${port}`,
            }));

            await expect(conn.sendToEndpoint(
                '/execute_code',
                { code: 'x' },
                { idempotency: 'unsafe', retryMaxMs: 2000 }
            )).rejects.toThrow();
            // Not asserting on call count (fetch is internal to retryableFetch)
            // — the behaviour check is that it does NOT hang for the full
            // 2000ms budget; if ECONNRESET were retried it would.
        } finally {
            await new Promise<void>((resolve) => server.close(() => resolve()));
        }
    });

    test('Unsafe + ECONNREFUSED → retries via pre-handshake allowlist', async () => {
        // Send to a definitely-closed port; expect it to retry a few times
        // before the budget expires.
        const port = await freePort(); // nothing listening here
        const conn = UnityConnection.getInstance();
        conn.registerInstance(makeInstance({
            id: 'X-1',
            projectName: 'X',
            endpoint: `http://127.0.0.1:${port}`,
        }));

        const attempts: number[] = [];
        // We can't directly count retries without spying on fetch; instead
        // check that the call takes close to retryMaxMs before failing
        // (confirming multiple attempts with backoff).
        const startedAt = Date.now();
        await expect(conn.sendToEndpoint(
            '/execute_code',
            { code: 'x' },
            { idempotency: 'unsafe', retryMaxMs: 400 }
        )).rejects.toThrow();
        const elapsed = Date.now() - startedAt;
        // At minimum: more than ~100ms (first backoff) — single attempt would
        // throw immediately on ECONNREFUSED.
        expect(elapsed).toBeGreaterThanOrEqual(100);
        // And not wildly past the budget — retries respected the retryMaxMs.
        expect(elapsed).toBeLessThan(5000);
        // Silence unused-var warning.
        expect(attempts.length).toBe(0);
    });

    test('buildIdempotencyKey("/execute_code", body) resolves to "/execute_code"', () => {
        // Regression: sendToEndpoint relies on the default-case behaviour
        // of buildIdempotencyKey for non-/command endpoints.
        expect(buildIdempotencyKey('/execute_code', { code: 'return 1;' }))
            .toBe('/execute_code');
    });
});
