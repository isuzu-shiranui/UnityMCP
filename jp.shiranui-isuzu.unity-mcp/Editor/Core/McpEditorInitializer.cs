using UnityEditor;
using UnityEngine;

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

            // Quitting is not a domain reload, so nothing else would tear the server down —
            // without this the Editor exits leaving its descriptor behind, and clients keep
            // trying to reach a port that is gone.
            EditorApplication.quitting += OnEditorQuitting;

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

            if (McpSettings.instance.detailedLogs)
                Debug.Log("[McpEditorInitializer] Initializing Unity MCP system...");

            var settings = McpSettings.instance;

            // Clean up any existing server
            if (McpServiceManager.Instance.TryGetService<McpHttpServer>(out var existing))
            {
                existing.Dispose();
                McpServiceManager.Instance.RemoveService<McpHttpServer>();
            }

            // Read persisted state from SessionState (populated by OnBeforeAssemblyReload).
            // On first Editor launch, SessionState is empty: wasRunning=false and no saved port,
            // so the project's stable port is used.
            var wasRunning = SessionState.GetBool(SessionKeyWasRunning, false);
            var savedPort = SessionState.GetInt(SessionKeyBoundPort, 0);

            var server = new McpHttpServer();
            McpServiceManager.Instance.RegisterService(server);

            // Start the server if it was running before reload or if auto-start is configured
            if (wasRunning || settings.autoStartOnLaunch)
            {
                try
                {
                    server.Start(preferredPort: savedPort > 0 ? savedPort : (int?)null);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[McpEditorInitializer] Failed to start server: {e.Message}");
                }
            }

            if (McpSettings.instance.detailedLogs)
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

            // A reload discards the sequencer's static state, so anything still advancing would
            // never settle and its request would block for the whole sync window.
            FrameSequencer.CancelAll("Domain reload.");

            // The descriptor stays: the server returns on the same port in a moment, and
            // withdrawing it would make clients drop the instance and any active selection.
            server.Dispose(withdrawDescriptor: false);
            McpServiceManager.Instance.RemoveService<McpHttpServer>();
        }

        private static void OnEditorQuitting()
        {
            if (!McpServiceManager.Instance.TryGetService<McpHttpServer>(out var server))
            {
                return;
            }

            SessionState.SetBool(SessionKeyWasRunning, false);
            server.Dispose(withdrawDescriptor: true);
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
