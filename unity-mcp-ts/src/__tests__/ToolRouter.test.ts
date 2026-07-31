/**
 * Tests for ToolRouter: the layer that serves tools/list and tools/call.
 *
 * The router is constructed with its collaborators, so these use plain fakes rather than
 * module mocks — no MCP transport and no Unity Editor involved.
 */
import { describe, test, expect } from '@jest/globals';

import { ToolRouter, LocalTool, withTargetParameter } from '../core/ToolRouter.js';
import { UnityToolDefinition, sameToolSet } from '../core/ToolCatalogClient.js';

const EDITOR_TOOL: UnityToolDefinition = {
    name: 'scene_browse_hierarchy',
    description: 'Walk the scene hierarchy.',
    inputSchema: {
        type: 'object',
        properties: { name: { type: 'string' }, limit: { type: 'integer' } },
        required: ['name'],
    },
    idempotency: 'safe',
    mainThread: true,
    destructive: false,
};

const WRITE_TOOL: UnityToolDefinition = {
    name: 'inspect_write',
    description: 'Write a serialized property.',
    inputSchema: { type: 'object', properties: {} },
    idempotency: 'unsafe',
    mainThread: true,
    destructive: false,
};

interface SentCall {
    path: string;
    body: unknown;
    opts: any;
}

function makeRouter(options?: {
    tools?: UnityToolDefinition[];
    connected?: boolean;
    locals?: LocalTool[];
    sendResult?: unknown;
}) {
    const sent: SentCall[] = [];
    let refreshCount = 0;

    const tools = options?.tools ?? [EDITOR_TOOL, WRITE_TOOL];

    const refreshTargets: (string | undefined)[] = [];

    const catalog = {
        getTools: () => tools,
        findTool: (name: string) => tools.find(t => t.name === name),
        refresh: async (target?: string) => {
            refreshCount++;
            refreshTargets.push(target);
            return false;
        },
    };

    const connected = options?.connected ?? true;

    const connection = {
        isUnityConnected: () => connected,
        getConnectedClients: () =>
            connected
                ? [{ id: 'Proj-27182', state: 'healthy', isActive: true, info: {} }]
                : [],
        sendToEndpoint: async (path: string, body: unknown, opts: any) => {
            sent.push({ path, body, opts });
            return options?.sendResult ?? { ok: true };
        },
    };

    const router = new ToolRouter(
        {} as any,
        connection as any,
        catalog as any,
        options?.locals ?? []
    );

    return { router, sent, refreshCount: () => refreshCount, refreshTargets };
}

describe('ToolRouter.listTools', () => {
    test('serves local tools followed by Editor tools', () => {
        const local: LocalTool = {
            name: 'unity_list_clients',
            description: 'List connected projects.',
            inputSchema: { type: 'object', properties: {} },
            handler: async () => 'ok',
        };

        const { router } = makeRouter({ locals: [local] });
        const listed = router.listTools();

        expect(listed.map(t => t.name)).toEqual([
            'unity_list_clients',
            'scene_browse_hierarchy',
            'inspect_write',
        ]);
    });

    test('forwards the Editor _meta, and omits the key when there is none', () => {
        const hinted: UnityToolDefinition = {
            ...EDITOR_TOOL,
            name: 'console_read_logs',
            _meta: { 'anthropic/alwaysLoad': true, 'anthropic/maxResultSizeChars': 200000 },
        };

        const { router } = makeRouter({ tools: [hinted, WRITE_TOOL] });
        const listed = router.listTools();

        const withHints = listed.find(t => t.name === 'console_read_logs')! as any;
        expect(withHints._meta).toEqual({
            'anthropic/alwaysLoad': true,
            'anthropic/maxResultSizeChars': 200000,
        });

        // Spreading an absent _meta would put an explicit null on every other tool, which is a
        // different thing on the wire from not having sent the field at all.
        const plain = listed.find(t => t.name === 'inspect_write')! as any;
        expect('_meta' in plain).toBe(false);
    });

    test('forwards the Editor schema unchanged apart from target', () => {
        const { router } = makeRouter();
        const scene = router.listTools().find(t => t.name === 'scene_browse_hierarchy')!;
        const schema = scene.inputSchema as any;

        // The Editor is the sole author of this schema; the router must not reshape it.
        expect(schema.properties.name).toEqual({ type: 'string' });
        expect(schema.properties.limit).toEqual({ type: 'integer' });
        expect(schema.required).toEqual(['name']);
        expect(schema.properties.target.type).toBe('string');
    });

    test('target is optional so single-instance callers never supply it', () => {
        const augmented = withTargetParameter({
            type: 'object',
            properties: { a: { type: 'string' } },
            required: ['a'],
        }) as any;

        expect(augmented.required).toEqual(['a']);
        expect(augmented.properties.target).toBeDefined();
    });

    test('does not mutate the catalog entry it was given', () => {
        const { router } = makeRouter();
        router.listTools();

        expect((EDITOR_TOOL.inputSchema as any).properties.target).toBeUndefined();
    });
});

