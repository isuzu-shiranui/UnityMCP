import { existsSync } from 'fs';
import { promises as fs } from 'fs';
import * as os from 'os';
import * as path from 'path';
import { fileURLToPath } from 'url';

import { readDescriptors } from './InstanceDescriptors.js';
import {
    catalogCachePath,
    descriptorDirectories,
    legacyPaths,
    primaryStateRoot,
    stateRoots,
} from './StatePaths.js';

/**
 * Setup and teardown for everything this tool puts on the machine.
 *
 * The point is that nothing gets installed without a matching way to remove it, and that a
 * user can ask where the residue is rather than having to know.
 */

export interface RemovalResult {
    path: string;
    removed: boolean;
    reason?: string;
}

/** MCP client config files this tool knows how to edit. */
export interface McpClientTarget {
    name: string;
    configPath: string;
    exists: boolean;
}

export function mcpClientTargets(): McpClientTarget[] {
    const home = os.homedir();
    const targets: { name: string; configPath: string }[] = [];

    if (process.platform === 'win32') {
        if (process.env.APPDATA) {
            targets.push({
                name: 'claude-desktop',
                configPath: path.join(process.env.APPDATA, 'Claude', 'claude_desktop_config.json'),
            });
        }
    } else if (process.platform === 'darwin') {
        targets.push({
            name: 'claude-desktop',
            configPath: path.join(home, 'Library', 'Application Support', 'Claude', 'claude_desktop_config.json'),
        });
    }

    targets.push({ name: 'claude-code', configPath: path.join(home, '.claude.json') });

    return targets.map(t => ({ ...t, exists: existsSync(t.configPath) }));
}

/** Absolute path of the built MCP server, derived from this module's own location. */
export function serverEntryPoint(): string {
    const here = path.dirname(fileURLToPath(import.meta.url));
    return path.resolve(here, '..', 'index.js');
}

/** Where the skill is installed for Claude Code. */
export function skillDirectory(): string {
    return path.join(os.homedir(), '.claude', 'skills', 'unity-mcp');
}

/**
 * Where the shipped skill lives.
 *
 * Found by walking up from this module rather than by a fixed number of `..` segments: the
 * depth differs between running from `build/core/` and from a source tree, and a hard-coded
 * count silently resolves to a directory that does not exist.
 */
export function bundledSkillDirectory(): string | null {
    let directory = path.dirname(fileURLToPath(import.meta.url));

    for (let i = 0; i < 6; i++) {
        const candidate = path.join(directory, 'skills', 'unity-mcp');

        if (existsSync(path.join(candidate, 'SKILL.md'))) {
            return candidate;
        }

        const parent = path.dirname(directory);
        if (parent === directory) {
            break;
        }

        directory = parent;
    }

    return null;
}

/**
 * Adds or updates this server's entry in an MCP client config, preserving everything else in
 * the file. Returns the path written.
 */
export async function writeMcpClientConfig(configPath: string, serverName = 'unity-mcp'): Promise<string> {
    let config: any = {};

    try {
        config = JSON.parse(await fs.readFile(configPath, 'utf8'));
    } catch {
        // A missing or unparseable config starts from an empty object rather than being
        // overwritten blind — an unreadable file is not the same as an absent one, so a parse
        // failure on an existing file is reported by the caller before we get here.
    }

    config.mcpServers ??= {};
    config.mcpServers[serverName] = {
        command: process.execPath,
        args: [serverEntryPoint()],
    };

    await fs.mkdir(path.dirname(configPath), { recursive: true });
    await fs.writeFile(configPath, JSON.stringify(config, null, 2), 'utf8');

    return configPath;
}

/** Removes this server's entry from an MCP client config, leaving the rest untouched. */
export async function removeMcpClientConfig(configPath: string, serverName = 'unity-mcp'): Promise<RemovalResult> {
    let config: any;

    try {
        config = JSON.parse(await fs.readFile(configPath, 'utf8'));
    } catch {
        return { path: configPath, removed: false, reason: 'not present' };
    }

    if (!config?.mcpServers?.[serverName]) {
        return { path: configPath, removed: false, reason: 'no entry' };
    }

    delete config.mcpServers[serverName];
    await fs.writeFile(configPath, JSON.stringify(config, null, 2), 'utf8');

    return { path: configPath, removed: true };
}

