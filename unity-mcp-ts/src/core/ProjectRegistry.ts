import { EventEmitter } from 'events';
import {
    UnityConnection,
    UnityInstance,
    UnityInstanceState,
} from './UnityConnection.js';
import { InstanceDescriptor, readDescriptors } from './InstanceDescriptors.js';
import { extractErrorCode, Idempotency } from './retryableFetch.js';
import { ConsecutiveFailureMonitor } from './TaskResilience.js';

/** Authorization header for an instance, empty when its descriptor carried no token. */
function authHeader(instance: UnityInstance): Record<string, string> {
    return instance.token ? { Authorization: `Bearer ${instance.token}` } : {};
}

export interface ProjectRegistryOptions {
    /** How often the descriptor directory is re-read. Default 2000 ms. */
    descriptorPollIntervalMs?: number;
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
    /** Descriptor reader injection for tests. */
    readDescriptorsImpl?: () => Promise<InstanceDescriptor[]>;
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
    private descriptorInterval: ReturnType<typeof setInterval> | null = null;
    private healthInterval: ReturnType<typeof setInterval> | null = null;
    private evictionInterval: ReturnType<typeof setInterval> | null = null;
    private unityConnection: UnityConnection;
    /** Instances for which we've fetched /health at least once. */
    private healthFetched: Set<string> = new Set();
    private readonly healthPollMonitor: ConsecutiveFailureMonitor;
    private readonly evictionMonitor: ConsecutiveFailureMonitor;

    public readonly descriptorPollIntervalMs: number;
    public readonly healthPollIntervalMs: number;
    public readonly staleThresholdMs: number;
    public readonly unhealthyCooldownMs: number;
    public readonly evictionIntervalMs: number;
    public readonly healthPollTimeoutMs: number;
    public readonly initialHealthFetchEnabled: boolean;

    private readonly nowFn: () => number;
    private readonly fetchFn: typeof fetch;
    private readonly readDescriptorsFn: () => Promise<InstanceDescriptor[]>;

    constructor(unityConnection: UnityConnection, options?: ProjectRegistryOptions) {
        super();
        this.unityConnection = unityConnection;
        this.descriptorPollIntervalMs = options?.descriptorPollIntervalMs ?? 2000;
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
        this.readDescriptorsFn = options?.readDescriptorsImpl ?? readDescriptors;
        this.healthPollMonitor = new ConsecutiveFailureMonitor('health-poll', { nowImpl: this.nowFn });
        this.evictionMonitor = new ConsecutiveFailureMonitor('eviction', { nowImpl: this.nowFn });
    }

    /**
     * Starts descriptor discovery and health polling.
     */
    public start(): void {
        void this.sweepDescriptors();
        this.descriptorInterval = setInterval(() => {
            void this.sweepDescriptors();
        }, this.descriptorPollIntervalMs);

        this.startHealthPolling();
        this.startEvictionLoop();
        console.error(
            `[INFO] ProjectRegistry started (descriptor sweep ${this.descriptorPollIntervalMs}ms, ` +
            `health poll ${this.healthPollIntervalMs}ms, cooldown ${this.unhealthyCooldownMs}ms)`
        );
    }

    /**
     * Stops all background processes.
     */
    public stop(): void {
        if (this.descriptorInterval) {
            clearInterval(this.descriptorInterval);
            this.descriptorInterval = null;
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
    //  Descriptor discovery
    // ──────────────────────────────────────────────

    /**
     * Reads the descriptor directory and reconciles it with the registry.
     *
     * Replaces the UDP broadcast listener. Broadcasting could not distinguish a local Editor
     * from one on another machine — a remote announce registered as a dead local instance and
     * then made every call fail with "target required" — and it could not carry the auth
     * token. It also imposed a 30-second floor on noticing a new Editor; a 2-second directory
     * read is both faster and quieter.
     */
    public async sweepDescriptors(): Promise<UnityInstance[]> {
        let descriptors: InstanceDescriptor[];

        try {
            descriptors = await this.readDescriptorsFn();
        } catch (err) {
            console.error(
                `[WARN] Could not read instance descriptors: ${err instanceof Error ? err.message : String(err)}`
            );
            return [];
        }

        const now = this.nowFn();
        const seen = new Set<string>();
        const instances: UnityInstance[] = [];

        for (const descriptor of descriptors) {
            const id = `${descriptor.projectName}-${descriptor.port}`;
            seen.add(id);

            const existing = this.unityConnection.getInstanceById(id);

            const instance: UnityInstance = {
                ...(existing ?? {}),
                id,
                projectName: descriptor.projectName,
                projectPath: descriptor.projectPath ?? '',
                port: descriptor.port,
                unityVersion: descriptor.unityVersion ?? '',
                endpoint: descriptor.endpoint || `http://127.0.0.1:${descriptor.port}`,
                version: descriptor.protocolVersion ?? '',
                token: descriptor.token,
                // A present descriptor means the Editor published itself and has not withdrawn
                // it, so treat it the way an announce was treated: back to healthy.
                state: 'healthy',
                lastSeen: now,
                lastContact: now,
                consecutiveFailures: 0,
            };

            instances.push(instance);

            if (!existing) {
                this.unityConnection.registerInstance(instance);
                this.emit('instanceDiscovered', instance);

                if (this.initialHealthFetchEnabled && !this.healthFetched.has(id)) {
                    this.healthFetched.add(id);
                    this.fetchInitialHealth(instance).catch((err) => {
                        console.error(
                            `[WARN] Initial /health fetch failed for ${id}: ${err instanceof Error ? err.message : String(err)}`
                        );
                    });
                }
            } else {
                this.unityConnection.registerInstance(instance);
            }
        }

        // A withdrawn descriptor is a clean shutdown, which is more definite than any health
        // poll result — drop the instance immediately rather than waiting for it to go stale.
        for (const instance of this.unityConnection.getAllInstances()) {
            if (!seen.has(instance.id)) {
                console.error(`[INFO] ${instance.id} withdrew its descriptor; unregistering`);
                this.healthFetched.delete(instance.id);
                this.unityConnection.removeInstance(instance.id);
                this.emit('instanceRemoved', instance);
            }
        }

        return instances;
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
                headers: authHeader(instance),
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
                    headers: authHeader(instance),
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
