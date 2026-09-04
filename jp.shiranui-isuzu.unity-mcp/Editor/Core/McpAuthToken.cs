using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// The bearer token for one project, kept in the user's profile so it is the same across
    /// Editor sessions.
    /// </summary>
    /// <remarks>
    /// MCP clients hold the token in their configuration, which is written once. A token that
    /// changed on every launch would make every configured client fail with 401 after a restart.
    /// The file lives beside the instance descriptors and, like them, is a credential: anyone
    /// who can read it can run code in the Editor. On Unix the file is created readable by the
    /// owner only. Rotating is explicit, through <see cref="Regenerate"/>, after which every
    /// client has to be registered again.
    /// </remarks>
    internal static class McpAuthToken
    {
        public static string DirectoryPath => Path.Combine(McpInstanceDescriptor.StateRoot, "tokens");

        public static string PathFor(string projectPath) =>
            Path.Combine(DirectoryPath, $"{McpInstanceDescriptor.HashProjectPath(projectPath)}.token");

        /// <summary>The project's token, minting one on first use.</summary>
        public static string Load(string projectPath)
        {
            var path = PathFor(projectPath);

            try
            {
                if (File.Exists(path))
                {
                    var existing = File.ReadAllText(path, Encoding.UTF8).Trim();
                    if (IsWellFormed(existing))
                    {
                        return existing;
                    }
                }
            }
            catch (Exception)
            {
                // Unreadable: mint a fresh one below and try to persist it.
            }

            return Regenerate(projectPath);
        }

        /// <summary>Replaces the project's token. Clients registered with the old one stop working.</summary>
        public static string Regenerate(string projectPath)
        {
            var token = Generate();
            var path = PathFor(projectPath);

            try
            {
                Directory.CreateDirectory(DirectoryPath);
                File.WriteAllText(path, token, new UTF8Encoding(false));
                McpInstanceDescriptor.RestrictToOwner(path);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"[McpAuthToken] Could not persist the token at {path}: {e.Message}");
            }

            return token;
        }

        /// <summary>Generates a token with enough entropy that guessing is not a concern.</summary>
        public static string Generate()
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

        private static bool IsWellFormed(string token)
        {
            if (token == null || token.Length != 64)
            {
                return false;
            }

            foreach (var c in token)
            {
                if (!Uri.IsHexDigit(c))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
