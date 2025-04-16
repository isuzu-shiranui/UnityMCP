import { IMcpToolDefinition } from "../core/interfaces/ICommandHandler.js";
import { JObject } from "../types/index.js";
import { z } from "zod";
import { BaseCommandHandler } from "../core/BaseCommandHandler.js";

/**
 * Command handler for executing Unity menu items.
 */
export abstract class MenuItemCommandHandler extends BaseCommandHandler {
    /**
     * Gets the command prefix for this handler.
     */
    public get commandPrefix(): string {
        return "menu";
    }

    /**
     * Gets the description of this command handler.
     */
    public get description(): string {
        return "Executes Unity Editor menu items";
    }

    /**
     * Executes the command with the given parameters.
     * @param action The action to execute.
     * @param parameters The parameters for the command.
     * @returns A Promise that resolves to a JSON object containing the execution result.
     */
    public async execute(action: string, parameters: JObject): Promise<JObject> {
        if (action.toLowerCase() !== "execute") {
            return {
                success: false,
                error: `Unknown action: ${action}. Only 'execute' is supported.`
            };
        }

        return this.executeMenuItem(action, parameters);
    }

    /**
     * Gets the tool definitions supported by this handler.
     * @returns A map of tool names to their definitions.
     */
    public getToolDefinitions(): Map<string, IMcpToolDefinition> {
        const tools = new Map<string, IMcpToolDefinition>();

        // Add menu_execute tool
        tools.set("menu_execute", {
            description: "Executes a Unity Editor menu item",
            parameterSchema: {
                menuItem: z.string().describe("The menu item path to execute")
            }
        });

        return tools;
    }

    /**
     * Executes a menu item by name.
     * @param action The action to execute.
     * @param parameters The parameters containing the menu item name.
     * @returns A Promise that resolves to a JSON object indicating success or failure.
     */
    private async executeMenuItem(action: string, parameters: JObject): Promise<JObject> {
        const menuItemPath = parameters.menuItem as string;
        if (!menuItemPath) {
            return {
                success: false,
                error: "MenuItem parameter is required"
            };
        }

        try {
            // First ensure we have a valid connection to Unity
            await this.ensureUnityConnection();

            // Forward the request to Unity using the base class helper
            const response = await this.sendUnityRequest(
                `${this.commandPrefix}.${action}`,
                { menuItem: menuItemPath }
            );

            return response;
        } catch (ex) {
            const errorMessage = ex instanceof Error ? ex.message : String(ex);
            console.error(`Error executing menu item '${menuItemPath}': ${errorMessage}`);

            return {
                success: false,
                error: errorMessage
            };
        }
    }
}
