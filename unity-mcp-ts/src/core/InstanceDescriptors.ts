import { promises as fs } from 'fs';
import * as os from 'os';
import * as path from 'path';

/**
 * A running Editor as published by `McpInstanceDescriptor` on the C# side.
 */
export interface InstanceDescriptor {
    projectPath: string;
    projectName: string;
    unityVersion: string;
    port: number;
    token: string;
    pid: number;
    protocolVersion: string;
    endpoint: string;
}

/**
 * Directories that may hold descriptors.
 *
 * The Editor writes to .NET's LocalApplicationData, which resolves differently per platform
 * and per runtime — and Mono has not always agreed with .NET about macOS. Rather than
 * reimplement that mapping and risk disagreeing with the writer, every plausible location is
 * scanned; a directory that does not exist costs one failed readdir.
 */
export function descriptorDirectories(): string[] {
    const candidates: string[] = [];
    const home = os.homedir();

    if (process.env.LOCALAPPDATA) {
        candidates.push(path.join(process.env.LOCALAPPDATA, 'UnityMCP', 'instances'));
    }

    if (process.env.XDG_DATA_HOME) {
        candidates.push(path.join(process.env.XDG_DATA_HOME, 'UnityMCP', 'instances'));
    }

    candidates.push(path.join(home, '.local', 'share', 'UnityMCP', 'instances'));
    candidates.push(path.join(home, 'Library', 'Application Support', 'UnityMCP', 'instances'));

    return Array.from(new Set(candidates));
}

/**
 * Reads every descriptor currently published.
 *
 * Unreadable or half-written files are skipped rather than reported: the Editor rewrites its
 * descriptor on every start, so catching one mid-write is expected and self-correcting.
 */
export async function readDescriptors(): Promise<InstanceDescriptor[]> {
    const found: InstanceDescriptor[] = [];

    for (const directory of descriptorDirectories()) {
        let names: string[];

        try {
            names = await fs.readdir(directory);
        } catch {
            continue;
        }

        for (const name of names) {
            if (!name.endsWith('.json')) {
                continue;
            }

            try {
                const raw = await fs.readFile(path.join(directory, name), 'utf8');
                const parsed = JSON.parse(raw);

                // An Editor that crashed rather than quit leaves its descriptor behind. The
                // Editor sweeps those on next start, but if it never starts again the file
                // would sit there forever and register a phantom instance — the same failure
                // the UDP scheme had, where one dead entry made every call ambiguous.
                if (isDescriptor(parsed) && isProcessAlive(parsed.pid)) {
                    found.push(parsed);
                }
            } catch {
                // Skip; the next sweep will pick it up once it is complete.
            }
        }
    }

    return found;
}

/**
 * True when a process with this id exists.
 *
 * `kill(pid, 0)` sends no signal; it only performs the existence and permission check.
 * EPERM means the process is there but owned by someone else, which still counts as alive —
 * treating it as dead would discard a working Editor.
 */
export function isProcessAlive(pid: number): boolean {
    if (!Number.isInteger(pid) || pid <= 0) {
        // No pid recorded: assume alive rather than discard a possibly valid descriptor.
        return true;
    }

    try {
        process.kill(pid, 0);
        return true;
    } catch (err) {
        return (err as NodeJS.ErrnoException)?.code === 'EPERM';
    }
}

function isDescriptor(value: unknown): value is InstanceDescriptor {
    if (!value || typeof value !== 'object') {
        return false;
    }

    const candidate = value as Record<string, unknown>;

    return typeof candidate.port === 'number'
        && candidate.port > 0
        && typeof candidate.token === 'string'
        && candidate.token !== ''
        && typeof candidate.projectName === 'string';
}
