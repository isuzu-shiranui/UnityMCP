/**
 * Tests for descriptor discovery helpers.
 */
import { describe, test, expect } from '@jest/globals';
import * as path from 'path';

import { descriptorDirectories, isProcessAlive } from '../core/InstanceDescriptors.js';

describe('descriptorDirectories', () => {
    test('always ends each candidate with UnityMCP/instances', () => {
        for (const directory of descriptorDirectories()) {
            expect(directory.endsWith(path.join('UnityMCP', 'instances'))).toBe(true);
        }
    });

    test('returns no duplicates', () => {
        const directories = descriptorDirectories();
        expect(new Set(directories).size).toBe(directories.length);
    });

    test('covers more than one candidate', () => {
        // The Editor writes to .NET's LocalApplicationData, which resolves differently per
        // platform and runtime; scanning several locations avoids having to reimplement that
        // mapping and risk disagreeing with the writer.
        expect(descriptorDirectories().length).toBeGreaterThan(1);
    });
});

describe('isProcessAlive', () => {
    test('recognises this process', () => {
        expect(isProcessAlive(process.pid)).toBe(true);
    });

    test('reports a pid that cannot exist as dead', () => {
        // Above every platform's pid_max, so it can never be assigned.
        expect(isProcessAlive(0x7ffffffe)).toBe(false);
    });

    test('treats a missing or nonsensical pid as alive', () => {
        // Discarding a descriptor over a malformed pid would drop a working Editor.
        expect(isProcessAlive(0)).toBe(true);
        expect(isProcessAlive(-1)).toBe(true);
        expect(isProcessAlive(NaN)).toBe(true);
    });
});
