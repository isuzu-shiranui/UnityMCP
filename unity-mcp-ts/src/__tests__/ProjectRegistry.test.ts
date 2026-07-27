/**
 * Tests for ProjectRegistry:
 *   - Descriptor discovery (sweepDescriptors)
 *   - State transitions driven by applyPollOutcome
 *
 * We stub the descriptor reader and inject a controllable `now()` so all transitions can be
 * asserted deterministically.
 */
import { describe, test, expect, beforeEach, jest } from '@jest/globals';
import { ProjectRegistry } from '../core/ProjectRegistry.js';
import { InstanceDescriptor } from '../core/InstanceDescriptors.js';
import { UnityConnection } from '../core/UnityConnection.js';

function makeDescriptor(overrides: Partial<InstanceDescriptor> = {}): InstanceDescriptor {
    return {
        projectPath: '/some/path',
        projectName: 'MyGame',
        unityVersion: '2022.3.10f1',
        port: 27182,
        token: 'secret-token',
        pid: 1234,
        protocolVersion: '3.0.0',
        endpoint: 'http://127.0.0.1:27182',
        ...overrides,
    };
}

function makeRegistry(opts: {
    now: () => number;
    cooldownMs?: number;
    staleMs?: number;
    descriptors?: () => Promise<InstanceDescriptor[]>;
}): { registry: ProjectRegistry; conn: UnityConnection } {
    UnityConnection.resetInstanceForTesting();
    const conn = UnityConnection.getInstance();
    const registry = new ProjectRegistry(conn, {
        nowImpl: opts.now,
        unhealthyCooldownMs: opts.cooldownMs ?? 60_000,
        staleThresholdMs: opts.staleMs ?? 90_000,
        initialHealthFetchEnabled: false,
        readDescriptorsImpl: opts.descriptors ?? (async () => []),
    });
    return { registry, conn };
}

describe('ProjectRegistry.sweepDescriptors', () => {
    beforeEach(() => {
        UnityConnection.resetInstanceForTesting();
    });

    test('registers an instance from a descriptor', async () => {
        const t = 1_000_000;
        const { registry, conn } = makeRegistry({
            now: () => t,
            descriptors: async () => [makeDescriptor()],
        });

        const result = await registry.sweepDescriptors();

        expect(result).toHaveLength(1);
        expect(result[0].id).toBe('MyGame-27182');
        expect(result[0].endpoint).toBe('http://127.0.0.1:27182');
        expect(result[0].state).toBe('healthy');
        expect(result[0].lastSeen).toBe(t);
        expect(conn.getAllInstances()).toHaveLength(1);
    });

    test('carries the token so requests can authenticate', async () => {
        // Without this the server reaches the Editor and is refused: loopback binding is not
        // access control, so every call must present the descriptor's token.
        const { registry, conn } = makeRegistry({
            now: () => 0,
            descriptors: async () => [makeDescriptor({ token: 'abc123' })],
        });

        await registry.sweepDescriptors();

        expect(conn.getInstanceById('MyGame-27182')!.token).toBe('abc123');
    });

    test('emits instanceDiscovered only the first time', async () => {
        const { registry } = makeRegistry({
            now: () => 0,
            descriptors: async () => [makeDescriptor()],
        });

        const discovered = jest.fn();
        registry.on('instanceDiscovered', discovered);

        await registry.sweepDescriptors();
        await registry.sweepDescriptors();

        expect(discovered).toHaveBeenCalledTimes(1);
    });

    test('a withdrawn descriptor unregisters the instance immediately', async () => {
        // Descriptor removal is a clean shutdown, which is more definite than any health poll
        // result, so the instance should go at once rather than aging out.
        let present = true;
        const { registry, conn } = makeRegistry({
            now: () => 0,
            descriptors: async () => (present ? [makeDescriptor()] : []),
        });

        const removed = jest.fn();
        registry.on('instanceRemoved', removed);

        await registry.sweepDescriptors();
        expect(conn.getAllInstances()).toHaveLength(1);

        present = false;
        await registry.sweepDescriptors();

        expect(conn.getAllInstances()).toHaveLength(0);
        expect(removed).toHaveBeenCalledTimes(1);
    });

    test('a reappearing descriptor resets an unhealthy instance to healthy', async () => {
        let t = 1000;
        const { registry, conn } = makeRegistry({
            now: () => t,
            descriptors: async () => [makeDescriptor()],
        });

        await registry.sweepDescriptors();
        registry.applyPollOutcome('MyGame-27182', false);
        expect(conn.getInstanceById('MyGame-27182')!.state).toBe('reloading');

        t = 2000;
        await registry.sweepDescriptors();

        expect(conn.getInstanceById('MyGame-27182')!.state).toBe('healthy');
        expect(conn.getInstanceById('MyGame-27182')!.consecutiveFailures).toBe(0);
    });

    test('registers several Editors independently', async () => {
        const { registry, conn } = makeRegistry({
            now: () => 0,
            descriptors: async () => [
                makeDescriptor({ projectName: 'A', port: 27182 }),
                makeDescriptor({ projectName: 'B', port: 27185 }),
            ],
        });

        await registry.sweepDescriptors();

        expect(conn.getAllInstances().map(i => i.id).sort()).toEqual(['A-27182', 'B-27185']);
    });

    test('a failing reader does not throw or disturb the registry', async () => {
        const { registry, conn } = makeRegistry({
            now: () => 0,
            descriptors: async () => { throw new Error('permission denied'); },
        });

        await expect(registry.sweepDescriptors()).resolves.toEqual([]);
        expect(conn.getAllInstances()).toHaveLength(0);
    });
});

