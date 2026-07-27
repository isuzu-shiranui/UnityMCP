import { existsSync } from 'fs';
import { promises as fs } from 'fs';
import * as path from 'path';
import { fileURLToPath } from 'url';

import { AgentTarget, agentTargets } from './AgentTargets.js';
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
 * The rule is that nothing gets installed without a matching way to remove it, and that a
 * user can ask where the residue is rather than having to know.
 */

export interface RemovalResult {
    path: string;
    removed: boolean;
    reason?: string;
}

/** Absolute path of the built MCP server, derived from this module's own location. */
export function serverEntryPoint(): string {
    const here = path.dirname(fileURLToPath(import.meta.url));
    return path.resolve(here, '..', 'index.js');
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

/** Where this skill is installed for a given agent. */
export function skillDirectoryFor(target: AgentTarget): string | null {
    return target.skillsDirectory === null
        ? null
        : path.join(target.skillsDirectory, 'unity-mcp');
}

/**
 * Copies the bundled skill into an agent's skills directory.
 *
 * Staged through a sibling directory and swapped in at the end. Removing the destination
 * first and copying into it means a failure part-way — a missing source, an unreadable file —
 * leaves the user with no skill at all, having destroyed the working one they had.
 */
export async function installSkill(
    sourceOverride?: string,
    destinationOverride?: string
): Promise<string> {
    const source = sourceOverride ?? bundledSkillDirectory();

    if (source === null) {
        throw new Error(
            'The bundled skill was not found. It ships at skills/unity-mcp inside this package; ' +
            'this looks like a partial checkout or an unexpected install layout.'
        );
    }

    if (destinationOverride === undefined) {
        throw new Error('installSkill needs a destination; pass one from skillDirectoryFor().');
    }

    const destination = destinationOverride;
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

export interface InventoryItem {
    path: string;
    kind: string;
    exists: boolean;
    detail?: string;
    /** True when `uninstall` removes this path outright. */
    removable: boolean;
}

/**
 * Everything on disk that belongs to this tool, whether or not it currently exists.
 * `doctor` prints it and `uninstall` removes it, from the same list.
 */
export async function stateInventory(): Promise<InventoryItem[]> {
    const items: InventoryItem[] = [];

    for (const root of stateRoots()) {
        items.push({
            path: root,
            kind: root === primaryStateRoot() ? 'state root (written here)' : 'state root (scanned)',
            exists: await pathExists(root),
            removable: true,
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

        items.push({ path: directory, kind: 'Editor descriptors', exists, detail, removable: false });
    }

    const cache = catalogCachePath();
    items.push({
        path: cache,
        kind: 'tool catalog cache',
        exists: await pathExists(cache),
        removable: false,
    });

    for (const target of agentTargets()) {
        const skill = skillDirectoryFor(target);

        if (skill !== null) {
            items.push({
                path: skill,
                kind: `${target.label} skill`,
                exists: await pathExists(skill),
                removable: true,
            });
        }
    }

    for (const legacy of legacyPaths()) {
        items.push({
            path: legacy,
            kind: 'legacy (no longer written)',
            exists: await pathExists(legacy),
            removable: true,
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
 * Removes the tool's state and installed skills.
 *
 * Refuses while an Editor is running: its descriptor would be recreated a moment later, and
 * reporting a clean uninstall that immediately un-cleans itself would be a lie.
 */
export async function removeState(options: { includeSkills: boolean }): Promise<RemovalResult[]> {
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

    if (options.includeSkills) {
        for (const agent of agentTargets()) {
            const skill = skillDirectoryFor(agent);
            if (skill !== null) {
                targets.push(skill);
            }
        }
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
