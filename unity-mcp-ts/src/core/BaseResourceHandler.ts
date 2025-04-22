import { IResourceHandler } from "./interfaces/IResourceHandler.js";
import { JObject } from "../types/index.js";
import { UnityConnection } from "./UnityConnection.js";
import { URL } from "url";
import {
    McpErrorCode
} from "../types/ErrorCodes.js";

/**
 * Base class for resource handlers providing common functionality.
 * Implements the Template Method pattern for resource fetching.
 */
export abstract class BaseResourceHandler implements IResourceHandler {
    /**
     * The UnityConnection instance used by this handler.
     */
    protected unityConnection: UnityConnection | null = null;

    /**
     * Gets the resource name for this handler.
     */
    public abstract get resourceName(): string;

    /**
     * Gets the description of this resource handler.
     */
    public abstract get description(): string;

    /**
     * Gets the URI template for this resource.
     */
    public abstract get resourceUriTemplate(): string;

    /**
     * Initializes the handler with required dependencies.
     * @param unityConnection The Unity connection to use for communication with Unity.
     */
    public initialize(unityConnection: UnityConnection): void {
        this.unityConnection = unityConnection;
    }

    /**
     * Fetches the resource data with the provided URI and parameters.
     * Template method that ensures Unity connection before fetching the resource.
     * @param uri The resource URI.
     * @param parameters Additional parameters extracted from the URI.
     * @returns A Promise that resolves to a resource result.
     */
    public async fetchResource(uri: URL, parameters?: JObject): Promise<{
        contents: Array<{
            uri: string;
            text: string;
            mimeType?: string;
        }>;
    }> {
        try {
            // First, ensure we have a valid connection to Unity
            await this.ensureUnityConnection();

            // Let the concrete implementation handle the actual resource fetching
            const result = await this.fetchResourceData(uri, parameters);

            return {
                contents: [{
                    uri: uri.href,
                    text: typeof result === 'string' ? result : JSON.stringify(result),
                    mimeType: 'application/json'
                }]
            };
        } catch (ex) {
            const errorMessage = ex instanceof Error ? ex.message : String(ex);
            console.error(`Error fetching resource ${this.resourceName}: ${errorMessage}`);

            const error = new Error(`Resource fetch failed: ${errorMessage}`);
            (error as any).code = McpErrorCode.ResourceNotFound;
            (error as any).resourceName = this.resourceName;
            (error as any).uri = uri.href;
            throw error;
        }
    }

    /**
     * Concrete implementation of resource fetching to be provided by subclasses.
     * @param uri The resource URI.
     * @param parameters Additional parameters extracted from the URI.
     * @returns A Promise that resolves to a JSON object or string containing the resource data.
     */
    protected abstract fetchResourceData(uri: URL, parameters?: JObject): Promise<JObject | string>;

    /**
     * Ensures there is a valid connection to Unity before fetching a resource.
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
            type: "resource",
            params: parameters
        });
    }
}