describe('ToolRouter.dispatch', () => {
    test('runs a local tool without contacting Unity', async () => {
        const local: LocalTool = {
            name: 'unity_get_active_client',
            description: 'Report the active project.',
            inputSchema: { type: 'object', properties: {} },
            handler: async () => 'the answer',
        };

        const { router, sent } = makeRouter({ locals: [local] });

        expect(await router.dispatch('unity_get_active_client', {})).toBe('the answer');
        expect(sent).toHaveLength(0);
    });

    test('posts an Editor tool to /tools/<name>', async () => {
        const { router, sent } = makeRouter({ sendResult: { count: 3 } });

        const out = await router.dispatch('scene_browse_hierarchy', { name: 'Player', limit: 5 });

        expect(sent).toHaveLength(1);
        expect(sent[0].path).toBe('/tools/scene_browse_hierarchy');
        expect(sent[0].body).toEqual({ name: 'Player', limit: 5 });
        expect(out).toBe(JSON.stringify({ count: 3 }));
    });

    test('strips target from the body and passes it as routing', async () => {
        const { router, sent } = makeRouter();

        await router.dispatch('scene_browse_hierarchy', { name: 'Player', target: 'MyProject' });

        expect(sent[0].body).toEqual({ name: 'Player' });
        expect(sent[0].opts.target).toBe('MyProject');
    });

    test('an empty target is treated as absent', async () => {
        const { router, sent } = makeRouter();

        await router.dispatch('scene_browse_hierarchy', { name: 'Player', target: '' });

        expect(sent[0].opts.target).toBeUndefined();
    });

    test('idempotency comes from the tool declaration', async () => {
        // v2 looked this up in a separate /health table keyed by endpoint, which was one more
        // thing to keep in sync. Retrying an unsafe call is a real bug, so this must be exact.
        const { router, sent } = makeRouter();

        await router.dispatch('scene_browse_hierarchy', { name: 'a' });
        await router.dispatch('inspect_write', {});

        expect(sent[0].opts.idempotency).toBe('safe');
        expect(sent[1].opts.idempotency).toBe('unsafe');
    });

    test('refreshes once before declaring a tool unknown', async () => {
        const { router, refreshCount } = makeRouter({ connected: true });

        await expect(router.dispatch('added_after_startup', {})).rejects.toThrow(/Unknown tool/);
        expect(refreshCount()).toBe(1);
    });

    test('the lazy refresh names an instance', async () => {
        // An untargeted catalog fetch throws "target required" as soon as a second Editor
        // registers, which silently left the tool list empty until this was pinned down.
        const { router, refreshTargets } = makeRouter({ connected: true });

        await expect(router.dispatch('added_after_startup', {})).rejects.toThrow();

        expect(refreshTargets).toEqual(['Proj-27182']);
    });

    test('an explicit target is used for the lazy refresh', async () => {
        const { router, refreshTargets } = makeRouter({ connected: true });

        await expect(router.dispatch('added_after_startup', { target: 'Other' })).rejects.toThrow();

        expect(refreshTargets).toEqual(['Other']);
    });

    test('does not refresh when no Editor is connected', async () => {
        const { router, refreshCount } = makeRouter({ connected: false });

        await expect(router.dispatch('whatever', {})).rejects.toThrow(/No Unity Editor is currently connected/);
        expect(refreshCount()).toBe(0);
    });

    test('the unknown-tool error lists what is available', async () => {
        const { router } = makeRouter({ connected: false });

        await expect(router.dispatch('nope', {})).rejects.toThrow(/scene_browse_hierarchy/);
    });
});

describe('sameToolSet', () => {
    test('ignores ordering', () => {
        expect(sameToolSet([EDITOR_TOOL, WRITE_TOOL], [WRITE_TOOL, EDITOR_TOOL])).toBe(true);
    });

    test('detects a changed schema', () => {
        const changed = { ...EDITOR_TOOL, inputSchema: { type: 'object', properties: {} } };
        expect(sameToolSet([EDITOR_TOOL], [changed])).toBe(false);
    });

    test('detects a changed description', () => {
        expect(sameToolSet([EDITOR_TOOL], [{ ...EDITOR_TOOL, description: 'other' }])).toBe(false);
    });

    test('detects a different count', () => {
        expect(sameToolSet([EDITOR_TOOL], [EDITOR_TOOL, WRITE_TOOL])).toBe(false);
    });
});
