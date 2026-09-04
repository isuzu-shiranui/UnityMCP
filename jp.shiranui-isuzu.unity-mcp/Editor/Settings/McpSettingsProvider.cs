using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

using UnityEditor;
using UnityEngine;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Settings
{
    /// <summary>
    /// Preferences > Unity MCP: what still has to be done to reach a working setup, the
    /// connection details an MCP client needs, and the settings behind them.
    /// </summary>
    internal sealed class McpSettingsProvider : SettingsProvider
    {
        private static readonly string[] SnippetLabels =
        {
            "Claude Code (command)",
            "Cursor / Claude Code .mcp.json",
            "Codex config.toml",
            "Gemini CLI settings.json",
            "VS Code .vscode/mcp.json",
            "Claude Desktop (stdio bridge)",
        };

        private const string SettingsFoldoutKey = "UnityMCP.Preferences.Settings";
        private const string HelpFoldoutKey = "UnityMCP.Preferences.Help";

        private const string GuideUrlEnglish = "https://unity-mcp.shiranui-isuzu.dev/en/";
        private const string GuideUrlJapanese = "https://unity-mcp.shiranui-isuzu.dev/";
        private const string RepositoryUrl = "https://github.com/isuzu-shiranui/UnityMCP";
        private const string TroubleshootingUrlEnglish = RepositoryUrl + "/blob/main/docs/en/troubleshooting.md";
        private const string TroubleshootingUrlJapanese = RepositoryUrl + "/blob/main/docs/troubleshooting.md";

        /// <summary>Width of the label column, wide enough for the Japanese labels as well.</summary>
        private const float LabelWidth = 140f;

        private UnityEditor.Editor editor;
        private McpHttpServer mcpServer;
        private GUIStyle headerStyle;
        private GUIStyle snippetStyle;
        private int snippetIndex;
        private bool showSnippet;
        private string cliPath;
        private bool cliLooked;

        // EditorStyles はドメインリロード直後の OnActivate では未初期化で NullReferenceException になり、
        // SettingsWindow が選択復元を無限再帰してエディタが固まる。GUI リソースは OnGUI 内で遅延初期化する。
        private GUIStyle HeaderStyle => this.headerStyle ??= new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 14,
            margin = new RectOffset(0, 0, 10, 5)
        };

        private GUIStyle SnippetStyle => this.snippetStyle ??= new GUIStyle(EditorStyles.textArea)
        {
            wordWrap = false,
            font = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Menlo", "DejaVu Sans Mono" }, 11),
        };

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
        }

        public override void OnActivate(string searchContext, UnityEngine.UIElements.VisualElement rootElement)
        {
            var settings = McpSettings.instance;
            settings.hideFlags = HideFlags.HideAndDontSave & ~HideFlags.NotEditable;
            UnityEditor.Editor.CreateCachedEditor(settings, null, ref this.editor);
            this.cliLooked = false;
        }

        public override void OnGUI(string searchContext)
        {
            // The server is recreated on every domain reload, so the reference is refreshed
            // rather than captured once in the constructor.
            McpServiceManager.Instance.TryGetService(out this.mcpServer);

            EditorGUI.BeginChangeCheck();

            this.DrawSetupSection();
            EditorGUILayout.Space(10);

            this.DrawConnectionSection();
            EditorGUILayout.Space(10);

            this.DrawSettingsSection();
            this.DrawHelpSection();

            if (EditorGUI.EndChangeCheck())
            {
                McpSettings.instance.Save();
            }
        }

        /// <summary>
        /// What is done and what is not, in the order it has to happen. A client registration
        /// cannot be detected from inside the Editor, so it is stated as the remaining step
        /// rather than shown with a mark that would have to be guessed.
        /// </summary>
        private void DrawSetupSection()
        {
            GUILayout.Label(McpEditorText.Tr("Setup"), this.HeaderStyle);

            this.DrawServerRow();
            this.DrawCliRow();

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                McpEditorText.Tr("Register an MCP client with the configuration below, or run isuzu-unity-cli setup --mcp."),
                MessageType.Info);

            if (this.mcpServer != null && this.mcpServer.IsRunning && this.mcpServer.PortMismatch)
            {
                EditorGUILayout.HelpBox(
                    string.Format(
                        McpEditorText.Tr("Port {0} was busy, so this Editor is on {1}. Clients configured for the usual URL cannot reach it. Close whatever holds the port, or pin an HTTP Port in Settings and register the clients again."),
                        this.mcpServer.PreferredPort,
                        this.mcpServer.BoundPort),
                    MessageType.Warning);
            }

            EditorGUILayout.LabelField(
                $"{Application.productName} · {Application.unityVersion}"
                + (this.mcpServer != null && this.mcpServer.IsRunning
                    ? " · " + this.mcpServer.ConnectedSince.ToString("HH:mm:ss")
                    : string.Empty),
                EditorStyles.miniLabel);
        }

        private void DrawServerRow()
        {
            if (this.mcpServer == null)
            {
                if (this.DrawCheckRow(false, McpEditorText.Tr("Server not initialized"), McpEditorText.Tr("Initialize")))
                {
                    this.mcpServer = new McpHttpServer();
                    McpServiceManager.Instance.RegisterService(this.mcpServer);
                }

                return;
            }

            if (this.mcpServer.IsRunning)
            {
                var label = string.Format(McpEditorText.Tr("Listening on port {0}"), this.mcpServer.BoundPort);

                if (this.DrawCheckRow(true, label, McpEditorText.Tr("Stop")))
                {
                    this.mcpServer.Stop();
                }

                return;
            }

            if (this.DrawCheckRow(false, McpEditorText.Tr("Server stopped"), McpEditorText.Tr("Start")))
            {
                this.mcpServer.Start();
            }
        }

        private void DrawCliRow()
        {
            if (!this.cliLooked)
            {
                this.cliLooked = true;
                this.cliPath = IsuzuCliLocator.TryFind(out var found) ? found : null;
            }

            if (this.cliPath != null)
            {
                if (this.DrawCheckRow(true, McpEditorText.Tr("isuzu-unity-cli found") + "  " + this.cliPath, McpEditorText.Tr("Refresh")))
                {
                    this.cliLooked = false;
                }

                return;
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("✗", GUILayout.Width(16));
            GUILayout.Label(McpEditorText.Tr("isuzu-unity-cli not found on PATH"));
            GUILayout.FlexibleSpace();

            if (GUILayout.Button(McpEditorText.Tr("Install"), GUILayout.Width(90)))
            {
                OpenInstallTerminal();
                this.cliLooked = false;
            }

            if (GUILayout.Button(McpEditorText.Tr("Copy command"), GUILayout.Width(130)))
            {
                EditorGUIUtility.systemCopyBuffer = IsuzuCliLocator.InstallCommand();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(
                McpEditorText.Tr("Command-line agents need the CLI. MCP clients reach the endpoint below without it.")
                + "\n" + IsuzuCliLocator.InstallCommand(),
                MessageType.None);
        }

        /// <summary>One checklist row. Returns true on the frame its button is pressed.</summary>
        private bool DrawCheckRow(bool done, string label, string button)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(done ? "✓" : "✗", GUILayout.Width(16));
            GUILayout.Label(label);
            GUILayout.FlexibleSpace();
            var pressed = GUILayout.Button(button, GUILayout.Width(90));
            EditorGUILayout.EndHorizontal();
            return pressed;
        }

        private void DrawConnectionSection()
        {
            GUILayout.Label(McpEditorText.Tr("Connection"), this.HeaderStyle);

            if (this.mcpServer == null || !this.mcpServer.IsRunning)
            {
                EditorGUILayout.HelpBox(McpEditorText.Tr("Start the server to see the connection details."), MessageType.Info);
                return;
            }

            var url = this.mcpServer.McpUrl;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(McpEditorText.Tr("MCP URL"), GUILayout.Width(LabelWidth));
            EditorGUILayout.SelectableLabel(url, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            if (GUILayout.Button(McpEditorText.Tr("Copy"), GUILayout.Width(70)))
            {
                EditorGUIUtility.systemCopyBuffer = url;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(McpEditorText.Tr("Bearer token"), GUILayout.Width(LabelWidth));
            GUILayout.Label("••••••••", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(McpEditorText.Tr("Copy"), GUILayout.Width(70)))
            {
                EditorGUIUtility.systemCopyBuffer = this.mcpServer.Token;
            }
            if (GUILayout.Button(McpEditorText.Tr("Regenerate"), GUILayout.Width(100)))
            {
                if (EditorUtility.DisplayDialog(
                        McpEditorText.Tr("Regenerate token"),
                        McpEditorText.Tr("Every MCP client registered with the current token stops working until it is registered again with isuzu-unity-cli doctor --fix. Continue?"),
                        McpEditorText.Tr("Regenerate"),
                        McpEditorText.Tr("Cancel")))
                {
                    this.mcpServer.RegenerateToken();
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(McpEditorText.Tr("Configuration for"), GUILayout.Width(LabelWidth));
            this.snippetIndex = EditorGUILayout.Popup(this.snippetIndex, SnippetLabels);
            if (GUILayout.Button(McpEditorText.Tr(this.showSnippet ? "Hide" : "Show"), GUILayout.Width(70)))
            {
                this.showSnippet = !this.showSnippet;
            }
            if (GUILayout.Button(McpEditorText.Tr("Copy"), GUILayout.Width(70)))
            {
                EditorGUIUtility.systemCopyBuffer = this.Snippet();
            }
            EditorGUILayout.EndHorizontal();

            if (this.showSnippet)
            {
                EditorGUILayout.TextArea(this.Snippet(), this.SnippetStyle);
            }

            EditorGUILayout.HelpBox(
                string.Format(
                    McpEditorText.Tr("The descriptor and token files under {0} are credentials. Anything that can read them can run code in this Editor."),
                    McpInstanceDescriptor.StateRoot),
                MessageType.Info);
        }

        private string Snippet()
        {
            var url = this.mcpServer.McpUrl;
            var token = this.mcpServer.Token;

            switch (this.snippetIndex)
            {
                case 0: return McpClientConfigSnippets.ClaudeCodeCommand(url, token);
                case 1: return McpClientConfigSnippets.CursorJson(url, token);
                case 2: return McpClientConfigSnippets.CodexToml(url, token);
                case 3: return McpClientConfigSnippets.GeminiJson(url, token);
                case 4: return McpClientConfigSnippets.VsCodeJson(url, token);
                default:
                    return McpClientConfigSnippets.ClaudeDesktopJson(
                        this.cliPath ?? IsuzuCliLocator.ExecutableName,
                        Application.productName);
            }
        }

        private void DrawSettingsSection()
        {
            var open = EditorPrefs.GetBool(SettingsFoldoutKey, false);
            var next = EditorGUILayout.Foldout(open, McpEditorText.Tr("Settings"), true, EditorStyles.foldoutHeader);

            if (next != open)
            {
                EditorPrefs.SetBool(SettingsFoldoutKey, next);
            }

            if (!next)
            {
                return;
            }

            var settings = McpSettings.instance;
            EditorGUI.indentLevel++;

            var previousWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = LabelWidth + 40f;

            settings.httpPort = EditorGUILayout.IntField(
                McpEditorText.Content(
                    "HTTP Port",
                    "0 derives a stable port from the project path, which is what MCP client configuration relies on. Set a positive port only to resolve a collision; clients then have to be registered again."),
                settings.httpPort);

            if (settings.httpPort != 0 && (settings.httpPort < 1024 || settings.httpPort > 65535))
            {
                EditorGUILayout.HelpBox(
                    string.Format(McpEditorText.Tr("A port must be 0, or between 1024 and 65535. The server cannot bind {0}."), settings.httpPort),
                    MessageType.Error);
            }

            settings.autoStartOnLaunch = EditorGUILayout.Toggle(
                McpEditorText.Content("Auto-start on launch", "Start the server when the Editor opens this project."),
                settings.autoStartOnLaunch);

            settings.syncWaitMs = EditorGUILayout.IntField(
                McpEditorText.Content(
                    "Sync wait (ms)",
                    "How long a request waits for its main-thread work before the server answers with a job id instead. The server does not go below 250 ms."),
                settings.syncWaitMs);

            if (settings.syncWaitMs < 250)
            {
                EditorGUILayout.HelpBox(McpEditorText.Tr("The server uses 250 ms, which is its floor."), MessageType.Info);
            }

            settings.detailedLogs = EditorGUILayout.Toggle(
                McpEditorText.Content(
                    "Detailed logs",
                    "Write every request and each start and stop step to the Console. Those lines come back to the agent through console_read_logs. Warnings and errors are written either way."),
                settings.detailedLogs);

            var keepTicking = EditorGUILayout.Toggle(
                McpEditorText.Content(
                    "Keep Editor awake",
                    "Without focus the Editor runs its main loop about every 100 ms, so calls that need it wait. The server wakes the loop while requests are queued. Turning this on keeps it awake for the whole session instead, at the cost of idle CPU."),
                settings.keepEditorAwake);

            if (keepTicking != settings.keepEditorAwake)
            {
                settings.keepEditorAwake = keepTicking;
                if (this.mcpServer != null)
                {
                    this.mcpServer.KeepEditorAwake = keepTicking;
                }
            }

            settings.uiLanguage = EditorGUILayout.Popup(
                McpEditorText.Content("Language", "The language of this page. Tool descriptions and CLI output stay in English."),
                settings.uiLanguage,
                new[] { McpEditorText.Tr("Follow the Editor"), "English", "日本語" });

            EditorGUIUtility.labelWidth = previousWidth;
            EditorGUI.indentLevel--;

            EditorGUILayout.HelpBox(
                McpEditorText.Tr("These settings live in Unity's preferences folder and are shared by every project on this machine."),
                MessageType.None);
        }

        private void DrawHelpSection()
        {
            var open = EditorPrefs.GetBool(HelpFoldoutKey, false);
            var next = EditorGUILayout.Foldout(open, McpEditorText.Tr("Help"), true, EditorStyles.foldoutHeader);

            if (next != open)
            {
                EditorPrefs.SetBool(HelpFoldoutKey, next);
            }

            if (!next)
            {
                return;
            }

            var japanese = McpEditorText.Resolve() == SystemLanguage.Japanese;

            EditorGUI.indentLevel++;
            DrawLink(McpEditorText.Tr("Getting started"), japanese ? GuideUrlJapanese : GuideUrlEnglish);
            DrawLink(McpEditorText.Tr("Documentation"), RepositoryUrl);
            DrawLink(McpEditorText.Tr("Troubleshooting"), japanese ? TroubleshootingUrlJapanese : TroubleshootingUrlEnglish);
            EditorGUI.indentLevel--;
        }

        private static void DrawLink(string label, string url)
        {
            var rect = EditorGUI.IndentedRect(EditorGUILayout.GetControlRect());

            if (EditorGUI.LinkButton(rect, label))
            {
                Application.OpenURL(url);
            }
        }

        /// <summary>
        /// Runs the install script in a terminal the user can see, so a failure is readable
        /// rather than swallowed by a hidden process.
        /// </summary>
        private static void OpenInstallTerminal()
        {
            try
            {
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    // Security software refuses to create a process whose command line downloads
                    // and runs a script in one expression, and the refusal arrives as an access
                    // denied from Process.Start rather than anything the terminal could show.
                    // Fetching the script here and handing the terminal a path avoids that shape,
                    // and leaves the script on disk to read when an install fails.
                    var script = Path.Combine(Path.GetTempPath(), "isuzu-unity-cli-install.ps1");

                    using (var client = new System.Net.WebClient())
                    {
                        client.DownloadFile(IsuzuCliLocator.InstallScriptUrlWindows, script);
                    }

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoExit -ExecutionPolicy Bypass -File \"{script}\"",
                        UseShellExecute = true,
                    });
                }
                else if (Application.platform == RuntimePlatform.OSXEditor)
                {
                    var script = $"curl -fsSL {IsuzuCliLocator.InstallScriptUrlUnix} | sh";
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "osascript",
                        Arguments = $"-e 'tell application \"Terminal\" to do script \"{script}\"' -e 'tell application \"Terminal\" to activate'",
                        UseShellExecute = false,
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "x-terminal-emulator",
                        Arguments = $"-e sh -c \"curl -fsSL {IsuzuCliLocator.InstallScriptUrlUnix} | sh; exec sh\"",
                        UseShellExecute = false,
                    });
                }
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"[Unity MCP] Could not open a terminal: {e.Message}. Run this yourself: {IsuzuCliLocator.InstallCommand()}");
            }
        }
    }
}