/**
 * Copies the bundled skill into the user's Claude Code skills directory.
 *
 * Staged through a sibling directory and swapped in at the end. Removing the destination
 * first and copying into it would mean a failure part-way — a missing source, an unreadable
 * file — leaves the user with no skill at all, having destroyed the working one they had.
 */
export async function installSkill(
    sourceOverride?: string,
    destinationOverride?: string
): Promise<string> {
    const source = sourceOverride ?? bundledSkillDirectory();

    if (source === null) {
        throw new Error(
            'The bundled skill was not found. It ships at skills/unity-mcp in the repository; ' +
            'this looks like a partial checkout or an unexpected install layout.'
        );
    }

    const destination = destinationOverride ?? skillDirectory();
    const staging = `${destination}.incoming`;

    await fs.rm(staging, { recursive: true, force: true });
    await fs.mkdir(staging, { recursive: true });

    try {
        await copyDirectory(source, staging);
    } catch (err) {
        await fs.rm(staging, { recursive: true, force: true });
        throw err;
    }

    await fs.rm(destination, { recursive: true, force: true });
    await fs.mkdir(path.dirname(destination), { recursive: true });
    await fs.rename(staging, destination);

    return destination;
}

async function copyDirectory(source: string, destination: string): Promise<void> {
    for (const entry of await fs.readdir(source, { withFileTypes: true })) {
        const from = path.join(source, entry.name);
        const to = path.join(destination, entry.name);

        if (entry.isDirectory()) {
            await fs.mkdir(to, { recursive: true });
            await copyDirectory(from, to);
        } else {
            await fs.copyFile(from, to);
        }
    }
}

/**
 * Everything on disk that belongs to this tool, whether or not it currently exists.
 * `doctor` prints it and `uninstall` removes it, from the same list.
 */
export async function stateInventory(): Promise<Array<{ path: string; kind: string; exists: boolean; detail?: string }>> {
    const items: Array<{ path: string; kind: string; exists: boolean; detail?: string }> = [];

    for (const root of stateRoots()) {
        items.push({
            path: root,
            kind: root === primaryStateRoot() ? 'state root (written here)' : 'state root (scanned)',
            exists: await pathExists(root),
        });
    }

    for (const directory of descriptorDirectories()) {
        const exists = await pathExists(directory);
        let detail: string | undefined;

        if (exists) {
            try {
                const names = (await fs.readdir(directory)).filter(n => n.endsWith('.json'));
                detail = `${names.length} descriptor(s)`;
            } catch {
                detail = 'unreadable';
            }
        }

        items.push({ path: directory, kind: 'Editor descriptors', exists, detail });
    }

    const cache = catalogCachePath();
    items.push({ path: cache, kind: 'tool catalog cache', exists: await pathExists(cache) });

    const skill = skillDirectory();
    items.push({ path: skill, kind: 'Claude Code skill', exists: await pathExists(skill) });

    for (const legacy of legacyPaths()) {
        items.push({
            path: legacy,
            kind: 'legacy (no longer written)',
            exists: await pathExists(legacy),
        });
    }

    return items;
}

export async function pathExists(target: string): Promise<boolean> {
    try {
        await fs.access(target);
        return true;
    } catch {
        return false;
    }
}

/**
 * Removes the tool's state.
 *
 * Refuses while an Editor is still running: its descriptor would be recreated a moment later,
 * and reporting a clean uninstall that immediately un-cleans itself would be a lie.
 */
export async function removeState(options: { includeSkill: boolean }): Promise<RemovalResult[]> {
    const running = await readDescriptors();

    if (running.length > 0) {
        throw new Error(
            'These Editors are still running and would republish their descriptors: ' +
            running.map(d => d.projectName).join(', ') +
            '. Close them first.'
        );
    }

    const results: RemovalResult[] = [];
    const targets = [...stateRoots(), ...legacyPaths()];

    if (options.includeSkill) {
        targets.push(skillDirectory());
    }

    for (const target of targets) {
        if (!(await pathExists(target))) {
            results.push({ path: target, removed: false, reason: 'not present' });
            continue;
        }

        try {
            await fs.rm(target, { recursive: true, force: true });
            results.push({ path: target, removed: true });
        } catch (err) {
            results.push({
                path: target,
                removed: false,
                reason: err instanceof Error ? err.message : String(err),
            });
        }
    }

    return results;
}
