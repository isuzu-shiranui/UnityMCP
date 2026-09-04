using Newtonsoft.Json.Linq;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;
using UnityMCP.Editor.Handlers;
using UnityMCP.Editor.Resources;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Screenshots, arbitrary code execution, menu items, and project metadata.
    /// </summary>
    internal static class EditorTools
    {
        private static readonly MenuItemCommandHandler MenuHandler = new();
        private static readonly AssembliesResourceHandler Assemblies = new();
        private static readonly PackagesResourceHandler Packages = new();

        [McpTool(
            "capture_screenshot",
            "Capture the Game or Scene view, or an Editor panel, as an image. " +
            "Every view whose name ends in _window, and inspector, hierarchy, project and console, is read " +
            "off the screen and is Windows-only; game and scene render through the camera and work " +
            "everywhere. A screen-read view returns whatever is drawn in that rectangle, so the call " +
            "is refused when another application is in front of the Editor. It also focuses the " +
            "window first, which raises a docked tab over whatever the person at the Editor was " +
            "looking at.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject CaptureScreenshot(
            [McpArg("view", "What to capture. 'game' and 'scene' render through the camera. " +
                            "'game_view_window' and 'scene_view_window' grab those windows off the " +
                            "screen instead, which is the only way to get gizmos, overlays and the " +
                            "toolbar. 'inspector', 'hierarchy', 'project', 'console' and " +
                            "'window:<title>' are grabbed the same way.")]
            string view = "game",
            [McpArg("max_size", "Longest edge of the returned image, in pixels.")]
            int maxSize = 1024,
            [McpArg("width", "Exact capture width; overrides max_size.")]
            int? width = null,
            [McpArg("height", "Exact capture height; overrides max_size.")]
            int? height = null,
            [McpArg("save_path", "Write the PNG here and return the path instead of the image. " +
                                 "Use this when the picture is going to render_compare rather than " +
                                 "to a human. Any missing directories are created, and an existing " +
                                 "file at this path is overwritten.")]
            string savePath = null)
        {
            return ScreenshotCapture.Capture(ToolArgs.Of(
                ("view", view),
                ("maxSize", maxSize),
                ("width", width),
                ("height", height),
                ("savePath", savePath)));
        }

        [McpTool(
            "execute_code",
            "Compile and run a C# snippet inside the Editor and return its value. The snippet is " +
            "placed in a method body, so use `return <expr>;` to surface a result and write type " +
            "names in full: a using directive is a compile error there. Full Editor API access, " +
            "including destructive operations, so read before you write. " +
            "Identical snippets are compiled once and reused.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject ExecuteCode(
            [McpArg("code", "C# statements to run. Use `return <expr>;` to return a value.")]
            string code = null,
            [McpArg("code_base64",
                "The same snippet, base64-encoded; takes precedence over `code`. Plain `code` is " +
                "fine when a real JSON encoder builds the request. Use this instead when the " +
                "request is assembled by hand — a shell heredoc, string concatenation — because " +
                "backslashes and newlines in C# string literals are then easy to mangle, and the " +
                "result is a compile error in generated source the caller never sees.")]
            string codeBase64 = null)
        {
            if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(codeBase64))
            {
                throw new McpToolException("invalid_params", "Pass either 'code' or 'code_base64'.");
            }

            return CodeExecutor.Execute(ToolArgs.Of(
                ("code", code),
                ("code_base64", codeBase64)));
        }

        [McpTool(
            "menu_execute",
            "Invoke an Editor menu item by its full path, e.g. 'Assets/Refresh'. " +
            "Menu items can do anything the menu can, including irreversible operations.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject MenuExecute(
            [McpArg("menu_item", "Full menu path, e.g. 'Window/General/Console'.")]
            string menuItem)
        {
            return MenuHandler.Execute("execute", ToolArgs.Of(("menuItem", menuItem)));
        }

        [McpTool(
            "project_assemblies",
            "List the assemblies loaded in the Editor. Use this to find the assembly a type lives " +
            "in before referencing it from execute_code.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject ProjectAssemblies(
            [McpArg("include_system_assemblies", "Include System.* and framework assemblies.")]
            bool includeSystemAssemblies = false,
            [McpArg("include_unity_assemblies", "Include Unity's own assemblies.")]
            bool includeUnityAssemblies = true,
            [McpArg("include_project_assemblies", "Include assemblies built from this project.")]
            bool includeProjectAssemblies = true,
            [McpArg("limit", "Maximum entries to return.")]
            int? limit = null,
            [McpArg("offset", "Entries to skip, for paging.")]
            int offset = 0,
            [McpArg("fields", "Comma-separated field whitelist, to keep responses small.")]
            string fields = null)
        {
            return Assemblies.FetchResource(ToolArgs.Of(
                ("includeSystemAssemblies", includeSystemAssemblies),
                ("includeUnityAssemblies", includeUnityAssemblies),
                ("includeProjectAssemblies", includeProjectAssemblies),
                ("limit", limit),
                ("offset", offset),
                ("fields", fields)));
        }

        [McpTool(
            "project_packages",
            "List the UPM packages this project depends on, with their resolved versions.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject ProjectPackages(
            [McpArg("include_registry", "Include registry metadata for each package.")]
            bool includeRegistry = false,
            [McpArg("limit", "Maximum entries to return.")]
            int? limit = null,
            [McpArg("offset", "Entries to skip, for paging.")]
            int offset = 0,
            [McpArg("fields", "Comma-separated field whitelist, to keep responses small.")]
            string fields = null)
        {
            return Packages.FetchResource(ToolArgs.Of(
                ("includeRegistry", includeRegistry),
                ("limit", limit),
                ("offset", offset),
                ("fields", fields)));
        }
    }
}
