import { z } from "zod";

/**
 * Defines the structure for MCP prompt definitions.
 */
export interface IMcpPromptDefinition {
    /**
     * Gets the description of the prompt.
     */
    description: string;

    /**
     * Gets the template text or content of the prompt.
     */
    template: string;

    /**
     * Optional additional properties for the prompt.
     */
    additionalProperties?: Record<string, z.ZodType<any>>;
}

/**
 * Defines the structure for prompt providers.
 */
export interface IPromptHandler {
    /**
     * Gets the name of the prompt handler.
     */
    readonly promptName: string;

    /**
     * Gets a description of the prompt handler.
     */
    readonly description: string;

    /**
     * Initializes the handler with required dependencies.
     * @param unityConnection The Unity connection to use for communication with Unity.
     */
    initialize(unityConnection: any): void;

    /**
     * Gets the prompt definitions supported by this handler.
     * @returns A map of prompt names to their definitions, or null if not supported.
     */
    getPromptDefinitions(): Map<string, IMcpPromptDefinition> | null;
}
