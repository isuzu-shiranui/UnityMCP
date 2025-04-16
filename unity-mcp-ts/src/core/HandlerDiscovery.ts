import { promises as fs } from 'fs';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';
import { HandlerAdapter } from './HandlerAdapter.js';
import { ICommandHandler } from './interfaces/ICommandHandler.js';
import { UnityConnection } from './UnityConnection.js';

/**
 * Discovers and registers command handlers automatically.
 */
export class HandlerDiscovery {
    private adapter: HandlerAdapter;
    private unityConnection: UnityConnection;

    /**
     * Initializes a new instance of the HandlerDiscovery class.
     * @param adapter The handler adapter to register handlers with.
     * @param unityConnection The Unity connection to inject into handlers.
     */
    constructor(adapter: HandlerAdapter, unityConnection: UnityConnection) {
        this.adapter = adapter;
        this.unityConnection = unityConnection;
    }

    /**
     * Discovers and registers all handlers in the handlers directory.
     * @returns A promise that resolves to the number of handlers registered.
     */
    public async discoverAndRegisterHandlers(): Promise<number> {
        let count = 0;

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

                    // Look for classes implementing ICommandHandler
                    for (const exportName in module) {
                        const exportedValue = module[exportName];

                        // Check if it's a constructor that can be instantiated
                        if (typeof exportedValue === 'function' &&
                            exportedValue.prototype &&
                            typeof exportedValue.prototype.execute === 'function' &&
                            typeof exportedValue.prototype.commandPrefix === 'string') {
                            try {
                                // Create an instance
                                const instance = new exportedValue();

                                if (this.isCommandHandler(instance)) {
                                    // Initialize the handler with Unity connection
                                    instance.initialize(this.unityConnection);

                                    // Register the handler
                                    this.adapter.registerHandler(instance);
                                    console.error(`[INFO] Registered handler: ${instance.commandPrefix}`);
                                    count++;
                                }
                            } catch (err) {
                                console.error(`[ERROR] Failed to create instance from ${file}: ${err instanceof Error ? err.message : String(err)}`);
                            }
                        }
                    }
                } catch (err) {
                    console.error(`[ERROR] Failed to import ${file}: ${err instanceof Error ? err.message : String(err)}`);
                }
            }
        } catch (err) {
            console.error(`[ERROR] Error in handler discovery: ${err instanceof Error ? err.message : String(err)}`);
        }

        return count;
    }

    /**
     * Checks if an object implements ICommandHandler.
     * @param obj The object to check.
     * @returns True if the object implements ICommandHandler; otherwise, false.
     */
    private isCommandHandler(obj: any): obj is ICommandHandler {
        return obj &&
            typeof obj.commandPrefix === 'string' &&
            typeof obj.description === 'string' &&
            typeof obj.execute === 'function' &&
            typeof obj.initialize === 'function';
    }
}
