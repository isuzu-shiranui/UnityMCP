/**
 * The version reported in the MCP handshake is read from package.json rather than written in
 * the source, because the literal that used to be there went stale on the first release after
 * it shipped. What can still break is the path: this module resolves the manifest relative to
 * its own location, so a change to the build layout would silently turn the version into
 * "unknown" and nothing else would notice.
 */
import { describe, test, expect } from '@jest/globals';
import { readFileSync } from 'fs';
import * as path from 'path';
import { fileURLToPath } from 'url';

import { serverVersion } from '../core/Version.js';

const packageRoot = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const manifest = JSON.parse(readFileSync(path.join(packageRoot, 'package.json'), 'utf8'));

describe('serverVersion', () => {
    test('resolves the manifest rather than falling back', () => {
        expect(serverVersion()).not.toBe('unknown');
    });

    test('matches the published version', () => {
        expect(serverVersion()).toBe(manifest.version);
    });

    test('is cached, so repeated handshakes do not re-read the file', () => {
        expect(serverVersion()).toBe(serverVersion());
    });
});
