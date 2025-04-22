import { ICommandHandler, IMcpToolDefinition } from "./interfaces/ICommandHandler.js";
import { JObject } from "../types/index.js";
import { UnityConnection } from "./UnityConnection.js";

/**
 * Base class for command handlers providing common functionality.
 * Implements the Template Method pattern for command execution.
 */
export abstract class BaseCommandHandler implements ICommandHandler {
    /**
     * The UnityConnection instance used by this handler.
     */
    protected unityConnection: UnityConnection | null = null;

    /**
     * Gets the command prefix for this handler.
     */
    public abstract get commandPrefix(): string;

    /**
     * Gets the description of this command handler.
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
     * Executes the command with the given parameters.
     * Template method that ensures Unity connection before executing the command.
     * @param action The action to execute.
     * @param parameters The parameters for the command.
     * @returns A Promise that resolves to a JSON object containing the execution result.
     */
    public async execute(action: string, parameters: JObject): Promise<JObject> {
        try {
            // First, ensure we have a valid connection to Unity
            await this.ensureUnityConnection();

            // Let the concrete implementation handle the actual command execution
            return await this.executeCommand(action, parameters);
        } catch (ex) {
            const errorMessage = ex instanceof Error ? ex.message : String(ex);
            console.error(`Error executing ${this.commandPrefix}.${action}: ${errorMessage}`);

            return {
                success: false,
                error: errorMessage,
                errorDetails: {
                    command: `${this.commandPrefix}.${action}`,
                    timestamp: new Date().toISOString(),
                    type: ex instanceof Error ? ex.name || "Error" : "UnknownError"
                }
            };
        }
    }

    /**
     * Concrete implementation of command execution to be provided by subclasses.
     * @param action The action to execute.
     * @param parameters The parameters for the command.
     * @returns A Promise that resolves to a JSON object containing the execution result.
     */
    protected abstract executeCommand(action: string, parameters: JObject): Promise<JObject>;

    /**
     * Gets the tool definitions supported by this handler.
     * @returns A map of tool names to their definitions, or null if not supported.
     */
    public abstract getToolDefinitions?(): Map<string, IMcpToolDefinition> | null;

    /**
     * Ensures there is a valid connection to Unity before executing a command.
     * Attempts to reconnect if not connected.
     * @returns A Promise that resolves when connected or rejects with an error.
     * @throws Error if the connection cannot be established.
     */
    protected async ensureUnityConnection(): Promise<void> {
        if (!this.unityConnection) {
            throw new Error("Unity connection not initialized");
        }

        if (!this.unityConnection.isUnityConnected()) {
            try {
                await this.unityConnection.reconnect();
            } catch (err) {
                throw new Error(`Failed to connect to Unity: ${err instanceof Error ? err.message : String(err)}`);
            }
        }
    }

    /**
     * Sends a request to Unity, ensuring connection first.
     * @param command The command string (prefix.action).
     * @param parameters The parameters for the command.
     * @returns A Promise that resolves to the response from Unity.
     * @throws Error if the request fails or connection cannot be established.
     */
    protected async sendUnityRequest(command: string, parameters: JObject): Promise<JObject> {
        await this.ensureUnityConnection();

        // Explicit non-null assertion since we've checked in ensureUnityConnection
        return this.unityConnection!.sendRequest({
            command,
            params: parameters
        });
    }
}
