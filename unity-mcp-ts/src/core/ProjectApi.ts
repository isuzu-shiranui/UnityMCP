import * as http from 'http';
import { Readable } from 'stream';
import { ProjectRegistry } from './ProjectRegistry.js';
import {
    UnityInstance,
    matchInstancesByTarget,
} from './UnityConnection.js';
import {
    retryableFetch,
    RetryableFetchError,
    Idempotency,
} from './retryableFetch.js';

/**
 * HTTP API for CLI tools (curl) to discover and interact with Unity instances.
 *
 *   GET  /projects           - List all registered Unity instances
 *   GET  /projects/:name     - Find instance by project name
 *   ANY  /proxy/:name/*      - Transparent reverse-proxy to Unity HTTP
 *
 * The server binds 27180-27189 (first-come fallback). The actual port is
 * logged to stderr on startup.
 *
 * Idempotency for /proxy retries is determined by the sub-path only (never
 * from external headers — we deliberately do NOT honor X-MCP-Idempotency).
 */
export class ProjectApi {
    private server: http.Server | null = null;
    private registry: ProjectRegistry;
    private readonly preferredPort: number;
    private readonly portRangeEnd: number;
    private actualPort: number | null = null;

    /** Sub-paths that are Safe for retry purposes (design §3.3). */
    private static readonly SAFE_PROXY_SUBPATHS = new Set<string>([
        'health',
        'read_logs',
        'browse_hierarchy',
        'capture_screenshot',
        'resource',
    ]);

    /** Hop-by-hop headers stripped from both directions (RFC 7230 §6.1). */
    private static readonly HOP_BY_HOP_HEADERS = new Set<string>([
        'connection',
        'transfer-encoding',
        'keep-alive',
        'te',
        'trailer',
        'upgrade',
        'proxy-authorization',
        'proxy-authenticate',
    ]);

    /** 10 MB body cap for /proxy. */
    private static readonly MAX_PROXY_BODY_BYTES = 10 * 1024 * 1024;

    constructor(
        registry: ProjectRegistry,
        preferredPort: number = 27180,
        portRangeEnd: number = 27189
    ) {
        this.registry = registry;
        this.preferredPort = preferredPort;
        this.portRangeEnd = portRangeEnd;
    }

    /**
     * Returns the actual port the server bound to (null before start()).
     */
    public getPort(): number | null {
        return this.actualPort;
    }

    /**
     * Starts the HTTP API server. Tries preferredPort first, then walks up to
     * portRangeEnd on EADDRINUSE.
     */
    public async start(): Promise<void> {
        for (let port = this.preferredPort; port <= this.portRangeEnd; port++) {
            try {
                await this.tryListen(port);
                this.actualPort = port;
                console.error(
                    `[INFO] ProjectApi listening on http://127.0.0.1:${port} ` +
                    (port === this.preferredPort ? '' : `(fell back from :${this.preferredPort})`)
                );
                return;
            } catch (err: any) {
                if (err?.code === 'EADDRINUSE') {
                    console.error(
                        `[INFO] ProjectApi port ${port} in use, trying next...`
                    );
                    continue;
                }
                throw err;
            }
        }
        throw new Error(
            `ProjectApi could not bind any port in [${this.preferredPort}-${this.portRangeEnd}]`
        );
    }

    private tryListen(port: number): Promise<void> {
        return new Promise((resolve, reject) => {
            const server = http.createServer((req, res) => {
                this.handleRequest(req, res).catch((err) => {
                    console.error(
                        `[ERROR] Unhandled in ProjectApi handler: ${err instanceof Error ? err.message : String(err)}`
                    );
                    if (!res.headersSent) {
                        res.statusCode = 500;
                        res.setHeader('Content-Type', 'application/json');
                        res.end(JSON.stringify({
                            status: 'error',
                            error: {
                                code: 'internal_error',
                                message: err instanceof Error ? err.message : String(err),
                            },
                        }));
                    } else {
                        try { res.end(); } catch { /* ignore */ }
                    }
                });
            });

            const onError = (err: NodeJS.ErrnoException) => {
                server.removeListener('listening', onListening);
                reject(err);
            };
            const onListening = () => {
                server.removeListener('error', onError);
                this.server = server;
                resolve();
            };

            server.once('error', onError);
            server.once('listening', onListening);
            server.listen(port, '127.0.0.1');
        });
    }

