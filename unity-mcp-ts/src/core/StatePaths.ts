import * as os from 'os';
import * as path from 'path';

/**
 * Every path this tool writes to, in one place.
 *
 * State used to be split between the descriptor directory the Editor writes and a separate
 * dot-directory for the catalog cache, which meant there was no single thing to delete when
 * someone wanted the tool gone. Everything now lives under one root, so `unity-mcp uninstall`
 * can remove it wholesale and say exactly what it removed.
 */

/**
 * Candidate roots, in preference order.
 *
 * The Editor writes to .NET's LocalApplicationData, which resolves differently per platform
 * and per runtime — Mono and .NET have not always agreed about macOS. Rather than reimplement
 * that mapping and risk disagreeing with the writer, reads scan every plausible root; a
 * directory that does not exist costs one failed readdir.
 */
export function stateRoots(): string[] {
    const roots: string[] = [];
    const home = os.homedir();

    if (process.env.LOCALAPPDATA) {
        roots.push(path.join(process.env.LOCALAPPDATA, 'UnityMCP'));
    }

    if (process.env.XDG_DATA_HOME) {
        roots.push(path.join(process.env.XDG_DATA_HOME, 'UnityMCP'));
    }

    roots.push(path.join(home, '.local', 'share', 'UnityMCP'));
    roots.push(path.join(home, 'Library', 'Application Support', 'UnityMCP'));

    return Array.from(new Set(roots));
}

/** The root this process writes to. Reads still scan all of {@link stateRoots}. */
export function primaryStateRoot(): string {
    return stateRoots()[0];
}

/** Directories that may hold Editor descriptors. */
export function descriptorDirectories(): string[] {
    return stateRoots().map(root => path.join(root, 'instances'));
}

/**
 * Where the tool catalog is mirrored.
 *
 * Cached because MCP clients ask for tools/list as soon as they start, which is routinely
 * before any Editor is running; answering from the last known catalog beats answering
 * "no tools".
 */
export function catalogCachePath(): string {
    return path.join(primaryStateRoot(), 'cache', 'tool-catalog.json');
}

/**
 * Paths from earlier versions that are no longer written but may still be lying around.
 * Reported by `doctor` and removed by `uninstall` so upgrading does not leave residue.
 */
export function legacyPaths(): string[] {
    return [path.join(os.homedir(), '.unity-mcp')];
}
