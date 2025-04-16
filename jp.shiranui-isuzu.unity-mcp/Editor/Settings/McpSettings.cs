using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor.Settings
{
    /// <summary>
    /// Stores and manages Unity MCP settings.
    /// </summary>
    [FilePath("UserSettings/UnityMcpSettings.asset", FilePathAttribute.Location.PreferencesFolder)]
    internal sealed class McpSettings : ScriptableSingleton<McpSettings>
    {
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
        public bool autoStartOnLaunch = false;

        /// <summary>
        /// Gets or sets whether to auto-restart the server when play mode changes.
        /// </summary>
        [SerializeField]
        public bool autoRestartOnPlayModeChange = true;

        /// <summary>
        /// Gets or sets whether to store detailed logs.
        /// </summary>
        [SerializeField]
        public bool detailedLogs = false;

        /// <summary>
        /// Gets or sets the dictionary of command handlers and their enabled states.
        /// </summary>
        [SerializeField]
        public Dictionary<string, bool> handlerEnabledStates = new();

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
    }
}
