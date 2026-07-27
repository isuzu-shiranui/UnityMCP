using System;
using System.Threading.Tasks;

using UnityEditor;

using UnityEngine;

namespace UnityMCP.Editor.Installer
{
    /// <summary>
    /// One window for getting the companion MCP server installed and registered.
    /// </summary>
    /// <remarks>
    /// It runs npm and then the server's own CLI, rather than reimplementing either. The CLI
    /// already knows five agents, two config formats and where each keeps its skills; a second
    /// implementation in C# would be exactly the duplication this release removes elsewhere.
    /// </remarks>
    public class McpInstallerWindow : EditorWindow
    {
        private Vector2 scroll;
        private string log = string.Empty;
        private bool busy;

        private static readonly string[] AgentChoices =
        {
            "every agent found",
            "claude-code",
            "claude-desktop",
            "codex",
            "cursor",
            "gemini",
        };

        private int agentChoice;

        [MenuItem("Tools/Unity MCP/Installer")]
        public static void Open()
        {
            var window = GetWindow<McpInstallerWindow>("Unity MCP Installer");
            window.minSize = new Vector2(520, 440);
        }

        private void OnGUI()
        {
            this.scroll = EditorGUILayout.BeginScrollView(this.scroll);

            EditorGUILayout.LabelField("Unity MCP", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            this.DrawEnvironment();
            EditorGUILayout.Space(8);
            this.DrawInstall();
            EditorGUILayout.Space(8);
            this.DrawSetup();
            EditorGUILayout.Space(8);
            this.DrawLog();

            EditorGUILayout.EndScrollView();
        }

        private void DrawEnvironment()
        {
            EditorGUILayout.LabelField("Environment", EditorStyles.boldLabel);

            var hasNode = McpInstallHelper.IsNodeInstalled();
            var npmCli = McpInstallHelper.ResolveNpmCliScript();

            EditorGUILayout.LabelField("Node.js", hasNode ? "found" : "not found");
            EditorGUILayout.LabelField("npm", string.IsNullOrEmpty(npmCli) ? "not found" : npmCli);

            if (!hasNode || string.IsNullOrEmpty(npmCli))
            {
                EditorGUILayout.HelpBox(
                    "Node.js 18 or newer is required. Install it, then restart the Editor so it " +
                    "inherits the updated PATH — a running Editor keeps the PATH it started with.",
                    MessageType.Warning);

                if (GUILayout.Button("Open nodejs.org"))
                {
                    Application.OpenURL("https://nodejs.org/en/download/");
                }
            }
        }

        private void DrawInstall()
        {
            EditorGUILayout.LabelField("MCP server", EditorStyles.boldLabel);

            var packageVersion = McpNpmInstaller.PackageVersion ?? "unknown";
            var installedVersion = McpNpmInstaller.InstalledVersion;

            EditorGUILayout.LabelField("This package", packageVersion);
            EditorGUILayout.LabelField("Installed server", installedVersion ?? "not installed");
            EditorGUILayout.SelectableLabel(
                McpNpmInstaller.InstallRoot,
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));

            if (McpNpmInstaller.IsVersionMismatched)
            {
                EditorGUILayout.HelpBox(
                    $"The installed server is {installedVersion} but this package is {packageVersion}. " +
                    "They speak one protocol and are released together, so reinstall before relying on it.",
                    MessageType.Warning);
            }

            using (new EditorGUI.DisabledScope(this.busy))
            {
                if (GUILayout.Button(McpNpmInstaller.IsInstalled ? "Reinstall server" : "Install server"))
                {
                    this.Run("install", McpNpmInstaller.InstallAsync);
                }
            }

            EditorGUILayout.HelpBox(
                $"Runs: npm install {McpNpmInstaller.NpmPackageName}@{packageVersion}\n" +
                "The version is pinned to this package so the two halves cannot drift apart.",
                MessageType.None);
        }

        private void DrawSetup()
        {
            EditorGUILayout.LabelField("Agent setup", EditorStyles.boldLabel);

            this.agentChoice = EditorGUILayout.Popup("Register with", this.agentChoice, AgentChoices);

            using (new EditorGUI.DisabledScope(this.busy || !McpNpmInstaller.IsInstalled))
            {
                if (GUILayout.Button("Register and install skill"))
                {
                    var agent = this.agentChoice == 0 ? null : AgentChoices[this.agentChoice];
                    this.Run("setup", () => McpNpmInstaller.RunSetupAsync(agent));
                }
            }

            if (!McpNpmInstaller.IsInstalled)
            {
                EditorGUILayout.HelpBox("Install the server first.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Adds this Editor's MCP server to the chosen agent's config and installs the " +
                    "unity-mcp skill where that agent keeps them. Restart the agent afterwards.",
                    MessageType.None);
            }

            using (new EditorGUI.DisabledScope(this.busy))
            {
                if (GUILayout.Button("Remove the installed server"))
                {
                    var result = McpNpmInstaller.Uninstall();
                    this.Append(result.Succeeded ? result.Output : result.Error);
                }
            }

            EditorGUILayout.HelpBox(
                "Removing the server here leaves agent configs and skills alone. " +
                "`unity-mcp uninstall --yes` removes those too.",
                MessageType.None);
        }

        private void DrawLog()
        {
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);

            if (this.busy)
            {
                EditorGUILayout.HelpBox("Working…", MessageType.Info);
            }

            EditorGUILayout.SelectableLabel(
                string.IsNullOrEmpty(this.log) ? "(nothing yet)" : this.log,
                EditorStyles.textArea,
                GUILayout.MinHeight(140));
        }

        private async void Run(string label, Func<Task<McpNpmInstaller.CommandResult>> action)
        {
            this.busy = true;
            this.Append($"--- {label} ---");

            try
            {
                var result = await action();

                if (!string.IsNullOrWhiteSpace(result.Output))
                {
                    this.Append(result.Output.TrimEnd());
                }

                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    // npm writes progress and warnings to stderr even when it succeeds, so this
                    // is shown as output rather than treated as failure on its own.
                    this.Append(result.Error.TrimEnd());
                }

                this.Append(result.Succeeded ? $"{label}: done" : $"{label}: failed");
            }
            catch (Exception e)
            {
                this.Append($"{label}: {e.Message}");
            }
            finally
            {
                this.busy = false;
                this.Repaint();
            }
        }

        private void Append(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            this.log = string.IsNullOrEmpty(this.log) ? text : $"{this.log}\n{text}";
            this.Repaint();
        }
    }
}
