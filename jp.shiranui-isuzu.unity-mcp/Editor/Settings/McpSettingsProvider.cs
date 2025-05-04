using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor.Core;
using UnityMCP.Editor.Installer;

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

            GUILayout.Label("TypeScript MCP Settings", this.headerStyle);
            if (GUILayout.Button("Open Installer Window", GUILayout.Height(25)))
            {
                EditorWindow.GetWindow<McpInstallerWindow>();
            }

            GUILayout.Label("Server Configuration", this.headerStyle);
            EditorGUILayout.Space(5);

            var settings = McpSettings.instance;

            // Server host and port
            settings.host = EditorGUILayout.TextField("Host", settings.host);
            settings.port = EditorGUILayout.IntField("Port", settings.port);

            EditorGUILayout.Space(5);

            // UDP Discovery settings
            EditorGUILayout.LabelField("Connection Method", this.subHeaderStyle);
            settings.useUdpDiscovery = EditorGUILayout.Toggle("Use UDP Discovery", settings.useUdpDiscovery);
            settings.udpDiscoveryPort = EditorGUILayout.IntField("UDP Discovery Port", settings.udpDiscoveryPort);

            EditorGUILayout.HelpBox("UDP Discovery allows the MCP server to automatically find Unity when it starts. Disable this if you experience connection issues.", MessageType.Info);

            EditorGUILayout.Space(10);

            // Auto-start options
            settings.autoStartOnLaunch = EditorGUILayout.Toggle("Auto-start on Launch", settings.autoStartOnLaunch);
            settings.autoRestartOnPlayModeChange = EditorGUILayout.Toggle("Auto-restart on Play Mode Change", settings.autoRestartOnPlayModeChange);

            EditorGUILayout.Space(5);

            // Logging options
            settings.detailedLogs = EditorGUILayout.Toggle("Detailed Logs", settings.detailedLogs);

            EditorGUILayout.Space(10);

            // Connection status section
            this.DrawConnectionStateSection();

            EditorGUILayout.Space(10);

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

        /// <summary>
        /// Draws the connection status section of the settings UI.
        /// With privacy-focused updates.
        /// </summary>
        private void DrawConnectionStateSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            GUILayout.Label("Connection Status", this.headerStyle);

            if (this.mcpServer != null)
            {
                var connected = this.mcpServer.IsConnected;
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Status:", GUILayout.Width(120));

                var oldColor = GUI.color;
                if (connected)
                {
                    GUI.color = Color.green;
                    GUILayout.Label("● Connected", EditorStyles.boldLabel);
                }
                else if (this.mcpServer.IsRunning)
                {
                    GUI.color = new Color(1.0f, 0.7f, 0.0f); // Orange
                    GUILayout.Label("● Connecting...", EditorStyles.boldLabel);
                }
                else
                {
                    GUI.color = Color.red;
                    GUILayout.Label("● Disconnected", EditorStyles.boldLabel);
                }
                GUI.color = oldColor;
                EditorGUILayout.EndHorizontal();

                // Show Client ID
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Client ID:", GUILayout.Width(120));

                // Truncate ID for display and add copy button
                var clientId = this.mcpServer.ClientId;
                var shortId = clientId.Length > 40 ? clientId.Substring(0, 37) + "..." : clientId;
                GUILayout.Label(shortId);

                if (GUILayout.Button("Copy", GUILayout.Width(70)))
                {
                    EditorGUIUtility.systemCopyBuffer = clientId;
                }
                EditorGUILayout.EndHorizontal();

                // Project details - simplified for privacy
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Project:", GUILayout.Width(120));
                GUILayout.Label($"{Application.productName}");
                EditorGUILayout.EndHorizontal();

                // Unity version
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Unity Version:", GUILayout.Width(120));
                GUILayout.Label(Application.unityVersion);
                EditorGUILayout.EndHorizontal();

                // Connected since timestamp
                if (connected)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("Connected Since:", GUILayout.Width(120));
                    GUILayout.Label(this.mcpServer.ConnectedSince.ToString("yyyy-MM-dd HH:mm:ss"));
                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.Space(10);

                // Connection controls
                EditorGUILayout.BeginHorizontal();

                if (connected)
                {
                    // Disconnect button
                    GUI.backgroundColor = new Color(0.9f, 0.6f, 0.6f); // Light red
                    if (GUILayout.Button("Disconnect", GUILayout.Height(25)))
                    {
                        this.mcpServer.Stop();
                    }
                    GUI.backgroundColor = this.defaultBackgroundColor;
                }
                else if (this.mcpServer.IsRunning)
                {
                    // Cancel connection button
                    GUI.backgroundColor = new Color(0.9f, 0.8f, 0.5f); // Light yellow
                    if (GUILayout.Button("Cancel Connection Attempt", GUILayout.Height(25)))
                    {
                        this.mcpServer.Stop();
                    }
                    GUI.backgroundColor = this.defaultBackgroundColor;
                }
                else
                {
                    // Connect button
                    GUI.backgroundColor = new Color(0.6f, 0.9f, 0.6f); // Light green
                    if (GUILayout.Button("Connect", GUILayout.Height(25)))
                    {
                        this.mcpServer.Start();
                    }
                    GUI.backgroundColor = this.defaultBackgroundColor;
                }

                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.HelpBox("MCP client not initialized", MessageType.Warning);

                // Initialize button
                if (GUILayout.Button("Initialize MCP Client"))
                {
                    // Create and register the client
                    this.mcpServer = new McpServer();
                    McpServiceManager.Instance.RegisterService<McpServer>(this.mcpServer);
                }
            }

            EditorGUILayout.EndVertical();
        }
    }
}
