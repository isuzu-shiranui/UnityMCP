/**
 * Tests for the CLI's argument handling.
 *
 * Parsing and argument assembly are the parts a user hits on every invocation and the parts
 * that fail silently — a mis-parsed flag becomes a wrong tool argument, not an error.
 */
import { describe, test, expect } from '@jest/globals';

import { parseArgs, buildToolArguments, coerceScalar } from '../core/CliArgs.js';

describe('parseArgs', () => {
    test('takes the command from the first positional', () => {
        expect(parseArgs(['tools']).command).toBe('tools');
    });

    test('reads --name value pairs', () => {
        const parsed = parseArgs(['call', 'console_read_logs', '--type', 'error', '--limit', '20']);

        expect(parsed.command).toBe('call');
        expect(parsed.positional).toEqual(['console_read_logs']);
        expect(parsed.options.get('type')).toBe('error');
        expect(parsed.options.get('limit')).toBe('20');
    });

    test('treats a trailing --name as a boolean flag', () => {
        const parsed = parseArgs(['call', 'x', '--raw']);

        expect(parsed.flags.has('raw')).toBe(true);
        expect(parsed.options.has('raw')).toBe(false);
    });

    test('treats --name followed by another flag as a boolean', () => {
        const parsed = parseArgs(['call', 'x', '--raw', '--project', 'MyGame']);

        expect(parsed.flags.has('raw')).toBe(true);
        expect(parsed.options.get('project')).toBe('MyGame');
    });

    test('accepts -h as help', () => {
        expect(parseArgs(['-h']).flags.has('help')).toBe(true);
    });
});

describe('coerceScalar', () => {
    test('recognises booleans and numbers', () => {
        expect(coerceScalar('true')).toBe(true);
        expect(coerceScalar('false')).toBe(false);
        expect(coerceScalar('20')).toBe(20);
        expect(coerceScalar('1.5')).toBe(1.5);
    });

    test('leaves anything else as text', () => {
        expect(coerceScalar('error')).toBe('error');
        expect(coerceScalar('')).toBe('');
        // A name that merely starts with a digit is still a name.
        expect(coerceScalar('2Player')).toBe('2Player');
    });
});

describe('buildToolArguments', () => {
    test('turns flags into typed arguments', async () => {
        const args = await buildToolArguments('console_read_logs', parseArgs([
            'call', 'console_read_logs', '--type', 'error', '--limit', '20',
        ]));

        expect(args).toEqual({ type: 'error', limit: 20 });
    });

    test('merges --json with individual flags', async () => {
        const args = await buildToolArguments('scene_browse_hierarchy', parseArgs([
            'call', 'scene_browse_hierarchy', '--json', '{"name":"Player"}', '--limit', '5',
        ]));

        expect(args).toEqual({ name: 'Player', limit: 5 });
    });

    test('does not leak CLI-only options into the tool call', async () => {
        const args = await buildToolArguments('play_mode_status', parseArgs([
            'call', 'play_mode_status', '--project', 'MyGame', '--raw',
        ]));

        expect(args).toEqual({});
    });

    test('reports malformed --json rather than sending it', async () => {
        await expect(
            buildToolArguments('x', parseArgs(['call', 'x', '--json', '{not json']))
        ).rejects.toThrow(/not valid JSON/);
    });

    test('rejects a --json array', async () => {
        await expect(
            buildToolArguments('x', parseArgs(['call', 'x', '--json', '[1,2]']))
        ).rejects.toThrow(/must be a JSON object/);
    });

    test('a bare flag becomes a boolean argument', async () => {
        const args = await buildToolArguments('project_packages', parseArgs([
            'call', 'project_packages', '--include_registry',
        ]));

        expect(args).toEqual({ include_registry: true });
    });

    test('--file sends execute_code snippets base64-encoded', async () => {
        // The point of --file: a snippet never passes through shell or JSON escaping, so
        // backslashes in C# string literals cannot be eaten on the way in.
        const source = 'var s = "a\\nb"; return s.Length;';
        const args = await buildToolArguments(
            'execute_code',
            parseArgs(['call', 'execute_code', '--file', 'snippet.cs']),
            async () => source
        );

        expect(args.code).toBeUndefined();
        expect(Buffer.from(args.code_base64 as string, 'base64').toString('utf8')).toBe(source);
    });

    test('--file on any other tool sends plain code', async () => {
        const args = await buildToolArguments(
            'some_other_tool',
            parseArgs(['call', 'some_other_tool', '--file', 'x.txt']),
            async () => 'contents'
        );

        expect(args.code).toBe('contents');
    });
});
