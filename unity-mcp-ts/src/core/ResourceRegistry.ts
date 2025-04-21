import { IResourceHandler } from "./interfaces/IResourceHandler.js";

/**
 * Manages the registration and lookup of resource handlers.
 */
export class ResourceRegistry {
    private handlers: Map<string, IResourceHandler> = new Map();
    private enabledState: Map<string, boolean> = new Map();
    private uriMap: Map<string, string> = new Map();

    /**
     * Registers a resource handler.
     * @param handler The resource handler to register.
     * @param enabled Whether the handler is initially enabled.
     * @returns True if registration succeeded, false if already registered.
     */
    public registerHandler(handler: IResourceHandler, enabled: boolean = true): boolean {
        const resourceName = handler.resourceName;

        if (!resourceName) {
            return false;
        }

        if (this.handlers.has(resourceName)) {
            return false;
        }

        this.handlers.set(resourceName, handler);
        this.enabledState.set(resourceName, enabled);

        // Register URI mapping if available
        if (handler.resourceUriTemplate) {
            this.uriMap.set(handler.resourceUriTemplate, resourceName);
        }

        return true;
    }

    /**
     * Gets all registered resource handlers.
     * @returns A map of resource names to handlers.
     */
    public getAllHandlers(): Map<string, IResourceHandler> {
        return new Map(this.handlers);
    }

    /**
     * Checks if a handler is registered for the given resource name.
     * @param resourceName The resource name to check.
     * @returns True if a handler is registered, false otherwise.
     */
    public hasHandler(resourceName: string): boolean {
        return this.handlers.has(resourceName);
    }

    /**
     * Gets a handler for the given resource name.
     * @param resourceName The resource name to get the handler for.
     * @returns The handler if found, undefined otherwise.
     */
    public getHandler(resourceName: string): IResourceHandler | undefined {
        return this.handlers.get(resourceName);
    }

    /**
     * Gets a resource handler by URI template.
     * @param uriTemplate The URI template to look up.
     * @returns The handler if found by URI template, undefined otherwise.
     */
    public getHandlerByUriTemplate(uriTemplate: string): IResourceHandler | undefined {
        const resourceName = this.uriMap.get(uriTemplate);
        if (resourceName) {
            return this.handlers.get(resourceName);
        }
        return undefined;
    }

    /**
     * Sets whether a handler is enabled.
     * @param resourceName The resource name of the handler to enable/disable.
     * @param enabled Whether the handler should be enabled.
     * @returns True if the handler was found and its enabled state was set, false otherwise.
     */
    public setHandlerEnabled(resourceName: string, enabled: boolean): boolean {
        if (!this.handlers.has(resourceName)) {
            return false;
        }

        this.enabledState.set(resourceName, enabled);
        return true;
    }

    /**
     * Checks if a handler is enabled.
     * @param resourceName The resource name of the handler to check.
     * @returns True if the handler is found and enabled, false otherwise.
     */
    public isHandlerEnabled(resourceName: string): boolean {
        return this.handlers.has(resourceName) && this.enabledState.get(resourceName) === true;
    }
}
