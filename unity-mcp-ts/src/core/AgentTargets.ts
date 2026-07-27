import { existsSync } from 'fs';
import { promises as fs } from 'fs';
import * as os from 'os';
import * as path from 'path';

/**
 * The agent tools this package knows how to register itself with.
 *
 * Each entry says where the MCP server list lives, what format it is in, and where skills go
 * for that agent — the three things setup needs and the three things uninstall has to undo.
 */
export interface AgentTarget {
    /** Identifier used with `--agent`. */
    name: string;
    label: string;
    /** MCP server registry for this agent, or null if it has none. */
    configPath: string | null;
    configFormat: 'json' | 'toml';
    /** Directory holding installed skills, or null if the agent has no skill mechanism. */
    skillsDirectory: string | null;
}

export interface AgentPresence extends AgentTarget {
    /** True when this agent appears to be installed on the machine. */
    detected: boolean;
}

export function agentTargets(): AgentTarget[] {
    const home = os.homedir();
    const targets: AgentTarget[] = [];

    targets.push({
        name: 'claude-code',
        label: 'Claude Code',
        configPath: path.join(home, '.claude.json'),
        configFormat: 'json',
        skillsDirectory: path.join(home, '.claude', 'skills'),
    });

    if (process.platform === 'win32' && process.env.APPDATA) {
        targets.push({
            name: 'claude-desktop',
            label: 'Claude Desktop',
            configPath: path.join(process.env.APPDATA, 'Claude', 'claude_desktop_config.json'),
            configFormat: 'json',
            skillsDirectory: null,
        });
    } else if (process.platform === 'darwin') {
        targets.push({
            name: 'claude-desktop',
            label: 'Claude Desktop',
            configPath: path.join(home, 'Library', 'Application Support', 'Claude', 'claude_desktop_config.json'),
            configFormat: 'json',
            skillsDirectory: null,
        });
    }

    targets.push({
        name: 'codex',
        label: 'Codex',
        configPath: path.join(home, '.codex', 'config.toml'),
        configFormat: 'toml',
        skillsDirectory: path.join(home, '.codex', 'skills'),
    });

    targets.push({
        name: 'cursor',
        label: 'Cursor',
        configPath: path.join(home, '.cursor', 'mcp.json'),
        configFormat: 'json',
        skillsDirectory: null,
    });

    targets.push({
        name: 'gemini',
        label: 'Gemini CLI',
        configPath: path.join(home, '.gemini', 'settings.json'),
        configFormat: 'json',
        skillsDirectory: null,
    });

    return targets;
}

/**
 * Agents present on this machine.
 *
 * Detection is by the agent's own directory rather than by its config file: a fresh install
 * has the directory but may not have written a config yet, and refusing to set up in that
 * case would be wrong.
 */
export function detectAgents(): AgentPresence[] {
    return agentTargets().map(target => {
        const markers = [target.skillsDirectory, target.configPath]
            .filter((p): p is string => p !== null)
            .map(p => path.dirname(p));

        return {
            ...target,
            detected: markers.some(marker => existsSync(marker)) ||
                (target.configPath !== null && existsSync(target.configPath)),
        };
    });
}

export function findAgent(name: string): AgentTarget | undefined {
    return agentTargets().find(t => t.name === name);
}

// ──────────────────────────────────────────────
//  JSON configs
// ──────────────────────────────────────────────

export function upsertJsonMcpServer(
    content: string,
    serverName: string,
    command: string,
    args: string[]
): string {
    let config: any = {};

    if (content.trim() !== '') {
        config = JSON.parse(content);
    }

    config.mcpServers ??= {};
    config.mcpServers[serverName] = { command, args };

    return JSON.stringify(config, null, 2);
}

export function removeJsonMcpServer(content: string, serverName: string): string | null {
    const config = JSON.parse(content);

    if (!config?.mcpServers?.[serverName]) {
        return null;
    }

    delete config.mcpServers[serverName];
    return JSON.stringify(config, null, 2);
}

// ──────────────────────────────────────────────
//  TOML configs
// ──────────────────────────────────────────────

/**
 * Rewrites just the `[mcp_servers.<name>]` block of a TOML file.
 *
 * Done by locating that block textually rather than parsing and re-emitting the whole
 * document. Codex's config holds project trust settings, plugin state and machine-generated
 * paths; a parse-and-rewrite would reformat all of it and drop comments, which is a lot of
 * collateral damage for adding four lines. Everything outside the block is preserved byte for
 * byte.
 */
