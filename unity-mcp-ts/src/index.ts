import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { HandlerAdapter } from "./core/HandlerAdapter.js";
import { HandlerDiscovery, HandlerType } from "./core/HandlerDiscovery.js";
import { UnityConnection } from "./core/UnityConnection.js";
import { CommandRegistry } from "./core/CommandRegistry.js";
import { ResourceRegistry } from "./core/ResourceRegistry.js";
import { PromptRegistry } from "./core/PromptRegistry.js";
import { ProjectRegistry } from "./core/ProjectRegistry.js";
import { ProjectApi } from "./core/ProjectApi.js";
import { registerUnityClientTools } from "./core/UnityClientHandler.js";
import { UnhandledErrorTracker } from "./core/TaskResilience.js";

/**
 * Main entry point for the MCP server application.
 * Acts as a bridge between LLMs (via MCP/stdio) and Unity Editor instances (via HTTP).
 */
async function main() {
  try {
    // Initialize MCP server with the official SDK
    const mcpServer = new McpServer({
      name: "unity-mcp",
      version: "2.1.1"
    });

    // Initialize UnityConnection (HTTP client)
    const unityConnection = UnityConnection.getInstance();

    // Create and start ProjectRegistry (UDP listener + health polling)
    const registry = new ProjectRegistry(unityConnection, {
      udpPort: parseInt(process.env.MCP_UDP_PORT || '27183', 10),
      healthPollIntervalMs: parseInt(process.env.MCP_HEALTH_INTERVAL || '10000', 10),
      staleThresholdMs: 90000,
      unhealthyCooldownMs: parseInt(process.env.MCP_UNHEALTHY_COOLDOWN_MS || '60000', 10),
    });
    registry.start();

    // Start ProjectApi — port 27180 preferred, 27180-27189 first-come fallback.
    const preferredApiPort = parseInt(process.env.MCP_PROJECT_API_PORT || process.env.MCP_API_PORT || '27180', 10);
    const apiPortRangeEnd = parseInt(process.env.MCP_PROJECT_API_PORT_END || '27189', 10);
    const projectApi = new ProjectApi(registry, preferredApiPort, apiPortRangeEnd);
    try {
      await projectApi.start();
      const actual = projectApi.getPort();
      console.error(`[INFO] Project API available at http://127.0.0.1:${actual}/projects`);
      console.error(`[INFO] Proxy endpoint available at http://127.0.0.1:${actual}/proxy/<name>/<subpath>`);
    } catch (err) {
      console.error(`[WARN] Could not start Project API in [${preferredApiPort}-${apiPortRangeEnd}]: ${err instanceof Error ? err.message : String(err)}`);
      console.error('[WARN] CLI discovery via /projects will not be available');
    }

    // Log discovery events
    registry.on('instanceDiscovered', (instance) => {
      console.error(`[INFO] Discovered Unity instance: ${instance.projectName} on :${instance.port}`);
    });

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

    // Register unity client management tools
    registerUnityClientTools(mcpServer);

    // Discover and register handlers
    const counts = await handlerDiscovery.discoverAndRegisterHandlers();
    console.error(`[INFO] Discovered and registered:
      Command Handlers: ${counts[HandlerType.COMMAND]}
      Resource Handlers: ${counts[HandlerType.RESOURCE]}
      Prompt Handlers: ${counts[HandlerType.PROMPT]}`);

    // Register connection events
    unityConnection.on('clientRegistered', (client) => {
      console.error(`[INFO] Unity client registered: ${client.clientId}`);
    });

    unityConnection.on('clientDisconnected', (client) => {
      console.error(`[INFO] Unity client disconnected: ${client.clientId}`);
    });

    unityConnection.on('activeClientChanged', (client) => {
      console.error(`[INFO] Active Unity client changed to: ${client.clientId}`);
    });

    // Create transport using standard I/O for MCP communication
    const transport = new StdioServerTransport();

    // Connect the server to the transport
    await mcpServer.connect(transport);

    console.error("[INFO] Unity MCP Server running on stdio (HTTP mode)");
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

// The process intentionally survives unhandled errors (killing it would drop
// the MCP stdio connection), but repeated errors indicate a degraded server.
// Track them in a sliding window and warn so the operator can restart (#9).
const unhandledErrorTracker = new UnhandledErrorTracker();

// Handle uncaught exceptions to prevent crashing
process.on('uncaughtException', (error) => {
  const errorCode = 'code' in error ? `[Code: ${(error as any).code}] ` : '';
  console.error(`[ERROR] Uncaught exception: ${errorCode}${error.message}`);
  console.error(error.stack);
  unhandledErrorTracker.record();
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
  unhandledErrorTracker.record();
});

// Execute main function
main().catch(error => {
  console.error(`[FATAL] Unhandled error: ${error instanceof Error ? error.message : String(error)}`);
  process.exit(1);
});
