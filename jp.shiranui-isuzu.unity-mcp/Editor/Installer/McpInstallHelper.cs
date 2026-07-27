using System;
using System.Diagnostics;
using System.IO;
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
        /// Resolves the executable to use for spawning npm, or an empty string when it cannot
        /// be found.
        /// </summary>
        /// <remarks>
        /// On Windows npm is a .cmd shim, which <c>Process.Start</c> will not launch without
        /// the extension, so the name is resolved rather than assumed. Where node was found at
        /// an absolute path — the macOS Homebrew case — npm is looked for beside it, since a
        /// node that is not on PATH usually means npm is not either.
        /// </remarks>
        public static string ResolveNpmExecutable()
        {
            if (!string.IsNullOrEmpty(resolvedNpmExecutable))
            {
                return resolvedNpmExecutable;
            }

            var node = ResolveNodeExecutableOrNull();

            if (!string.IsNullOrEmpty(node) && node != "node")
            {
                var beside = Path.Combine(Path.GetDirectoryName(node) ?? string.Empty, "npm");
                if (File.Exists(beside) && TryRunVersion(beside))
                {
                    resolvedNpmExecutable = beside;
                    return resolvedNpmExecutable;
                }
            }

            foreach (var candidate in Application.platform == RuntimePlatform.WindowsEditor
                         ? new[] { "npm.cmd", "npm" }
                         : new[] { "npm" })
            {
                if (TryRunVersion(candidate))
                {
                    resolvedNpmExecutable = candidate;
                    return resolvedNpmExecutable;
                }
            }

            return string.Empty;
        }

        private static string resolvedNpmExecutable;

        /// <summary>Returns true when "&lt;exe&gt; --version" exits cleanly.</summary>
        private static bool TryRunVersion(string exe)
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

                process.StandardOutput.ReadToEnd();
                process.WaitForExit(10000);
                return process.HasExited && process.ExitCode == 0;
            }
            catch
            {
                return false;
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