export function upsertTomlMcpServer(
    content: string,
    serverName: string,
    command: string,
    args: string[]
): string {
    const block = renderTomlBlock(serverName, command, args);
    const bounds = findTomlBlock(content, serverName);

    if (bounds === null) {
        const separator = content.length === 0 || content.endsWith('\n\n') ? '' : content.endsWith('\n') ? '\n' : '\n\n';
        return `${content}${separator}${block}`;
    }

    return content.slice(0, bounds.start) + block + content.slice(bounds.end);
}

export function removeTomlMcpServer(content: string, serverName: string): string | null {
    const bounds = findTomlBlock(content, serverName);

    if (bounds === null) {
        return null;
    }

    return content.slice(0, bounds.start) + content.slice(bounds.end);
}

function renderTomlBlock(serverName: string, command: string, args: string[]): string {
    const renderedArgs = args.map(a => JSON.stringify(a)).join(', ');

    return `[mcp_servers.${serverName}]\n` +
        `command = ${JSON.stringify(command)}\n` +
        `args = [${renderedArgs}]\n`;
}

/**
 * Locates a table and everything belonging to it: the header line, its key/value pairs, and
 * any sub-tables such as `[mcp_servers.<name>.env]`.
 */
function findTomlBlock(content: string, serverName: string): { start: number; end: number } | null {
    const lines = content.split('\n');
    const header = `[mcp_servers.${serverName}]`;
    const subHeaderPrefix = `[mcp_servers.${serverName}.`;

    let start = -1;
    let offset = 0;
    let startOffset = 0;

    for (let i = 0; i < lines.length; i++) {
        const trimmed = lines[i].trim();
        const lineLength = lines[i].length + 1;

        if (start === -1) {
            if (trimmed === header) {
                start = i;
                startOffset = offset;
            }
        } else if (trimmed.startsWith('[') && !trimmed.startsWith(subHeaderPrefix)) {
            // A new top-level table ends the block.
            return { start: startOffset, end: offset };
        }

        offset += lineLength;
    }

    return start === -1 ? null : { start: startOffset, end: content.length };
}

// ──────────────────────────────────────────────
//  Applying to disk
// ──────────────────────────────────────────────

export interface ConfigChange {
    agent: string;
    configPath: string;
    changed: boolean;
    reason?: string;
}

export async function registerWithAgent(
    target: AgentTarget,
    command: string,
    args: string[],
    serverName = 'isuzu-unity-mcp'
): Promise<ConfigChange> {
    if (target.configPath === null) {
        return { agent: target.name, configPath: '', changed: false, reason: 'no MCP config' };
    }

    let existing = '';
    try {
        existing = await fs.readFile(target.configPath, 'utf8');
    } catch {
        // Absent is fine; a new config is created below.
    }

    let updated: string;
    try {
        updated = target.configFormat === 'toml'
            ? upsertTomlMcpServer(existing, serverName, command, args)
            : upsertJsonMcpServer(existing, serverName, command, args);
    } catch (err) {
        // An unreadable config is left alone rather than replaced: it is far more likely to
        // hold settings worth keeping than to be disposable.
        return {
            agent: target.name,
            configPath: target.configPath,
            changed: false,
            reason: `could not parse: ${err instanceof Error ? err.message : String(err)}`,
        };
    }

    await fs.mkdir(path.dirname(target.configPath), { recursive: true });
    await fs.writeFile(target.configPath, updated, 'utf8');

    return { agent: target.name, configPath: target.configPath, changed: true };
}

export async function unregisterFromAgent(
    target: AgentTarget,
    serverName = 'isuzu-unity-mcp'
): Promise<ConfigChange> {
    if (target.configPath === null) {
        return { agent: target.name, configPath: '', changed: false, reason: 'no MCP config' };
    }

    let existing: string;
    try {
        existing = await fs.readFile(target.configPath, 'utf8');
    } catch {
        return { agent: target.name, configPath: target.configPath, changed: false, reason: 'not present' };
    }

    let updated: string | null;
    try {
        updated = target.configFormat === 'toml'
            ? removeTomlMcpServer(existing, serverName)
            : removeJsonMcpServer(existing, serverName);
    } catch (err) {
        return {
            agent: target.name,
            configPath: target.configPath,
            changed: false,
            reason: `could not parse: ${err instanceof Error ? err.message : String(err)}`,
        };
    }

    if (updated === null) {
        return { agent: target.name, configPath: target.configPath, changed: false, reason: 'no entry' };
    }

    await fs.writeFile(target.configPath, updated, 'utf8');
    return { agent: target.name, configPath: target.configPath, changed: true };
}
