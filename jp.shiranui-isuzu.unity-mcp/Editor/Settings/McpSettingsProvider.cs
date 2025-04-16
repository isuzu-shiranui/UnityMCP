using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Settings
{
    /// <summary>
    /// Provides a settings UI for Unity MCP in the Preferences window.
    /// </summary>
    internal sealed class McpSettingsProvider : SettingsProvider
    {
        private UnityEditor.Editor editor;
        private bool showHandlers = false;
        private Vector2 handlersScrollPosition;
        private McpServer mcpServer;

        /// <summary>
        /// Creates and registers the MCP settings provider.
        /// </summary>
        /// <returns>An instance of the settings provider.</returns>
        [SettingsProvider]
        public static SettingsProvider CreateMcpSettingsProvider()
        {
            var provider = new McpSettingsProvider("Preferences/Unity MCP", SettingsScope.User)
            {
                keywords = GetSearchKeywordsFromSerializedObject(new SerializedObject(McpSettings.instance))
            };
            return provider;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="McpSettingsProvider"/> class.
        /// </summary>
        /// <param name="path">The settings path.</param>
        /// <param name="scopes">The settings scope.</param>
        /// <param name="keywords">The search keywords.</param>
        public McpSettingsProvider(string path, SettingsScope scopes, IEnumerable<string> keywords = null)
            : base(path, scopes, keywords)
        {
            // Try to find the MCP server instance from the service manager
            if (McpServiceManager.Instance.TryGetService<McpServer>(out var server))
            {
                this.mcpServer = server;
            }
        }

        /// <summary>
        /// Called when the settings provider is activated.
        /// </summary>
        /// <param name="searchContext">The search context.</param>
        /// <param name="rootElement">The root UI element.</param>
        public override void OnActivate(string searchContext, UnityEngine.UIElements.VisualElement rootElement)
        {
            var settings = McpSettings.instance;
            settings.hideFlags = HideFlags.HideAndDontSave & ~HideFlags.NotEditable;
            UnityEditor.Editor.CreateCachedEditor(settings, null, ref this.editor);
        }

        /// <summary>
        /// Draws the settings UI.
        /// </summary>
        /// <param name="searchContext">The search context.</param>
        public override void OnGUI(string searchContext)
        {
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("Server Configuration", EditorStyles.boldLabel);

            var settings = McpSettings.instance;

            // Server host and port
            settings.host = EditorGUILayout.TextField("Host", settings.host);
            settings.port = EditorGUILayout.IntField("Port", settings.port);

            EditorGUILayout.Space();

            // Auto-start options
            settings.autoStartOnLaunch = EditorGUILayout.Toggle("Auto-start on Launch", settings.autoStartOnLaunch);
            settings.autoRestartOnPlayModeChange = EditorGUILayout.Toggle("Auto-restart on Play Mode Change", settings.autoRestartOnPlayModeChange);

            EditorGUILayout.Space();

            // Logging options
            settings.detailedLogs = EditorGUILayout.Toggle("Detailed Logs", settings.detailedLogs);

            EditorGUILayout.Space();

            // Server control buttons
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = this.mcpServer != null;

            if (this.mcpServer != null && this.mcpServer.IsRunning)
            {
                if (GUILayout.Button("Stop Server"))
                {
                    this.mcpServer.Stop();
                }
            }
            else
            {
                if (GUILayout.Button("Start Server"))
                {
                    if (this.mcpServer == null)
                    {
                        // Create and register the server if it doesn't exist
                        this.mcpServer = new McpServer(settings.port);
                        McpServiceManager.Instance.RegisterService<McpServer>(this.mcpServer);
                    }

                    this.mcpServer.Start();
                }
            }

            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();

            // Command handlers section
            if (this.mcpServer != null)
            {
                this.DrawCommandHandlersSection();
            }

            if (EditorGUI.EndChangeCheck())
            {
                settings.Save();
            }
        }

        /// <summary>
        /// Draws the command handlers section.
        /// </summary>
        private void DrawCommandHandlersSection()
        {
            this.showHandlers = EditorGUILayout.Foldout(this.showHandlers, "Command Handlers", true);

            if (!this.showHandlers)
            {
                return;
            }

            var handlers = this.mcpServer.GetRegisteredHandlers();
            if (handlers.Count == 0)
            {
                EditorGUILayout.HelpBox("No command handlers registered", MessageType.Info);
                return;
            }

            // Group handlers by assembly
            var handlersByAssembly = new Dictionary<string, List<KeyValuePair<string, McpServer.HandlerRegistration>>>();

            foreach (var handler in handlers)
            {
                var assemblyName = handler.Value.AssemblyName;
                if (!handlersByAssembly.ContainsKey(assemblyName))
                {
                    handlersByAssembly[assemblyName] = new List<KeyValuePair<string, McpServer.HandlerRegistration>>();
                }

                handlersByAssembly[assemblyName].Add(handler);
            }

            this.handlersScrollPosition = EditorGUILayout.BeginScrollView(this.handlersScrollPosition);

            foreach (var assemblyGroup in handlersByAssembly)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField(assemblyGroup.Key, EditorStyles.boldLabel);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                foreach (var handler in assemblyGroup.Value)
                {
                    EditorGUILayout.BeginHorizontal();

                    var enabled = handler.Value.Enabled;
                    var newEnabled = EditorGUILayout.Toggle(enabled, GUILayout.Width(20));

                    if (enabled != newEnabled)
                    {
                        this.mcpServer.SetHandlerEnabled(handler.Key, newEnabled);

                        // Save the setting
                        McpSettings.instance.UpdateHandlerEnabledState(handler.Key, newEnabled);
                    }

                    EditorGUILayout.LabelField(handler.Key, GUILayout.Width(120));
                    EditorGUILayout.LabelField(handler.Value.Description, EditorStyles.miniLabel);

                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.EndScrollView();
        }
    }
}
