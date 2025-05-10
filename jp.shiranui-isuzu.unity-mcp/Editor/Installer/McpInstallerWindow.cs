using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor.Settings;

namespace UnityMCP.Editor.Installer
{
    /// <summary>
    /// Editor window for installing and configuring the MCP TypeScript client.
    /// </summary>
    public class McpInstallerWindow : EditorWindow
    {
        // UI state variables
        private string installPath = "";
        private string version = "1.0.0";
        private bool isDownloading;
        private float downloadProgress;
        private string statusMessage = "";
        private bool isNodeInstalled;
        private bool showConfigPreview = true;
        private Vector2 scrollPosition;
        private GUIStyle headerStyle;
        private GUIStyle subHeaderStyle;
        private GUIStyle codeStyle;
        private Color defaultBackgroundColor;

        private static readonly string BraceColor = "#CCCCFF";
        private static readonly string QuoteColor = "#88FF88";
        private static readonly string KeyColor = "#FF88FF";
        private static readonly string ValueColor = "#FFFF00";
        private static readonly string NumberColor = "#FF8888";
        private static readonly string BoolColor = "#8888FF";
        private static readonly string NullColor = "#888888";

        // Constants
        private const string WINDOW_TITLE = "MCP TypeScript Installer";

        /// <summary>
        /// Shows the installer window.
        /// </summary>
        [MenuItem("Tools/Unity MCP/TypeScript Client Installer")]
        public static void ShowWindow()
        {
            var window = GetWindow<McpInstallerWindow>(WINDOW_TITLE);
            window.minSize = new Vector2(550, 550);
            window.Show();
        }

        /// <summary>
        /// Initializes the window styles and checks for Node.js installation.
        /// </summary>
        private void OnEnable()
        {
            this.installPath = McpSettings.instance.clientInstallationPath;

            // Set default install path to Documents/UnityMCP folder
            if (string.IsNullOrEmpty(this.installPath))
            {
                var documentsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                this.installPath = documentsFolder;
            }

            // Check for Node.js installation
            Task.Run(() =>
            {
                this.isNodeInstalled = McpInstallHelper.IsNodeInstalled();
            });

            this.FetchLatestVersion();

            // Initialize styles in OnGUI to ensure EditorStyles are initialized
        }

        /// <summary>
        /// Draws the editor window UI.
        /// </summary>
        private void OnGUI()
        {
            this.InitializeStyles();

            this.scrollPosition = EditorGUILayout.BeginScrollView(this.scrollPosition);

            this.DrawHeader();
            EditorGUILayout.Space(10);

            this.DrawNodeJsSection();
            EditorGUILayout.Space(10);

            this.DrawInstallationSection();
            EditorGUILayout.Space(10);

            this.DrawClaudeConfigSection();
            EditorGUILayout.Space(10);

            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// Initializes and configures GUI styles for the window.
        /// </summary>
        private void InitializeStyles()
        {
            if (this.headerStyle == null)
            {
                this.headerStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 16,
                    margin = new RectOffset(0, 0, 10, 5)
                };
            }

            if (this.subHeaderStyle == null)
            {
                this.subHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 14,
                    margin = new RectOffset(0, 0, 5, 3)
                };
            }

            if (this.codeStyle == null)
            {
                this.codeStyle = new GUIStyle(EditorStyles.textArea)
                {
                    font = EditorStyles.standardFont,
                    wordWrap = true,
                    richText = true
                };
            }

            this.defaultBackgroundColor = GUI.backgroundColor;
        }

        /// <summary>
        /// Draws the header section with title and description.
        /// </summary>
        private void DrawHeader()
        {
            GUILayout.Label("MCP TypeScript Client Installer", this.headerStyle);

            EditorGUILayout.HelpBox(
                "This utility helps you install and configure the TypeScript client for the Unity MCP framework, " +
                "which enables integration and other MCP-compatible clients.",
                MessageType.Info);
        }

