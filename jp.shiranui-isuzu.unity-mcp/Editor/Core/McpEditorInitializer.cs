using UnityEditor;
using UnityEngine;

using UnityMCP.Editor.Resources;
using UnityMCP.Editor.Settings;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Handles initialization of the MCP system when the Unity editor starts.
    /// Survives assembly reload by saving state to SessionState before reload
    /// and restoring it after reload (design §2.1).
    /// PlayModeStateChanged is intentionally NOT handled here (R2.3).
    /// </summary>
    [InitializeOnLoad]
    internal static class McpEditorInitializer
    {
        private const string SessionKeyBoundPort = "UnityMCP.BoundPort";
        private const string SessionKeyWasRunning = "UnityMCP.WasRunning";

        static McpEditorInitializer()
        {
            // The MCP server must only run in the main interactive Editor process.
            // AssetImportWorker / batchmode processes share the project settings and
            // would otherwise race for the same HTTP port (#13).
            if (ShouldSkipServerInCurrentProcess())
            {
                return;
            }

            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;

            // Initial setup via delayCall (first load only — afterAssemblyReload handles reloads)
            EditorApplication.delayCall += Initialize;
        }

        private static void Initialize()
        {
            if (ShouldSkipServerInCurrentProcess())
            {
                return;
            }

            // Skip if already initialized (afterAssemblyReload may have already run)
            if (McpServiceManager.Instance.TryGetService<McpHttpServer>(out _))
            {
                return;
            }

            InitializeServer();
        }

        private static bool ShouldSkipServerInCurrentProcess()
        {
            if (Application.isBatchMode)
            {
                return true;
            }

            var args = System.Environment.GetCommandLineArgs();
            foreach (var arg in args)
            {
                if (arg.IndexOf("AssetImportWorker", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static void InitializeServer()
        {
            if (ShouldSkipServerInCurrentProcess())
            {
                if (McpSettings.instance.detailedLogs)
                    Debug.Log("[McpEditorInitializer] Skipping MCP server startup (batchmode or AssetImportWorker process)");

                return;
            }

            Debug.Log("[McpEditorInitializer] Initializing Unity MCP system...");

            var settings = McpSettings.instance;

            // Clean up any existing server
            if (McpServiceManager.Instance.TryGetService<McpHttpServer>(out var existing))
            {
                existing.Dispose();
                McpServiceManager.Instance.RemoveService<McpHttpServer>();
            }

            // Read persisted state from SessionState (populated by OnBeforeAssemblyReload).
            // On first Editor launch, SessionState is empty: wasRunning=false, savedPort=settings.httpPort.
            var wasRunning = SessionState.GetBool(SessionKeyWasRunning, false);
            var savedPort = SessionState.GetInt(SessionKeyBoundPort, settings.httpPort);

            var server = new McpHttpServer();
            McpServiceManager.Instance.RegisterService(server);

            // Register handlers before Start so /health.handlers[] is fully populated
            var commandDiscovery = new McpHandlerDiscovery<IMcpCommandHandler>(handler => server.RegisterHandler(handler));
            var commandCount = commandDiscovery.DiscoverAndRegister();

            if (settings.detailedLogs)
                Debug.Log($"[McpEditorInitializer] Discovered {commandCount} command handlers");

            var resourceDiscovery = new McpHandlerDiscovery<IMcpResourceHandler>(handler => server.RegisterResourceHandler(handler));
            var resourceCount = resourceDiscovery.DiscoverAndRegister();

            if (settings.detailedLogs)
                Debug.Log($"[McpEditorInitializer] Discovered {resourceCount} resource handlers");

            // Start the server if it was running before reload or if auto-start is configured
            if (wasRunning || settings.autoStartOnLaunch)
            {
                try
                {
                    server.Start(preferredPort: savedPort);
                }
                catch
                {
                    // Preferred port failed — fall back to settings.httpPort scan
                    try
                    {
                        server.Start(preferredPort: null);
                    }
                    catch (System.Exception fallbackEx)
                    {
                        Debug.LogError($"[McpEditorInitializer] Failed to start server: {fallbackEx.Message}");
                    }
                }
            }

            Debug.Log("[McpEditorInitializer] Unity MCP system initialized");
        }

        private static void OnBeforeAssemblyReload()
        {
            if (!McpServiceManager.Instance.TryGetService<McpHttpServer>(out var server))
                return;

            if (McpSettings.instance.detailedLogs)
                Debug.Log("[McpEditorInitializer] Saving state before assembly reload...");

            // Persist state so afterAssemblyReload can restore it (design §2.1)
            SessionState.SetInt(SessionKeyBoundPort, server.BoundPort);
            SessionState.SetBool(SessionKeyWasRunning, server.IsRunning);

            server.Dispose();
            McpServiceManager.Instance.RemoveService<McpHttpServer>();
        }

        private static void OnAfterAssemblyReload()
        {
            if (McpSettings.instance.detailedLogs)
                Debug.Log("[McpEditorInitializer] Re-initializing MCP server after assembly reload...");

            InitializeServer();
        }
    }
}
