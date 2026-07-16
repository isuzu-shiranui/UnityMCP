/**
 * Tests for task-resilience helpers (#9):
 *   - computeBackoffDelayMs (exponential backoff for subsystem restarts)
 *   - ConsecutiveFailureMonitor (rate-limited degradation warnings)
 *   - UnhandledErrorTracker (sliding-window process-level degradation)
 *
 * All time-dependent behavior is driven via injected now()/log sinks —
 * no real timers involved.
 */
import { describe, test, expect } from '@jest/globals';
import {
    computeBackoffDelayMs,
    ConsecutiveFailureMonitor,
    UnhandledErrorTracker,
} from '../core/TaskResilience.js';

describe('computeBackoffDelayMs', () => {
    test('doubles from base and caps at max', () => {
        expect(computeBackoffDelayMs(0)).toBe(5000);
        expect(computeBackoffDelayMs(1)).toBe(10000);
        expect(computeBackoffDelayMs(2)).toBe(20000);
        expect(computeBackoffDelayMs(3)).toBe(40000);
        expect(computeBackoffDelayMs(4)).toBe(60000);   // 80000 capped
        expect(computeBackoffDelayMs(10)).toBe(60000);
    });

    test('stays at cap for absurd attempt counts', () => {
        expect(computeBackoffDelayMs(1000)).toBe(60000);
        expect(Number.isFinite(computeBackoffDelayMs(10_000))).toBe(true);
    });

    test('negative/fractional attempts clamp to attempt 0', () => {
        expect(computeBackoffDelayMs(-3)).toBe(5000);
        expect(computeBackoffDelayMs(0.9)).toBe(5000);
    });

    test('honors custom base and max', () => {
        expect(computeBackoffDelayMs(0, 1000, 4000)).toBe(1000);
        expect(computeBackoffDelayMs(1, 1000, 4000)).toBe(2000);
        expect(computeBackoffDelayMs(2, 1000, 4000)).toBe(4000);
        expect(computeBackoffDelayMs(3, 1000, 4000)).toBe(4000);
    });
});

describe('ConsecutiveFailureMonitor', () => {
    function makeMonitor(opts?: { threshold?: number; intervalMs?: number }) {
        let t = 0;
        const logs: string[] = [];
        const monitor = new ConsecutiveFailureMonitor('test-task', {
            warnThreshold: opts?.threshold ?? 3,
            warnIntervalMs: opts?.intervalMs ?? 60_000,
            nowImpl: () => t,
            logImpl: (m) => logs.push(m),
        });
        return { monitor, logs, setTime: (v: number) => { t = v; } };
    }

    test('does not warn below threshold', () => {
        const { monitor, logs } = makeMonitor();
        expect(monitor.recordFailure(new Error('x'))).toBe(false);
        expect(monitor.recordFailure(new Error('x'))).toBe(false);
        expect(logs).toHaveLength(0);
        expect(monitor.consecutiveFailures).toBe(2);
    });

    test('warns at threshold with task name and count', () => {
        const { monitor, logs } = makeMonitor();
        monitor.recordFailure(new Error('boom'));
        monitor.recordFailure(new Error('boom'));
        expect(monitor.recordFailure(new Error('boom'))).toBe(true);
        expect(logs).toHaveLength(1);
        expect(logs[0]).toContain('[WARN]');
        expect(logs[0]).toContain('test-task');
        expect(logs[0]).toContain('3 consecutive');
        expect(logs[0]).toContain('boom');
    });

    test('rate-limits repeated warnings within warnIntervalMs', () => {
        const { monitor, logs, setTime } = makeMonitor({ intervalMs: 60_000 });
        setTime(0);
        monitor.recordFailure(); monitor.recordFailure();
        expect(monitor.recordFailure()).toBe(true);        // warn #1 at t=0
        setTime(30_000);
        expect(monitor.recordFailure()).toBe(false);       // rate-limited
        setTime(60_000);
        expect(monitor.recordFailure()).toBe(true);        // warn #2 after interval
        expect(logs).toHaveLength(2);
    });

    test('success resets counter and logs recovery after degradation', () => {
        const { monitor, logs } = makeMonitor();
        monitor.recordFailure(); monitor.recordFailure(); monitor.recordFailure();
        expect(logs).toHaveLength(1);
        monitor.recordSuccess();
        expect(monitor.consecutiveFailures).toBe(0);
        expect(logs).toHaveLength(2);
        expect(logs[1]).toContain('recovered');

        // Counter restarts from zero: two failures do not warn again.
        monitor.recordFailure(); monitor.recordFailure();
        expect(logs).toHaveLength(2);
    });

    test('success below threshold does not log recovery', () => {
        const { monitor, logs } = makeMonitor();
        monitor.recordFailure();
        monitor.recordSuccess();
        expect(logs).toHaveLength(0);
    });
});

describe('UnhandledErrorTracker', () => {
    function makeTracker(opts?: { windowMs?: number; threshold?: number }) {
        let t = 0;
        const logs: string[] = [];
        const tracker = new UnhandledErrorTracker({
            windowMs: opts?.windowMs ?? 60_000,
            threshold: opts?.threshold ?? 5,
            nowImpl: () => t,
            logImpl: (m) => logs.push(m),
        });
        return { tracker, logs, setTime: (v: number) => { t = v; } };
    }

    test('does not warn below threshold', () => {
        const { tracker, logs } = makeTracker();
        for (let i = 0; i < 4; i++) {
            expect(tracker.record()).toBe(false);
        }
        expect(logs).toHaveLength(0);
    });

    test('warns when threshold errors cluster within the window', () => {
        const { tracker, logs, setTime } = makeTracker();
        for (let i = 0; i < 4; i++) {
            setTime(i * 1000);
            tracker.record();
        }
        setTime(4000);
        expect(tracker.record()).toBe(true);
        expect(logs).toHaveLength(1);
        expect(logs[0]).toContain('degraded');
    });

    test('errors outside the window do not count', () => {
        const { tracker, logs, setTime } = makeTracker({ windowMs: 10_000 });
        // 4 errors long ago, then 1 recent — window only holds the recent one.
        for (let i = 0; i < 4; i++) {
            setTime(i * 1000);
            tracker.record();
        }
        setTime(100_000);
        expect(tracker.record()).toBe(false);
        expect(logs).toHaveLength(0);
    });

    test('rate-limits repeated warnings', () => {
        const { tracker, logs, setTime } = makeTracker({ windowMs: 60_000 });
        for (let i = 0; i < 5; i++) {
            setTime(i * 1000);
            tracker.record();
        }
        expect(logs).toHaveLength(1);          // warned at t=4000
        setTime(10_000);
        expect(tracker.record()).toBe(false);  // still within warn interval
        setTime(70_000);
        // Old errors fell out of the window; build back up to threshold.
        for (let i = 0; i < 5; i++) {
            setTime(70_000 + i * 1000);
            tracker.record();
        }
        expect(logs).toHaveLength(2);
    });
});