        /// <summary>
        /// Draws the Node.js verification section.
        /// </summary>
        private void DrawNodeJsSection()
        {
            GUILayout.Label("1. Node.js Installation Check", this.subHeaderStyle);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            if (this.isNodeInstalled)
            {
                EditorGUILayout.HelpBox("✅ Node.js is installed and available on your system.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "❌ Node.js is not installed or not found in your system PATH. " +
                    "The TypeScript client requires Node.js to run.",
                    MessageType.Warning);

                EditorGUILayout.Space(5);

                if (GUILayout.Button("Download Node.js", GUILayout.Height(30)))
                {
                    Application.OpenURL("https://nodejs.org/en/download/");
                }

                EditorGUILayout.Space(5);

                if (GUILayout.Button("Recheck Node.js Installation", GUILayout.Height(25)))
                {
                    Task.Run(() =>
                    {
                        this.isNodeInstalled = McpInstallHelper.IsNodeInstalled();
                    });
                }
            }

            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Draws the client installation section.
        /// </summary>
        private void DrawInstallationSection()
        {
            GUILayout.Label("2. TypeScript Client Installation", this.subHeaderStyle);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Version input
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Version:", GUILayout.Width(70));
            this.version = EditorGUILayout.TextField(this.version);

            if (GUILayout.Button("Latest", GUILayout.Width(60)))
            {
                this.FetchLatestVersion();
            }
            EditorGUILayout.EndHorizontal();

            // Installation path
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Install Path:", GUILayout.Width(70));

            this.installPath = EditorGUILayout.TextField(this.installPath);
            if (McpSettings.instance.clientInstallationPath != this.installPath)
            {
                McpSettings.instance.clientInstallationPath = this.installPath;
                McpSettings.instance.Save();
            }

            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                var selectedPath = EditorUtility.SaveFolderPanel(
                    "Select TypeScript Client Installation Folder", this.installPath,
                    "");

                if (!string.IsNullOrEmpty(selectedPath))
                {
                    this.installPath = selectedPath;
                }
            }
            EditorGUILayout.EndHorizontal();

            // Download and install button
            EditorGUILayout.Space(5);
            GUI.enabled = !this.isDownloading && !string.IsNullOrEmpty(this.installPath);

            if (GUILayout.Button("Download and Install TypeScript Client", GUILayout.Height(30)))
            {
                this.DownloadAndInstallClient();
            }

            GUI.enabled = true;

            // Progress bar
            if (this.isDownloading)
            {
                EditorGUILayout.Space(5);
                EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(false, 20f), this.downloadProgress, "Downloading...");
            }

