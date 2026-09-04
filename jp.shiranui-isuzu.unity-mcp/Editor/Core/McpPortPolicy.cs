using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

using UnityMCP.Editor.Settings;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Chooses the port a project's server prefers.
    /// </summary>
    /// <remarks>
    /// MCP clients are configured with a URL, so the port has to be the same every time the
    /// project opens. Deriving it from the project path gives each project its own stable port
    /// without any state to keep, and two projects opened side by side land on different ports
    /// unless their hashes collide, in which case the server scans and reports the mismatch.
    /// <para>
    /// The path is normalised before hashing because the same project reaches the Editor as
    /// <c>C:\A\B</c> from the Hub and <c>c:/a/b/</c> from a script, and both must agree.
    /// </para>
    /// </remarks>
    internal static class McpPortPolicy
    {
        public const int RangeStart = 27200;
        public const int RangeEnd = 27999;

        /// <summary>The port this project prefers when no explicit port is configured.</summary>
        public static int Derive(string projectPath)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(Normalize(projectPath)));
            var value = (uint)(hash[0] | (hash[1] << 8) | (hash[2] << 16) | (hash[3] << 24));
            return RangeStart + (int)(value % (uint)(RangeEnd - RangeStart + 1));
        }

        /// <summary>The configured port when it is positive, otherwise the derived one.</summary>
        public static int Resolve(McpSettings settings, string projectPath)
        {
            return settings != null && settings.httpPort > 0 ? settings.httpPort : Derive(projectPath);
        }

        public static string Normalize(string projectPath)
        {
            var full = string.IsNullOrEmpty(projectPath) ? string.Empty : Path.GetFullPath(projectPath);
            full = full.Replace('\\', '/').TrimEnd('/');

            // Windows paths are case-insensitive; elsewhere two paths differing in case are
            // different directories and must keep different ports.
            if (Path.DirectorySeparatorChar == '\\')
            {
                full = full.ToLowerInvariant();
            }

            return full;
        }
    }
}
