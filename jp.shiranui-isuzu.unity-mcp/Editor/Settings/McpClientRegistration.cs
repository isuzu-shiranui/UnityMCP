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
    /// The test is a search of each file's text rather than a reading of each client's format.
    /// The URL carries the port, which is unique to this Editor while it runs, so a hit means
    /// that file reaches this Editor and nothing else does. It also gives the right answer for
    /// free when the port has moved: the old entry no longer names this URL, and an entry that
    /// cannot reach the Editor is not a finished setup. A stdio entry carries no URL to match,
    /// so it is recognised by the bridge subcommand and the project name instead.
    /// </para>
    /// </remarks>
    internal static class McpClientRegistration
    {
        /// <summary>The CLI subcommand a stdio entry launches.</summary>
        private const string StdioSubcommand = "mcp-stdio";

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

        /// <summary>The configuration files that point a client at this Editor.</summary>
        /// <remarks>
        /// Two shapes, not one. Claude Desktop cannot open a local HTTP server, so its entry
        /// launches the CLI's stdio bridge and names the project rather than the URL. Looking only
        /// for the URL reported a working registration as missing, next to a button offering to
        /// write the one that was already there.
        /// </remarks>
        public static List<string> Registered(string mcpUrl, string projectRoot, string projectName = null)
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
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    var text = File.ReadAllText(path);

                    if (text.Contains(mcpUrl) || BridgedTo(text, projectName))
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

        /// <summary>Whether a stdio entry in this file names this project.</summary>
        /// <remarks>
        /// The two have to appear together. Looked for anywhere in the file, an entry bridging
        /// another project plus any mention of this one's name read as registered, and Unity's
        /// default product name is "My project", which every unrenamed project shares. The name is
        /// matched as a whole argument rather than as a substring, so "Demo" does not answer for
        /// an entry whose project is "DemoScene".
        /// </remarks>
        private static bool BridgedTo(string text, string projectName)
        {
            if (string.IsNullOrEmpty(projectName))
            {
                return false;
            }

            var at = text.IndexOf(StdioSubcommand, StringComparison.Ordinal);

            while (at >= 0)
            {
                // The arguments of one entry, which end at the closing bracket of its list.
                var end = text.IndexOf(']', at);
                var entry = end < 0 ? text.Substring(at) : text.Substring(at, end - at);

                foreach (var argument in entry.Split('"', '\''))
                {
                    if (string.Equals(argument.Trim(), projectName, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                at = text.IndexOf(StdioSubcommand, at + StdioSubcommand.Length, StringComparison.Ordinal);
            }

            return false;
        }
    }
}
