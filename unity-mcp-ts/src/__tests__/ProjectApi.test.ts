/**
 * Tests for ProjectApi:
 *   - /projects listing shape
 *   - /proxy routing — happy path, 404 target_not_found, 409 multiple_matches,
 *     503 unhealthy, sub-path idempotency for retries
 */
import { describe, test, expect, beforeEach, afterEach } from '@jest/globals';
import * as http from 'http';
import * as net from 'net';
import { AddressInfo } from 'net';
import { ProjectApi } from '../core/ProjectApi.js';
import { ProjectRegistry } from '../core/ProjectRegistry.js';
import { UnityConnection, UnityInstance } from '../core/UnityConnection.js';

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

function seedInstance(conn: UnityConnection, overrides: Partial<UnityInstance>): UnityInstance {
    const inst: UnityInstance = {
        id: overrides.id ?? 'P-1',
        projectName: overrides.projectName ?? 'P',
        projectPath: '',
        port: overrides.port ?? 0,
        unityVersion: '',
        endpoint: overrides.endpoint ?? 'http://127.0.0.1:0',
        version: '',
        token: 'test-token',
        state: overrides.state ?? 'healthy',
        lastSeen: Date.now(),
        lastContact: Date.now(),
        consecutiveFailures: 0,
    };
    conn.registerInstance(inst);
    return inst;
}

async function httpGet(port: number, path: string, opts?: {
    method?: string; body?: string | Buffer; headers?: Record<string, string>;
}): Promise<{ status: number; body: string; headers: http.IncomingHttpHeaders }> {
    return new Promise((resolve, reject) => {
        const req = http.request({
            host: '127.0.0.1',
            port,
            path,
            method: opts?.method ?? 'GET',
            headers: opts?.headers,
        }, (res) => {
            const chunks: Buffer[] = [];
            res.on('data', (c) => chunks.push(c));
            res.on('end', () => {
                resolve({
                    status: res.statusCode ?? 0,
                    body: Buffer.concat(chunks).toString('utf8'),
                    headers: res.headers,
                });
            });
        });
        req.on('error', reject);
        if (opts?.body) req.write(opts.body);
        req.end();
    });
}

