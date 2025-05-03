import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { UnityConnection } from "./UnityConnection.js";

/**
 * Registers Unity client management tools with the MCP server.
 * These tools allow for listing, selecting, and managing connected Unity clients.
 */
export function registerUnityClientTools(server: McpServer): void {
    // Get the Unity connection instance
    const connection = UnityConnection.getInstance();

    // List all connected Unity clients
    server.tool(
        "unity_listClients",
        "Lists all connected Unity projects",
        {},
        async () => {

            connection.clearClients();

            connection.sendInitialBroadcast("listClients");

            // Wait for the clients
            await new Promise(resolve => setTimeout(resolve, 3000));

            const clients = connection.getConnectedClients();

            // Filter out clients with invalid/unknown information
            const validClients = clients.filter(client => {
                const info = client.info || {};
                // Consider a client valid if it has a valid project name
                return info.productName && info.productName !== "Unknown" &&
                    info.productName !== "UnknownProject";
            });

            if (validClients.length === 0) {
                return {
                    content: [{
                        type: "text",
                        text: "No Unity projects are currently connected."
                    }]
                };
            }

            // Focus on project information in the display
            let responseText = "Connected Unity projects:\n\n";

            validClients.forEach((client, index) => {
                const info = client.info || {};
                responseText += `${index + 1}. ${client.isActive ? '✓ ' : ''}${info.productName || 'Unknown Project'}\n`;
                responseText += `   ID: ${client.id}\n`;
                responseText += `   Unity: ${info.unityVersion || 'Unknown'}\n`;
                responseText += `   Mode: ${info.isEditor ? 'Editor' : 'Player'}\n`;
                responseText += `   Project Hash: ${info.projectPathHash || 'Unknown'}\n`;
                responseText += '\n';
            });

            return {
                content: [{
                    type: "text",
                    text: responseText
                }]
            };
        }
    );

    // Set active client by ID
    server.tool(
        "unity_setActiveClient",
        "Sets the active Unity project",
        {
            clientId: z.string().describe("The ID of the client to set as active")
        },
        async (params) => {
            const success = connection.setActiveClient(params.clientId);

            if (!success) {
                return {
                    isError: true,
                    content: [{
                        type: "text",
                        text: `Error: Client with ID ${params.clientId} not found`
                    }]
                };
            }

            const clients = connection.getConnectedClients();
            const activeClient = clients.find(c => c.id === params.clientId);
            const info = activeClient?.info || {};

            return {
                content: [{
                    type: "text",
                    text: `Successfully set ${info.productName || params.clientId} as the active project`
                }]
            };
        }
    );

    // Connect to project by name
    server.tool(
        "unity_connectToProject",
        "Connect to a Unity project by name",
        {
            projectName: z.string().describe("The name of the Unity project to connect to")
        },
        async (params) => {
            const clients = connection.getConnectedClients();

            // Filter for valid clients first, then search by name
            const validClients = clients.filter(client => {
                const info = client.info || {};
                return info.productName && info.productName !== "Unknown" &&
                    info.productName !== "UnknownProject";
            });

            // Search for clients by project name
            const matchingClients = validClients.filter(c =>
                (c.info?.productName || "").toLowerCase().includes(params.projectName.toLowerCase())
            );

            if (matchingClients.length === 0) {
                return {
                    isError: true,
                    content: [{
                        type: "text",
                        text: `Error: No projects found matching "${params.projectName}"`
                    }]
                };
            }

            // Select the first matching client
            const client = matchingClients[0];
            const success = connection.setActiveClient(client.id);

            if (!success) {
                return {
                    isError: true,
                    content: [{
                        type: "text",
                        text: `Error: Failed to connect to project "${params.projectName}"`
                    }]
                };
            }

            return {
                content: [{
                    type: "text",
                    text: `Successfully connected to "${client.info?.productName}" (${client.info?.isEditor ? 'Editor' : 'Player'})`
                }]
            };
        }
    );

    // Get active client info
    server.tool(
        "unity_getActiveClient",
        "Get information about the currently active Unity project",
        {},
        async () => {
            if (!connection.hasConnectedClients()) {
                return {
                    content: [{
                        type: "text",
                        text: "No Unity projects are currently connected."
                    }]
                };
            }

            const activeClientId = connection.getActiveClientId();
            if (!activeClientId) {
                return {
                    content: [{
                        type: "text",
                        text: "No active Unity project is selected."
                    }]
                };
            }

            const clients = connection.getConnectedClients();
            const activeClient = clients.find(c => c.id === activeClientId);

            if (!activeClient) {
                return {
                    content: [{
                        type: "text",
                        text: "Active client information not found. This is unexpected and may indicate an internal error."
                    }]
                };
            }

            const info = activeClient.info || {};

            // Check if this is a valid client with proper information
            if (!info.productName || info.productName === "Unknown" || info.productName === "UnknownProject") {
                return {
                    content: [{
                        type: "text",
                        text: "The active client has incomplete information and may not be properly initialized."
                    }]
                };
            }

            // Format active client info with focus on project information
            let responseText = "Active Unity project:\n\n";
            responseText += `Project: ${info.productName}\n`;
            responseText += `Unity Version: ${info.unityVersion || 'Unknown'}\n`;
            responseText += `Mode: ${info.isEditor ? 'Editor' : 'Player'}\n`;
            responseText += `Project Hash: ${info.projectPathHash || 'Unknown'}\n`;

            return {
                content: [{
                    type: "text",
                    text: responseText
                }]
            };
        }
    );
}
