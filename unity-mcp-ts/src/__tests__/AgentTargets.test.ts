/**
 * Tests for editing agent config files.
 *
 * These files hold every MCP server the user has registered, plus — in Codex's case — project
 * trust settings, plugin state and machine-generated paths. Adding our entry must leave all of
 * that exactly as it was, and removing ours must not take anything else with it. A bug here
 * costs the user configuration they did not back up.
 */
import { describe, test, expect } from '@jest/globals';

import {
    agentTargets,
    findAgent,
    removeJsonMcpServer,
    removeTomlMcpServer,
    upsertJsonMcpServer,
    upsertTomlMcpServer,
} from '../core/AgentTargets.js';

const CODEX_CONFIG = `model = "gpt-5.6-sol"
notify = [ "something" ]

[projects.'C:\\Users\\x']
trust_level = "trusted"

[mcp_servers.maya]
command = "uv"
args = ["--directory", "H:\\\\MayaMCP", "run"]

[mcp_servers.node_repl]
args = []
command = "node_repl.exe"

[mcp_servers.node_repl.env]
CODEX_HOME = "C:\\\\Users\\\\x\\\\.codex"

[shell_environment_policy]
inherit = "core"
`;

describe('agent registry', () => {
    test('covers the agents this package claims to support', () => {
        const names = agentTargets().map(a => a.name);

        expect(names).toContain('claude-code');
        expect(names).toContain('codex');
        expect(names).toContain('cursor');
        expect(names).toContain('gemini');
    });

    test('records the config format each agent uses', () => {
        expect(findAgent('codex')?.configFormat).toBe('toml');
        expect(findAgent('claude-code')?.configFormat).toBe('json');
    });

    test('only agents with a skill mechanism advertise a skills directory', () => {
        expect(findAgent('claude-code')?.skillsDirectory).not.toBeNull();
        expect(findAgent('codex')?.skillsDirectory).not.toBeNull();
        expect(findAgent('cursor')?.skillsDirectory).toBeNull();
    });
});

describe('JSON configs', () => {
    test('adds an entry to an empty file', () => {
        const result = JSON.parse(upsertJsonMcpServer('', 'isuzu-unity-mcp', 'node', ['x.js']));

        expect(result.mcpServers['isuzu-unity-mcp']).toEqual({ command: 'node', args: ['x.js'] });
    });

    test('preserves other servers and unrelated keys', () => {
        const before = JSON.stringify({
            theme: 'dark',
            mcpServers: { other: { command: 'other' } },
        });

        const after = JSON.parse(upsertJsonMcpServer(before, 'isuzu-unity-mcp', 'node', ['x.js']));

        expect(after.theme).toBe('dark');
        expect(after.mcpServers.other).toEqual({ command: 'other' });
    });

    test('removes only our entry', () => {
        const before = JSON.stringify({
            mcpServers: { other: { command: 'other' }, 'isuzu-unity-mcp': { command: 'node' } },
        });

        const after = JSON.parse(removeJsonMcpServer(before, 'isuzu-unity-mcp') as string);

        expect(after.mcpServers.other).toBeDefined();
        expect(after.mcpServers['isuzu-unity-mcp']).toBeUndefined();
    });

    test('reports nothing to remove', () => {
        expect(removeJsonMcpServer(JSON.stringify({ mcpServers: {} }), 'isuzu-unity-mcp')).toBeNull();
    });
});

describe('TOML configs', () => {
    test('appends a new block without disturbing the rest', () => {
        const after = upsertTomlMcpServer(CODEX_CONFIG, 'isuzu-unity-mcp', 'node', ['H:/build/index.js']);

        // Everything that was there before is still there, byte for byte.
        expect(after.startsWith(CODEX_CONFIG.trimEnd())).toBe(true);
        expect(after).toContain('[mcp_servers.isuzu-unity-mcp]');
        expect(after).toContain('args = ["H:/build/index.js"]');
        expect(after).toContain('[mcp_servers.maya]');
        expect(after).toContain('[shell_environment_policy]');
    });

    test('replaces an existing block in place', () => {
        const once = upsertTomlMcpServer(CODEX_CONFIG, 'isuzu-unity-mcp', 'node', ['old.js']);
        const twice = upsertTomlMcpServer(once, 'isuzu-unity-mcp', 'node', ['new.js']);

        expect(twice.match(/\[mcp_servers.isuzu-unity-mcp]/g)).toHaveLength(1);
        expect(twice).toContain('new.js');
        expect(twice).not.toContain('old.js');
    });

    test('replacing a middle block leaves the following one intact', () => {
        // The block runs to the next top-level table; getting that boundary wrong would eat
        // whatever came after it.
        const withOurs = upsertTomlMcpServer(CODEX_CONFIG, 'isuzu-unity-mcp', 'node', ['a.js']);
        const reordered = withOurs.replace(
            '[shell_environment_policy]',
            '[mcp_servers.isuzu-unity-mcp]\ncommand = "stale"\nargs = []\n\n[shell_environment_policy]'
        );

        const after = upsertTomlMcpServer(reordered, 'isuzu-unity-mcp', 'node', ['b.js']);

        expect(after).toContain('[shell_environment_policy]');
        expect(after).toContain('inherit = "core"');
    });

    test('removes only our block', () => {
        const withOurs = upsertTomlMcpServer(CODEX_CONFIG, 'isuzu-unity-mcp', 'node', ['x.js']);
        const after = removeTomlMcpServer(withOurs, 'isuzu-unity-mcp') as string;

        expect(after).not.toContain('[mcp_servers.isuzu-unity-mcp]');
        expect(after).toContain('[mcp_servers.maya]');
        expect(after).toContain('[mcp_servers.node_repl]');
        expect(after).toContain('[mcp_servers.node_repl.env]');
        expect(after).toContain('[shell_environment_policy]');
        expect(after).toContain('model = "gpt-5.6-sol"');
    });

    test('a sub-table belongs to its parent block', () => {
        // [mcp_servers.x.env] is part of x, so removing x must take it along and stop there.
        const config = `[mcp_servers.isuzu-unity-mcp]
command = "node"
args = []

[mcp_servers.isuzu-unity-mcp.env]
FOO = "bar"

[other]
keep = true
`;

        const after = removeTomlMcpServer(config, 'isuzu-unity-mcp') as string;

        expect(after).not.toContain('FOO');
        expect(after).toContain('[other]');
        expect(after).toContain('keep = true');
    });

    test('reports nothing to remove', () => {
        expect(removeTomlMcpServer(CODEX_CONFIG, 'isuzu-unity-mcp')).toBeNull();
    });

    test('handles a block at the end of the file', () => {
        const config = '[a]\nx = 1\n\n[mcp_servers.isuzu-unity-mcp]\ncommand = "node"\nargs = []\n';
        const after = removeTomlMcpServer(config, 'isuzu-unity-mcp') as string;

        expect(after).toContain('[a]');
        expect(after).not.toContain('isuzu-unity-mcp');
    });

    test('quotes arguments so Windows paths survive', () => {
        const after = upsertTomlMcpServer('', 'isuzu-unity-mcp', 'node', ['C:\\Users\\x\\build\\index.js']);

        // A raw backslash in a TOML basic string is an escape; it has to be doubled.
        expect(after).toContain('"C:\\\\Users\\\\x\\\\build\\\\index.js"');
    });
});
