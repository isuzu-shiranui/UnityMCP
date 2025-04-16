import { JObject } from "../../types/index.js";
import { z } from "zod";
import { UnityConnection } from "../UnityConnection.js";

/**
 * Defines the base structure for MCP tool parameters.
 */
export interface IMcpToolDefinition {
    /**
     * Gets the description of the tool.
     */
    description: string;

    /**
     * Gets the zod schema for the tool parameters.
     */
    parameterSchema: Record<string, z.ZodType<any>>;
}

/**
 * Interface for command handlers that process MCP requests.
 */
export interface ICommandHandler {
    /**
     * Gets the name of the command prefix this handler is responsible for.
     */
    readonly commandPrefix: string;

    /**
     * Gets a description of the command handler.
     */
    readonly description: string;

    /**
     * Initializes the handler with required dependencies.
     * @param unityConnection The Unity connection to use for communication with Unity.
     */
    initialize(unityConnection: UnityConnection): void;

    /**
     * Executes the command with the given parameters.
     * @param action The action to execute within this command prefix.
     * @param parameters The parameters for the command.
     * @returns A Promise that resolves to a JSON object containing the command result.
     * @throws Error when action or parameters are invalid.
     */
    execute(action: string, parameters: JObject): Promise<JObject>;

    /**
     * Gets the tool definitions supported by this handler.
     * @returns A map of tool names to their definitions, or null if not supported.
     */
    getToolDefinitions?(): Map<string, IMcpToolDefinition> | null;

    /**
     * Gets the resource definitions supported by this handler.
     * @returns A map of resource names to their definitions, or null if not supported.
     */
    getResourceDefinitions?(): Map<string, any> | null;

    /**
     * Gets the prompt definitions supported by this handler.
     * @returns A map of prompt names to their definitions, or null if not supported.
     */
    getPromptDefinitions?(): Map<string, any> | null;
}
