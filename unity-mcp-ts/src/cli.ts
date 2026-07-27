#!/usr/bin/env node
import { InstanceDescriptor, readDescriptors } from './core/InstanceDescriptors.js';
import { buildToolArguments, matchByProjectName, parseArgs } from './core/CliArgs.js';
import { matchByWorkingDirectory, projectRootOf } from './core/ProjectMatch.js';
import {
    detectAgents,
    findAgent,
    registerWithAgent,
    unregisterFromAgent,
} from './core/AgentTargets.js';
import {
    installSkill,
    removeState,
    serverEntryPoint,
    skillDirectoryFor,
    stateInventory,
} from './core/Housekeeping.js';

/**
 * Terminal front end for a running Unity Editor.
 *
 * It talks to the Editor directly, reading the descriptor file for the port and token, and
 * deliberately does not go through the MCP server. The proxy the MCP server exposes only
 * exists while an MCP client has spawned it, which made the documented curl workflow depend
 * on Claude Code being open — the one thing a shell user is least likely to want to require.
 *
 * `unity-mcp serve` still starts the stdio MCP server, so a single binary covers both.
 */

const USAGE = `unity-mcp - drive a running Unity Editor from the terminal

USAGE
  unity-mcp <command> [options]

COMMANDS
  projects                       List Editors that are currently running
  tools                          List the tools the Editor publishes
  call <tool> [args]             Invoke a tool
  health                         Show the Editor's server status
  jobs [id]                      List background jobs, or show one
  serve                          Run the MCP server on stdio (for MCP clients)

  setup                          Register with an MCP client and install the skill
  doctor                         Show what is installed, where, and what is stale
  uninstall                      Remove everything this tool put on the machine

CALL ARGUMENTS
  --json '<object>'              Arguments as one JSON object
  --<name> <value>               Individual argument; repeatable
  --file <path>                  For execute_code: read the snippet from a file and
                                 send it base64-encoded, so nothing can mangle it

OPTIONS
  --project <name>               Which Editor to use; needed when several are running
  --raw                          Print the response envelope instead of just the result
  -h, --help                     Show this help

SETUP / UNINSTALL OPTIONS
  --agent <name>                 Which agent to set up: claude-code, claude-desktop, codex,
                                 cursor, gemini. Defaults to every one found installed.
  --no-skill                     Do not install or remove skills
  --yes                          Actually remove, rather than listing what would be removed

EXAMPLES
  unity-mcp setup
  unity-mcp projects
  unity-mcp tools
  unity-mcp call play_mode_status
  unity-mcp call console_read_logs --type error --limit 20
  unity-mcp call scene_browse_hierarchy --json '{"name":"Player","limit":5}'
  unity-mcp call execute_code --file snippet.cs
  unity-mcp uninstall --yes
`;

/** Picks the Editor to talk to, failing with something actionable when it cannot. */
async function resolveInstance(projectName?: string): Promise<InstanceDescriptor> {
    const descriptors = await readDescriptors();

    if (descriptors.length === 0) {
        throw new Error(
            'No running Unity Editor found. Open a project with the Unity MCP package installed; ' +
            'the Editor publishes a descriptor file once its server starts.'
        );
    }

    if (projectName) {
        return matchByProjectName(descriptors, projectName);
    }

    if (descriptors.length === 1) {
        return descriptors[0];
    }

    // Running from inside a project says which one is meant, so there is nothing to guess.
    const fromCwd = matchByWorkingDirectory(descriptors, process.cwd());
    if (fromCwd !== null) {
        console.error(`[using ${fromCwd.projectName} — the project this directory belongs to]`);
        return fromCwd;
    }

    throw new Error(
        'Several Editors are running and this directory is not inside any of them. ' +
        'Pass --project to choose one: ' +
        descriptors.map(d => d.projectName).join(', ')
    );
}

async function request(
    instance: InstanceDescriptor,
    method: 'GET' | 'POST',
    path: string,
    body?: unknown
): Promise<{ ok: boolean; envelope: any }> {
    const response = await fetch(`${instance.endpoint}${path}`, {
        method,
        headers: {
            Authorization: `Bearer ${instance.token}`,
            ...(method === 'POST' ? { 'Content-Type': 'application/json' } : {}),
        },
        body: method === 'POST' ? JSON.stringify(body ?? {}) : undefined,
    });

    const text = await response.text();

    let envelope: any;
    try {
        envelope = JSON.parse(text);
    } catch {
        throw new Error(`Unity returned a non-JSON response (HTTP ${response.status}): ${text.slice(0, 200)}`);
    }

    return { ok: response.ok, envelope };
}

