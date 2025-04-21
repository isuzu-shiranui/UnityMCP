import { IMcpToolDefinition } from "../core/interfaces/ICommandHandler.js";
import { JObject } from "../types/index.js";
import { z } from "zod";
import { BaseCommandHandler } from "../core/BaseCommandHandler.js";

/**
 * Command handler for accessing and managing Unity Console logs.
 */
export abstract class ConsoleCommandHandler extends BaseCommandHandler {
    /**
     * Gets the command prefix for this handler.
     */
    public get commandPrefix(): string {
        return "console";
    }

    /**
     * Gets the description of this command handler.
     */
    public get description(): string {
        return "Access and manage Unity Console logs";
    }

    /**
     * Executes the command with the given parameters.
     * @param action The action to execute.
     * @param parameters The parameters for the command.
     * @returns A Promise that resolves to a JSON object containing the execution result.
     */
    public async execute(action: string, parameters: JObject): Promise<JObject> {
        switch (action.toLowerCase()) {
            case "getlogs":
                return this.getLogs(parameters);
            case "getcount":
                return this.getLogCount();
            case "clear":
                return this.clearLogs();
            case "setfilter":
                return this.setFilter(parameters);
            default:
                return {
                    success: false,
                    error: `Unknown action: ${action}. Supported actions: getLogs, getCount, clear, setFilter`
                };
        }
    }

    /**
     * Gets the tool definitions supported by this handler.
     * @returns A map of tool names to their definitions.
     */
    public getToolDefinitions(): Map<string, IMcpToolDefinition> {
        const tools = new Map<string, IMcpToolDefinition>();

        // Add console_getLogs tool
        tools.set("console_getLogs", {
            description: "Gets logs from the Unity Console",
            parameterSchema: {
                startRow: z.number().optional().describe("The starting row index (defaults to 0)"),
                count: z.number().optional().describe("The maximum number of logs to retrieve (defaults to 100)")
            }
        });

        // Add console_getCount tool
        tools.set("console_getCount", {
            description: "Gets the count of logs by type",
            parameterSchema: {}
        });

        // Add console_clear tool
        tools.set("console_clear", {
            description: "Clears all logs from the console",
            parameterSchema: {}
        });

        // Add console_setFilter tool
        tools.set("console_setFilter", {
            description: "Sets a filter on the console logs",
            parameterSchema: {
                filter: z.string().describe("The filter text to apply")
            }
        });

        return tools;
    }

    /**
     * Gets logs from the Unity Console.
     * @param parameters Optional parameters for filtering logs.
     * @returns A Promise that resolves to a JSON object containing the logs.
     */
    private async getLogs(parameters: JObject): Promise<JObject> {
        try {
            // First ensure we have a valid connection to Unity
            await this.ensureUnityConnection();

            // Forward the request to Unity
            return await this.sendUnityRequest(
                `${this.commandPrefix}.getLogs`,
                parameters
            );
        } catch (ex) {
            const errorMessage = ex instanceof Error ? ex.message : String(ex);
            console.error(`Error getting logs: ${errorMessage}`);

            return {
                success: false,
                error: errorMessage
            };
        }
    }

    /**
     * Gets the count of logs by type.
     * @returns A Promise that resolves to a JSON object containing the counts.
     */
    private async getLogCount(): Promise<JObject> {
        try {
            // First ensure we have a valid connection to Unity
            await this.ensureUnityConnection();

            // Forward the request to Unity
            return await this.sendUnityRequest(
                `${this.commandPrefix}.getCount`,
                {}
            );
        } catch (ex) {
            const errorMessage = ex instanceof Error ? ex.message : String(ex);
            console.error(`Error getting log counts: ${errorMessage}`);

            return {
                success: false,
                error: errorMessage
            };
        }
    }

    /**
     * Clears all logs from the console.
     * @returns A Promise that resolves to a JSON object indicating success or failure.
     */
    private async clearLogs(): Promise<JObject> {
        try {
            // First ensure we have a valid connection to Unity
            await this.ensureUnityConnection();

            // Forward the request to Unity
            return await this.sendUnityRequest(
                `${this.commandPrefix}.clear`,
                {}
            );
        } catch (ex) {
            const errorMessage = ex instanceof Error ? ex.message : String(ex);
            console.error(`Error clearing logs: ${errorMessage}`);

            return {
                success: false,
                error: errorMessage
            };
        }
    }

    /**
     * Sets a filter on the console logs.
     * @param parameters Parameters containing the filter text.
     * @returns A Promise that resolves to a JSON object indicating success or failure.
     */
    private async setFilter(parameters: JObject): Promise<JObject> {
        try {
            // First ensure we have a valid connection to Unity
            await this.ensureUnityConnection();

            // Forward the request to Unity
            return await this.sendUnityRequest(
                `${this.commandPrefix}.setFilter`,
                parameters
            );
        } catch (ex) {
            const errorMessage = ex instanceof Error ? ex.message : String(ex);
            console.error(`Error setting filter: ${errorMessage}`);

            return {
                success: false,
                error: errorMessage
            };
        }
    }
}
