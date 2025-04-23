// core/HandlerDiscovery.ts
import { promises as fs } from 'fs';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';
import { HandlerAdapter } from './HandlerAdapter.js';
import { CommandRegistry } from './CommandRegistry.js';
import { ResourceRegistry } from './ResourceRegistry.js';
import { PromptRegistry } from './PromptRegistry.js';
import { ICommandHandler } from './interfaces/ICommandHandler.js';
import { IResourceHandler } from './interfaces/IResourceHandler.js';
import { IPromptHandler } from './interfaces/IPromptHandler.js';
import { UnityConnection } from './UnityConnection.js';

/**
 * Handler types supported by the discovery system.
 */
export enum HandlerType {
    COMMAND = 'command',
    RESOURCE = 'resource',
    PROMPT = 'prompt'
}

/**
 * Discovers and registers handlers automatically.
 */
export class HandlerDiscovery {
    private adapter: HandlerAdapter;
    private unityConnection: UnityConnection;
    private commandRegistry: CommandRegistry;
    private resourceRegistry: ResourceRegistry;
    private promptRegistry: PromptRegistry;

    /**
     * Initializes a new instance of the HandlerDiscovery class.
     * @param adapter The handler adapter to register handlers with.
     * @param unityConnection The Unity connection to inject into handlers.
     * @param commandRegistry The command registry to register command handlers with.
     * @param resourceRegistry The resource registry to register resource handlers with.
     * @param promptRegistry The prompt registry to register prompt handlers with.
     */
    constructor(
        adapter: HandlerAdapter,
        unityConnection: UnityConnection,
        commandRegistry: CommandRegistry,
        resourceRegistry: ResourceRegistry,
        promptRegistry: PromptRegistry
    ) {
        this.adapter = adapter;
        this.unityConnection = unityConnection;
        this.commandRegistry = commandRegistry;
        this.resourceRegistry = resourceRegistry;
        this.promptRegistry = promptRegistry;
    }

    /**
     * Discovers and registers all handlers in the handlers directory.
     * @returns A promise that resolves to the number of handlers registered by type.
     */
    public async discoverAndRegisterHandlers(): Promise<Record<HandlerType, number>> {
        const counts = {
            [HandlerType.COMMAND]: 0,
            [HandlerType.RESOURCE]: 0,
            [HandlerType.PROMPT]: 0
        };

        try {
            // Get the current file's directory
            const __filename = fileURLToPath(import.meta.url);
            const __dirname = dirname(__filename);

            // Path to the handlers directory
            const handlersDir = join(__dirname, '../handlers');

            // Get all files in the directory
            const files = await fs.readdir(handlersDir);

            // Filter for .js files (assuming TypeScript compiled to JS)
            const jsFiles = files.filter(file => file.endsWith('.js'));

            for (const file of jsFiles) {
                try {
                    // Dynamic import for ES modules
                    const module = await import(`../handlers/${file}`);

                    // Look for exported classes
                    for (const exportName in module) {
                        const exportedValue = module[exportName];

                        // Skip non-constructor exports
                        if (typeof exportedValue !== 'function' || !exportedValue.prototype) {
                            continue;
                        }

                        try {
                            // Create an instance of the class
                            const instance = new exportedValue();

                            // Initialize with Unity connection
                            instance.initialize(this.unityConnection);

                            // Try to register as each handler type
                            let registered = false;

                            // Try as command handler
                            if (this.isCommandHandler(instance)) {
                                if (this.commandRegistry.registerHandler(instance)) {
                                    this.adapter.registerCommandHandler(instance);
                                    counts[HandlerType.COMMAND]++;
                                    console.error(`[INFO] Registered command handler: ${instance.commandPrefix}`);
                                    registered = true;
                                }
                            }

                            // Try as resource handler
                            if (this.isResourceHandler(instance)) {
                                if (this.resourceRegistry.registerHandler(instance)) {
                                    this.adapter.registerResourceHandler(instance);
                                    counts[HandlerType.RESOURCE]++;
                                    console.error(`[INFO] Registered resource handler: ${instance.resourceName}`);
                                    registered = true;
                                }
                            }

                            // Try as prompt handler
                            if (this.isPromptHandler(instance)) {
                                if (this.promptRegistry.registerHandler(instance)) {
                                    this.adapter.registerPromptHandler(instance);
                                    counts[HandlerType.PROMPT]++;
                                    console.error(`[INFO] Registered prompt handler: ${instance.promptName}`);
                                    registered = true;
                                }
                            }

                            if (!registered) {
                                console.error(`[WARN] Class ${exportName} in ${file} does not implement any known handler interface`);
                            }
                        } catch (err) {
                            console.error(`[ERROR] Failed to instantiate ${exportName} from ${file}: ${err instanceof Error ? err.message : String(err)}`);
                        }
                    }
                } catch (err) {
                    console.error(`[ERROR] Failed to import ${file}: ${err instanceof Error ? err.message : String(err)}`);
                }
            }
        } catch (err) {
            console.error(`[ERROR] Error in handler discovery: ${err instanceof Error ? err.message : String(err)}`);
        }

        return counts;
    }

    /**
     * Checks if an object implements ICommandHandler.
     * @param obj The object to check.
     * @returns True if the object implements ICommandHandler, false otherwise.
     */
    private isCommandHandler(obj: any): obj is ICommandHandler {
        return obj &&
            typeof obj.commandPrefix === 'string' &&
            typeof obj.description === 'string' &&
            typeof obj.execute === 'function' &&
            typeof obj.initialize === 'function';
    }

    /**
     * Checks if an object implements IResourceHandler.
     * @param obj The object to check.
     * @returns True if the object implements IResourceHandler, false otherwise.
     */
    private isResourceHandler(obj: any): obj is IResourceHandler {
        return obj &&
            typeof obj.resourceName === 'string' &&
            typeof obj.description === 'string' &&
            typeof obj.resourceUriTemplate === 'string' &&
            typeof obj.fetchResource === 'function' &&
            typeof obj.initialize === 'function';
    }

    /**
     * Checks if an object implements IPromptHandler.
     * @param obj The object to check.
     * @returns True if the object implements IPromptHandler, false otherwise.
     */
    private isPromptHandler(obj: any): obj is IPromptHandler {
        return obj &&
            typeof obj.promptName === 'string' &&
            typeof obj.description === 'string' &&
            typeof obj.getPromptDefinitions === 'function' &&
            typeof obj.initialize === 'function';
    }
}
