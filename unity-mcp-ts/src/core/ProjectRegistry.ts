import * as dgram from 'dgram';
import { EventEmitter } from 'events';
import {
    UnityConnection,
    UnityInstance,
    UnityInstanceState,
} from './UnityConnection.js';
import { extractErrorCode, Idempotency } from './retryableFetch.js';
import { computeBackoffDelayMs, ConsecutiveFailureMonitor } from './TaskResilience.js';

export interface ProjectRegistryOptions {
    udpPort?: number;
    healthPollIntervalMs?: number;
    /** After this many ms of no contact, an unhealthy instance is removed. */
    staleThresholdMs?: number;
    /**
     * Cooldown before a "reloading" instance escalates to "unhealthy".
     * Default 60s (= 2× UDP announce interval of 30s).
     */
    unhealthyCooldownMs?: number;
    /** Interval to sweep unhealthy-&-stale instances. Default 15s. */
    evictionIntervalMs?: number;
    /** Per-poll HTTP timeout (default 5000 ms). */
    healthPollTimeoutMs?: number;
    /**
     * If true, fetch /health once on register to populate handler idempotency
     * cache. Default true.
     */
    initialHealthFetchEnabled?: boolean;
    /** now() injection for tests. */
    nowImpl?: () => number;
    /** fetch injection for tests. */
    fetchImpl?: typeof fetch;
}

/**
 * Discovers Unity instances via UDP broadcast and manages their lifecycle.
 * Listens for "unity_announce" UDP messages and maintains state via HTTP polling.
 *
 * State machine (design §3.4):
 *   - healthy: last /health 200 ok OR UDP announce just seen
 *   - reloading: poll failed but lastContact within unhealthyCooldownMs
 *   - unhealthy: poll failed consecutively ≥2 AND lastContact ≥ cooldownMs ago
 *   - UDP announce resets state → healthy regardless
 *   - Separate eviction loop removes unhealthy instances after staleThresholdMs.
 */
export class ProjectRegistry extends EventEmitter {
    private udpSocket: dgram.Socket | null = null;
    private healthInterval: ReturnType<typeof setInterval> | null = null;
    private evictionInterval: ReturnType<typeof setInterval> | null = null;
    private unityConnection: UnityConnection;
    /** Instances for which we've fetched /health at least once. */
    private healthFetched: Set<string> = new Set();
    /** Consecutive UDP listener restarts since the last successful bind (#9). */
    private udpRestartAttempts: number = 0;
    private readonly healthPollMonitor: ConsecutiveFailureMonitor;
    private readonly evictionMonitor: ConsecutiveFailureMonitor;

    public readonly udpPort: number;
    public readonly healthPollIntervalMs: number;
    public readonly staleThresholdMs: number;
    public readonly unhealthyCooldownMs: number;
    public readonly evictionIntervalMs: number;
    public readonly healthPollTimeoutMs: number;
    public readonly initialHealthFetchEnabled: boolean;

    private readonly nowFn: () => number;
    private readonly fetchFn: typeof fetch;

    constructor(unityConnection: UnityConnection, options?: ProjectRegistryOptions) {
        super();
        this.unityConnection = unityConnection;
        this.udpPort = options?.udpPort ?? 27183;
        this.healthPollIntervalMs = options?.healthPollIntervalMs ?? 10000;
        this.staleThresholdMs = options?.staleThresholdMs ?? 90000;
        this.unhealthyCooldownMs =
            options?.unhealthyCooldownMs
            ?? parseInt(process.env.MCP_UNHEALTHY_COOLDOWN_MS ?? '60000', 10)
            ?? 60000;
        this.evictionIntervalMs = options?.evictionIntervalMs ?? 15000;
        this.healthPollTimeoutMs = options?.healthPollTimeoutMs ?? 5000;
        this.initialHealthFetchEnabled = options?.initialHealthFetchEnabled ?? true;
        this.nowFn = options?.nowImpl ?? Date.now;
        this.fetchFn = options?.fetchImpl ?? fetch;
        this.healthPollMonitor = new ConsecutiveFailureMonitor('health-poll', { nowImpl: this.nowFn });
        this.evictionMonitor = new ConsecutiveFailureMonitor('eviction', { nowImpl: this.nowFn });
    }

    /**
     * Starts UDP listener and health polling.
     */
    public start(): void {
        this.startUdpListener();
        this.startHealthPolling();
        this.startEvictionLoop();
        console.error(
            `[INFO] ProjectRegistry started (UDP :${this.udpPort}, health poll ${this.healthPollIntervalMs}ms, cooldown ${this.unhealthyCooldownMs}ms)`
        );
    }

    /**
     * Stops all background processes.
     */
    public stop(): void {
        if (this.udpSocket) {
            try { this.udpSocket.close(); } catch { /* ignore */ }
            this.udpSocket = null;
        }
        if (this.healthInterval) {
            clearInterval(this.healthInterval);
            this.healthInterval = null;
        }
        if (this.evictionInterval) {
            clearInterval(this.evictionInterval);
            this.evictionInterval = null;
        }
        console.error('[INFO] ProjectRegistry stopped');
    }

