import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { UnityConnection } from "./core/UnityConnection.js";
import { ProjectRegistry } from "./core/ProjectRegistry.js";
import { ProjectApi } from "./core/ProjectApi.js";
import { createUnityClientTools } from "./core/UnityClientHandler.js";
import { ToolCatalogClient } from "./core/ToolCatalogClient.js";
import { ToolRouter } from "./core/ToolRouter.js";
import { registerCodePrompt } from "./core/CodePrompt.js";
import { UnhandledErrorTracker } from "./core/TaskResilience.js";

/**
 * Main entry point for the MCP server application.
 * Acts as a bridge between LLMs (via MCP/stdio) and Unity Editor instances (via HTTP).
 */
async function main() {
  try {
    // listChanged is declared because the tool list is not static: it comes from whichever
    // Editor is connected, and changes when one connects or recompiles.
    const mcpServer = new McpServer(
      { name: "unity-mcp", version: "3.0.0" },
      { capabilities: { tools: { listChanged: true }, prompts: {} } }
    );

    // Initialize UnityConnection (HTTP client)
    const unityConnection = UnityConnection.getInstance();

    // Create and start ProjectRegistry (UDP listener + health polling)
    const registry = new ProjectRegistry(unityConnection, {
      descriptorPollIntervalMs: parseInt(process.env.MCP_DESCRIPTOR_INTERVAL || '2000', 10),
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

    // The Editor publishes the tool catalog; this server only forwards it.
    const catalog = new ToolCatalogClient(unityConnection);
    const router = new ToolRouter(mcpServer, unityConnection, catalog, createUnityClientTools());
    router.install();
    registerCodePrompt(mcpServer);

    // Clients ask for tools/list immediately, usually before any Editor is running, so start
    // from the last known catalog rather than answering "no tools".
    if (await catalog.loadCache()) {
      console.error(`[INFO] Loaded ${catalog.getTools().length} tools from cache (no Editor contacted yet)`);
    }

    // The catalog is always fetched from a named instance. Leaving the target off would make
    // this fail with "target required" the moment a second Editor is registered, which is
    // both common and exactly when a working tool list matters most.
    const refreshCatalog = async (reason: string, target: string) => {
      try {
        const changed = await catalog.refresh(target);
        console.error(`[INFO] Tool catalog refreshed from ${target} (${reason}): ${catalog.getTools().length} tools`);
        if (changed) {
          router.notifyToolsChanged();
        }
      } catch (err) {
        console.error(
          `[WARN] Could not fetch the tool catalog from ${target} (${reason}): ${err instanceof Error ? err.message : String(err)}`
        );
      }
    };

    registry.on('instanceDiscovered', (instance) => {
      console.error(`[INFO] Discovered Unity instance: ${instance.projectName} on :${instance.port}`);
      // Also covers reconnect after a domain reload, which is when the catalog is most
      // likely to have gained or lost tools.
      void refreshCatalog('discovered', instance.id);
    });

    // Try each registered instance rather than only the first: a UDP announce from another
    // machine on the subnet registers an instance whose endpoint is forced to loopback, so a
    // dead entry can sit at the head of the list.
    const usable = unityConnection
      .getConnectedClients()
      .filter(c => c.state === 'healthy' || c.state === 'reloading')
      .sort((a, b) => (a.state === 'healthy' ? 0 : 1) - (b.state === 'healthy' ? 0 : 1));

    for (const client of usable) {
      await refreshCatalog('startup', client.id);
      if (catalog.getTools().length > 0) {
        break;
      }
    }

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
