/**
 * Task-resilience helpers for long-running background tasks (#9).
 *
 * The MCP server intentionally survives uncaught exceptions (see index.ts),
 * so background subsystems need their own degradation detection:
 *   - computeBackoffDelayMs: exponential backoff for subsystem restarts
 *   - ConsecutiveFailureMonitor: consecutive-failure counting with
 *     rate-limited degradation warnings
 */

/**
 * Computes an exponential backoff delay for the given restart attempt.
 * attempt 0 → baseMs, attempt 1 → baseMs*2, ... capped at maxMs.
 */
export function computeBackoffDelayMs(
    attempt: number,
    baseMs: number = 5000,
    maxMs: number = 60000
): number {
    const safeAttempt = Math.max(0, Math.floor(attempt));
    // Past this many doublings we are guaranteed to exceed maxMs — avoids
    // Math.pow overflow to Infinity for absurd attempt counts.
    if (safeAttempt >= 31) {
        return maxMs;
    }
    return Math.min(baseMs * Math.pow(2, safeAttempt), maxMs);
}

export interface ConsecutiveFailureMonitorOptions {
    /** Consecutive failures required before warning. Default 3. */
    warnThreshold?: number;
    /** Minimum interval between repeated warnings. Default 60000 ms. */
    warnIntervalMs?: number;
    /** now() injection for tests. */
    nowImpl?: () => number;
    /** Log sink injection for tests. Default console.error. */
    logImpl?: (message: string) => void;
}

/**
 * Tracks consecutive failures of a named background task and emits a
 * rate-limited "server may be degraded" warning once the threshold is hit.
 * A success resets the counter (and logs recovery if it had degraded).
 */
export class ConsecutiveFailureMonitor {
    private readonly name: string;
    private readonly warnThreshold: number;
    private readonly warnIntervalMs: number;
    private readonly nowFn: () => number;
    private readonly logFn: (message: string) => void;

    private failures: number = 0;
    private lastWarnAt: number | null = null;

    constructor(name: string, options?: ConsecutiveFailureMonitorOptions) {
        this.name = name;
        this.warnThreshold = options?.warnThreshold ?? 3;
        this.warnIntervalMs = options?.warnIntervalMs ?? 60000;
        this.nowFn = options?.nowImpl ?? Date.now;
        this.logFn = options?.logImpl ?? ((message) => console.error(message));
    }

    public get consecutiveFailures(): number {
        return this.failures;
    }

    /**
     * Records a successful tick. Resets the failure counter and logs
     * recovery if the task had previously been reported as degraded.
     */
    public recordSuccess(): void {
        if (this.failures >= this.warnThreshold) {
            this.logFn(`[INFO] Background task "${this.name}" recovered after ${this.failures} consecutive failure(s)`);
        }
        this.failures = 0;
        this.lastWarnAt = null;
    }

    /**
     * Records a failed tick. Returns true if a degradation warning was
     * emitted (threshold reached and not rate-limited).
     */
    public recordFailure(error?: unknown): boolean {
        this.failures++;

        const message = error instanceof Error ? error.message : error !== undefined ? String(error) : '';
        if (this.failures < this.warnThreshold) {
            return false;
        }

        const now = this.nowFn();
        if (this.lastWarnAt !== null && now - this.lastWarnAt < this.warnIntervalMs) {
            return false;
        }

        this.lastWarnAt = now;
        this.logFn(
            `[WARN] Background task "${this.name}" failed ${this.failures} consecutive time(s) — server may be degraded${message ? `: ${message}` : ''}`
        );
        return true;
    }
}

export interface UnhandledErrorTrackerOptions {
    /** Sliding window size. Default 60000 ms. */
    windowMs?: number;
    /** Errors within the window required before warning. Default 5. */
    threshold?: number;
    /** Minimum interval between repeated warnings. Default = windowMs. */
    warnIntervalMs?: number;
    /** now() injection for tests. */
    nowImpl?: () => number;
    /** Log sink injection for tests. Default console.error. */
    logImpl?: (message: string) => void;
}

/**
 * Tracks process-level unhandled errors (uncaughtException /
 * unhandledRejection) in a sliding window and warns when they cluster,
 * signalling that the always-alive server may be in a degraded state.
 */
export class UnhandledErrorTracker {
    private readonly windowMs: number;
    private readonly threshold: number;
    private readonly warnIntervalMs: number;
    private readonly nowFn: () => number;
    private readonly logFn: (message: string) => void;

    private timestamps: number[] = [];
    private lastWarnAt: number | null = null;

    constructor(options?: UnhandledErrorTrackerOptions) {
        this.windowMs = options?.windowMs ?? 60000;
        this.threshold = options?.threshold ?? 5;
        this.warnIntervalMs = options?.warnIntervalMs ?? this.windowMs;
        this.nowFn = options?.nowImpl ?? Date.now;
        this.logFn = options?.logImpl ?? ((message) => console.error(message));
    }

    /**
     * Records one unhandled error. Returns true if a degradation warning
     * was emitted.
     */
    public record(): boolean {
        const now = this.nowFn();
        this.timestamps.push(now);
        this.timestamps = this.timestamps.filter((t) => now - t <= this.windowMs);

        if (this.timestamps.length < this.threshold) {
            return false;
        }
        if (this.lastWarnAt !== null && now - this.lastWarnAt < this.warnIntervalMs) {
            return false;
        }

        this.lastWarnAt = now;
        this.logFn(
            `[WARN] ${this.timestamps.length} unhandled error(s) in the last ${Math.round(this.windowMs / 1000)}s — server may be in a degraded state; consider restarting the MCP server`
        );
        return true;
    }
}