describe('ProjectRegistry.applyPollOutcome (state machine)', () => {
    beforeEach(() => {
        UnityConnection.resetInstanceForTesting();
    });

    function seed(now: number, conn: UnityConnection, id: string = 'P-1'): void {
        conn.registerInstance({
            id,
            projectName: 'P',
            projectPath: '',
            port: 1,
            unityVersion: '',
            endpoint: 'http://127.0.0.1:1',
            version: '',
            token: 'test-token',
            state: 'healthy',
            lastSeen: now,
            lastContact: now,
            consecutiveFailures: 0,
        });
    }

    test('200 ok keeps healthy, resets consecutiveFailures', () => {
        let t = 1000;
        const { registry, conn } = makeRegistry({ now: () => t });
        seed(t, conn);
        t = 2000;
        registry.applyPollOutcome('P-1', true);
        const inst = conn.getInstanceById('P-1')!;
        expect(inst.state).toBe('healthy');
        expect(inst.lastSeen).toBe(2000);
        expect(inst.lastContact).toBe(2000);
        expect(inst.consecutiveFailures).toBe(0);
    });

    test('failure from healthy → reloading, failures=1', () => {
        let t = 1000;
        const { registry, conn } = makeRegistry({ now: () => t });
        seed(t, conn);
        t = 2000;
        registry.applyPollOutcome('P-1', false);
        const inst = conn.getInstanceById('P-1')!;
        expect(inst.state).toBe('reloading');
        expect(inst.consecutiveFailures).toBe(1);
        // lastContact should NOT have been bumped on failure.
        expect(inst.lastContact).toBe(1000);
    });

    test('failure from reloading within cooldown stays reloading', () => {
        let t = 1000;
        const { registry, conn } = makeRegistry({ now: () => t, cooldownMs: 60_000 });
        seed(t, conn);
        // First failure: healthy → reloading
        t = 2000; registry.applyPollOutcome('P-1', false);
        // Second failure but still within cooldown (60s) — stays reloading
        t = 30_000; registry.applyPollOutcome('P-1', false);
        const inst = conn.getInstanceById('P-1')!;
        expect(inst.state).toBe('reloading');
        expect(inst.consecutiveFailures).toBe(2);
    });

    test('failure from reloading with >=2 failures and past cooldown → unhealthy', () => {
        let t = 1000;
        const { registry, conn } = makeRegistry({ now: () => t, cooldownMs: 60_000 });
        seed(t, conn);
        // 1st failure → reloading
        t = 2000; registry.applyPollOutcome('P-1', false);
        // 2nd failure, now > cooldown from lastContact (1000 + 60000 = 61000)
        t = 70_000; registry.applyPollOutcome('P-1', false);
        const inst = conn.getInstanceById('P-1')!;
        expect(inst.state).toBe('unhealthy');
    });

    test('unhealthy stays unhealthy on failure', () => {
        let t = 1000;
        const { registry, conn } = makeRegistry({ now: () => t, cooldownMs: 100 });
        seed(t, conn);
        t = 2000; registry.applyPollOutcome('P-1', false);  // → reloading
        t = 3000; registry.applyPollOutcome('P-1', false);  // → unhealthy (>=2 fails, >cooldown)
        t = 4000; registry.applyPollOutcome('P-1', false);  // stays unhealthy
        const inst = conn.getInstanceById('P-1')!;
        expect(inst.state).toBe('unhealthy');
    });

    test('200 ok recovers unhealthy → healthy', () => {
        let t = 1000;
        const { registry, conn } = makeRegistry({ now: () => t, cooldownMs: 100 });
        seed(t, conn);
        t = 2000; registry.applyPollOutcome('P-1', false);
        t = 3000; registry.applyPollOutcome('P-1', false);
        expect(conn.getInstanceById('P-1')!.state).toBe('unhealthy');
        t = 4000; registry.applyPollOutcome('P-1', true);
        expect(conn.getInstanceById('P-1')!.state).toBe('healthy');
        expect(conn.getInstanceById('P-1')!.consecutiveFailures).toBe(0);
    });

    // "announce resets state to healthy" is now covered by
    // sweepDescriptors › 'a reappearing descriptor resets an unhealthy instance to healthy'.

    test('sweepStaleUnhealthy removes unhealthy instances past staleThreshold', () => {
        let t = 1000;
        const { registry, conn } = makeRegistry({
            now: () => t, cooldownMs: 100, staleMs: 5_000,
        });
        seed(t, conn);
        t = 2000; registry.applyPollOutcome('P-1', false);
        t = 3000; registry.applyPollOutcome('P-1', false);
        expect(conn.getInstanceById('P-1')!.state).toBe('unhealthy');
        // Still within stale window.
        t = 4000; registry.sweepStaleUnhealthy();
        expect(conn.getInstanceById('P-1')).toBeDefined();

        // lastSeen was 1000. 1000 + 5000 = 6000 threshold.
        t = 10_000; registry.sweepStaleUnhealthy();
        expect(conn.getInstanceById('P-1')).toBeUndefined();
    });
});