    /**
     * Stops the HTTP API server.
     */
    public stop(): Promise<void> {
        return new Promise((resolve) => {
            if (!this.server) {
                resolve();
                return;
            }
            this.server.close(() => {
                this.server = null;
                this.actualPort = null;
                resolve();
            });
        });
    }

    private async handleRequest(
        req: http.IncomingMessage,
        res: http.ServerResponse
    ): Promise<void> {
        // CORS
        res.setHeader('Access-Control-Allow-Origin', '*');
        res.setHeader('Access-Control-Allow-Methods', 'GET, POST, PUT, PATCH, DELETE, OPTIONS');

        if (req.method === 'OPTIONS') {
            res.writeHead(204);
            res.end();
            return;
        }

        const url = new URL(req.url || '/', `http://${req.headers.host || '127.0.0.1'}`);
        const path = url.pathname;

        if (path === '/projects' && req.method === 'GET') {
            this.handleListProjects(res);
            return;
        }
        if (path.startsWith('/projects/') && req.method === 'GET') {
            const name = decodeURIComponent(path.slice('/projects/'.length));
            this.handleFindProject(res, name);
            return;
        }
        if (path.startsWith('/proxy/')) {
            await this.handleProxy(req, res, url);
            return;
        }

        this.writeJsonError(res, 404, 'handler_not_found', `Unknown endpoint: ${req.method} ${path}`);
    }

    private writeJsonError(
        res: http.ServerResponse,
        status: number,
        code: string,
        message: string
    ): void {
        res.statusCode = status;
        res.setHeader('Content-Type', 'application/json');
        res.end(JSON.stringify({
            status: 'error',
            error: { code, message },
        }));
    }

    // ──────────────────────────────────────────────
    //  /projects listing
    // ──────────────────────────────────────────────

    private handleListProjects(res: http.ServerResponse): void {
        const instances = this.registry.getInstances();

        const projects = instances.map(i => ({
            n: i.projectName,
            port: i.port,
            endpoint: i.endpoint,
            unity: i.unityVersion,
            state: i.state,
            clientId: i.id,
        }));

        res.statusCode = 200;
        res.setHeader('Content-Type', 'application/json');
        res.end(JSON.stringify({ status: 'success', result: { projects } }));
    }

    private handleFindProject(res: http.ServerResponse, name: string): void {
        const instances = this.registry.getInstances();
        const matches = matchInstancesByTarget(instances, name);

        if (matches.length === 0) {
            this.writeJsonError(res, 404, 'target_not_found', `No project found matching "${name}"`);
            return;
        }
        // First match only — /projects is a convenience endpoint.
        const match = matches[0];
        res.statusCode = 200;
        res.setHeader('Content-Type', 'application/json');
        res.end(JSON.stringify({
            status: 'success',
            result: {
                n: match.projectName,
                port: match.port,
                endpoint: match.endpoint,
                unity: match.unityVersion,
                state: match.state,
                clientId: match.id,
            },
        }));
    }

    // ──────────────────────────────────────────────
    //  /proxy/:name/*
    // ──────────────────────────────────────────────

