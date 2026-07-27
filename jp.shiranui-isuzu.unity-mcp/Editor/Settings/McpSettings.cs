using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor.Settings
{
    /// <summary>
    /// Stores and manages Unity MCP settings.
    /// </summary>
    [FilePath("UserSettings/UnityMcpSettings.asset", FilePathAttribute.Location.PreferencesFolder)]
    public sealed class McpSettings : ScriptableSingleton<McpSettings>
    {
        /// <summary>
        /// Gets or sets the path to the client installation.
        /// </summary>
        [SerializeField]
        public string clientInstallationPath = string.Empty;

        /// <summary>
        /// Gets or sets the HTTP port for the Unity HTTP server.
        /// </summary>
        [SerializeField]
        public int httpPort = 27182;

        /// <summary>
        /// Gets or sets whether to auto-start the server when Unity starts.
        /// </summary>
        [SerializeField]
        public bool autoStartOnLaunch = true;

        // Removed in v3, all of them dead settings that only looked like controls:
        //   portPersistenceEnabled — never read; port persistence was always on.
        //   reloadRetryMaxMs       — documented as "read from /health", but /health never
        //                            emitted it; the TS server reads MCP_RELOAD_RETRY_MAX_MS.
        //   useUdpBroadcast        — the server started the broadcaster unconditionally, so
        //                            turning it off did nothing. Discovery is now file-based.
        //   udpBroadcastPort, broadcastIntervalSeconds — belonged to that broadcaster.

        /// <summary>
        /// Gets or sets how long (ms) a request waits for its main-thread work before the
        /// server hands back a job id instead. Clamped to a 250 ms floor by the server.
        /// </summary>
        /// <remarks>
        /// Deliberately far below v2's fixed 10 s: work slower than this is better tracked
        /// through a job the caller can poll than through a socket held open, and the old
        /// behaviour of returning 504 while leaving the work queued made retries dangerous.
        /// </remarks>
        [SerializeField]
        public int syncWaitMs = 3000;

        /// <summary>
        /// Gets or sets whether to store detailed logs.
        /// </summary>
        [SerializeField]
        public bool detailedLogs = true;

        /// <summary>
        /// Gets or sets the dictionary of command handlers and their enabled states.
        /// </summary>
        [SerializeField]
        public Dictionary<string, bool> handlerEnabledStates = new Dictionary<string, bool>();

        /// <summary>
        /// Gets or sets the dictionary of resource handlers and their enabled states.
        /// </summary>
        [SerializeField]
        public Dictionary<string, bool> resourceHandlerEnabledStates = new Dictionary<string, bool>();

        // ── Legacy compatibility properties ──
        // These allow old code references to still compile during migration.

        /// <summary>
        /// Legacy: returns "127.0.0.1". HTTP server always binds to localhost.
        /// </summary>
        public string host => "127.0.0.1";

        /// <summary>
        /// Legacy: maps to httpPort.
        /// </summary>
        public int port
        {
            get => this.httpPort;
            set => this.httpPort = value;
        }

        /// <summary>
        /// Saves the settings to disk.
        /// </summary>
        public void Save()
        {
            this.Save(true);
        }

        public void UpdateHandlerEnabledState(string commandPrefix, bool enabled)
        {
            this.handlerEnabledStates[commandPrefix] = enabled;
            this.Save();
        }

        public bool GetHandlerEnabledState(string commandPrefix)
        {
            return this.handlerEnabledStates.TryGetValue(commandPrefix, out var enabled) ? enabled : true;
        }

        public Dictionary<string, bool> GetAllHandlerEnabledStates()
        {
            return new Dictionary<string, bool>(this.handlerEnabledStates);
        }

        public void UpdateResourceHandlerEnabledState(string resourceName, bool enabled)
        {
            this.resourceHandlerEnabledStates[resourceName] = enabled;
            this.Save();
        }

        public bool GetResourceHandlerEnabledState(string resourceName)
        {
            return this.resourceHandlerEnabledStates.TryGetValue(resourceName, out var enabled) ? enabled : true;
        }

        public Dictionary<string, bool> GetAllResourceHandlerEnabledStates()
        {
            return new Dictionary<string, bool>(this.resourceHandlerEnabledStates);
        }
    }
}
