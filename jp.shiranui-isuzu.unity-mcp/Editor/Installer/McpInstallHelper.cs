using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;

namespace UnityMCP.Editor.Installer
{
    /// <summary>
    /// Locates the Node.js toolchain the installer needs.
    /// </summary>
    /// <remarks>
    /// Everything else this class used to do — downloading a release zip, unpacking it, and
    /// writing a Claude Desktop config — is gone. See <see cref="McpNpmInstaller"/>.
    /// </remarks>
    public static class McpInstallHelper
    {
        /// <summary>
        /// Candidate absolute paths to the `node` binary, in priority order.
        /// Used as a fallback on macOS where Unity Editor's inherited PATH
        /// often does not include Homebrew's directory (issue #7).
        /// </summary>
        private static readonly string[] MacNodeFallbackPaths =
        {
            "/opt/homebrew/bin/node",   // Apple Silicon Homebrew
            "/usr/local/bin/node",      // Intel / manual Homebrew
            "/usr/bin/node",            // System default
        };

        /// <summary>
        /// Cached result of the last successful node lookup: the resolved executable
        /// (either "node" for a PATH hit or an absolute path for a fallback hit).
        /// Null when not yet looked up or when no installation was found.
        /// </summary>
        private static string resolvedNodeExecutable;

        /// <summary>
        /// Checks if Node.js is installed on the system. On macOS, falls back to
        /// common Homebrew / system paths when "node" is not resolvable via PATH
        /// (Unity Editor launched from Finder does not inherit the shell PATH).
        /// </summary>
        /// <returns>True if Node.js is installed, false otherwise.</returns>
        public static bool IsNodeInstalled()
        {
            return !string.IsNullOrEmpty(ResolveNodeExecutableOrNull());
        }

        /// <summary>
        /// Resolves the absolute or PATH-relative executable to use for spawning
        /// node. Returns an empty string if Node.js cannot be located.
        /// </summary>
        public static string ResolveNodeExecutable()
        {
            return ResolveNodeExecutableOrNull() ?? string.Empty;
        }

        /// <summary>
        /// Absolute path to npm's own entry script, or an empty string when it cannot be found.
        /// </summary>
        /// <remarks>
        /// npm is driven as <c>node npm-cli.js</c> rather than through the <c>npm</c> command.
        /// The Windows shim resolves npm's module directory relative to the working directory,
        /// so spawning <c>npm.cmd</c> from an arbitrary folder starts and then exits 1 with
        /// "Cannot find module ...\node_modules\npm\bin\npm-cli.js" — observed on a stock
        /// Node 22 install. Going through node sidesteps the shim entirely, and works the same
        /// on every platform.
        /// </remarks>
        public static string ResolveNpmCliScript()
        {
            if (npmCliScriptResolved)
            {
                return resolvedNpmCliScript;
            }

            npmCliScriptResolved = true;
            resolvedNpmCliScript = string.Empty;

            var nodeDirectory = ResolveNodeDirectory();

            if (string.IsNullOrEmpty(nodeDirectory))
            {
                return resolvedNpmCliScript;
            }

            // Windows keeps npm beside node; the usual Unix layout puts it one level up in lib.
            string[] candidates =
            {
                Path.Combine(nodeDirectory, "node_modules", "npm", "bin", "npm-cli.js"),
                Path.Combine(nodeDirectory, "..", "lib", "node_modules", "npm", "bin", "npm-cli.js"),
            };

            foreach (var candidate in candidates)
            {
                var full = Path.GetFullPath(candidate);
                if (File.Exists(full))
                {
                    resolvedNpmCliScript = full;
                    return resolvedNpmCliScript;
                }
            }

            return resolvedNpmCliScript;
        }

        /// <summary>True when both node and npm's entry script were located.</summary>
        public static bool IsNpmAvailable() => !string.IsNullOrEmpty(ResolveNpmCliScript());

        private static string resolvedNpmCliScript;
        private static bool npmCliScriptResolved;

        /// <summary>
        /// Directory holding the node executable, resolved through the OS when node was found
        /// on PATH rather than at a known absolute location.
        /// </summary>
        private static string ResolveNodeDirectory()
        {
            var node = ResolveNodeExecutableOrNull();

            if (string.IsNullOrEmpty(node))
            {
                return string.Empty;
            }

            if (node != "node")
            {
                return Path.GetDirectoryName(node) ?? string.Empty;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Application.platform == RuntimePlatform.WindowsEditor ? "where" : "which",
                    Arguments = "node",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    return string.Empty;
                }

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(10000);

                // `where` can report several matches; the first is the one PATH would pick.
                var first = output
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(line => line.Trim().Length > 0)
                    ?.Trim();

                return string.IsNullOrEmpty(first) ? string.Empty : Path.GetDirectoryName(first) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ResolveNodeExecutableOrNull()
        {
            if (!string.IsNullOrEmpty(resolvedNodeExecutable))
            {
                return resolvedNodeExecutable;
            }

            // First attempt: plain "node" via PATH (Windows / Linux / macOS with shell-inherited PATH).
            if (TryRunNode("node"))
            {
                resolvedNodeExecutable = "node";
                return resolvedNodeExecutable;
            }

            // Second attempt (macOS only): well-known Homebrew / system install locations.
            if (Application.platform == RuntimePlatform.OSXEditor)
            {
                foreach (var candidate in MacNodeFallbackPaths)
                {
                    if (File.Exists(candidate) && TryRunNode(candidate))
                    {
                        resolvedNodeExecutable = candidate;
                        return resolvedNodeExecutable;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Attempts to invoke "<paramref name="exe"/> --version" and returns true
        /// iff the process exits cleanly and prints a version string starting with 'v'.
        /// </summary>
        private static bool TryRunNode(string exe)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    return false;
                }

                var output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(3000))
                {
                    try { process.Kill(); } catch { /* ignore */ }
                    return false;
                }

                return process.ExitCode == 0
                       && !string.IsNullOrEmpty(output)
                       && output.TrimStart().StartsWith("v");
            }
            catch
            {
                return false;
            }
        }

        // Removed with the move to npm: the GitHub release download, the zip
        // extraction and the hand-written Claude Desktop config. Extracting an archive
        // could never produce a working install because the server has runtime
        // dependencies and an unpacked archive has no node_modules; npm supplies both.
        // Agent registration now goes through the CLI, which knows five agents rather
        // than one. See McpNpmInstaller.
    }
}
