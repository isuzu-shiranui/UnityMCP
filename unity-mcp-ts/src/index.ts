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
import { serverVersion } from "./core/Version.js";

/**
 * Main entry point for the MCP server application.
 * Acts as a bridge between LLMs (via MCP/stdio) and Unity Editor instances (via HTTP).
 */
async function main() {
  try {
    // listChanged is declared because the tool list is not static: it comes from whichever
    // Editor is connected, and changes when one connects or recompiles.
    //
    // The instructions matter more than their length suggests. A client with a large tool
    // catalogue defers the definitions and discovers them by search, which means this text and the
    // tool names are all it has at the start of a session — so it says which kinds of work live
    // here, in the words someone would use to ask for them. Kept short because clients truncate
    // it, with the tool-name prefixes first since those are what a search matches.
    const mcpServer = new McpServer(
      { name: "unity-mcp", version: serverVersion() },
      {
        capabilities: { tools: { listChanged: true }, prompts: {} },
        instructions: [
          "Controls a running Unity Editor. Search this server's tools for any Unity work:",
          "inspecting or editing a scene, assets and prefabs, Timeline and Recorder, shaders and",
          "rendering, play mode, the console, tests, and builds.",
          "",
          "Tool name prefixes: scene_ gameobject_ inspect_ asset_ prefab_ console_ compile_",
          "play_mode_ timeline_ recorder_ render_ shader_ material_ reflect_ gpu_ test_ build_",
          "project_ editor_ menu_ capture_ execute_.",
          "",
          "Reach for these when asked to look at, change or debug anything in a Unity project,",
          "including questions like why a material renders wrong, what a Timeline does at a given",
          "moment, why a script did not compile, or what the console reported. Read the console",
          "(console_read_logs) before building any instrumentation of your own; Unity has usually",
          "already written down the cause. Prefer a specific tool over execute_code, which cannot",
          "be undone and is the last resort when nothing else reaches.",
          "",
          "Requires a Unity Editor to be running with the jp.shiranui-isuzu.unity-mcp package",
          "installed; it is discovered automatically. Which tools exist depends on that project's",
          "packages, so Timeline and Recorder tools appear only where those packages are present.",
        ].join("\n"),
      }
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
