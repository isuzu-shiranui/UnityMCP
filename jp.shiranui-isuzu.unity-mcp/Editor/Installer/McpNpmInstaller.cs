using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Debug = UnityEngine.Debug;

// UnityEditor also has a PackageInfo, so the Package Manager one is named explicitly.
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace UnityMCP.Editor.Installer
{
    /// <summary>
    /// Installs the companion npm package and hands setup over to its CLI.
    /// </summary>
    /// <remarks>
    /// The previous installer downloaded a zip from GitHub Releases and unpacked it. That could
    /// not work for long: the server has runtime dependencies, and an unpacked archive has no
    /// node_modules, so it installed something that could not start. Letting npm do the install
    /// gets the dependencies and the bin links for free.
    /// <para>
    /// The npm version is pinned to this Unity package's own version rather than taken as
    /// "latest". The two halves speak one protocol and are released together; fetching whatever
    /// npm currently calls latest is how you end up with an Editor and a server that disagree.
    /// </para>
    /// </remarks>
    public static class McpNpmInstaller
    {
        public const string NpmPackageName = "unity-mcp-ts";

        /// <summary>Where the npm package is installed, under the project's Library folder.</summary>
        public static string InstallRoot =>
            Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..", "Library", "UnityMCP"));

        public static string CliPath =>
            Path.Combine(InstallRoot, "node_modules", NpmPackageName, "build", "cli.js");

        public static string ServerPath =>
            Path.Combine(InstallRoot, "node_modules", NpmPackageName, "build", "index.js");

        /// <summary>True when the companion package is already installed here.</summary>
        public static bool IsInstalled => File.Exists(CliPath) && File.Exists(ServerPath);

        /// <summary>
        /// This Unity package's version, used to pin the npm install.
        /// </summary>
        public static string PackageVersion
        {
            get
            {
                var info = PackageInfo.FindForAssembly(typeof(McpNpmInstaller).Assembly);
                return info?.version;
            }
        }

        /// <summary>Version of the installed npm package, or null when it is absent.</summary>
        public static string InstalledVersion
        {
            get
            {
                var manifest = Path.Combine(InstallRoot, "node_modules", NpmPackageName, "package.json");

                if (!File.Exists(manifest))
                {
                    return null;
                }

                try
                {
                    return Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(manifest))["version"]?.ToString();
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// True when what is installed does not match this package. Surfaced in the installer
        /// window, because a mismatch is the one failure that produces confusing behaviour
        /// rather than an outright error.
        /// </summary>
        public static bool IsVersionMismatched =>
            IsInstalled && PackageVersion != null && InstalledVersion != PackageVersion;

        public sealed class CommandResult
        {
            public bool Succeeded { get; set; }

            public string Output { get; set; }

            public string Error { get; set; }
        }

        /// <summary>
        /// Runs `npm install unity-mcp-ts@&lt;version&gt;` into the project's Library folder.
        /// </summary>
        public static async Task<CommandResult> InstallAsync()
        {
            var version = PackageVersion;

            if (string.IsNullOrEmpty(version))
            {
                return new CommandResult
                {
                    Succeeded = false,
                    Error = "Could not determine this package's version, so the matching npm version is unknown.",
                };
            }

            Directory.CreateDirectory(InstallRoot);

            // A package.json here keeps npm from walking up and installing into the Unity
            // project root, or worse, somewhere above it.
            var manifest = Path.Combine(InstallRoot, "package.json");
            if (!File.Exists(manifest))
            {
                File.WriteAllText(
                    manifest,
                    "{\n  \"name\": \"unity-mcp-install-root\",\n  \"private\": true\n}\n",
                    new UTF8Encoding(false));
            }

            var node = McpInstallHelper.ResolveNodeExecutable();
            var npmCli = McpInstallHelper.ResolveNpmCliScript();

            if (string.IsNullOrEmpty(node) || string.IsNullOrEmpty(npmCli))
            {
                return new CommandResult
                {
                    Succeeded = false,
                    Error = "Node.js and npm were not found. Install Node.js 18 or newer, then restart " +
                            "the Editor so it inherits the updated PATH.",
                };
            }

            return await RunAsync(
                node,
                $"\"{npmCli}\" install {NpmPackageName}@{version} --no-audit --no-fund",
                InstallRoot);
        }

        /// <summary>
        /// Runs the CLI's own setup, which registers the server with every agent found on the
        /// machine and installs the skill for those that support one.
        /// </summary>
        /// <remarks>
        /// Deliberately delegated rather than reimplemented in C#. The CLI already knows five
        /// agents, two config formats and where each keeps its skills; a second implementation
        /// here would be the same duplication this release exists to remove.
        /// </remarks>
        public static async Task<CommandResult> RunSetupAsync(string agent = null)
        {
            if (!IsInstalled)
            {
                return new CommandResult
                {
                    Succeeded = false,
                    Error = "The npm package is not installed yet.",
                };
            }

            var node = McpInstallHelper.ResolveNodeExecutable();

            if (string.IsNullOrEmpty(node))
            {
                return new CommandResult { Succeeded = false, Error = "Node.js was not found." };
            }

            var arguments = $"\"{CliPath}\" setup";
            if (!string.IsNullOrEmpty(agent))
            {
                arguments += $" --agent {agent}";
            }

            return await RunAsync(node, arguments, InstallRoot);
        }

        /// <summary>Removes the installed npm package and everything under the install root.</summary>
        public static CommandResult Uninstall()
        {
            try
            {
                if (Directory.Exists(InstallRoot))
                {
                    Directory.Delete(InstallRoot, true);
                }

                return new CommandResult { Succeeded = true, Output = $"Removed {InstallRoot}" };
            }
            catch (Exception e)
            {
                return new CommandResult { Succeeded = false, Error = e.Message };
            }
        }

        private static Task<CommandResult> RunAsync(string fileName, string arguments, string workingDirectory)
        {
            var completion = new TaskCompletionSource<CommandResult>();

            if (string.IsNullOrEmpty(fileName))
            {
                completion.SetResult(new CommandResult
                {
                    Succeeded = false,
                    Error = "npm was not found on PATH. Install Node.js, then reopen the Editor so it inherits the updated PATH.",
                });

                return completion.Task;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            Task.Run(() =>
            {
                try
                {
                    using var process = new Process { StartInfo = startInfo };
                    var output = new StringBuilder();
                    var error = new StringBuilder();

                    process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                    process.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit();

                    completion.SetResult(new CommandResult
                    {
                        // npm writes progress and warnings to stderr even on success, so the
                        // exit code is the only reliable verdict.
                        Succeeded = process.ExitCode == 0,
                        Output = output.ToString(),
                        Error = error.ToString(),
                    });
                }
                catch (Exception e)
                {
                    completion.SetResult(new CommandResult { Succeeded = false, Error = e.Message });
                }
            });

            return completion.Task;
        }
    }
}
