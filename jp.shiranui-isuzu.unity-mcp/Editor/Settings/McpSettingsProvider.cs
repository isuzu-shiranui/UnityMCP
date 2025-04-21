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
        private bool showCommandHandlers = false;
        private bool showResourceHandlers = false;
        private Vector2 handlersRootScrollPosition;
        private McpServer mcpServer;
        private GUIStyle headerStyle;
        private GUIStyle subHeaderStyle;
        private GUIStyle descriptionStyle;
        private GUIContent enabledIcon;
        private GUIContent disabledIcon;
        private Color defaultBackgroundColor;

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

            // Prepare styles
            this.headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                margin = new RectOffset(0, 0, 10, 5)
            };

            this.subHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                margin = new RectOffset(0, 0, 5, 3)
            };

            this.descriptionStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                wordWrap = true
            };

            // Icons
            this.enabledIcon = EditorGUIUtility.IconContent("TestPassed");
            this.disabledIcon = EditorGUIUtility.IconContent("TestFailed");

            // Store default background color for later use
            this.defaultBackgroundColor = GUI.backgroundColor;
        }

        /// <summary>
        /// Draws the settings UI.
        /// </summary>
        /// <param name="searchContext">The search context.</param>
        public override void OnGUI(string searchContext)
        {
            EditorGUI.BeginChangeCheck();

            GUILayout.Label("Server Configuration", this.headerStyle);
            EditorGUILayout.Space(5);

            var settings = McpSettings.instance;

            // Server host and port
            settings.host = EditorGUILayout.TextField("Host", settings.host);
            settings.port = EditorGUILayout.IntField("Port", settings.port);

            EditorGUILayout.Space(10);

            // Auto-start options
            settings.autoStartOnLaunch = EditorGUILayout.Toggle("Auto-start on Launch", settings.autoStartOnLaunch);
            settings.autoRestartOnPlayModeChange = EditorGUILayout.Toggle("Auto-restart on Play Mode Change", settings.autoRestartOnPlayModeChange);

            EditorGUILayout.Space(5);

            // Logging options
            settings.detailedLogs = EditorGUILayout.Toggle("Detailed Logs", settings.detailedLogs);

            EditorGUILayout.Space(10);

            // Server control buttons
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = this.mcpServer != null;

            if (this.mcpServer is { IsRunning: true })
            {
                GUI.backgroundColor = new Color(0.9f, 0.6f, 0.6f);
                if (GUILayout.Button("Stop Server - Now Running", GUILayout.Height(30)))
                {
                    this.mcpServer.Stop();
                }
                GUI.backgroundColor = this.defaultBackgroundColor;
            }
            else
            {
                GUI.backgroundColor = new Color(0.6f, 0.9f, 0.6f);
                if (GUILayout.Button("Start Server - Now Stopped", GUILayout.Height(30)))
                {
                    if (this.mcpServer == null)
                    {
                        // Create and register the server if it doesn't exist
                        this.mcpServer = new McpServer(settings.port);
                        McpServiceManager.Instance.RegisterService<McpServer>(this.mcpServer);
                    }

                    this.mcpServer.Start();
                }
                GUI.backgroundColor = this.defaultBackgroundColor;
            }

            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(20);

            this.handlersRootScrollPosition = EditorGUILayout.BeginScrollView(this.handlersRootScrollPosition);

            // Command handlers section
            if (this.mcpServer != null)
            {
                this.DrawHandlersSection();
            }

            // Resource handlers section
            if (this.mcpServer != null)
            {
                this.DrawResourceHandlersSection();
            }

            EditorGUILayout.EndScrollView();

            if (EditorGUI.EndChangeCheck())
            {
                settings.Save();
            }
        }

        /// <summary>
        /// Draws the command handlers section.
        /// </summary>
        private void DrawHandlersSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            this.showCommandHandlers = EditorGUILayout.Foldout(this.showCommandHandlers, "Command Handlers", true);

            // Display count of handlers
            var handlers = this.mcpServer.GetRegisteredHandlers();
            var enabledCount = 0;
            foreach (var handler in handlers)
            {
                if (handler.Value.Enabled) enabledCount++;
            }
            EditorGUILayout.LabelField($"{enabledCount}/{handlers.Count} enabled", EditorStyles.miniLabel, GUILayout.Width(80));
            EditorGUILayout.EndHorizontal();

            if (!this.showCommandHandlers)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            if (handlers.Count == 0)
            {
                EditorGUILayout.HelpBox("No command handlers registered", MessageType.Info);
                EditorGUILayout.EndVertical();
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

            foreach (var assemblyGroup in handlersByAssembly)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField(assemblyGroup.Key, this.subHeaderStyle);

                foreach (var handler in assemblyGroup.Value)
                {
                    EditorGUILayout.BeginHorizontal(GUILayout.Height(24));

                    // Toggle for enabling/disabling
                    var enabled = handler.Value.Enabled;
                    var newEnabled = EditorGUILayout.Toggle(enabled, GUILayout.Width(20));

                    if (enabled != newEnabled)
                    {
                        this.mcpServer.SetHandlerEnabled(handler.Key, newEnabled);
                    }

                    // Display enabled/disabled icon
                    GUILayout.Label(enabled ? this.enabledIcon : this.disabledIcon, GUILayout.Width(20));

                    // Handler name
                    EditorGUILayout.LabelField(handler.Key, GUILayout.Width(120));

                    // Description
                    EditorGUILayout.LabelField(handler.Value.Description, this.descriptionStyle);
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Draws the resource handlers section.
        /// </summary>
        private void DrawResourceHandlersSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            this.showResourceHandlers = EditorGUILayout.Foldout(this.showResourceHandlers, "Resource Handlers", true);

            // Display count of resource handlers
            var resourceHandlers = this.mcpServer.GetRegisteredResourceHandlers();
            var enabledCount = 0;
            foreach (var handler in resourceHandlers)
            {
                if (handler.Value.Enabled) enabledCount++;
            }
            EditorGUILayout.LabelField($"{enabledCount}/{resourceHandlers.Count} enabled", EditorStyles.miniLabel, GUILayout.Width(80));
            EditorGUILayout.EndHorizontal();

            if (!this.showResourceHandlers)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            if (resourceHandlers.Count == 0)
            {
                EditorGUILayout.HelpBox("No resource handlers registered", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            // Group resource handlers by assembly
            var handlersByAssembly = new Dictionary<string, List<KeyValuePair<string, McpServer.ResourceHandlerRegistration>>>();

            foreach (var handler in resourceHandlers)
            {
                var assemblyName = handler.Value.AssemblyName;
                if (!handlersByAssembly.ContainsKey(assemblyName))
                {
                    handlersByAssembly[assemblyName] = new List<KeyValuePair<string, McpServer.ResourceHandlerRegistration>>();
                }

                handlersByAssembly[assemblyName].Add(handler);
            }

            foreach (var assemblyGroup in handlersByAssembly)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField(assemblyGroup.Key, this.subHeaderStyle);

                foreach (var handler in assemblyGroup.Value)
                {
                    EditorGUILayout.BeginHorizontal(GUILayout.Height(24));

                    // Toggle for enabling/disabling
                    var enabled = handler.Value.Enabled;
                    var newEnabled = EditorGUILayout.Toggle(enabled, GUILayout.Width(20));

                    if (enabled != newEnabled)
                    {
                        this.mcpServer.SetResourceHandlerEnabled(handler.Key, newEnabled);
                    }

                    // Display enabled/disabled icon
                    GUILayout.Label(enabled ? this.enabledIcon : this.disabledIcon, GUILayout.Width(20));

                    // Resource name
                    EditorGUILayout.LabelField(handler.Key, GUILayout.Width(120));

                    // URI in a different color
                    var handler_obj = handler.Value.Handler;
                    var resourceUri = handler_obj.ResourceUri;
                    var oldColor = GUI.contentColor;
                    GUI.contentColor = new Color(0.4f, 0.8f, 1.0f);
                    EditorGUILayout.LabelField(resourceUri, GUILayout.Width(150));
                    GUI.contentColor = oldColor;

                    // Description
                    EditorGUILayout.LabelField(handler.Value.Description, this.descriptionStyle);
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndVertical();
        }
    }
}
