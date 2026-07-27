#!/usr/bin/env node
import { InstanceDescriptor, readDescriptors } from './core/InstanceDescriptors.js';
import { buildToolArguments, parseArgs } from './core/CliArgs.js';

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

CALL ARGUMENTS
  --json '<object>'              Arguments as one JSON object
  --<name> <value>               Individual argument; repeatable
  --file <path>                  For execute_code: read the snippet from a file and
                                 send it base64-encoded, so nothing can mangle it

OPTIONS
  --project <name>               Which Editor to use; needed when several are running
  --raw                          Print the response envelope instead of just the result
  -h, --help                     Show this help

EXAMPLES
  unity-mcp projects
  unity-mcp tools
  unity-mcp call play_mode_status
  unity-mcp call console_read_logs --type error --limit 20
  unity-mcp call scene_browse_hierarchy --json '{"name":"Player","limit":5}'
  unity-mcp call execute_code --file snippet.cs
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
        const lowered = projectName.toLowerCase();
        const matches = descriptors.filter(d => d.projectName.toLowerCase().includes(lowered));

        if (matches.length === 0) {
            throw new Error(
                `No running Editor matches "${projectName}". Running: ` +
                descriptors.map(d => d.projectName).join(', ')
            );
        }

        return matches[0];
    }

    if (descriptors.length > 1) {
        throw new Error(
            'Several Editors are running; pass --project to choose one: ' +
            descriptors.map(d => d.projectName).join(', ')
        );
    }

    return descriptors[0];
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

    if (parsed.command === 'projects') {
        const descriptors = await readDescriptors();

        if (descriptors.length === 0) {
            console.error('No running Unity Editor found.');
            return 1;
        }

        print(descriptors.map(d => ({
            projectName: d.projectName,
            projectPath: d.projectPath,
            unityVersion: d.unityVersion,
            endpoint: d.endpoint,
            pid: d.pid,
            protocolVersion: d.protocolVersion,
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