            // Status message
            if (!string.IsNullOrEmpty(this.statusMessage))
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.HelpBox(this.statusMessage, MessageType.Info);
            }

            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Draws the MCP configuration section.
        /// </summary>
        private void DrawClaudeConfigSection()
        {
            GUILayout.Label("3. MCP Configuration", this.subHeaderStyle);

            // Path to index.js
            var installDir = Path.Combine(this.installPath, "UnityMCP/build");
            var clientJsPath = Path.Combine(installDir, "index.js").Replace("/", "\\");
            var displayPath = clientJsPath;

            // Highlight the path for better visibility
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Client Path:", GUILayout.Width(80));

            var pathStyle = new GUIStyle(EditorStyles.textField);
            pathStyle.fontStyle = FontStyle.Bold;
            GUILayout.TextField(displayPath.Replace("\\", @"\\"), pathStyle);

            if (GUILayout.Button("Copy Path", GUILayout.Width(80)))
            {
                GUIUtility.systemCopyBuffer = displayPath;
                this.ShowNotification(new GUIContent("Path copied to clipboard!"));
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            // Show configuration preview toggle
            this.showConfigPreview = EditorGUILayout.Foldout(this.showConfigPreview, "Show Configuration Preview", true);

            if (this.showConfigPreview)
            {
                EditorGUILayout.Space(5);

                var configJson = McpInstallHelper.GenerateMCPConfig(clientJsPath);

                // Make the JSON more readable with syntax highlighting
                var coloredJson = HighlightKeysAndValues(configJson,
                    new[] { clientJsPath.Replace("/", "\\") });

                // Display the config in a text area with enhanced styling
                GUI.backgroundColor = new Color(0.15f, 0.15f, 0.15f);
                EditorGUILayout.LabelField("Configuration JSON:", EditorStyles.boldLabel);
                var richCodeStyle = new GUIStyle(EditorStyles.textArea)
                {
                    richText = true,
                    wordWrap = true,
                    fontSize = 12
                };
                EditorGUILayout.TextArea(coloredJson, richCodeStyle, GUILayout.Height(150));
                GUI.backgroundColor = this.defaultBackgroundColor;

                EditorGUILayout.Space(5);

                // Copy config button
                if (GUILayout.Button("Copy Configuration to Clipboard", GUILayout.Height(25)))
                {
                    GUIUtility.systemCopyBuffer = configJson;
                    this.ShowNotification(new GUIContent("Configuration copied to clipboard!"));
                }
            }

            EditorGUILayout.Space(5);

            // Add manual configuration instructions
            EditorGUILayout.LabelField("Manual Configuration Steps:", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "1. Copy the configuration above\n" +
                "2. Open Claude Desktop\n" +
                "3. Click on the Claude menu and select 'Settings...'\n" +
                "4. Click on 'Developer' tab, then 'Edit Config'\n" +
                "5. Paste the configuration and save\n",
                MessageType.Info);
        }


        /// <summary>
        /// Downloads and installs the TypeScript client.
        /// </summary>
        private async void DownloadAndInstallClient()
        {
            if (string.IsNullOrEmpty(this.installPath))
            {
                this.statusMessage = "Error: Please specify an installation path.";
                return;
            }

            this.isDownloading = true;
            this.downloadProgress = 0f;
            this.statusMessage = "Starting download...";
            this.Repaint();

            try
            {
                var installDir = Path.Combine(this.installPath, "UnityMCP/build");
                // Download and extract the client
                var success = await McpInstallHelper.DownloadAndExtractClient(this.version, installDir,
                    progress => {
                        this.downloadProgress = progress;
                        this.Repaint();
                    }
                );

                if (success)
                {
                    this.statusMessage = $"TypeScript client v{this.version} successfully installed to {installDir}";

                    // Check if index.js exists in the expected location
                    var indexJsPath = Path.Combine(installDir, "/index.js");
                    if (!File.Exists(indexJsPath))
                    {
                        this.statusMessage += "\nWarning: index.js not found at the expected location. The installation may be incomplete.";
                    }
                }
                else
                {
                    this.statusMessage = "Error: Failed to download or extract the TypeScript client.";
                }
            }
            catch (Exception ex)
            {
                this.statusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                this.isDownloading = false;
            }

            this.Repaint();
        }

        /// <summary>
        /// Fetches the latest version from GitHub.
        /// </summary>
        private async void FetchLatestVersion()
        {
            this.statusMessage = "Checking for latest version...";
            this.isDownloading = true;
            this.Repaint();

            try
            {
                var latestVersion = await McpInstallHelper.GetLatestVersionAsync();
                this.version = latestVersion;
                this.statusMessage = $"Latest version found: v{latestVersion}";
            }
            catch (Exception ex)
            {
                this.statusMessage = $"Error fetching latest version: {ex.Message}";
            }
            finally
            {
                this.isDownloading = false;
            }

            this.Repaint();
        }


        /// <summary>
        /// Highlights key-value pairs in the JSON text
        /// </summary>
        private static string HighlightKeysAndValues(string text, string[] specialPaths)
        {
            // Match key-value pairs: "key": value
            // where value can be "string", number, {object}, [array], true, false, or null
            var keyValuePattern = new Regex(
                @"""([^""]+)""\s*:\s*(?:""([^""]*)""|([0-9]+(?:\.[0-9]+)?)|(\{)|(\[)|true|false|null)"
            );

            return keyValuePattern.Replace(text, match =>
            {
                var key = match.Groups[1].Value;

                // Highlight the key
                var replacement = $"<color={QuoteColor}>\"</color><color={KeyColor}>{key}</color><color={QuoteColor}>\"</color>:";

                // Highlight the value based on its type
                if (match.Groups[2].Success)
                {
                    // String value
                    var value = match.Groups[2].Value;

                    // Check if this is a special path to highlight differently
                    var isSpecialPath = false;
                    foreach (var path in specialPaths)
                    {
                        if (value == path)
                        {
                            replacement += $" <color={QuoteColor}>\"</color><color={ValueColor}>{value}</color><color={QuoteColor}>\"</color>";
                            isSpecialPath = true;
                            break;
                        }
                    }

                    if (!isSpecialPath)
                    {
                        // Regular string value
                        replacement += $" <color={QuoteColor}>\"</color>{value}<color={QuoteColor}>\"</color>";
                    }
                }
                else if (match.Groups[3].Success)
                {
                    // Number value
                    replacement += $" <color={NumberColor}>{match.Groups[3].Value}</color>";
                }
                else if (match.Groups[4].Success || match.Groups[5].Success)
                {
                    // Object or array start
                    replacement += match.Groups[4].Success ? " {" : " [";
                }
                else if (match.Value.Contains("true") || match.Value.Contains("false"))
                {
                    // Boolean value
                    replacement += match.Value.Contains("true")
                        ? $" <color={BoolColor}>true</color>"
                        : $" <color={BoolColor}>false</color>";
                }
                else if (match.Value.Contains("null"))
                {
                    // Null value
                    replacement += $" <color={NullColor}>null</color>";
                }

                return replacement;
            });
        }
    }
}
