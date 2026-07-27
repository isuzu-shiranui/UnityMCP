import { promises as fs } from 'fs';

/**
 * Command-line parsing for the `unity-mcp` binary.
 *
 * Kept apart from `cli.ts` so tests can exercise it without importing the entry point, which
 * would run the program. These are also the parts that fail quietly: a mis-parsed flag turns
 * into a wrong tool argument rather than an error, so they are worth testing directly.
 */
export interface ParsedArgs {
    command: string;
    positional: string[];
    options: Map<string, string>;
    flags: Set<string>;
}

/** Options consumed by the CLI itself, never forwarded to a tool. */
const CLI_ONLY_OPTIONS = new Set(['json', 'project', 'file', 'raw', 'help', 'agent', 'client', 'yes', 'no-skill']);

export function parseArgs(argv: string[]): ParsedArgs {
    const positional: string[] = [];
    const options = new Map<string, string>();
    const flags = new Set<string>();

    for (let i = 0; i < argv.length; i++) {
        const token = argv[i];

        if (!token.startsWith('--')) {
            if (token === '-h') {
                flags.add('help');
                continue;
            }

            positional.push(token);
            continue;
        }

        const name = token.slice(2);
        const next = argv[i + 1];

        // A bare `--flag` at the end, or one followed by another flag, is a boolean.
        if (next === undefined || next.startsWith('--')) {
            flags.add(name);
        } else {
            options.set(name, next);
            i++;
        }
    }

    return {
        command: positional.shift() ?? '',
        positional,
        options,
        flags,
    };
}

/**
 * Picks the Editor a `--project` value refers to.
 *
 * An exact name wins outright before substrings are considered. Without that rule, asking for
 * "MyGame" while "MyGame" and "MyGame Sandbox" are both open resolves by whichever descriptor
 * happened to be read first — and the failure is silent, because the call succeeds against the
 * wrong project. An ambiguous substring is refused for the same reason: sending a write to the
 * wrong Editor is worse than making the caller be specific.
 */
export function matchByProjectName<T extends { projectName: string }>(
    candidates: T[],
    query: string
): T {
    const lowered = query.trim().toLowerCase();

    const exact = candidates.filter(c => c.projectName.toLowerCase() === lowered);
    if (exact.length === 1) {
        return exact[0];
    }

    const partial = candidates.filter(c => c.projectName.toLowerCase().includes(lowered));

    if (partial.length === 1) {
        return partial[0];
    }

    if (partial.length === 0) {
        throw new Error(
            `No running Editor matches "${query}". Running: ` +
            candidates.map(c => c.projectName).join(', ')
        );
    }

    throw new Error(
        `"${query}" matches more than one running Editor: ` +
        partial.map(c => c.projectName).join(', ') +
        '. Use the full project name.'
    );
}

/**
 * Turns obviously numeric or boolean command-line text into the matching JSON type.
 *
 * The Editor coerces scalars anyway, so this is not required for the call to work; it keeps
 * `--raw` output honest about what was actually sent.
 */
export function coerceScalar(value: string): unknown {
    if (value === 'true') {
        return true;
    }

    if (value === 'false') {
        return false;
    }

    if (value !== '' && value.trim() !== '' && !Number.isNaN(Number(value))) {
        return Number(value);
    }

    return value;
}

/**
 * Assembles a tool's arguments from `--json`, individual `--name value` pairs, and `--file`.
 */
export async function buildToolArguments(
    tool: string,
    parsed: ParsedArgs,
    readFile: (path: string) => Promise<string> = path => fs.readFile(path, 'utf8')
): Promise<Record<string, unknown>> {
    const args: Record<string, unknown> = {};

    const json = parsed.options.get('json');
    if (json !== undefined) {
        let decoded: unknown;

        try {
            decoded = JSON.parse(json);
        } catch (err) {
            throw new Error(`--json is not valid JSON: ${err instanceof Error ? err.message : String(err)}`);
        }

        if (typeof decoded !== 'object' || decoded === null || Array.isArray(decoded)) {
            throw new Error('--json must be a JSON object.');
        }

        Object.assign(args, decoded);
    }

    for (const [name, value] of parsed.options) {
        if (!CLI_ONLY_OPTIONS.has(name)) {
            args[name] = coerceScalar(value);
        }
    }

    for (const flag of parsed.flags) {
        if (!CLI_ONLY_OPTIONS.has(flag)) {
            args[flag] = true;
        }
    }

    const file = parsed.options.get('file');
    if (file !== undefined) {
        const source = await readFile(file);

        // Sent base64 so nothing between here and the compiler can alter the snippet.
        // Passing C# through a shell and a JSON encoder is precisely where backslashes in
        // string literals get eaten, and the resulting compile error names generated source
        // the caller never sees.
        if (tool === 'execute_code') {
            args.code_base64 = Buffer.from(source, 'utf8').toString('base64');
        } else {
            args.code = source;
        }
    }

    return args;
}