function print(value: unknown): void {
    console.log(typeof value === 'string' ? value : JSON.stringify(value, null, 2));
}

/**
 * Prints an envelope and reports whether it was an error.
 * Errors go to stderr so `unity-mcp call ... > out.json` keeps stdout clean.
 */
function report(envelope: any, raw: boolean): boolean {
    if (raw) {
        print(envelope);
        return envelope?.status !== 'error';
    }

    if (envelope?.status === 'error') {
        const error = envelope.error ?? {};
        console.error(`error${error.code ? ` [${error.code}]` : ''}: ${error.message ?? 'unknown'}`);
        return false;
    }

    print(envelope?.result ?? envelope);
    return true;
}

async function runSetup(parsed: ReturnType<typeof parseArgs>): Promise<number> {
    const requested = parsed.options.get('agent') ?? parsed.options.get('client');
    const detected = detectAgents();

    let chosen = requested
        ? detected.filter(a => a.name === requested)
        // Default to agents that are actually installed: writing a config for a tool the user
        // does not have would be litter, which is the opposite of the point.
        : detected.filter(a => a.detected);

    if (requested && chosen.length === 0) {
        const known = findAgent(requested);
        if (known) {
            chosen = [{ ...known, detected: false }];
        } else {
            console.error(
                `Unknown agent '${requested}'. Known: ${detected.map(a => a.name).join(', ')}`
            );
            return 1;
        }
    }

    if (chosen.length === 0) {
        console.error(
            'No supported agent found on this machine. Pass --agent <name> to set one up anyway: ' +
            detected.map(a => a.name).join(', ')
        );
        return 1;
    }

    const entry = serverEntryPoint();
    let failed = false;

    for (const agent of chosen) {
        const result = await registerWithAgent(agent, process.execPath, [entry]);

        if (result.changed) {
            console.log(`registered with ${agent.label}: ${result.configPath}`);
        } else {
            console.error(`${agent.label}: ${result.reason}`);
            failed = failed || result.reason?.startsWith('could not parse') === true;
        }

        if (parsed.flags.has('no-skill')) {
            continue;
        }

        const destination = skillDirectoryFor(agent);
        if (destination === null) {
            continue;
        }

        try {
            console.log(`installed skill:  ${await installSkill(undefined, destination)}`);
        } catch (err) {
            console.error(`${agent.label}: could not install the skill: ${err instanceof Error ? err.message : String(err)}`);
            failed = true;
        }
    }

    console.log('\nRestart the agent so it picks up the new server.');
    return failed ? 1 : 0;
}

async function runDoctor(): Promise<number> {
    console.log(`server entry point: ${serverEntryPoint()}`);
    console.log(`node:               ${process.execPath}\n`);

    console.log('Agents');
    for (const agent of detectAgents()) {
        const skills = agent.skillsDirectory === null ? 'no skills' : 'skills supported';
        console.log(
            `  ${agent.detected ? '[found]  ' : '[absent] '}${agent.name.padEnd(15)}` +
            `${(agent.configPath ?? '-').padEnd(60)} (${agent.configFormat}, ${skills})`
        );
    }

    console.log('\nOn disk');
    for (const item of await stateInventory()) {
        const mark = item.exists ? '[exists] ' : '[absent] ';
        const detail = item.detail ? ` (${item.detail})` : '';
        console.log(`  ${mark}${item.kind.padEnd(28)} ${item.path}${detail}`);
    }

    console.log('\nRunning Editors');
    const descriptors = await readDescriptors();

    if (descriptors.length === 0) {
        console.log('  none');
    } else {
        for (const descriptor of descriptors) {
            console.log(`  ${descriptor.projectName} (${descriptor.unityVersion}) ${descriptor.endpoint} pid ${descriptor.pid}`);
        }
    }

    return 0;
}

