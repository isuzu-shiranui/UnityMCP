import { z } from "zod";
import { BaseCommandHandler } from "../core/BaseCommandHandler.js";
/**
 * Command handler for executing C# code in the Unity editor.
 */
export class CodeExecutionCommandHandler extends BaseCommandHandler {
    /**
     * Gets the command prefix for this handler.
     */
    get commandPrefix() {
        return "code";
    }
    /**
     * Gets the description of this command handler.
     */
    get description() {
        return "Execute C# code in the Unity editor";
    }
    /**
     * Executes the command with the given parameters.
     * @param action The action to execute.
     * @param parameters The parameters for the command.
     * @returns A Promise that resolves to a JSON object containing the execution result.
     */
    async execute(action, parameters) {
        switch (action.toLowerCase()) {
            case "execute":
                return this.executeCode(parameters);
            default:
                return {
                    success: false,
                    error: `Unknown action: ${action}. Supported actions: execute`
                };
        }
    }
    /**
     * Gets the tool definitions supported by this handler.
     * @returns A map of tool names to their definitions.
     */
    getToolDefinitions() {
        const tools = new Map();
        // Add code_execute tool
        tools.set("code_execute", {
            description: "Execute C# code in the Unity editor",
            parameterSchema: {
                code: z.string().describe("Write direct executable C# code without using statements or class/method wrappers, valid inside a method body, with return statements for value output, and direct access to Unity and .NET APIs.")
            },
            annotations: {
                title: "Execute Code",
                readOnlyHint: false,
                destructiveHint: true,
                idempotentHint: true,
                openWorldHint: false
            }
        });
        return tools;
    }
    /**
     * Executes C# code in the Unity editor.
     * @param parameters Parameters containing the code to execute.
     * @returns A Promise that resolves to a JSON object containing the execution result.
     */
    async executeCode(parameters) {
        try {
            // Validate required parameters
            const code = parameters.code;
            if (!code) {
                return {
                    success: false,
                    error: "The 'code' parameter is required and cannot be empty."
                };
            }
            // First ensure we have a valid connection to Unity
            await this.ensureUnityConnection();
            // Forward the request to Unity
            return await this.sendUnityRequest(`${this.commandPrefix}.execute`, parameters);
        }
        catch (ex) {
            const errorMessage = ex instanceof Error ? ex.message : String(ex);
            console.error(`Error executing code: ${errorMessage}`);
            return {
                success: false,
                error: errorMessage
            };
        }
    }
}
