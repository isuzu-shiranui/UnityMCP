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
        try {
            // First ensure we have a valid connection to Unity
            await this.ensureUnityConnection();
            // Forward the request to Unity using the base class helper
            return await this.sendUnityRequest(`${this.commandPrefix}.${action}`, parameters);
        }
        catch (ex) {
            const errorMessage = ex instanceof Error ? ex.message : String(ex);
            console.error(`Error executing asset command '${action}': ${errorMessage}`);
            return {
                success: false,
                error: errorMessage
            };
        }
    }
    /**
     * Gets the tool definitions supported by this handler.
     * @returns A map of tool names to their definitions.
     */
    getToolDefinitions() {
        const tools = new Map();
        // General search tool
        tools.set("asset_search", {
            description: "Search for assets in the Unity project using a query",
            parameterSchema: {
                query: z.string().describe("The search query"),
                limit: z.number().optional().describe("Maximum number of results to return (default: 100)")
            }
        });
        // Search by name tool
        tools.set("asset_findbyname", {
            description: "Find assets by name in the Unity project",
            parameterSchema: {
                name: z.string().describe("The name to search for"),
                exact: z.boolean().optional().describe("Whether to perform an exact match (default: false)"),
                limit: z.number().optional().describe("Maximum number of results to return (default: 100)")
            }
        });
        // Search by type tool
        tools.set("asset_findbytype", {
            description: "Find assets by type in the Unity project",
            parameterSchema: {
                type: z.string().describe("The type name to search for (e.g., 'Texture2D', 'Material')"),
                limit: z.number().optional().describe("Maximum number of results to return (default: 100)")
            }
        });
        return tools;
    }
}
//# sourceMappingURL=AssetSearchCommandHandler.js.map