/**
 * Tests for the parts of setup/uninstall that touch a file someone else owns.
 *
 * An MCP client config holds every server the user has registered. Writing ours in must leave
 * the rest exactly as it was, and removing ours must not take anything else with it — a bug
 * here costs the user configuration they did not back up.
 */
import { describe, test, expect, beforeEach, afterEach } from '@jest/globals';
import { promises as fs } from 'fs';
import * as os from 'os';
import * as path from 'path';

import {
    bundledSkillDirectory,
    installSkill,
    removeMcpClientConfig,
    serverEntryPoint,
    skillDirectory,
    writeMcpClientConfig,
} from '../core/Housekeeping.js';
import { catalogCachePath, descriptorDirectories, stateRoots } from '../core/StatePaths.js';

let workDir: string;
let configPath: string;

beforeEach(async () => {
    workDir = await fs.mkdtemp(path.join(os.tmpdir(), 'unity-mcp-test-'));
    configPath = path.join(workDir, 'config.json');
});

afterEach(async () => {
    await fs.rm(workDir, { recursive: true, force: true });
});

async function readConfig(): Promise<any> {
    return JSON.parse(await fs.readFile(configPath, 'utf8'));
}

describe('writeMcpClientConfig', () => {
    test('creates a config that did not exist', async () => {
        await writeMcpClientConfig(configPath);

        const config = await readConfig();
        expect(config.mcpServers['unity-mcp'].args[0]).toBe(serverEntryPoint());
        expect(config.mcpServers['unity-mcp'].command).toBe(process.execPath);
    });

    test('creates missing parent directories', async () => {
        const nested = path.join(workDir, 'a', 'b', 'config.json');
        await writeMcpClientConfig(nested);

        expect(JSON.parse(await fs.readFile(nested, 'utf8')).mcpServers['unity-mcp']).toBeDefined();
    });

    test('leaves other servers and unrelated keys untouched', async () => {
        await fs.writeFile(configPath, JSON.stringify({
            theme: 'dark',
            mcpServers: {
                other: { command: 'node', args: ['other.js'] },
            },
        }), 'utf8');

        await writeMcpClientConfig(configPath);

        const config = await readConfig();
        expect(config.theme).toBe('dark');
        expect(config.mcpServers.other).toEqual({ command: 'node', args: ['other.js'] });
        expect(config.mcpServers['unity-mcp']).toBeDefined();
    });

    test('replaces a previous registration rather than duplicating it', async () => {
        await fs.writeFile(configPath, JSON.stringify({
            mcpServers: { 'unity-mcp': { command: 'stale', args: ['old.js'] } },
        }), 'utf8');

        await writeMcpClientConfig(configPath);

        const config = await readConfig();
        expect(Object.keys(config.mcpServers)).toEqual(['unity-mcp']);
        expect(config.mcpServers['unity-mcp'].command).toBe(process.execPath);
    });
});

describe('removeMcpClientConfig', () => {
    test('removes only our entry', async () => {
        await fs.writeFile(configPath, JSON.stringify({
            theme: 'dark',
            mcpServers: {
                other: { command: 'node', args: ['other.js'] },
                'unity-mcp': { command: 'node', args: ['ours.js'] },
            },
        }), 'utf8');

        const result = await removeMcpClientConfig(configPath);

        expect(result.removed).toBe(true);

        const config = await readConfig();
        expect(config.theme).toBe('dark');
        expect(config.mcpServers.other).toBeDefined();
        expect(config.mcpServers['unity-mcp']).toBeUndefined();
    });

    test('reports a missing file without creating one', async () => {
        const result = await removeMcpClientConfig(path.join(workDir, 'absent.json'));

        expect(result.removed).toBe(false);
        expect(result.reason).toBe('not present');
        await expect(fs.access(path.join(workDir, 'absent.json'))).rejects.toThrow();
    });

    test('reports a config with no entry of ours', async () => {
        await fs.writeFile(configPath, JSON.stringify({ mcpServers: { other: {} } }), 'utf8');

        const result = await removeMcpClientConfig(configPath);

        expect(result.removed).toBe(false);
        expect(result.reason).toBe('no entry');
        expect((await readConfig()).mcpServers.other).toBeDefined();
    });

    test('leaves an unreadable file alone', async () => {
        await fs.writeFile(configPath, 'not json at all', 'utf8');

        const result = await removeMcpClientConfig(configPath);

        expect(result.removed).toBe(false);
        expect(await fs.readFile(configPath, 'utf8')).toBe('not json at all');
    });
});

