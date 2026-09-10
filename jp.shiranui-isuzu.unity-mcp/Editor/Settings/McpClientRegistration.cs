using System;
using System.Collections.Generic;
using System.IO;

using UnityEngine;

namespace UnityMCP.Editor.Settings
{
    /// <summary>
    /// Whether an MCP client on this machine is pointed at this Editor.
    /// </summary>
    /// <remarks>
    /// The Settings page is a checklist, and its last step — registering a client — was the one
    /// step it could neither do nor report on, so a user had no way to tell a finished setup from
    /// an unfinished one.
    /// <para>
    /// The test is a search for this Editor's own URL rather than a reading of each client's
    /// format. The URL carries the port, which is unique to this Editor while it runs, so a hit
    /// means that file reaches this Editor and nothing else does. It also gives the right answer
    /// for free when the port has moved: the old entry no longer names this URL, and an entry
    /// that cannot reach the Editor is not a finished setup.
    /// </para>
    /// </remarks>
    internal static class McpClientRegistration
    {
        /// <summary>The configuration files the CLI's setup writes, in their usual places.</summary>
        /// <remarks>
        /// Duplicating the CLI's catalog here would be worse than this list: the Editor needs only
        /// the paths, and reaching for the CLI to answer a question drawn on every repaint would
        /// put a process launch in the GUI loop.
        /// </remarks>
        public static IEnumerable<string> ConfigPaths(string projectRoot)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (home.Length > 0)
            {
                // Both are documented overrides of the CLI's, so a machine that sets one has its
                // configuration somewhere this would otherwise miss.
                var claude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                var codex = Environment.GetEnvironmentVariable("CODEX_HOME");

                yield return string.IsNullOrEmpty(claude)
                    ? Path.Combine(home, ".claude.json")
                    : Path.Combine(claude, ".claude.json");

                yield return string.IsNullOrEmpty(codex)
                    ? Path.Combine(home, ".codex", "config.toml")
                    : Path.Combine(codex, "config.toml");

                yield return Path.Combine(home, ".cursor", "mcp.json");
                yield return Path.Combine(home, ".gemini", "settings.json");

                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    yield return Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Claude", "claude_desktop_config.json");
                }
                else if (Application.platform == RuntimePlatform.OSXEditor)
                {
                    yield return Path.Combine(
                        home, "Library", "Application Support", "Claude", "claude_desktop_config.json");
                }
            }

            if (projectRoot.Length == 0)
            {
                yield break;
            }

            // Project scope: setup --scope project writes these inside the Unity project.
            yield return Path.Combine(projectRoot, ".mcp.json");
            yield return Path.Combine(projectRoot, ".vscode", "mcp.json");
        }

        /// <summary>The configuration files that name this Editor's URL.</summary>
        public static List<string> Registered(string mcpUrl, string projectRoot)
        {
            var found = new List<string>();

            if (string.IsNullOrEmpty(mcpUrl))
            {
                return found;
            }

            foreach (var path in ConfigPaths(projectRoot))
            {
                try
                {
                    if (File.Exists(path) && File.ReadAllText(path).Contains(mcpUrl))
                    {
                        found.Add(path);
                    }
                }
                catch (Exception)
                {
                    // A file being written, or one this user cannot read, is not an answer either
                    // way. Reporting it as registered would be worse than leaving it out.
                }
            }

            return found;
        }
    }
}
