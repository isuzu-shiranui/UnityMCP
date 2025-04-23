import { JObject } from "../../types/index.js";
import { URL } from "url";

/**
 * Defines the structure for resources that provide data from Unity.
 */
export interface IResourceHandler {
    /**
     * Gets the name of the resource this handler is responsible for.
     */
    readonly resourceName: string;

    /**
     * Gets a description of the resource.
     */
    readonly description: string;

    /**
     * Gets the URI template for this resource.
     */
    readonly resourceUriTemplate: string;

    /**
     * Initializes the handler with required dependencies.
     * @param unityConnection The Unity connection to use for communication with Unity.
     */
    initialize(unityConnection: any): void;

    /**
     * Fetches the resource data with the provided URI and parameters.
     * @param uri The resource URI.
     * @param parameters Additional parameters extracted from the URI.
     * @returns A Promise that resolves to a resource result.
     */
    fetchResource(uri: URL, parameters?: JObject): Promise<{
        contents: Array<{
            uri: string;
            text: string;
            mimeType?: string;
        }>;
    }>;
}