    /**
     * Returns all known Unity instances (direct references — treat as read-only).
     */
    public getInstances(): UnityInstance[] {
        return this.unityConnection.getAllInstances();
    }

    // ──────────────────────────────────────────────
    //  UDP Listener
    // ──────────────────────────────────────────────

    private startUdpListener(): void {
        try {
            this.udpSocket = dgram.createSocket({ type: 'udp4', reuseAddr: true });

            this.udpSocket.on('error', (err) => {
                // Restart with exponential backoff so a persistent failure
                // (e.g. port taken) doesn't turn into a hot retry loop (#9).
                const delayMs = computeBackoffDelayMs(this.udpRestartAttempts);
                this.udpRestartAttempts++;
                console.error(
                    `[ERROR] UDP listener error: ${err.message} — restart #${this.udpRestartAttempts} in ${delayMs}ms`
                );
                try { this.udpSocket?.close(); } catch { /* ignore */ }
                this.udpSocket = null;
                setTimeout(() => this.startUdpListener(), delayMs);
            });

            this.udpSocket.on('message', (msg, rinfo) => {
                this.handleUdpMessage(msg, rinfo);
            });

            this.udpSocket.bind(this.udpPort, () => {
                console.error(`[INFO] UDP listener bound to port ${this.udpPort}`);
                this.udpRestartAttempts = 0;
            });
        } catch (err) {
            console.error(
                `[ERROR] Failed to start UDP listener: ${err instanceof Error ? err.message : String(err)}`
            );
        }
    }

    /**
     * Parses an incoming UDP message. Exposed for testing.
     */
    public handleUdpMessage(msg: Buffer, rinfo: dgram.RemoteInfo): UnityInstance | null {
        try {
            const data = JSON.parse(msg.toString('utf8'));

            if (data.type !== 'unity_announce') {
                return null;
            }

            const projectName = data.n || data.projectName || 'Unknown';
            const port = data.port;
            const projectPath = data.path || '';
            const unityVersion = data.unity || data.unityVersion || '';
            const version = data.v || '';

            if (!port) {
                console.error('[WARN] Received unity_announce without port, ignoring');
                return null;
            }

            // Unity Editor binds HTTP on 127.0.0.1 only (requirements R7.2), but
            // its UDP broadcast can egress via any NIC, so rinfo.address may be
            // the machine's LAN address (e.g. 10.x.x.x). For loopback-bound HTTP
            // we must always contact via 127.0.0.1.
            const endpoint = `http://127.0.0.1:${port}`;
            const id = `${projectName}-${port}`;
            const now = this.nowFn();

            const existing = this.unityConnection.getInstanceById(id);
            const instance: UnityInstance = existing ? {
                ...existing,
                // UDP announce → reset to healthy regardless of prior state.
                projectName,
                projectPath,
                port,
                unityVersion,
                endpoint,
                version,
                state: 'healthy',
                lastSeen: now,
                lastContact: now,
                consecutiveFailures: 0,
            } : {
                id,
                projectName,
                projectPath,
                port,
                unityVersion,
                endpoint,
                version,
                state: 'healthy',
                lastSeen: now,
                lastContact: now,
                consecutiveFailures: 0,
            };

            this.unityConnection.registerInstance(instance);
            this.emit('instanceDiscovered', instance);

            // Kick off a one-shot /health fetch to populate the idempotency cache.
            if (!existing && this.initialHealthFetchEnabled && !this.healthFetched.has(id)) {
                this.healthFetched.add(id);
                this.fetchInitialHealth(instance).catch((err) => {
                    console.error(
                        `[WARN] Initial /health fetch failed for ${id}: ${err instanceof Error ? err.message : String(err)}`
                    );
                });
            }

            return instance;
        } catch (err) {
            console.error(
                `[WARN] Failed to parse UDP message: ${err instanceof Error ? err.message : String(err)}`
            );
            return null;
        }
    }