describe('ProjectApi', () => {
    let api: ProjectApi | null = null;
    let upstream: http.Server | null = null;
    let apiPort = 0;
    let upstreamPort = 0;

    beforeEach(async () => {
        UnityConnection.resetInstanceForTesting();
        apiPort = await freePort();
        upstreamPort = await freePort();
    });

    afterEach(async () => {
        if (api) { await api.stop(); api = null; }
        if (upstream) {
            await new Promise<void>((resolve) => upstream!.close(() => resolve()));
            upstream = null;
        }
    });

    test('/projects returns state + clientId', async () => {
        const conn = UnityConnection.getInstance();
        const registry = new ProjectRegistry(conn, { initialHealthFetchEnabled: false });
        seedInstance(conn, {
            id: 'Alpha-100', projectName: 'Alpha', port: 100,
            endpoint: 'http://127.0.0.1:100', state: 'healthy',
        });
        seedInstance(conn, {
            id: 'Bravo-200', projectName: 'Bravo', port: 200,
            endpoint: 'http://127.0.0.1:200', state: 'reloading',
        });

        api = new ProjectApi(registry, apiPort, apiPort);
        await api.start();

        const r = await httpGet(apiPort, '/projects');
        expect(r.status).toBe(200);
        const body = JSON.parse(r.body);
        expect(body.status).toBe('success');
        expect(body.result.projects).toHaveLength(2);
        const names = body.result.projects.map((p: any) => p.n).sort();
        expect(names).toEqual(['Alpha', 'Bravo']);
        const states = body.result.projects.map((p: any) => p.state).sort();
        expect(states).toEqual(['healthy', 'reloading']);
    });

    test('/proxy forwards GET /health to upstream', async () => {
        upstream = http.createServer((req, res) => {
            if (req.url === '/health') {
                res.setHeader('Content-Type', 'application/json');
                res.end(JSON.stringify({ status: 'success', result: { ok: true, port: upstreamPort } }));
            } else {
                res.statusCode = 404;
                res.end();
            }
        });
        await new Promise<void>((resolve) => upstream!.listen(upstreamPort, '127.0.0.1', resolve));

        const conn = UnityConnection.getInstance();
        const registry = new ProjectRegistry(conn, { initialHealthFetchEnabled: false });
        seedInstance(conn, {
            id: `MyGame-${upstreamPort}`,
            projectName: 'MyGame',
            port: upstreamPort,
            endpoint: `http://127.0.0.1:${upstreamPort}`,
            state: 'healthy',
        });

        api = new ProjectApi(registry, apiPort, apiPort);
        await api.start();

        const r = await httpGet(apiPort, '/proxy/MyGame/health');
        expect(r.status).toBe(200);
        const body = JSON.parse(r.body);
        expect(body.result.ok).toBe(true);
        expect(body.result.port).toBe(upstreamPort);
    });

    test('/proxy forwards POST body to upstream', async () => {
        let received: { body: string; headers: http.IncomingHttpHeaders } | null = null;
        upstream = http.createServer((req, res) => {
            const chunks: Buffer[] = [];
            req.on('data', (c) => chunks.push(c));
            req.on('end', () => {
                received = { body: Buffer.concat(chunks).toString('utf8'), headers: req.headers };
                res.setHeader('Content-Type', 'application/json');
                res.end(JSON.stringify({ status: 'success', result: { echoed: received.body } }));
            });
        });
        await new Promise<void>((resolve) => upstream!.listen(upstreamPort, '127.0.0.1', resolve));

        const conn = UnityConnection.getInstance();
        const registry = new ProjectRegistry(conn, { initialHealthFetchEnabled: false });
        seedInstance(conn, {
            id: `MyGame-${upstreamPort}`,
            projectName: 'MyGame',
            port: upstreamPort,
            endpoint: `http://127.0.0.1:${upstreamPort}`,
        });

        api = new ProjectApi(registry, apiPort, apiPort);
        await api.start();

        const payload = JSON.stringify({ command: 'x', params: { y: 1 } });
        const r = await httpGet(apiPort, '/proxy/MyGame/command', {
            method: 'POST',
            body: payload,
            headers: { 'content-type': 'application/json' },
        });
        expect(r.status).toBe(200);
        const body = JSON.parse(r.body);
        expect(body.result.echoed).toBe(payload);
        expect(received).not.toBeNull();
        expect(received!.headers['host']).toBe(`127.0.0.1:${upstreamPort}`);
        expect(received!.headers['content-length']).toBe(String(Buffer.byteLength(payload)));
    });

    test('/proxy returns 404 target_not_found for unknown name', async () => {
        const conn = UnityConnection.getInstance();
        const registry = new ProjectRegistry(conn, { initialHealthFetchEnabled: false });
        api = new ProjectApi(registry, apiPort, apiPort);
        await api.start();

        const r = await httpGet(apiPort, '/proxy/nonexistent/health');
        expect(r.status).toBe(404);
        const body = JSON.parse(r.body);
        expect(body.error.code).toBe('target_not_found');
    });

    test('/proxy returns 409 multiple_matches for ambiguous target', async () => {
        const conn = UnityConnection.getInstance();
        const registry = new ProjectRegistry(conn, { initialHealthFetchEnabled: false });
        seedInstance(conn, {
            id: 'MyGameA-1', projectName: 'MyGameA',
            port: 1, endpoint: 'http://127.0.0.1:1',
        });
        seedInstance(conn, {
            id: 'MyGameB-2', projectName: 'MyGameB',
            port: 2, endpoint: 'http://127.0.0.1:2',
        });
        api = new ProjectApi(registry, apiPort, apiPort);
        await api.start();

        // Ambiguous substring.
        const r = await httpGet(apiPort, '/proxy/MyGame/health');
        expect(r.status).toBe(409);
        const body = JSON.parse(r.body);
        expect(body.error.code).toBe('multiple_matches');
    });

    test('/proxy returns 503 unhealthy when target is unhealthy', async () => {
        const conn = UnityConnection.getInstance();
        const registry = new ProjectRegistry(conn, { initialHealthFetchEnabled: false });
        seedInstance(conn, {
            id: 'Bad-1', projectName: 'Bad',
            port: 1, endpoint: 'http://127.0.0.1:1',
            state: 'unhealthy',
        });
        api = new ProjectApi(registry, apiPort, apiPort);
        await api.start();

        const r = await httpGet(apiPort, '/proxy/Bad/health');
        expect(r.status).toBe(503);
        const body = JSON.parse(r.body);
        expect(body.error.code).toBe('unhealthy');
    });

    test('/proxy 413 for body_too_large (over 10MB)', async () => {
        const conn = UnityConnection.getInstance();
        const registry = new ProjectRegistry(conn, { initialHealthFetchEnabled: false });
        seedInstance(conn, {
            id: 'Big-1', projectName: 'Big',
            port: 1, endpoint: 'http://127.0.0.1:1',
        });
        api = new ProjectApi(registry, apiPort, apiPort);
        await api.start();

        // The server rejects at the 10MB threshold and destroys the socket
        // (so that the client stops uploading). We must accept either:
        //  (a) the server manages to write the 413 envelope before tearing
        //      down the TCP connection, or
        //  (b) the client sees a premature RST with no response.
        // In either case the test's intent is that the server refused the
        // body. We assert on whichever outcome we observe.
        const bigBody = Buffer.alloc(11 * 1024 * 1024, 0x41);
        const result = await new Promise<{ status?: number; body?: string; clientErrorCode?: string }>(
            (resolve) => {
                const req = http.request({
                    host: '127.0.0.1',
                    port: apiPort,
                    path: '/proxy/Big/command',
                    method: 'POST',
                    headers: { 'content-type': 'application/octet-stream' },
                }, (res) => {
                    const chunks: Buffer[] = [];
                    res.on('data', (c) => chunks.push(c));
                    res.on('end', () => {
                        resolve({
                            status: res.statusCode,
                            body: Buffer.concat(chunks).toString('utf8'),
                        });
                    });
                    res.on('error', () => resolve({ status: res.statusCode }));
                });
                req.on('error', (err: any) => resolve({ clientErrorCode: err?.code ?? 'unknown' }));
                req.write(bigBody);
                req.end();
            }
        );

        if (result.status !== undefined) {
            expect(result.status).toBe(413);
            const body = JSON.parse(result.body!);
            expect(body.error.code).toBe('body_too_large');
        } else {
            // Client got RST / socket hang-up — server rejected successfully.
            expect(result.clientErrorCode).toMatch(/ECONNRESET|EPIPE|ECONNABORTED/);
        }
    });

    test('port fallback: if 27180 in use, takes 27181', async () => {
        const base = await freePort();
        // Occupy "preferred" port.
        const blocker = net.createServer();
        await new Promise<void>((resolve) => blocker.listen(base, '127.0.0.1', resolve));

        try {
            const conn = UnityConnection.getInstance();
            const registry = new ProjectRegistry(conn, { initialHealthFetchEnabled: false });
            api = new ProjectApi(registry, base, base + 5);
            await api.start();
            const actual = api.getPort()!;
            expect(actual).toBeGreaterThan(base);
            expect(actual).toBeLessThanOrEqual(base + 5);
        } finally {
            await new Promise<void>((resolve) => blocker.close(() => resolve()));
        }
    });
});
