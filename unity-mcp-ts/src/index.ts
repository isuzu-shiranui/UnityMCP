import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { HandlerAdapter } from "./core/HandlerAdapter.js";
import { HandlerDiscovery, HandlerType } from "./core/HandlerDiscovery.js";
import { UnityConnection } from "./core/UnityConnection.js";
import { CommandRegistry } from "./core/CommandRegistry.js";
import { ResourceRegistry } from "./core/ResourceRegistry.js";
import { PromptRegistry } from "./core/PromptRegistry.js";
import { registerUnityClientTools } from "./core/UnityClientHandler.js";

/**
 * Main entry point for the MCP server application.
 * This server acts as a bridge between LLMs and Unity clients.
 */
async function main() {
  try {
    // Initialize MCP server with the official SDK
    const mcpServer = new McpServer({
      name: "unity-mcp",
      version: "0.2.0"
    });

    // Initialize UnityConnection in server mode
    const unityConnection = UnityConnection.getInstance();

    // Configure from environment variables or defaults
    const host = process.env.MCP_HOST || '127.0.0.1';
    const port = parseInt(process.env.MCP_PORT || '27182', 10);
    unityConnection.configure(host, port);

    // Create registries
    const commandRegistry = new CommandRegistry();
    const resourceRegistry = new ResourceRegistry();
    const promptRegistry = new PromptRegistry();

    // Create handler adapter
    const handlerAdapter = new HandlerAdapter(mcpServer);

    // Create handler discovery with Unity connection and registries
    const handlerDiscovery = new HandlerDiscovery(
        handlerAdapter,
        unityConnection,
        commandRegistry,
        resourceRegistry,
        promptRegistry
    );

    // Start the unity connection server
    try {
      await unityConnection.start();
      console.error(`[INFO] Started MCP server on ${host}:${port}, waiting for Unity clients to connect`);
    } catch (err) {
      console.error(`[ERROR] Failed to start Unity connection server: ${err instanceof Error ? err.message : String(err)}`);
      console.error('[WARN] Continuing execution, but Unity functionality may be limited');
      // Continue execution - Unity clients will attempt to connect
    }

    // Register unity client management tools
    registerUnityClientTools(mcpServer);

    // Discover and register handlers
    const counts = await handlerDiscovery.discoverAndRegisterHandlers();
    console.error(`[INFO] Discovered and registered:
      Command Handlers: ${counts[HandlerType.COMMAND]}
      Resource Handlers: ${counts[HandlerType.RESOURCE]}
      Prompt Handlers: ${counts[HandlerType.PROMPT]}`);

    // Register connection status change events
    unityConnection.on('clientConnected', (client) => {
      console.error(`[INFO] Unity client connected: ${client.clientId}`);
    });

    unityConnection.on('clientDisconnected', (client) => {
      console.error(`[INFO] Unity client disconnected: ${client.clientId}`);
    });

    unityConnection.on('clientRegistered', (client) => {
      console.error(`[INFO] Unity client registered: ${client.clientId}`);
      console.error(`[INFO] Client info: ${JSON.stringify(client.info)}`);
    });

    unityConnection.on('activeClientChanged', (client) => {
      console.error(`[INFO] Active Unity client changed to: ${client.clientId}`);
    });

    // Create transport using standard I/O for MCP communication
    const transport = new StdioServerTransport();

    // Connect the server to the transport
    await mcpServer.connect(transport);

    console.error("[INFO] Unity MCP Server running on stdio");
  } catch (error) {
    console.error(`[ERROR] Failed to start MCP server: ${error instanceof Error ? error.message : String(error)}`);
    process.exit(1);
  }
}

// Shutdown handling
process.on("SIGINT", () => {
  console.error("[INFO] Shutting down...");
  const unityConnection = UnityConnection.getInstance();
  unityConnection.stop();
  process.exit(0);
});

process.on("SIGTERM", () => {
  console.error("[INFO] Shutting down...");
  const unityConnection = UnityConnection.getInstance();
  unityConnection.stop();
  process.exit(0);
});

// Handle uncaught exceptions to prevent crashing
process.on('uncaughtException', (error) => {
  const errorCode = 'code' in error ? `[Code: ${(error as any).code}] ` : '';
  console.error(`[ERROR] Uncaught exception: ${errorCode}${error.message}`);
  console.error(error.stack);
  // Do not exit the process
});

// Handle unhandled promise rejections to prevent crashing
process.on('unhandledRejection', (reason, promise) => {
  if (reason instanceof Error) {
    const errorCode = 'code' in reason ? `[Code: ${(reason as any).code}] ` : '';
    console.error(`[ERROR] Unhandled Promise rejection: ${errorCode}${reason.message}`);
    console.error(reason.stack);
  } else {
    console.error('[ERROR] Unhandled Promise rejection at:', promise);
    console.error('Reason:', reason);
  }
  // Do not exit the process
});

// Execute main function
main().catch(error => {
  console.error(`[FATAL] Unhandled error: ${error instanceof Error ? error.message : String(error)}`);
  process.exit(1);
});
