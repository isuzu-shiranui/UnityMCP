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
        private McpHttpServer mcpServer;
        private GUIStyle headerStyle;
        private GUIStyle subHeaderStyle;
        private GUIStyle descriptionStyle;
        private GUIContent enabledIcon;
        private GUIContent disabledIcon;
        private Color defaultBackgroundColor;

        [SettingsProvider]
        public static SettingsProvider CreateMcpSettingsProvider()
        {
            var provider = new McpSettingsProvider("Preferences/Unity MCP", SettingsScope.User)
            {
                keywords = GetSearchKeywordsFromSerializedObject(new SerializedObject(McpSettings.instance))
            };
            return provider;
        }

        public McpSettingsProvider(string path, SettingsScope scopes, IEnumerable<string> keywords = null)
            : base(path, scopes, keywords)
        {
            if (McpServiceManager.Instance.TryGetService<McpHttpServer>(out var server))
            {
                this.mcpServer = server;
            }
        }

        public override void OnActivate(string searchContext, UnityEngine.UIElements.VisualElement rootElement)
        {
            var settings = McpSettings.instance;
            settings.hideFlags = HideFlags.HideAndDontSave & ~HideFlags.NotEditable;
            UnityEditor.Editor.CreateCachedEditor(settings, null, ref this.editor);

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

            this.enabledIcon = EditorGUIUtility.IconContent("TestPassed");
            this.disabledIcon = EditorGUIUtility.IconContent("TestFailed");
            this.defaultBackgroundColor = GUI.backgroundColor;
        }

        public override void OnGUI(string searchContext)
        {
            EditorGUI.BeginChangeCheck();

            GUILayout.Label("TypeScript MCP Settings", this.headerStyle);
            if (GUILayout.Button("Open Installer Window", GUILayout.Height(25)))
            {
                EditorWindow.GetWindow<McpInstallerWindow>();
            }

            GUILayout.Label("HTTP Server Configuration", this.headerStyle);
            EditorGUILayout.Space(5);

            var settings = McpSettings.instance;

            // HTTP port
            settings.httpPort = EditorGUILayout.IntField("HTTP Port", settings.httpPort);

            EditorGUILayout.HelpBox(
                "Unity starts an HTTP server on this port. If the port is in use, it will automatically try the next available port.",
                MessageType.Info);

            EditorGUILayout.Space(5);

            EditorGUILayout.LabelField("Discovery", this.subHeaderStyle);
            EditorGUILayout.SelectableLabel(
                UnityMCP.Editor.Core.McpInstanceDescriptor.PathFor(Application.dataPath),
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));

            EditorGUILayout.HelpBox(
                "While the server runs it publishes this descriptor file, holding the port and the " +
                "bearer token clients authenticate with. The MCP server reads it to find this Editor. " +
                "Treat the file as a credential: anything that can read it can run code in this Editor.",
                MessageType.Info);

            EditorGUILayout.Space(10);

            // Auto-start options
            settings.autoStartOnLaunch = EditorGUILayout.Toggle("Auto-start on Launch", settings.autoStartOnLaunch);

            EditorGUILayout.Space(5);

            settings.detailedLogs = EditorGUILayout.Toggle("Detailed Logs", settings.detailedLogs);

            EditorGUILayout.Space(10);

            // Server status section
            this.DrawServerStatusSection();

            EditorGUILayout.Space(10);

            this.handlersRootScrollPosition = EditorGUILayout.BeginScrollView(this.handlersRootScrollPosition);

            if (this.mcpServer != null)
            {
                this.DrawHandlersSection();
                this.DrawResourceHandlersSection();
            }

            EditorGUILayout.EndScrollView();

            if (EditorGUI.EndChangeCheck())
            {
                settings.Save();
            }
        }

        private void DrawHandlersSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            this.showCommandHandlers = EditorGUILayout.Foldout(this.showCommandHandlers, "Command Handlers", true);

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

            var handlersByAssembly = new Dictionary<string, List<KeyValuePair<string, McpHttpServer.HandlerRegistration>>>();

            foreach (var handler in handlers)
            {
                var assemblyName = handler.Value.AssemblyName;
                if (!handlersByAssembly.ContainsKey(assemblyName))
                {
                    handlersByAssembly[assemblyName] = new List<KeyValuePair<string, McpHttpServer.HandlerRegistration>>();
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

                    var enabled = handler.Value.Enabled;
                    var newEnabled = EditorGUILayout.Toggle(enabled, GUILayout.Width(20));
                    if (enabled != newEnabled)
                    {
                        this.mcpServer.SetHandlerEnabled(handler.Key, newEnabled);
                    }

                    GUILayout.Label(enabled ? this.enabledIcon : this.disabledIcon, GUILayout.Width(20));
                    EditorGUILayout.LabelField(handler.Key, GUILayout.Width(120));
                    EditorGUILayout.LabelField(handler.Value.Description, this.descriptionStyle);
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawResourceHandlersSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            this.showResourceHandlers = EditorGUILayout.Foldout(this.showResourceHandlers, "Resource Handlers", true);

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

            var handlersByAssembly = new Dictionary<string, List<KeyValuePair<string, McpHttpServer.ResourceHandlerRegistration>>>();

            foreach (var handler in resourceHandlers)
            {
                var assemblyName = handler.Value.AssemblyName;
                if (!handlersByAssembly.ContainsKey(assemblyName))
                {
                    handlersByAssembly[assemblyName] = new List<KeyValuePair<string, McpHttpServer.ResourceHandlerRegistration>>();
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

                    var enabled = handler.Value.Enabled;
                    var newEnabled = EditorGUILayout.Toggle(enabled, GUILayout.Width(20));
                    if (enabled != newEnabled)
                    {
                        this.mcpServer.SetResourceHandlerEnabled(handler.Key, newEnabled);
                    }

                    GUILayout.Label(enabled ? this.enabledIcon : this.disabledIcon, GUILayout.Width(20));
                    EditorGUILayout.LabelField(handler.Key, GUILayout.Width(120));

                    var resourceUri = handler.Value.Handler.ResourceUri;
                    var oldColor = GUI.contentColor;
                    GUI.contentColor = new Color(0.4f, 0.8f, 1.0f);
                    EditorGUILayout.LabelField(resourceUri, GUILayout.Width(150));
                    GUI.contentColor = oldColor;

                    EditorGUILayout.LabelField(handler.Value.Description, this.descriptionStyle);
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawServerStatusSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            GUILayout.Label("Server Status", this.headerStyle);

            if (this.mcpServer != null)
            {
                var isRunning = this.mcpServer.IsRunning;
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Status:", GUILayout.Width(120));

                var oldColor = GUI.color;
                if (isRunning)
                {
                    GUI.color = Color.green;
                    GUILayout.Label($"● Listening on port {this.mcpServer.BoundPort}", EditorStyles.boldLabel);
                }
                else
                {
                    GUI.color = Color.red;
                    GUILayout.Label("● Stopped", EditorStyles.boldLabel);
                }
                GUI.color = oldColor;
                EditorGUILayout.EndHorizontal();

                // Project info
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Project:", GUILayout.Width(120));
                GUILayout.Label(Application.productName);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Unity Version:", GUILayout.Width(120));
                GUILayout.Label(Application.unityVersion);
                EditorGUILayout.EndHorizontal();

                if (isRunning)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("Started Since:", GUILayout.Width(120));
                    GUILayout.Label(this.mcpServer.ConnectedSince.ToString("yyyy-MM-dd HH:mm:ss"));
                    EditorGUILayout.EndHorizontal();

                    // Endpoint info
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("Endpoint:", GUILayout.Width(120));
                    var endpoint = $"http://127.0.0.1:{this.mcpServer.BoundPort}";
                    GUILayout.Label(endpoint);
                    if (GUILayout.Button("Copy", GUILayout.Width(70)))
                    {
                        EditorGUIUtility.systemCopyBuffer = endpoint;
                    }
                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.Space(10);

                // Controls
                EditorGUILayout.BeginHorizontal();

                if (isRunning)
                {
                    GUI.backgroundColor = new Color(0.9f, 0.6f, 0.6f);
                    if (GUILayout.Button("Stop Server", GUILayout.Height(25)))
                    {
                        this.mcpServer.Stop();
                    }
                    GUI.backgroundColor = this.defaultBackgroundColor;
                }
                else
                {
                    GUI.backgroundColor = new Color(0.6f, 0.9f, 0.6f);
                    if (GUILayout.Button("Start Server", GUILayout.Height(25)))
                    {
                        this.mcpServer.Start();
                    }
                    GUI.backgroundColor = this.defaultBackgroundColor;
                }

                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.HelpBox("MCP HTTP server not initialized", MessageType.Warning);

                if (GUILayout.Button("Initialize MCP Server"))
                {
                    this.mcpServer = new McpHttpServer();
                    McpServiceManager.Instance.RegisterService(this.mcpServer);
                }
            }

            EditorGUILayout.EndVertical();
        }
    }
}
