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
        /// Gets or sets the host address to bind the server to.
        /// </summary>
        [SerializeField]
        public string host = "127.0.0.1";

        /// <summary>
        /// Gets or sets the port to listen on.
        /// </summary>
        [SerializeField]
        public int port = 27182;

        /// <summary>
        /// Gets or sets whether to auto-start the server when Unity starts.
        /// </summary>
        [SerializeField]
        public bool autoStartOnLaunch = true;

        /// <summary>
        /// Gets or sets whether to auto-restart the server when play mode changes.
        /// </summary>
        [SerializeField]
        public bool autoRestartOnPlayModeChange = true;

        /// <summary>
        /// Gets or sets whether to store detailed logs.
        /// </summary>
        [SerializeField]
        public bool detailedLogs = true;

        /// <summary>
        /// Gets or sets whether to use UDP broadcast discovery.
        /// </summary>
        [SerializeField]
        public bool useUdpDiscovery = true;

        /// <summary>
        /// Gets or sets the UDP broadcast port.
        /// </summary>
        [SerializeField]
        public int udpDiscoveryPort = 27183;

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

        /// <summary>
        /// Saves the settings to disk.
        /// </summary>
        public void Save()
        {
            this.Save(true);
        }

        /// <summary>
        /// Updates the enabled state of a command handler.
        /// </summary>
        /// <param name="commandPrefix">The prefix of the command handler.</param>
        /// <param name="enabled">Whether the handler is enabled.</param>
        public void UpdateHandlerEnabledState(string commandPrefix, bool enabled)
        {
            this.handlerEnabledStates[commandPrefix] = enabled;
            this.Save();
        }

        /// <summary>
        /// Gets the enabled state of a command handler.
        /// </summary>
        /// <param name="commandPrefix">The prefix of the command handler.</param>
        /// <returns>true if the handler is enabled; otherwise, false.</returns>
        public bool GetHandlerEnabledState(string commandPrefix)
        {
            return this.handlerEnabledStates.TryGetValue(commandPrefix, out var enabled) ? enabled : true;
        }

        /// <summary>
        /// Gets all handler enabled states.
        /// </summary>
        /// <returns>A dictionary of command prefixes and their enabled states.</returns>
        public Dictionary<string, bool> GetAllHandlerEnabledStates()
        {
            return new Dictionary<string, bool>(this.handlerEnabledStates);
        }

        /// <summary>
        /// Updates the enabled state of a resource handler.
        /// </summary>
        /// <param name="resourceName">The name of the resource handler.</param>
        /// <param name="enabled">Whether the handler is enabled.</param>
        public void UpdateResourceHandlerEnabledState(string resourceName, bool enabled)
        {
            this.resourceHandlerEnabledStates[resourceName] = enabled;
            this.Save();
        }

        /// <summary>
        /// Gets the enabled state of a resource handler.
        /// </summary>
        /// <param name="resourceName">The name of the resource handler.</param>
        /// <returns>true if the handler is enabled; otherwise, false.</returns>
        public bool GetResourceHandlerEnabledState(string resourceName)
        {
            return this.resourceHandlerEnabledStates.TryGetValue(resourceName, out var enabled) ? enabled : true;
        }

        /// <summary>
        /// Gets all resource handler enabled states.
        /// </summary>
        /// <returns>A dictionary of resource names and their enabled states.</returns>
        public Dictionary<string, bool> GetAllResourceHandlerEnabledStates()
        {
            return new Dictionary<string, bool>(this.resourceHandlerEnabledStates);
        }
    }
}
