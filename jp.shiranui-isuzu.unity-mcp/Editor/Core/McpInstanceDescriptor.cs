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
    /// Replaces v2's UDP broadcast. Broadcasting solved discovery but created two problems it
    /// could not solve: an announce leaves via a network interface, so an Editor on another
    /// machine was indistinguishable from a local one and got registered as a dead local
    /// instance; and the announce interval put a 30-second floor on noticing a new Editor.
    /// A file in a known directory has neither problem, and it can carry the auth token,
    /// which a broadcast obviously cannot.
    /// <para>
    /// Modelled on the port/descriptor file used by Unity's own <c>com.unity.pipeline</c>.
    /// </para>
    /// </remarks>
    internal static class McpInstanceDescriptor
    {
        /// <summary>Directory holding one descriptor per running Editor.</summary>
        public static string DirectoryPath
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

                return Path.Combine(root, "UnityMCP", "instances");
            }
        }

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
            string token,
            string protocolVersion)
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
                    ["token"] = token,
                    ["pid"] = Process.GetCurrentProcess().Id,
                    ["protocolVersion"] = protocolVersion,
                    ["endpoint"] = $"http://127.0.0.1:{port}",
                };

                File.WriteAllText(PathFor(projectPath), payload.ToString(Formatting.Indented), new UTF8Encoding(false));
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

        /// <summary>Generates a token with enough entropy that guessing is not a concern.</summary>
        public static string GenerateToken()
        {
            var bytes = new byte[32];

            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                builder.Append(b.ToString("x2"));
            }

            return builder.ToString();
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

        private static string HashProjectPath(string projectPath)
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
