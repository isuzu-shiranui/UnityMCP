import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import {
    CallToolRequestSchema,
    ListToolsRequestSchema,
} from '@modelcontextprotocol/sdk/types.js';

import { JObject } from '../types/index.js';
import { ToolCatalogClient } from './ToolCatalogClient.js';
import { UnityConnection } from './UnityConnection.js';

/**
 * A tool implemented here in the MCP server rather than in the Editor — instance selection,
 * which cannot be answered by any single Editor because it is about choosing between them.
 */
export interface LocalTool {
    name: string;
    description: string;
    inputSchema: Record<string, unknown>;
    handler: (args: Record<string, unknown>) => Promise<string>;
}

/**
 * Serves `tools/list` and `tools/call`.
 *
 * Registration goes through the low-level request handlers rather than `McpServer.tool()`
 * on purpose. MCP transports JSON Schema natively, and the Editor already generates it from
 * the C# signatures; routing through the SDK's zod helper would mean converting schema into
 * zod and back, losing whatever the converter did not cover. Here the Editor's schema
 * reaches the client byte for byte.
 */
export class ToolRouter {
    private readonly locals = new Map<string, LocalTool>();

    constructor(
        private readonly mcpServer: McpServer,
        private readonly connection: UnityConnection,
        private readonly catalog: ToolCatalogClient,
        localTools: LocalTool[]
    ) {
        for (const tool of localTools) {
            this.locals.set(tool.name, tool);
        }
    }

    /** Installs the request handlers. Call once, before connecting the transport. */
    public install(): void {
        const server = this.mcpServer.server;

        server.setRequestHandler(ListToolsRequestSchema, async () => ({
            tools: this.listTools(),
        }));

        server.setRequestHandler(CallToolRequestSchema, async (request) => {
            const name = request.params.name;
            const args = (request.params.arguments ?? {}) as Record<string, unknown>;

            try {
                return {
                    content: [{ type: 'text' as const, text: await this.dispatch(name, args) }],
                };
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                const code = (error as any)?.code;

                console.error(`[ERROR] tools/call ${name}: ${message}`);

                // Reported as a tool error rather than thrown, so the model sees the message
                // and can correct the call instead of the whole request failing at the
                // protocol level.
                return {
                    isError: true,
                    content: [{
                        type: 'text' as const,
                        text: code ? `Error [${code}]: ${message}` : `Error: ${message}`,
                    }],
                };
            }
        });
    }

    /** Notifies connected clients that the tool list changed. */
    public notifyToolsChanged(): void {
        try {
            void this.mcpServer.server.notification({
                method: 'notifications/tools/list_changed',
                params: {},
            });
        } catch (err) {
            console.error(
                `[WARN] Could not send tools/list_changed: ${err instanceof Error ? err.message : String(err)}`
            );
        }
    }

    /** The tool list served to clients: local tools first, then whatever the Editor publishes. */
    public listTools() {
        const local = Array.from(this.locals.values()).map(tool => ({
            name: tool.name,
            description: tool.description,
            inputSchema: tool.inputSchema,
        }));

        const unity = this.catalog.getTools().map(tool => ({
            name: tool.name,
            description: tool.description,
            inputSchema: withTargetParameter(tool.inputSchema),
        }));

        return [...local, ...unity];
    }

    /** Routes one call to a local handler or to the Editor. */
    public async dispatch(name: string, args: Record<string, unknown>): Promise<string> {
        const local = this.locals.get(name);
        if (local) {
            return local.handler(args);
        }

        let definition = this.catalog.findTool(name);

        // The catalog may have been served from disk before any Editor was up, or a
        // recompile may have added tools since. One refresh is cheaper than telling the
        // caller a tool does not exist when it does.
        if (!definition) {
            const refreshTarget = this.pickCatalogTarget(args.target);
            if (refreshTarget) {
                try {
                    await this.catalog.refresh(refreshTarget);
                    definition = this.catalog.findTool(name);
                } catch {
                    // Fall through to the not-found message below.
                }
            }
        }

        if (!definition) {
            const known = [...this.locals.keys(), ...this.catalog.getTools().map(t => t.name)];
            const hint = this.connection.isUnityConnected()
                ? ''
                : ' No Unity Editor is currently connected, so Editor tools are unavailable.';

            throw new Error(
                `Unknown tool '${name}'.${hint} Available: ${known.join(', ') || '(none)'}`
            );
        }

        const { target, ...toolArgs } = args as { target?: string } & Record<string, unknown>;

        const result = await this.connection.sendToEndpoint(
            `/tools/${definition.name}`,
            toolArgs as JObject,
            {
                target: typeof target === 'string' && target !== '' ? target : undefined,
                // Taken straight from the tool's own declaration. v2 had to look this up in a
                // separate /health table keyed by endpoint, which is one more thing to keep
                // in sync and get wrong.
                idempotency: definition.idempotency,
            }
        );

        return JSON.stringify(result);
    }

    /**
     * Chooses which Editor to fetch the catalog from.
     *
     * A named target is honoured; otherwise any usable instance will do, because the catalog
     * is a description of the installed package rather than of a particular project. Naming
     * one explicitly is what keeps this working once a second Editor registers — an
     * untargeted fetch fails with "target required" exactly then.
     */
    private pickCatalogTarget(target: unknown): string | undefined {
        if (typeof target === 'string' && target !== '') {
            return target;
        }

        // Healthy first: a 'reloading' instance is mid-domain-reload and will refuse the
        // connection, and stale registry entries linger in that state.
        const usable = this.connection
            .getConnectedClients()
            .filter(c => c.state === 'healthy' || c.state === 'reloading')
            .sort((a, b) => (a.state === 'healthy' ? 0 : 1) - (b.state === 'healthy' ? 0 : 1));

        return usable.length > 0 ? usable[0].id : undefined;
    }
}

/**
 * Adds the optional `target` parameter every Editor tool accepts for routing between
 * multiple open projects. The Editor does not know about it — the MCP server strips it
 * before forwarding — so it is injected here rather than declared in C#.
 */
export function withTargetParameter(schema: Record<string, unknown>): Record<string, unknown> {
    const properties = {
        ...((schema.properties as Record<string, unknown>) ?? {}),
        target: {
            type: 'string',
            description:
                'Unity project name or clientId to route this call to. ' +
                'Required when several Unity instances are registered and no active client is set.',
        },
    };

    return { ...schema, properties };
}
