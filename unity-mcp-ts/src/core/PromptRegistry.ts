import { IPromptHandler } from "./interfaces/IPromptHandler.js";

/**
 * Manages the registration and lookup of prompt handlers.
 */
export class PromptRegistry {
    private handlers: Map<string, IPromptHandler> = new Map();
    private enabledState: Map<string, boolean> = new Map();

    /**
     * Registers a prompt handler.
     * @param handler The prompt handler to register.
     * @param enabled Whether the handler is initially enabled.
     * @returns True if registration succeeded, false if already registered.
     */
    public registerHandler(handler: IPromptHandler, enabled: boolean = true): boolean {
        const promptName = handler.promptName;

        if (!promptName) {
            return false;
        }

        if (this.handlers.has(promptName)) {
            return false;
        }

        this.handlers.set(promptName, handler);
        this.enabledState.set(promptName, enabled);
        return true;
    }

    /**
     * Gets all registered prompt handlers.
     * @returns A map of prompt names to handlers.
     */
    public getAllHandlers(): Map<string, IPromptHandler> {
        return new Map(this.handlers);
    }

    /**
     * Checks if a handler is registered for the given prompt name.
     * @param promptName The prompt name to check.
     * @returns True if a handler is registered, false otherwise.
     */
    public hasHandler(promptName: string): boolean {
        return this.handlers.has(promptName);
    }

    /**
     * Gets a handler for the given prompt name.
     * @param promptName The prompt name to get the handler for.
     * @returns The handler if found, undefined otherwise.
     */
    public getHandler(promptName: string): IPromptHandler | undefined {
        return this.handlers.get(promptName);
    }

    /**
     * Sets whether a handler is enabled.
     * @param promptName The prompt name of the handler to enable/disable.
     * @param enabled Whether the handler should be enabled.
     * @returns True if the handler was found and its enabled state was set, false otherwise.
     */
    public setHandlerEnabled(promptName: string, enabled: boolean): boolean {
        if (!this.handlers.has(promptName)) {
            return false;
        }

        this.enabledState.set(promptName, enabled);
        return true;
    }

    /**
     * Checks if a handler is enabled.
     * @param promptName The prompt name of the handler to check.
     * @returns True if the handler is found and enabled, false otherwise.
     */
    public isHandlerEnabled(promptName: string): boolean {
        return this.handlers.has(promptName) && this.enabledState.get(promptName) === true;
    }

    /**
     * Gets all prompt definitions from all registered handlers.
     * @returns A map of prompt names to their definitions.
     */
    public getAllPromptDefinitions(): Map<string, any> {
        const definitions = new Map<string, any>();

        for (const [handlerName, handler] of this.handlers.entries()) {
            if (!this.isHandlerEnabled(handlerName)) {
                continue;
            }

            const handlerDefinitions = handler.getPromptDefinitions();
            if (handlerDefinitions) {
                for (const [promptName, definition] of handlerDefinitions.entries()) {
                    definitions.set(promptName, definition);
                }
            }
        }

        return definitions;
    }
}
