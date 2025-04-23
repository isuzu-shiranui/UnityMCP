// handlers/AssemblyResourceHandler.ts
import { BaseResourceHandler } from "../core/BaseResourceHandler.js";
import { JObject } from "../types/index.js";
import { URL } from "url";

/**
 * Resource handler for providing information about loaded assemblies in Unity.
 */
export class AssemblyResourceHandler extends BaseResourceHandler {
    /**
     * Gets the resource name for this handler.
     */
    public get resourceName(): string {
        return "assemblies";
    }

    /**
     * Gets the description of this resource handler.
     */
    public get description(): string {
        return "Provides information about assemblies loaded in the Unity project";
    }

    /**
     * Gets the URI template for this resource.
     */
    public get resourceUriTemplate(): string {
        return "unity://assemblies";
    }

    /**
     * Fetches assembly information from Unity.
     * @param uri The resource URI.
     * @param parameters Additional parameters from the URI.
     * @returns A Promise that resolves to assembly information.
     */
    protected async fetchResourceData(uri: URL, parameters?: JObject): Promise<JObject> {
        // Parse query parameters
        const includeSystemAssemblies = parameters?.includeSystemAssemblies === true ||
            uri.searchParams.get("includeSystemAssemblies") === "true";
        const includeUnityAssemblies = parameters?.includeUnityAssemblies !== false &&
            uri.searchParams.get("includeUnityAssemblies") !== "false";
        const includeProjectAssemblies = parameters?.includeProjectAssemblies !== false &&
            uri.searchParams.get("includeProjectAssemblies") !== "false";

        // Send the request to Unity
        const response = await this.sendUnityRequest("assemblies.get", {
            includeSystemAssemblies,
            includeUnityAssemblies,
            includeProjectAssemblies
        });

        if (!response.success) {
            throw new Error(response.error as string || "Failed to fetch assembly information");
        }

        // Process and return the data
        return {
            assemblies: response.assemblies || [],
            count: response.count || 0
        };
    }
}
