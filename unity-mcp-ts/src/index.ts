import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { HandlerAdapter } from "./core/HandlerAdapter.js";
import { HandlerDiscovery, HandlerType } from "./core/HandlerDiscovery.js";
import { UnityConnection } from "./core/UnityConnection.js";
import { CommandRegistry } from "./core/CommandRegistry.js";
import { ResourceRegistry } from "./core/ResourceRegistry.js";
import { PromptRegistry } from "./core/PromptRegistry.js";

// Main function
async function main() {
  try {
    // Initialize MCP server with the official SDK
    const mcpServer = new McpServer({
      name: "unity-mcp",
      version: "0.1.0"
    });

    // Initialize UnityConnection
    const unityConnection = UnityConnection.getInstance();

    // Configure from environment variables or defaults
    const unityHost = process.env.UNITY_HOST || '127.0.0.1';
    const unityPort = parseInt(process.env.UNITY_PORT || '27182', 10);
    unityConnection.configure(unityHost, unityPort);

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

    // Try to connect to Unity but don't fail if connection fails
    try {
      await unityConnection.connect();
      console.error(`[INFO] Connected to Unity at ${unityHost}:${unityPort}`);
    } catch (err) {
      console.error(`[WARN] Initial connection to Unity failed: ${err instanceof Error ? err.message : String(err)}`);
      console.error('[INFO] Will continue without Unity connection and attempt to reconnect when needed.');
      // Continue execution, don't exit - the reconnection will be attempted when needed
    }

    // Discover and register handlers
    const counts = await handlerDiscovery.discoverAndRegisterHandlers();
    console.error(`[INFO] Discovered and registered:
      Command Handlers: ${counts[HandlerType.COMMAND]}
      Resource Handlers: ${counts[HandlerType.RESOURCE]}
      Prompt Handlers: ${counts[HandlerType.PROMPT]}`);

    // Create transport using standard I/O
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
  unityConnection.disconnect();
  process.exit(0);
});

process.on("SIGTERM", () => {
  console.error("[INFO] Shutting down...");
  const unityConnection = UnityConnection.getInstance();
  unityConnection.disconnect();
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
