// handlers/PackageResourceHandler.ts
import { BaseResourceHandler } from "../core/BaseResourceHandler.js";
import { JObject } from "../types/index.js";
import { URL } from "url";

/**
 * Resource handler for providing information about Unity packages.
 */
export class PackageResourceHandler extends BaseResourceHandler {
    /**
     * Gets the resource name for this handler.
     */
    public get resourceName(): string {
        return "packages";
    }

    /**
     * Gets the description of this resource handler.
     */
    public get description(): string {
        return "Provides information about Unity packages installed and available";
    }

    /**
     * Gets the URI template for this resource.
     */
    public get resourceUriTemplate(): string {
        return "unity://packages";
    }

    /**
     * Fetches package information from Unity.
     * @param uri The resource URI.
     * @param parameters Additional parameters from the URI.
     * @returns A Promise that resolves to package information.
     */
    protected async fetchResourceData(uri: URL, parameters?: JObject): Promise<JObject> {
        // Check if we should include registry packages
        const includeRegistry = parameters?.includeRegistry === true ||
            uri.searchParams.get("includeRegistry") === "true";

        // Send the request to Unity
        const response = await this.sendUnityRequest("packages.get", {
            includeRegistry
        });

        if (!response.success) {
            throw new Error(response.error as string || "Failed to fetch package information");
        }

        // Transform the data into a structured format
        const projectPackages = (response.projectPackages || []) as Array<any>;
        const registryPackages = (response.registryPackages || []) as Array<any>;

        return {
            projectPackages: projectPackages.map((pkg: any) => ({
                name: pkg.name,
                displayName: pkg.displayName,
                version: pkg.version,
                description: pkg.description,
                category: pkg.category,
                source: pkg.source,
                state: pkg.state,
                author: pkg.author
            })),
            registryPackages: includeRegistry ? registryPackages.map((pkg: any) => ({
                name: pkg.name,
                displayName: pkg.displayName,
                version: pkg.version,
                description: pkg.description,
                category: pkg.category,
                source: pkg.source,
                state: pkg.state,
                author: pkg.author
            })) : []
        };
    }
}
