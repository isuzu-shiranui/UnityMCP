import {McpServer, ResourceTemplate} from "@modelcontextprotocol/sdk/server/mcp.js";
import { ICommandHandler } from "./interfaces/ICommandHandler.js";

/**
 * Adapts command handlers to MCP SDK tools.
 */
export class HandlerAdapter {
    private server: McpServer;

    /**
     * Initializes a new instance of the HandlerAdapter class.
     * @param server The MCP server to register tools with.
     */
    constructor(server: McpServer) {
        this.server = server;
    }

    /**
     * Registers a command handler with the MCP server.
     * @param handler The command handler to register.
     */
    public registerHandler(handler: ICommandHandler): void {
        // Register tools if supported
        this.registerHandlerTools(handler);

        // Register resources if supported
        this.registerHandlerResources(handler);

        // Register prompts if supported
        this.registerHandlerPrompts(handler);
    }

    /**
     * Registers tools provided by the handler.
     * @param handler The command handler.
     */
    private registerHandlerTools(handler: ICommandHandler): void {
        // Skip if the handler doesn't support tools
        if (!handler.getToolDefinitions) {
            return;
        }

        const toolDefinitions = handler.getToolDefinitions();
        if (!toolDefinitions) {
            return;
        }

        // Register each tool definition
        for (const [toolName, definition] of toolDefinitions.entries()) {
            this.server.tool(
                toolName,
                definition.description,
                definition.parameterSchema,
                async (params) => {
                    try {
                        // Extract the action from the tool name (e.g., "menu_execute" -> "execute")
                        const action = toolName.split('_')[1] || 'execute';

                        // Execute the command and await the result
                        const result = await handler.execute(action, params);

                        // Convert the result to a text response
                        return {
                            content: [{
                                type: "text",
                                text: JSON.stringify(result)
                            }]
                        };
                    } catch (error) {
                        const errorMessage = error instanceof Error ? error.message : String(error);
                        throw new Error(`Error executing tool ${toolName}: ${errorMessage}`);
                    }
                }
            );

            console.error(`[INFO] Registered tool: ${toolName}`);
        }
    }

    /**
     * Registers resources provided by the handler.
     * @param handler The command handler.
     */
    private registerHandlerResources(handler: ICommandHandler): void {
        // Skip if the handler doesn't support resources
        if (!handler.getResourceDefinitions) {
            return;
        }

        const resourceDefinitions = handler.getResourceDefinitions();
        if (!resourceDefinitions) {
            return;
        }

        // Register each resource definition
        for (const [resourceName, definition] of resourceDefinitions.entries()) {
            if (definition.template) {
                // Register dynamic resource with template
                this.server.resource(
                    resourceName,
                    new ResourceTemplate(definition.uriPattern, definition.parameters || {}),
                    async (uri, params) => {
                        try {
                            // Extract the action based on resource type
                            const action = definition.action || 'get';

                            // Execute the command with parameters
                            const result = await handler.execute(action, params);

                            return {
                                contents: [{
                                    uri: uri.href,
                                    text: JSON.stringify(result)
                                }]
                            };
                        } catch (error) {
                            const errorMessage = error instanceof Error ? error.message : String(error);
                            throw new Error(`Error executing resource ${resourceName}: ${errorMessage}`);
                        }
                    }
                );
            } else {
                // Register static resource
                this.server.resource(
                    resourceName,
                    definition.uriPattern,
                    async (uri) => {
                        try {
                            // Execute the command with minimal parameters
                            const result = await handler.execute('get', { uri: uri.href });

                            return {
                                contents: [{
                                    uri: uri.href,
                                    text: JSON.stringify(result)
                                }]
                            };
                        } catch (error) {
                            const errorMessage = error instanceof Error ? error.message : String(error);
                            throw new Error(`Error executing resource ${resourceName}: ${errorMessage}`);
                        }
                    }
                );
            }

            console.error(`[INFO] Registered resource: ${resourceName}`);
        }
    }

    /**
     * Registers prompts provided by the handler.
     * @param handler The command handler.
     */
    private registerHandlerPrompts(handler: ICommandHandler): void {
        // Skip if the handler doesn't support prompts
        if (!handler.getPromptDefinitions) {
            return;
        }

        const promptDefinitions = handler.getPromptDefinitions();
        if (!promptDefinitions) {
            return;
        }

        // Register each prompt definition
        for (const [promptName, definition] of promptDefinitions.entries()) {
            this.server.prompt(
                promptName,
                definition.description,
                definition.additionalProperties || {},
                (params) => {
                    return {
                        messages: [
                            {
                                role: "user",
                                content: {
                                    type: "text",
                                    text: this.processTemplateWithParams(definition.template, params)
                                }
                            }
                        ]
                    };
                }
            );

            console.error(`[INFO] Registered prompt: ${promptName}`);
        }
    }

    /**
     * Processes a template string with parameters.
     * @param template The template string with placeholders.
     * @param params The parameters to inject.
     * @returns The processed template.
     */
    private processTemplateWithParams(template: string, params: Record<string, any>): string {
        let result = template;

        // Replace placeholders like {paramName} with their values
        if (params) {
            Object.entries(params).forEach(([key, value]) => {
                const regex = new RegExp(`\\{${key}\\}`, 'g');
                result = result.replace(regex, String(value));
            });
        }

        return result;
    }
}
