using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Debug = UnityEngine.Debug;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Publishes this Editor's connection details to a well-known directory so clients can
    /// find it without guessing.
    /// </summary>
    /// <remarks>
    /// A file in a per-machine local directory rather than a network announce. That makes
    /// every instance it reports an Editor on this machine, keeps a new Editor visible the
    /// moment it publishes instead of at the next announce, and lets discovery carry the
    /// auth token.
    /// <para>
    /// Modelled on the port/descriptor file used by Unity's own <c>com.unity.pipeline</c>.
    /// </para>
    /// </remarks>
    internal static class McpInstanceDescriptor
    {
        /// <summary>Root of everything this package keeps on the machine.</summary>
        public static string StateRoot
        {
            get
            {
                var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                if (string.IsNullOrEmpty(root))
                {
                    root = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".local",
                        "share");
                }

                return Path.Combine(root, "UnityMCP");
            }
        }

        /// <summary>Directory holding one descriptor per running Editor.</summary>
        public static string DirectoryPath => Path.Combine(StateRoot, "instances");

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        /// <summary>
        /// Path of this project's descriptor. Keyed by project path so reopening the same
        /// project reuses the file rather than accumulating one per session.
        /// </summary>
        public static string PathFor(string projectPath)
        {
            return Path.Combine(DirectoryPath, $"{HashProjectPath(projectPath)}.json");
        }

        /// <summary>Writes (or overwrites) the descriptor for this Editor.</summary>
        public static void Write(
            string projectPath,
            string projectName,
            string unityVersion,
            int port,
            int preferredPort,
            string token,
            string protocolVersion,
            string[] mcpProtocolVersions)
        {
            try
            {
                CreateStateDirectory(DirectoryPath);

                var payload = new JObject
                {
                    ["projectPath"] = projectPath,
                    ["projectName"] = projectName,
                    ["unityVersion"] = unityVersion,
                    ["port"] = port,
                    ["preferredPort"] = preferredPort,
                    ["portMismatch"] = port != preferredPort,
                    ["token"] = token,
                    ["pid"] = Process.GetCurrentProcess().Id,
                    ["protocolVersion"] = protocolVersion,
                    ["endpoint"] = $"http://127.0.0.1:{port}",
                    ["mcpUrl"] = $"http://127.0.0.1:{port}/mcp",
                    ["mcpProtocolVersions"] = new JArray(mcpProtocolVersions ?? Array.Empty<string>()),
                };

                WriteSecret(PathFor(projectPath), payload.ToString(Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogError($"[McpInstanceDescriptor] Could not publish descriptor: {e.Message}");
            }
        }

        /// <summary>Removes this Editor's descriptor.</summary>
        public static void Delete(string projectPath)
        {
            try
            {
                var path = PathFor(projectPath);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[McpInstanceDescriptor] Could not remove descriptor: {e.Message}");
            }
        }

        /// <summary>
        /// Deletes descriptors whose owning process is gone.
        /// </summary>
        /// <remarks>
        /// An Editor killed rather than closed leaves its file behind. Without this sweep the
        /// stale entry would look like a second running Editor, which is exactly the failure
        /// mode the UDP scheme had.
        /// </remarks>
        public static void RemoveStale()
        {
            try
            {
                if (!Directory.Exists(DirectoryPath))
                {
                    return;
                }

                foreach (var file in Directory.GetFiles(DirectoryPath, "*.json"))
                {
                    int pid;

                    try
                    {
                        var payload = JObject.Parse(File.ReadAllText(file));
                        pid = payload["pid"]?.Value<int>() ?? 0;
                    }
                    catch
                    {
                        // Unreadable descriptors are useless to clients too.
                        TryDelete(file);
                        continue;
                    }

                    if (pid > 0 && !IsProcessAlive(pid))
                    {
                        TryDelete(file);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[McpInstanceDescriptor] Stale sweep failed: {e.Message}");
            }
        }

        /// <summary>
        /// Creates a directory under <see cref="StateRoot"/> for credential files, and restricts
        /// it and the root to their owner.
        /// </summary>
        /// <remarks>
        /// Both are restricted on every call rather than only when this call creates them: an
        /// install from an earlier version already has them, with the default mode. On Windows
        /// the user profile is already private to the account, so nothing is changed there.
        /// </remarks>
        public static void CreateStateDirectory(string directory)
        {
            Directory.CreateDirectory(directory);
            RestrictToOwner(StateRoot, "700");
            RestrictToOwner(directory, "700");
        }

        /// <summary>
        /// Writes a credential file that only its owner can read.
        /// </summary>
        /// <remarks>
        /// chmod needs the file to exist, so on Unix it is created empty and restricted before the
        /// contents go in. Restricted after writing instead, the contents are readable under the
        /// umask's default mode until chmod runs. Writing into an existing file keeps its mode.
        /// </remarks>
        public static void WriteSecret(string path, string contents)
        {
            if (!IsWindows)
            {
                File.WriteAllBytes(path, Array.Empty<byte>());
                RestrictToOwner(path, "600");
            }

            File.WriteAllText(path, contents, new UTF8Encoding(false));
        }

        /// <summary>
        /// Applies a Unix mode through chmod, and logs an error when chmod does not confirm it.
        /// </summary>
        /// <remarks>
        /// The file is still written, since the CLI and every registered client read it. Other users
        /// on the machine may then be able to read it, which is what the error says.
        /// </remarks>
        private static void RestrictToOwner(string path, string mode)
        {
            if (IsWindows)
            {
                return;
            }

            try
            {
                using var chmod = Process.Start(new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"{mode} \"{path}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

                if (chmod == null || !chmod.WaitForExit(2000))
                {
                    ReportNotRestricted(path,$"chmod {mode} did not finish");
                }
                else if (chmod.ExitCode != 0)
                {
                    ReportNotRestricted(path,$"chmod {mode} exited with code {chmod.ExitCode}");
                }
            }
            catch (Exception e)
            {
                ReportNotRestricted(path,$"chmod {mode} could not run: {e.Message}");
            }
        }

        private static void ReportNotRestricted(string path, string reason)
        {
            Debug.LogError(
                $"[McpInstanceDescriptor] Could not restrict {path} to its owner ({reason}). " +
                "Other users on this machine may be able to read it until it is restricted by hand.");
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                // No such process.
                return false;
            }
            catch
            {
                // Access denied and similar: assume alive rather than delete a live entry.
                return true;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Another Editor may be rewriting it right now; it will be swept later.
            }
        }

        public static string HashProjectPath(string projectPath)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(projectPath ?? string.Empty));

            var builder = new StringBuilder(16);
            for (var i = 0; i < 8; i++)
            {
                builder.Append(hash[i].ToString("x2"));
            }

            return builder.ToString();
        }
    }
}