async function runUninstall(parsed: ReturnType<typeof parseArgs>): Promise<number> {
    const includeSkills = !parsed.flags.has('no-skill');
    const agents = detectAgents().filter(a => a.detected);

    if (!parsed.flags.has('yes')) {
        // Listing before removing, by default: the alternative is a command that deletes
        // things the user has not seen named.
        console.log('Would remove:\n');

        for (const item of await stateInventory()) {
            if (!item.exists || !item.removable) {
                continue;
            }

            if (item.kind.endsWith('skill') && !includeSkills) {
                continue;
            }

            console.log(`  ${item.path}`);
        }

        for (const agent of agents) {
            if (agent.configPath !== null) {
                console.log(`  the unity-mcp entry in ${agent.configPath}`);
            }
        }

        console.log('\nRe-run with --yes to remove them.');
        return 0;
    }

    let failed = false;

    for (const agent of agents) {
        const result = await unregisterFromAgent(agent);
        console.log(result.changed
            ? `removed entry from ${result.configPath}`
            : `skipped ${agent.label} (${result.reason})`);
    }

    for (const result of await removeState({ includeSkills })) {
        if (result.removed) {
            console.log(`removed ${result.path}`);
        } else if (result.reason !== 'not present') {
            console.error(`could not remove ${result.path}: ${result.reason}`);
            failed = true;
        }
    }

    console.log('\nThe Unity package itself is removed through the Package Manager.');
    return failed ? 1 : 0;
}

async function main(): Promise<number> {
    const parsed = parseArgs(process.argv.slice(2));

    if (parsed.flags.has('help') || parsed.command === '' || parsed.command === 'help') {
        console.log(USAGE);
        return 0;
    }

    if (parsed.command === 'serve') {
        // Delegating rather than duplicating: one binary, one server implementation.
        await import('./index.js');
        return 0;
    }

    const raw = parsed.flags.has('raw');
    const project = parsed.options.get('project');

    // Housekeeping commands work with no Editor running, so they are handled before any
    // attempt to resolve one.
    if (parsed.command === 'setup') {
        return runSetup(parsed);
    }

    if (parsed.command === 'doctor') {
        return runDoctor();
    }

    if (parsed.command === 'uninstall') {
        return runUninstall(parsed);
    }

    if (parsed.command === 'projects') {
        const descriptors = await readDescriptors();

        if (descriptors.length === 0) {
            console.error('No running Unity Editor found.');
            return 1;
        }

        // Marked so it is obvious which one a bare command would reach from here.
        const here = matchByWorkingDirectory(descriptors, process.cwd());

        print(descriptors.map(d => ({
            projectName: d.projectName,
            projectRoot: projectRootOf(d.projectPath),
            unityVersion: d.unityVersion,
            endpoint: d.endpoint,
            pid: d.pid,
            protocolVersion: d.protocolVersion,
            containsWorkingDirectory: here !== null && here.pid === d.pid,
        })));
        return 0;
    }

    const instance = await resolveInstance(project);

    switch (parsed.command) {
        case 'health': {
            const { envelope } = await request(instance, 'GET', '/health');
            return report(envelope, raw) ? 0 : 1;
        }

        case 'tools': {
            const { envelope } = await request(instance, 'GET', '/tools');

            if (raw || envelope?.status === 'error') {
                return report(envelope, raw) ? 0 : 1;
            }

            for (const tool of envelope.result?.tools ?? []) {
                const params = Object.keys(tool.inputSchema?.properties ?? {});
                const required = new Set(tool.inputSchema?.required ?? []);
                const rendered = params
                    .map(p => (required.has(p) ? `<${p}>` : `[${p}]`))
                    .join(' ');

                console.log(`${tool.name}${rendered ? ' ' + rendered : ''}`);
                console.log(`    ${tool.description}`);
            }
            return 0;
        }

        case 'jobs': {
            const id = parsed.positional[0];
            const path = id ? `/jobs/${id}` : '/jobs';
            const { envelope } = await request(instance, 'GET', path);
            return report(envelope, raw) ? 0 : 1;
        }

        case 'call': {
            const tool = parsed.positional[0];

            if (!tool) {
                console.error('Which tool? Run `unity-mcp tools` to see what this Editor publishes.');
                return 2;
            }

            const args = await buildToolArguments(tool, parsed);
            const { envelope } = await request(instance, 'POST', `/tools/${tool}`, args);
            return report(envelope, raw) ? 0 : 1;
        }

        default:
            console.error(`Unknown command '${parsed.command}'.\n`);
            console.log(USAGE);
            return 2;
    }
}

main()
    .then(code => {
        // `serve` hands control to the MCP server, which owns the process from here.
        if (code !== 0 || process.argv[2] !== 'serve') {
            process.exitCode = code;
        }
    })
    .catch(err => {
        console.error(err instanceof Error ? err.message : String(err));
        process.exitCode = 1;
    });