    /**
     * One-shot /health fetch used to populate the handler idempotency cache.
     */
    private async fetchInitialHealth(instance: UnityInstance): Promise<void> {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), this.healthPollTimeoutMs);
        try {
            const res = await this.fetchFn(`${instance.endpoint}/health`, {
                signal: controller.signal,
            });
            if (!res.ok) return;
            const parsed: any = await res.json();
            // Envelope may be { status, result: { handlers: [...] } } OR a raw { handlers: [...] }.
            const body = parsed?.status === 'success' ? parsed.result : parsed;
            const handlers = body?.handlers;
            if (Array.isArray(handlers)) {
                const entries: Array<[string, Idempotency]> = [];
                for (const h of handlers) {
                    if (h && typeof h.name === 'string') {
                        const idem: Idempotency = (h.idempotency === 'safe') ? 'safe' : 'unsafe';
                        entries.push([h.name, idem]);
                    }
                }
                if (entries.length > 0) {
                    this.unityConnection.mergeHandlerIdempotency(entries);
                    console.error(
                        `[INFO] Populated idempotency cache for ${entries.length} handler(s) from ${instance.id}`
                    );
                }
            }
        } finally {
            clearTimeout(timer);
        }
    }

    // ──────────────────────────────────────────────
    //  Health Polling (state machine per design §3.4)
    // ──────────────────────────────────────────────

    private startHealthPolling(): void {
        this.healthInterval = setInterval(
            () => {
                this.pollHealth().then(
                    () => this.healthPollMonitor.recordSuccess(),
                    (err) => this.healthPollMonitor.recordFailure(err)
                );
            },
            this.healthPollIntervalMs
        );
    }

    private startEvictionLoop(): void {
        this.evictionInterval = setInterval(
            () => {
                try {
                    this.sweepStaleUnhealthy();
                    this.evictionMonitor.recordSuccess();
                } catch (err) {
                    this.evictionMonitor.recordFailure(err);
                }
            },
            this.evictionIntervalMs
        );
    }

    /**
     * Sweeps unhealthy instances whose lastSeen is older than staleThresholdMs.
     */
    public sweepStaleUnhealthy(): void {
        const now = this.nowFn();
        for (const instance of this.unityConnection.getAllInstances()) {
            if (
                instance.state === 'unhealthy' &&
                now - instance.lastSeen > this.staleThresholdMs
            ) {
                console.error(
                    `[INFO] Evicting stale unhealthy instance ${instance.id} (last seen ${now - instance.lastSeen}ms ago)`
                );
                this.unityConnection.removeInstance(instance.id);
            }
        }
    }

    /**
     * Polls /health for every registered instance. Exposed for testing.
     */
    public async pollHealth(): Promise<void> {
        const instances = this.unityConnection.getAllInstances();

        await Promise.all(instances.map((inst) => this.pollOne(inst)));
    }

    private async pollOne(instance: UnityInstance): Promise<void> {
        let ok = false;
        let gotResponseBody: any = null;
        try {
            const controller = new AbortController();
            const timer = setTimeout(() => controller.abort(), this.healthPollTimeoutMs);
            try {
                const response = await this.fetchFn(`${instance.endpoint}/health`, {
                    signal: controller.signal,
                });
                if (response.ok) {
                    ok = true;
                    try {
                        gotResponseBody = await response.json();
                    } catch { /* ignore */ }
                }
            } finally {
                clearTimeout(timer);
            }
        } catch (err) {
            // ECONNREFUSED / ECONNRESET / timeout → treat as failure.
            const code = extractErrorCode(err);
            void code; // for future logging
            ok = false;
        }

        this.applyPollOutcome(instance.id, ok, gotResponseBody);
    }

    /**
     * Applies a poll outcome to the state machine. Exposed for testing.
     */
    public applyPollOutcome(
        id: string,
        ok: boolean,
        responseBody?: any
    ): UnityInstanceState | null {
        const instance = this.unityConnection.getInstanceById(id);
        if (!instance) return null;

        const now = this.nowFn();
        const prevState = instance.state;

        if (ok) {
            // 200 OK → healthy
            instance.state = 'healthy';
            instance.lastSeen = now;
            instance.lastContact = now;
            instance.consecutiveFailures = 0;

            // Opportunistically populate handler idempotency.
            if (responseBody && !this.healthFetched.has(id)) {
                this.healthFetched.add(id);
                const body = responseBody?.status === 'success' ? responseBody.result : responseBody;
                const handlers = body?.handlers;
                if (Array.isArray(handlers)) {
                    const entries: Array<[string, Idempotency]> = [];
                    for (const h of handlers) {
                        if (h && typeof h.name === 'string') {
                            const idem: Idempotency = (h.idempotency === 'safe') ? 'safe' : 'unsafe';
                            entries.push([h.name, idem]);
                        }
                    }
                    if (entries.length > 0) {
                        this.unityConnection.mergeHandlerIdempotency(entries);
                    }
                }
            }
        } else {
            // Failure.
            instance.consecutiveFailures++;

            if (prevState === 'healthy') {
                instance.state = 'reloading';
            } else if (prevState === 'reloading') {
                const since = now - instance.lastContact;
                if (
                    instance.consecutiveFailures >= 2 &&
                    since >= this.unhealthyCooldownMs
                ) {
                    instance.state = 'unhealthy';
                } else {
                    // Stay reloading.
                    instance.state = 'reloading';
                }
            } else {
                // prevState === 'unhealthy' → stay unhealthy.
                instance.state = 'unhealthy';
            }
        }

        if (prevState !== instance.state) {
            console.error(
                `[INFO] Instance ${id} state: ${prevState} → ${instance.state}`
            );
            this.emit('stateChanged', {
                id,
                from: prevState,
                to: instance.state,
            });
        }

        return instance.state;
    }
}
