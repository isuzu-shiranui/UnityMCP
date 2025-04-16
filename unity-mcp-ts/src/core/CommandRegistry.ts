import { ICommandHandler } from "./interfaces/ICommandHandler.js";

/**
 * Manages the registration and lookup of command handlers.
 */
export class CommandRegistry {
    private handlers: Map<string, ICommandHandler> = new Map();
    private enabledState: Map<string, boolean> = new Map();

    /**
     * Registers a command handler.
     * @param handler The command handler to register.
     * @param enabled Whether the handler is initially enabled.
     * @returns True if registration succeeded, false if already registered.
     */
    public registerHandler(handler: ICommandHandler, enabled: boolean = true): boolean {
        const prefix = handler.commandPrefix;

        if (!prefix) {
            // console.error(`Cannot register handler with empty command prefix`);
            return false;
        }

        if (this.handlers.has(prefix)) {
            // console.warn(`Handler for command '${prefix}' is already registered`);
            return false;
        }

        this.handlers.set(prefix, handler);
        this.enabledState.set(prefix, enabled);

        // console.info(`Registered handler for command: ${prefix} (Enabled: ${enabled})`);
        return true;
    }

    /**
     * Gets all registered handlers.
     * @returns A map of command prefixes to handlers.
     */
    public getAllHandlers(): Map<string, ICommandHandler> {
        return new Map(this.handlers);
    }

    /**
     * Checks if a handler is registered for the given command prefix.
     * @param prefix The command prefix to check.
     * @returns True if a handler is registered, false otherwise.
     */
    public hasHandler(prefix: string): boolean {
        return this.handlers.has(prefix);
    }

    /**
     * Gets a handler for the given command prefix.
     * @param prefix The command prefix to get the handler for.
     * @returns The handler if found, undefined otherwise.
     */
    public getHandler(prefix: string): ICommandHandler | undefined {
        return this.handlers.get(prefix);
    }

    /**
     * Sets whether a handler is enabled.
     * @param prefix The command prefix of the handler to enable/disable.
     * @param enabled Whether the handler should be enabled.
     * @returns True if the handler was found and its enabled state was set, false otherwise.
     */
    public setHandlerEnabled(prefix: string, enabled: boolean): boolean {
        if (!this.handlers.has(prefix)) {
            // console.warn(`Handler for command '${prefix}' not found`);
            return false;
        }

        this.enabledState.set(prefix, enabled);
        // console.info(`Handler for '${prefix}' is now ${enabled ? "enabled" : "disabled"}`);
        return true;
    }

    /**
     * Checks if a handler is enabled.
     * @param prefix The command prefix of the handler to check.
     * @returns True if the handler is found and enabled, false otherwise.
     */
    public isHandlerEnabled(prefix: string): boolean {
        return this.handlers.has(prefix) && this.enabledState.get(prefix) === true;
    }
}
