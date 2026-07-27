import { LocalTool } from './ToolRouter.js';
import { UnityConnection } from './UnityConnection.js';

/**
 * Tools for choosing between connected Unity instances.
 *
 * These stay in the MCP server rather than moving to `[McpTool]` in C#: no single Editor can
 * answer "which Editors are there", and they must keep working when none is connected at all.
 * Everything else now lives in the Editor and is discovered through `/tools`.
 */
export function createUnityClientTools(): LocalTool[] {
    const connection = UnityConnection.getInstance();

    return [
        {
            name: 'unity_list_clients',
            description:
                'List the Unity projects currently connected to this server, with their state and ' +
                'endpoint. Call this first when a tool reports that a target is required.',
            inputSchema: { type: 'object', properties: {} },
            handler: async () => {
                const clients = connection.getConnectedClients();

                if (clients.length === 0) {
                    return 'No Unity projects are currently connected.';
                }

                const activeId = connection.getActiveClientId();

                return JSON.stringify({
                    activeClientId: activeId,
                    clients: clients.map(c => ({
                        clientId: c.id,
                        isActive: c.isActive || c.id === activeId,
                        state: c.state,
                        projectName: c.info?.productName,
                        unityVersion: c.info?.unityVersion,
                        endpoint: c.info?.endpoint,
                        port: c.info?.port,
                    })),
                });
            },
        },
        {
            name: 'unity_set_active_client',
            description:
                'Choose which Unity project subsequent tool calls go to, by clientId or project ' +
                'name (exact or substring, case-insensitive). Avoids passing `target` every time.',
            inputSchema: {
                type: 'object',
                properties: {
                    target: {
                        type: 'string',
                        description: 'A clientId, or a project name (exact or substring).',
                    },
                },
                required: ['target'],
            },
            handler: async (args) => {
                const target = typeof args.target === 'string' ? args.target : '';

                if (target === '') {
                    throw new Error('`target` is required. Call unity_list_clients to see the options.');
                }

                const picked = connection.setActiveClientByTarget(target);
                if (!picked) {
                    throw new Error(
                        `No Unity instance matches "${target}". Call unity_list_clients to see the options.`
                    );
                }

                return JSON.stringify({
                    activeClientId: picked.id,
                    projectName: picked.projectName,
                    endpoint: picked.endpoint,
                });
            },
        },
        {
            name: 'unity_get_active_client',
            description:
                'Report which Unity project tool calls currently go to. Use this to confirm the ' +
                'target before running anything that changes the project.',
            inputSchema: { type: 'object', properties: {} },
            handler: async () => {
                if (!connection.hasConnectedClients()) {
                    return 'No Unity projects are currently connected.';
                }

                const activeClientId = connection.getActiveClientId();
                if (!activeClientId) {
                    return 'No active Unity project is selected. Call unity_set_active_client to choose one.';
                }

                const active = connection.getConnectedClients().find(c => c.id === activeClientId);
                if (!active) {
                    return 'The previously active Unity project is no longer connected.';
                }

                return JSON.stringify({
                    clientId: active.id,
                    state: active.state,
                    projectName: active.info?.productName,
                    unityVersion: active.info?.unityVersion,
                    endpoint: active.info?.endpoint,
                });
            },
        },
    ];
}
