import { z } from "zod";
import { BaseCommandHandler } from "../core/BaseCommandHandler.js";
/**
 * Command handler for searching assets in the Unity project.
 */
export class AssetSearchCommandHandler extends BaseCommandHandler {
    /**
     * Gets the command prefix for this handler.
     */
    get commandPrefix() {
        return "asset";
    }
    /**
     * Gets the description of this command handler.
     */
    get description() {
        return "Search and retrieve assets from the project";
    }
    /**
     * Executes the command with the given parameters.
     * @param action The action to execute.
     * @param parameters The parameters for the command.
     * @returns A Promise that resolves to a JSON object containing the execution result.
     */
    async execute(action, parameters) {
        switch (action.toLowerCase()) {
            case "search":
                return this.searchAssets(parameters);
            case "findbyname":
                return this.findByName(parameters);
            case "findbytype":
                return this.findByType(parameters);
            default:
                return {
                    success: false,
                    error: `Unknown action: ${action}. Supported actions: search, findByName, findByType`
                };
        }
    }
    /**
     * Gets the tool definitions supported by this handler.
     * @returns A map of tool names to their definitions.
     */
    getToolDefinitions() {
        const tools = new Map();
        // Add asset_search tool
        tools.set("asset_search", {
            description: "Search for assets in the Unity project using a query",
            parameterSchema: {
                query: z.string().describe("The search query"),
                limit: z.number().optional().describe("Maximum number of results to return (default: 100)")
            },
            annotations: {
                title: "Search Assets",
                readOnlyHint: true,
                destructiveHint: false,
                idempotentHint: true,
                openWorldHint: false
            }
        });
        // Add asset_findByName tool
        tools.set("asset_findByName", {
            description: "Find assets by name in the Unity project",
            parameterSchema: {
                name: z.string().describe("The name to search for"),
                exact: z.boolean().optional().describe("Whether to perform an exact match (default: false)"),
                limit: z.number().optional().describe("Maximum number of results to return (default: 100)")
            },
            annotations: {
                title: "Find Assets by Name",
                readOnlyHint: true,
                destructiveHint: false,
                idempotentHint: true,
                openWorldHint: false
            }
        });
        // Add asset_findByType tool
        tools.set("asset_findByType", {
            description: "Find assets by type in the Unity project",
            parameterSchema: {
                type: z.string().describe("The type name to search for (e.g., 'Texture2D', 'Material')"),
                limit: z.number().optional().describe("Maximum number of results to return (default: 100)")
            },
            annotations: {
                title: "Find Assets by Type",
                readOnlyHint: true,
                destructiveHint: false,
                idempotentHint: true,
                openWorldHint: false
            }
        });
        return tools;
    }
    /**
     * Searches for assets using a query.
     * @param parameters Parameters containing the search query.
     * @returns A Promise that resolves to a JSON object containing the search results.
     */
    async searchAssets(parameters) {
        try {
            // First ensure we have a valid connection to Unity
            await this.ensureUnityConnection();
            // Forward the request to Unity
            return await this.sendUnityRequest(`${this.commandPrefix}.search`, parameters);
        }
        catch (ex) {
            const errorMessage = ex instanceof Error ? ex.message : String(ex);
            console.error(`Error searching assets: ${errorMessage}`);
            return {
                success: false,
                error: errorMessage
            };
        }
    }
    /**
     * Finds assets by name.
     * @param parameters Parameters containing the name to search for.
     * @returns A Promise that resolves to a JSON object containing the search results.
     */
    async findByName(parameters) {
        try {
            // First ensure we have a valid connection to Unity
            await this.ensureUnityConnection();
            // Forward the request to Unity
            return await this.sendUnityRequest(`${this.commandPrefix}.findByName`, parameters);
        }
        catch (ex) {
            const errorMessage = ex instanceof Error ? ex.message : String(ex);
            console.error(`Error finding assets by name: ${errorMessage}`);
            return {
                success: false,
                error: errorMessage
            };
        }
    }
    /**
     * Finds assets by type.
     * @param parameters Parameters containing the type to search for.
     * @returns A Promise that resolves to a JSON object containing the search results.
     */
    async findByType(parameters) {
        try {
            // First ensure we have a valid connection to Unity
            await this.ensureUnityConnection();
            // Forward the request to Unity
            return await this.sendUnityRequest(`${this.commandPrefix}.findByType`, parameters);
        }
        catch (ex) {
            const errorMessage = ex instanceof Error ? ex.message : String(ex);
            console.error(`Error finding assets by type: ${errorMessage}`);
            return {
                success: false,
                error: errorMessage
            };
        }
    }
}
