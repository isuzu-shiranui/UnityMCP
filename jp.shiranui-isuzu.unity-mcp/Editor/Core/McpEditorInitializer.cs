using UnityEditor;
using UnityEngine;
using UnityMCP.Editor.Resources;
using UnityMCP.Editor.Settings;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Handles initialization of the MCP system when the Unity editor starts.
    /// </summary>
    [InitializeOnLoad]
    internal static class McpEditorInitializer
    {
        /// <summary>
        /// Initializes the MCP system when the Unity editor starts.
        /// </summary>
        static McpEditorInitializer()
        {
            EditorApplication.delayCall += Initialize;
        }

        /// <summary>
        /// Initializes the MCP system, registering services and starting the server if configured.
        /// </summary>
        private static void Initialize()
        {
            Debug.Log("Initializing Unity MCP system...");

            // Create and register the MCP server
            var settings = McpSettings.instance;
            var server = new McpServer();
            McpServiceManager.Instance.RegisterService(server);

            // Discover and register command handlers
            var commandDiscovery = new McpHandlerDiscovery<IMcpCommandHandler>(handler => server.RegisterHandler(handler));
            var commandCount = commandDiscovery.DiscoverAndRegister();

            if (settings.detailedLogs)
                Debug.Log($"Discovered and registered {commandCount} command handlers");

            // Discover and register resource handlers
            var resourceDiscovery = new McpHandlerDiscovery<IMcpResourceHandler>(handler => server.RegisterResourceHandler(handler));
            var resourceCount = resourceDiscovery.DiscoverAndRegister();
            if (settings.detailedLogs)
                Debug.Log($"Discovered and registered {resourceCount} resource handlers");

            // Auto-start if configured
            if (settings.autoStartOnLaunch)
            {
                server.Start();
            }

            // Register for play mode state change if auto-restart is enabled
            if (settings.autoRestartOnPlayModeChange)
            {
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            }

            Debug.Log("Unity MCP system initialized");
        }

        /// <summary>
        /// Handles play mode state changes, restarting the server if needed.
        /// </summary>
        /// <param name="state">The new play mode state.</param>
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (!McpServiceManager.Instance.TryGetService<McpServer>(out var server))
            {
                return;
            }

            // When entering or exiting play mode
            if (state == PlayModeStateChange.EnteredPlayMode || state == PlayModeStateChange.EnteredEditMode)
            {
                if (server.IsRunning)
                {
                    Debug.Log("Restarting MCP server due to play mode change");
                    server.Stop();
                    server.Start();
                }
            }
        }
    }
}
