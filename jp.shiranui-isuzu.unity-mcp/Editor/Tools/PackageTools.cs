using Newtonsoft.Json.Linq;

using UnityEditor;
using UnityEditor.PackageManager;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Asking the Package Manager to act on what the manifest now says.
    /// </summary>
    internal static class PackageTools
    {
        [McpTool(
            "package_resolve",
            "Make the Package Manager read Packages/manifest.json again and install what changed. "
            + "Unity does this by itself when the Editor next has focus, so this is for the case "
            + "where something edited the manifest and the answer is wanted now - which is what "
            + "'isuzu-unity-cli update' does to move a project to a new release. The resolve "
            + "reloads the domain, so this answers first and the reload follows: an MCP call in "
            + "flight when it lands is lost, and a caller that waits for one is waiting for "
            + "something that will not arrive. Nothing is installed that the manifest does not "
            + "already ask for.",
            Idempotency = McpIdempotency.Unsafe,
            Destructive = true,
            UndoGroup = null)]
        public static JObject Resolve()
        {
            // Queued rather than called: Client.Resolve reloads the domain, and this assembly is
            // part of what the reload replaces. Answering first is what keeps the caller from
            // waiting on a response that the reload takes with it.
            EditorApplication.delayCall += () => Client.Resolve();

            return new JObject
            {
                ["requested"] = true,
                ["note"] = "The Package Manager reads the manifest on the next Editor tick. "
                           + "Resolving reloads the domain, which drops this connection; call "
                           + "compile_status once it answers again to see the Editor settle.",
            };
        }
    }
}