    private async handleProxy(
        req: http.IncomingMessage,
        res: http.ServerResponse,
        url: URL
    ): Promise<void> {
        // path === '/proxy/<name>/<subpath>...'
        const rest = url.pathname.slice('/proxy/'.length);
        const firstSlash = rest.indexOf('/');
        if (firstSlash < 0) {
            this.writeJsonError(res, 400, 'invalid_params', 'Missing sub-path: /proxy/<name>/<subpath>');
            return;
        }
        const rawName = rest.slice(0, firstSlash);
        const subpath = rest.slice(firstSlash + 1); // no leading slash
        const name = decodeURIComponent(rawName);

        // Resolve name.
        const all = this.registry.getInstances();
        const matches = matchInstancesByTarget(all, name);
        if (matches.length === 0) {
            this.writeJsonError(res, 404, 'target_not_found', `No project found matching "${name}"`);
            return;
        }
        if (matches.length > 1) {
            this.writeJsonError(
                res,
                409,
                'multiple_matches',
                `Ambiguous target "${name}" matched: ${matches.map(m => m.id).join(', ')}`
            );
            return;
        }
        const instance: UnityInstance = matches[0];
        if (instance.state === 'unhealthy') {
            this.writeJsonError(res, 503, 'unhealthy', `Target instance "${instance.id}" is unhealthy`);
            return;
        }

        // Buffer request body (cap 10MB). We always buffer — even for GET —
        // because we need to enable retries.
        let body: Buffer;
        try {
            body = await this.readRequestBody(req);
        } catch (err: any) {
            if (err?.code === 'body_too_large') {
                this.writeJsonError(res, 413, 'body_too_large', 'Request body exceeds 10MB limit');
                return;
            }
            throw err;
        }

        // Idempotency classification by sub-path (first segment only).
        const firstSubSegment = subpath.split('/')[0].split('?')[0];
        const idempotency: Idempotency =
            ProjectApi.SAFE_PROXY_SUBPATHS.has(firstSubSegment) ? 'safe' : 'unsafe';

        // Build target URL.
        const targetUrl = `http://127.0.0.1:${instance.port}/${subpath}${url.search}`;

        // Build headers: strip hop-by-hop, set Host, set Content-Length.
        const outHeaders: Record<string, string> = {};
        for (const [k, v] of Object.entries(req.headers)) {
            const lk = k.toLowerCase();
            if (ProjectApi.HOP_BY_HOP_HEADERS.has(lk)) continue;
            if (lk === 'host') continue;
            if (lk === 'content-length') continue;
            if (Array.isArray(v)) outHeaders[k] = v.join(', ');
            else if (typeof v === 'string') outHeaders[k] = v;
        }
        outHeaders['Host'] = `127.0.0.1:${instance.port}`;

        // The Editor now requires a bearer token. Supplying it here is the point of the
        // proxy: a curl user gets to keep writing plain requests against a stable port
        // instead of digging the token out of the descriptor file themselves.
        if (instance.token) {
            outHeaders['Authorization'] = `Bearer ${instance.token}`;
        }

        // Only set Content-Length for methods with body.
        const method = (req.method || 'GET').toUpperCase();
        const hasBody = body.length > 0 && method !== 'GET' && method !== 'HEAD';
        if (hasBody) {
            outHeaders['Content-Length'] = String(body.length);
        }

        // Perform retryable fetch.
        let response: Response;
        try {
            const result = await retryableFetch(
                targetUrl,
                {
                    method,
                    headers: outHeaders,
                    body: hasBody ? body : null,
                },
                {
                    idempotency,
                }
            );
            response = result.response;
        } catch (err) {
            if (err instanceof RetryableFetchError) {
                // Budget exhaustion or fatal upstream.
                if (err.httpStatus !== undefined) {
                    // Forward the last upstream status (5xx or 4xx).
                    res.statusCode = err.httpStatus;
                    res.setHeader('Content-Type', 'application/json');
                    res.end(JSON.stringify({
                        status: 'error',
                        error: {
                            code: err.code,
                            message: err.message,
                        },
                    }));
                    return;
                }
                // Network error exhausted budget → 504.
                this.writeJsonError(res, 504, 'timeout', err.message);
                return;
            }
            throw err;
        }

        // Stream response back — strip hop-by-hop headers.
        res.statusCode = response.status;
        response.headers.forEach((value, key) => {
            const lk = key.toLowerCase();
            if (ProjectApi.HOP_BY_HOP_HEADERS.has(lk)) return;
            // Node http's default chunked encoding for piped responses is fine;
            // we keep Content-Length if present.
            res.setHeader(key, value);
        });

        if (!response.body) {
            res.end();
            return;
        }

        // Convert WHATWG Readable to Node Readable and pipe.
        // @ts-ignore — Node 18+ provides fromWeb
        const nodeReadable = Readable.fromWeb(response.body as any);
        nodeReadable.on('error', (err) => {
            console.error(`[ERROR] Proxy stream error: ${err.message}`);
            try { res.end(); } catch { /* ignore */ }
        });
        nodeReadable.pipe(res);
    }

    private readRequestBody(req: http.IncomingMessage): Promise<Buffer> {
        return new Promise((resolve, reject) => {
            const chunks: Buffer[] = [];
            let total = 0;
            let aborted = false;
            req.on('data', (chunk: Buffer) => {
                if (aborted) return;
                total += chunk.length;
                if (total > ProjectApi.MAX_PROXY_BODY_BYTES) {
                    aborted = true;
                    const err: any = new Error('body_too_large');
                    err.code = 'body_too_large';
                    reject(err);
                    req.destroy();
                    return;
                }
                chunks.push(chunk);
            });
            req.on('end', () => {
                if (aborted) return;
                resolve(Buffer.concat(chunks, total));
            });
            req.on('error', (err) => {
                if (!aborted) reject(err);
            });
        });
    }
}
