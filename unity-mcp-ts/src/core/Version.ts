import { readFileSync } from 'fs';
import * as path from 'path';
import { fileURLToPath } from 'url';

/**
 * The version this server reports in the MCP `initialize` handshake.
 *
 * Read from package.json rather than written in the source. A literal was there through
 * 3.0.0 and went stale on the first release after it, telling clients 3.0.0 while the
 * package said 3.1.0 — the same defect the Editor side had, for the same reason.
 */
let cached: string | null = null;

export function serverVersion(): string {
    if (cached !== null) {
        return cached;
    }

    // build/core/Version.js and src/core/Version.ts are both two levels below the package root.
    const here = path.dirname(fileURLToPath(import.meta.url));
    let version = 'unknown';

    try {
        const manifest = JSON.parse(readFileSync(path.join(here, '..', '..', 'package.json'), 'utf8'));
        if (typeof manifest.version === 'string') {
            version = manifest.version;
        }
    } catch {
        // A missing manifest should not stop the server from starting; the handshake just
        // carries less information.
    }

    cached = version;

    return version;
}
