using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityMCP.Editor.Installer
{
    /// <summary>
    /// Helper utility for installing and configuring the TypeScript MCP client.
    /// </summary>
    public static class McpInstallHelper
    {
        private const string GITHUB_RELEASE_URL = "https://github.com/isuzu-shiranui/UnityMCP/releases/latest";
        private const string DOWNLOAD_URL_FORMAT = "https://github.com/isuzu-shiranui/UnityMCP/releases/download/v{0}/unity-mcp-build.zip";
        private const string DEFAULT_VERSION = "1.0.0";
        private const string NODE_DOWNLOAD_URL = "https://nodejs.org/en/download/";

        // Path to Claude Desktop config file
        private static string claudeConfigPath;
        public static string ClaudeConfigPath
        {
            get
            {
                if (string.IsNullOrEmpty(claudeConfigPath))
                {
                    claudeConfigPath = GetClaudeConfigPath();
                }
                return claudeConfigPath;
            }
        }

        /// <summary>
        /// Gets the appropriate Claude Desktop config path based on the operating system.
        /// </summary>
        private static string GetClaudeConfigPath()
        {
            if (Application.platform == RuntimePlatform.OSXEditor)
            {
                var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                return Path.Combine(homeDir, "Library/Application Support/Claude/claude_desktop_config.json");
            }
            else if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "Claude/claude_desktop_config.json");
            }
            else
            {
                // Linux or other platform - not officially supported
                return string.Empty;
            }
        }

        /// <summary>
        /// Checks if Node.js is installed on the system.
        /// </summary>
        /// <returns>True if Node.js is installed, false otherwise.</returns>
        public static bool IsNodeInstalled()
        {
            try
            {
                var process = new Process();
                process.StartInfo.FileName = "node";
                process.StartInfo.Arguments = "--version";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.CreateNoWindow = true;

                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                return !string.IsNullOrEmpty(output) && output.StartsWith("v");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Retrieves the latest version from GitHub releases.
        /// </summary>
        /// <returns>The latest version string or DEFAULT_VERSION if unavailable.</returns>
        public static async Task<string> GetLatestVersionAsync()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    // Set up user agent to avoid GitHub API limitations
                    client.DefaultRequestHeaders.Add("User-Agent", "Unity-MCP-Installer");

                    // GitHub API redirects from the 'latest' URL to the actual version
                    var response = await client.GetAsync(GITHUB_RELEASE_URL);

                    if (response.RequestMessage?.RequestUri != null)
                    {
                        var redirect = response.RequestMessage.RequestUri.ToString();
                        var versionRegex = new Regex(@"/v(\d+\.\d+\.\d+)");
                        var match = versionRegex.Match(redirect);

                        if (match.Success)
                        {
                            return match.Groups[1].Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"Error getting latest version: {ex.Message}");
            }

            return DEFAULT_VERSION;
        }

        /// <summary>
        /// Downloads and extracts the TypeScript client to the specified path.
        /// </summary>
        /// <param name="version">The version to download</param>
        /// <param name="destinationPath">The path to extract the client to</param>
        /// <param name="progressCallback">Optional callback to report download progress</param>
        /// <returns>A task representing the async operation</returns>
        public static async Task<bool> DownloadAndExtractClient(string version, string destinationPath, Action<float> progressCallback = null)
        {
            try
            {
                // Create directory if it doesn't exist
                if (!Directory.Exists(destinationPath))
                {
                    Directory.CreateDirectory(destinationPath);
                }

                // Temporary file path for the downloaded zip
                var tempZipPath = Path.Combine(Path.GetTempPath(), $"unity-mcp-{version}.zip");

                // Download URL
                var downloadUrl = string.Format(DOWNLOAD_URL_FORMAT, version);

                // Set up HTTP client
                using (var client = new HttpClient())
                {
                    // Set up user agent to avoid GitHub API limitations
                    client.DefaultRequestHeaders.Add("User-Agent", "Unity-MCP-Installer");

                    // Download the file with progress reporting
                    using (var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();

                        var totalBytes = response.Content.Headers.ContentLength ?? -1;

                        using (Stream contentStream = await response.Content.ReadAsStreamAsync(),
                                      fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            var buffer = new byte[8192];
                            long totalBytesRead = 0;
                            int bytesRead;

                            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await fileStream.WriteAsync(buffer, 0, bytesRead);

                                totalBytesRead += bytesRead;

                                if (totalBytes > 0 && progressCallback != null)
                                {
                                    var progress = (float)totalBytesRead / totalBytes;
                                    progressCallback(progress);
                                }
                            }
                        }
                    }
                }

                // Extract the zip file to the destination directory
                if (progressCallback != null)
                {
                    progressCallback(0.9f); // 90% progress - starting extraction
                }

                // Extract to a temp directory first to avoid overwriting files in use
                var tempExtractPath = Path.Combine(Path.GetTempPath(), $"unity-mcp-extract-{Guid.NewGuid()}");

                try
                {
                    // Extract to temp first
                    ZipFile.ExtractToDirectory(tempZipPath, tempExtractPath);

                    // Copy files from the temp directory to the final destination
                    CopyDirectory(tempExtractPath, destinationPath, true);
                }
                finally
                {
                    // Clean up temporary extraction directory
                    if (Directory.Exists(tempExtractPath))
                    {
                        Directory.Delete(tempExtractPath, true);
                    }
                }

                // Delete the temporary zip file
                if (File.Exists(tempZipPath))
                {
                    File.Delete(tempZipPath);
                }

                if (progressCallback != null)
                {
                    progressCallback(1.0f); // 100% progress - complete
                }

                return true;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"Error downloading and extracting client: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Copies a directory recursively with optional overwrite.
        /// </summary>
        /// <param name="sourceDir">Source directory path</param>
        /// <param name="destDir">Destination directory path</param>
        /// <param name="overwrite">Whether to overwrite existing files</param>
        private static void CopyDirectory(string sourceDir, string destDir, bool overwrite)
        {
            // Create destination directory if it doesn't exist
            if (!Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            // Copy files
            foreach (var filePath in Directory.GetFiles(sourceDir))
            {
                var fileName = Path.GetFileName(filePath);
                var destFilePath = Path.Combine(destDir, fileName);
                File.Copy(filePath, destFilePath, overwrite);
            }

            // Copy subdirectories recursively
            foreach (var dirPath in Directory.GetDirectories(sourceDir))
            {
                var dirName = Path.GetFileName(dirPath);
                var destSubDir = Path.Combine(destDir, dirName);
                CopyDirectory(dirPath, destSubDir, overwrite);
            }
        }

        /// <summary>
        /// Generates the configuration JSON for MCP.
        /// </summary>
        /// <param name="clientJsPath">The path to the client index.js file</param>
        /// <returns>A JSON string containing the configuration</returns>
        public static string GenerateMCPConfig(string clientJsPath)
        {
            try
            {
                // Normalize path to use the correct slashes for the platform
                var normalizedPath = NormalizePath(clientJsPath);

                // Create configuration object
                var configObject = new JObject
                {
                    ["mcpServers"] = new JObject
                    {
                        ["unity-mcp"] = new JObject
                        {
                            ["command"] = "node",
                            ["args"] = new JArray { normalizedPath }
                        }
                    }
                };

                return configObject.ToString(Formatting.Indented);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"Error generating Claude config: {ex.Message}");
                return "Error generating configuration.";
            }
        }

        /// <summary>
        /// Normalizes a file path according to the current platform.
        /// </summary>
        /// <param name="path">The path to normalize</param>
        /// <returns>The normalized path</returns>
        private static string NormalizePath(string path)
        {
            // For Windows, use double backslashes for JSON
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                return path.Replace("/", "\\");
            }

            // For other platforms, use forward slashes
            return path.Replace("\\", "/");
        }
    }
}
