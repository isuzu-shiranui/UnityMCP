using System;
using System.Collections.Generic;

using UnityEditor;

using Debug = UnityEngine.Debug;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Resolves the <c>view</c> argument a tool receives to a live <see cref="EditorWindow"/>.
    /// </summary>
    /// <remarks>
    /// Screenshot capture and input replay have to agree on what a view name means. If they
    /// each kept their own map, a click could land in one window while the capture that
    /// verified it came from another, and the mismatch would look like a broken click.
    /// </remarks>
    internal static class EditorWindowLocator
    {
        /// <summary>Prefix that matches a window by tab title instead of by type.</summary>
        public const string WindowPrefix = "window:";

        /// <summary>Panel view name to the full type name of the window serving it.</summary>
        public static readonly IReadOnlyDictionary<string, string> ViewToTypeName =
            new Dictionary<string, string>
            {
                ["inspector"]         = "UnityEditor.InspectorWindow",
                ["hierarchy"]         = "UnityEditor.SceneHierarchyWindow",
                ["project"]           = "UnityEditor.ProjectBrowser",
                ["console"]           = "UnityEditor.ConsoleWindow",
                ["game_view_window"]  = "UnityEditor.GameView",
                ["scene_view_window"] = "UnityEditor.SceneView",
            };

        /// <summary>
        /// Finds the window a view name refers to.
        /// </summary>
        /// <exception cref="McpToolException">
        /// <c>invalid_params</c> when the name is not a known view, <c>window_not_found</c>
        /// when it is but no such window is open.
        /// </exception>
        public static EditorWindow Resolve(string view)
        {
            if (view != null && view.StartsWith(WindowPrefix, StringComparison.Ordinal) && string.IsNullOrWhiteSpace(view.Substring(WindowPrefix.Length)))
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{view}' names no window; give part of a tab title after '{WindowPrefix}'.",
                    400);
            }

            var all = UnityEngine.Resources.FindObjectsOfTypeAll<EditorWindow>();

            List<EditorWindow> candidates;
            if (view != null && view.StartsWith(WindowPrefix, StringComparison.Ordinal))
            {
                var needle = view.Substring(WindowPrefix.Length);
                candidates = new List<EditorWindow>();
                foreach (var win in all)
                {
                    if (win == null) continue;
                    var title = win.titleContent?.text ?? string.Empty;
                    if (title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        candidates.Add(win);
                    }
                }
            }
            else if (view != null && ViewToTypeName.TryGetValue(view, out var typeName))
            {
                candidates = new List<EditorWindow>();
                foreach (var win in all)
                {
                    if (win == null) continue;
                    if (win.GetType().FullName == typeName)
                    {
                        candidates.Add(win);
                    }
                }
            }
            else
            {
                throw new McpToolException(
                    "invalid_params",
                    $"Unknown view '{view}'.",
                    400);
            }

            if (candidates.Count == 0)
            {
                throw new McpToolException(
                    "window_not_found",
                    $"No EditorWindow matches view '{view}'.",
                    400);
            }

            if (candidates.Count > 1)
            {
                Debug.LogWarning(
                    $"[EditorWindowLocator] multiple_matches: {candidates.Count} EditorWindows match view '{view}'. Using the first one ('{candidates[0].titleContent.text}').");
            }

            return candidates[0];
        }

        /// <summary>
        /// Maps the camera-based view names to the windows that host them.
        /// </summary>
        /// <remarks>
        /// Screenshot capture reads <c>scene</c> and <c>game</c> as "render through that
        /// camera", which has no window. An event has to go to a window, so those two names
        /// mean the Scene and Game windows here.
        /// </remarks>
        public static string NormalizeForInput(string view)
        {
            switch (view)
            {
                case "scene":
                    return "scene_view_window";
                case "game":
                    return "game_view_window";
                default:
                    return view;
            }
        }

        /// <summary>
        /// The view name that resolves back to <paramref name="window"/>, so a tool can report
        /// which window it acted on in the vocabulary the caller uses.
        /// </summary>
        public static string CanonicalView(EditorWindow window)
        {
            if (window == null)
            {
                return null;
            }

            var typeName = window.GetType().FullName;

            foreach (var pair in ViewToTypeName)
            {
                if (string.Equals(pair.Value, typeName, StringComparison.Ordinal))
                {
                    return pair.Key;
                }
            }

            return WindowPrefix + (window.titleContent?.text ?? string.Empty);
        }
    }
}
