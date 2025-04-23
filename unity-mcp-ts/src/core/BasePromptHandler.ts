import { IPromptHandler } from "./interfaces/IPromptHandler.js";
import { IMcpPromptDefinition } from "./interfaces/IPromptHandler.js";
import { UnityConnection } from "./UnityConnection.js";

/**
 * Base class for prompt handlers providing common functionality.
 */
export abstract class BasePromptHandler implements IPromptHandler {
    /**
     * The UnityConnection instance used by this handler.
     */
    protected unityConnection: UnityConnection | null = null;

    /**
     * Gets the prompt name for this handler.
     */
    public abstract get promptName(): string;

    /**
     * Gets the description of this prompt handler.
     */
    public abstract get description(): string;

    /**
     * Initializes the handler with required dependencies.
     * @param unityConnection The Unity connection to use for communication with Unity.
     */
    public initialize(unityConnection: UnityConnection): void {
        this.unityConnection = unityConnection;
    }

    /**
     * Gets the prompt definitions supported by this handler.
     * @returns A map of prompt names to their definitions, or null if not supported.
     */
    public abstract getPromptDefinitions(): Map<string, IMcpPromptDefinition> | null;

    /**
     * Ensures there is a valid connection to Unity if needed.
     * @returns A Promise that resolves when connected or rejects with an error.
     * @throws Error if the connection cannot be established.
     */
    protected async ensureUnityConnection(): Promise<void> {
        if (!this.unityConnection) {
            throw new Error("Unity connection not initialized");
        }

        if (!this.unityConnection.isUnityConnected()) {
            try {
                // In server mode, we just ensure a connection is available
                await this.unityConnection.ensureConnected();
            } catch (err) {
                throw new Error(`Failed to connect to Unity: ${err instanceof Error ? err.message : String(err)}`);
            }
        }
    }

    /**
     * Sends a request to Unity if needed, ensuring connection first.
     * @param command The command string (prefix.action).
     * @param parameters The parameters for the command.
     * @returns A Promise that resolves to the response from Unity.
     * @throws Error if the request fails or connection cannot be established.
     */
    protected async sendUnityRequest(command: string, parameters: any): Promise<any> {
        await this.ensureUnityConnection();

        // Explicit non-null assertion since we've checked in ensureUnityConnection
        return this.unityConnection!.sendRequest({
            command,
            params: parameters
        });
    }

    /**
     * Applies parameter values to a template string.
     * @param template The template string with {paramName} placeholders.
     * @param parameters The parameter values to apply.
     * @returns The template with parameter values applied.
     */
    protected applyTemplateParameters(template: string, parameters: Record<string, any>): string {
        let result = template;

        if (parameters) {
            for (const [key, value] of Object.entries(parameters)) {
                const placeholder = `{${key}}`;
                result = result.replace(new RegExp(placeholder, 'g'), String(value));
            }
        }

        return result;
    }
}
