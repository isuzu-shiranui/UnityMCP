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
                Directory.CreateDirectory(DirectoryPath);

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

                var path = PathFor(projectPath);
                File.WriteAllText(path, payload.ToString(Formatting.Indented), new UTF8Encoding(false));
                RestrictToOwner(path);
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
        /// Makes a credential file readable by its owner only. On Windows the user profile is
        /// already private to the account, so only Unix permissions are touched.
        /// </summary>
        public static void RestrictToOwner(string path)
        {
            if (Path.DirectorySeparatorChar == '\\')
            {
                return;
            }

            try
            {
                using var chmod = Process.Start(new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"600 \"{path}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                chmod?.WaitForExit(2000);
            }
            catch (Exception)
            {
                // A missing chmod leaves the file with the umask's default; nothing else to do.
            }
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
