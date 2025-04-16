import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { HandlerAdapter } from "./core/HandlerAdapter.js";
import { HandlerDiscovery } from "./core/HandlerDiscovery.js";
import { UnityConnection } from "./core/UnityConnection.js";

// Main function
async function main() {
  try {
    // Initialize MCP server with the official SDK
    const mcpServer = new McpServer({
      name: "unity-mcp",
      version: "0.0.1"
    });

    // Initialize UnityConnection
    const unityConnection = UnityConnection.getInstance();

    // Configure from environment variables or defaults
    const unityHost = process.env.UNITY_HOST || '127.0.0.1';
    const unityPort = parseInt(process.env.UNITY_PORT || '27182', 10);
    unityConnection.configure(unityHost, unityPort);

    // Create handler adapter
    const handlerAdapter = new HandlerAdapter(mcpServer);

    // Create handler discovery with Unity connection
    const handlerDiscovery = new HandlerDiscovery(handlerAdapter, unityConnection);

    // Connect to Unity
    try {
      await unityConnection.connect();
      console.error(`[INFO] Connected to Unity at ${unityHost}:${unityPort}`);
    } catch (err) {
      console.error(`[WARN] Failed to connect to Unity: ${err instanceof Error ? err.message : String(err)}`);
      console.error('[INFO] Will continue without Unity connection. Some features may not work.');
    }

    // Discover and register handlers
    const count = await handlerDiscovery.discoverAndRegisterHandlers();
    console.error(`[INFO] Discovered and registered ${count} command handlers`);

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

// Execute main function
main().catch(error => {
  console.error(`[FATAL] Unhandled error: ${error instanceof Error ? error.message : String(error)}`);
  process.exit(1);
});
