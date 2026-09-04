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
        /// <summary>Layout version of this asset, used to migrate values saved by older packages.</summary>
        [SerializeField]
        public int settingsVersion;

        /// <summary>
        /// The HTTP port to bind. Zero means "derive a stable port from the project path", which
        /// is what MCP client configuration relies on; a positive value overrides it.
        /// </summary>
        [SerializeField]
        public int httpPort;

        /// <summary>
        /// Gets or sets whether to auto-start the server when Unity starts.
        /// </summary>
        [SerializeField]
        public bool autoStartOnLaunch = true;

        /// <summary>
        /// Gets or sets how long (ms) a request waits for its main-thread work before the
        /// server hands back a job id instead. Clamped to a 250 ms floor by the server.
        /// </summary>
        /// <remarks>
        /// Work slower than this is better tracked through a job the caller can poll than
        /// through a socket held open.
        /// </remarks>
        [SerializeField]
        public int syncWaitMs = 3000;

        /// <summary>
        /// Log every request and each start and stop step to the Unity console. Off by default:
        /// these lines come back to the agent through <c>console_read_logs</c>, where they crowd
        /// out the project's own output. Warnings and errors are logged either way.
        /// </summary>
        [SerializeField]
        public bool detailedLogs;

        /// <summary>
        /// Keep the Editor main loop awake for the whole session, not only while a request is
        /// waiting. Costs idle CPU; for hosts where a fully idle Editor stops accepting
        /// connections at all.
        /// </summary>
        [SerializeField]
        public bool keepEditorAwake;

        /// <summary>
        /// Which language the Preferences page draws itself in, as <see cref="McpUiLanguage"/>.
        /// Zero follows the Editor. Only that page is translated.
        /// </summary>
        [SerializeField]
        public int uiLanguage;

        private const int CurrentSettingsVersion = 4;

        private void OnEnable()
        {
            // Before v4 this value was where the port scan started, not a pin, and every asset
            // holds one. Carrying it over as a pin would put every upgraded project on the same
            // port and none on its derived one. Not saved here: a ScriptableSingleton refuses to
            // save while it is being loaded, and the change is applied again on every load until
            // the next ordinary save persists it.
            if (this.settingsVersion < CurrentSettingsVersion)
            {
                this.httpPort = 0;
                this.settingsVersion = CurrentSettingsVersion;
            }
        }

        /// <summary>
        /// Saves the settings to disk.
        /// </summary>
        public void Save()
        {
            this.Save(true);
        }
    }
}