describe('bundled skill', () => {
    test('is found by walking up from the module', async () => {
        // Resolved by search rather than a fixed number of `..` segments: the depth differs
        // between build/core and a source tree, and a hard-coded count resolves silently to a
        // directory that does not exist.
        const source = bundledSkillDirectory();

        expect(source).not.toBeNull();
        await expect(fs.access(path.join(source as string, 'SKILL.md'))).resolves.toBeUndefined();
    });

    test('installs into the Claude Code skills directory', () => {
        expect(skillDirectory().endsWith(path.join('.claude', 'skills', 'unity-mcp'))).toBe(true);
    });

    test('a failed install leaves the existing skill intact', async () => {
        // The first version removed the destination and then copied into it, so a failure
        // part-way destroyed a working installation — observed for real, with the user's
        // installed skill left as an empty directory. Staging first is the whole point.
        const destination = path.join(workDir, 'installed');
        await fs.mkdir(destination, { recursive: true });
        await fs.writeFile(path.join(destination, 'SKILL.md'), 'the working one', 'utf8');

        await expect(
            installSkill(path.join(workDir, 'does-not-exist'), destination)
        ).rejects.toThrow();

        expect(await fs.readFile(path.join(destination, 'SKILL.md'), 'utf8')).toBe('the working one');
    });

    test('a failed install leaves no staging directory behind', async () => {
        const destination = path.join(workDir, 'installed');

        await expect(
            installSkill(path.join(workDir, 'does-not-exist'), destination)
        ).rejects.toThrow();

        await expect(fs.access(`${destination}.incoming`)).rejects.toThrow();
    });

    test('installing twice is idempotent', async () => {
        const source = path.join(workDir, 'source');
        await fs.mkdir(path.join(source, 'references'), { recursive: true });
        await fs.writeFile(path.join(source, 'SKILL.md'), 'skill body', 'utf8');
        await fs.writeFile(path.join(source, 'references', 'extra.md'), 'extra', 'utf8');

        const destination = path.join(workDir, 'installed');

        expect(await installSkill(source, destination)).toBe(destination);
        expect(await installSkill(source, destination)).toBe(destination);

        expect(await fs.readFile(path.join(destination, 'SKILL.md'), 'utf8')).toBe('skill body');
        expect(await fs.readFile(path.join(destination, 'references', 'extra.md'), 'utf8')).toBe('extra');
        await expect(fs.access(`${destination}.incoming`)).rejects.toThrow();
    });

    test('reinstalling drops files the new version no longer ships', async () => {
        const source = path.join(workDir, 'source');
        await fs.mkdir(source, { recursive: true });
        await fs.writeFile(path.join(source, 'SKILL.md'), 'new', 'utf8');

        const destination = path.join(workDir, 'installed');
        await fs.mkdir(destination, { recursive: true });
        await fs.writeFile(path.join(destination, 'stale-reference.md'), 'from an old version', 'utf8');

        await installSkill(source, destination);

        await expect(fs.access(path.join(destination, 'stale-reference.md'))).rejects.toThrow();
    });
});

describe('state paths', () => {
    test('descriptors and the catalog cache share one removable root', async () => {
        // The whole point of the layout: uninstall removes the roots, so anything written
        // outside them would survive and be residue nobody knows to delete.
        const roots = stateRoots();

        for (const directory of descriptorDirectories()) {
            expect(roots.some(root => directory.startsWith(root))).toBe(true);
        }

        expect(roots.some(root => catalogCachePath().startsWith(root))).toBe(true);
    });

    test('the first root is the one written to', () => {
        expect(catalogCachePath().startsWith(stateRoots()[0])).toBe(true);
    });
});
