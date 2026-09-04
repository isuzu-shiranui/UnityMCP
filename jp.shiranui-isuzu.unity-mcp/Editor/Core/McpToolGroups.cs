using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// The groups a tool can belong to, and the rule that assigns one when the attribute does not.
    /// </summary>
    /// <remarks>
    /// A client that loads every tool spends context on the ones it will never call and picks
    /// the wrong one more often. Groups let a client ask for a subset: <c>/mcp?group=diagnostics</c>
    /// or <c>isuzu-unity-cli tools --group rendering</c>. The default comes from the name
    /// prefix so existing tools need no annotation; <see cref="Attributes.McpToolAttribute.Group"/>
    /// overrides it for the few whose prefix says one thing and their use says another.
    /// </remarks>
    internal static class McpToolGroups
    {
        public const string Diagnostics = "diagnostics";
        public const string Authoring = "authoring";
        public const string Rendering = "rendering";
        public const string Timeline = "timeline";
        public const string Build = "build";
        public const string Code = "code";
        public const string Input = "input";

        public static readonly string[] Known = { Diagnostics, Authoring, Rendering, Timeline, Build, Code, Input };

        // Read-only tools whose prefix otherwise lands them in authoring.
        private static readonly HashSet<string> DiagnosticNames = new(StringComparer.Ordinal)
        {
            "play_mode_status",
            "scene_browse_hierarchy",
            "scene_list",
            "inspect_read",
            "inspect_list",
            "asset_find",
            "asset_info",
            "capture_screenshot",
            "animator_inspect",
            "animator_audit",
        };

        private static readonly (string Prefix, string Group)[] PrefixRules =
        {
            ("console_", Diagnostics),
            ("compile_", Diagnostics),
            ("test_", Diagnostics),
            ("editor_", Diagnostics),
            ("job_", Diagnostics),
            ("project_", Diagnostics),
            ("definitions_", Diagnostics),
            ("animator_", Authoring),
            ("gameobject_", Authoring),
            ("inspect_", Authoring),
            ("asset_", Authoring),
            ("scene_", Authoring),
            ("prefab_", Authoring),
            ("menu_", Authoring),
            ("play_mode_", Authoring),
            ("render_", Rendering),
            ("shader_", Rendering),
            ("material_", Rendering),
            ("gpu_", Rendering),
            ("timeline_", Timeline),
            ("recorder_", Timeline),
            ("build_", Build),
            ("input_", Input),
            ("execute_", Code),
            ("reflect_", Code),
        };

        public static bool IsKnown(string group) => Array.IndexOf(Known, group) >= 0;

        /// <summary>The group a tool name falls into by its prefix; <see cref="Code"/> when none matches.</summary>
        public static string Derive(string name)
        {
            if (DiagnosticNames.Contains(name))
            {
                return Diagnostics;
            }

            foreach (var (prefix, group) in PrefixRules)
            {
                if (name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return group;
                }
            }

            return Code;
        }

        /// <summary>
        /// Parses a comma-separated group list as it arrives in a query string. Unknown names
        /// are returned so the caller can refuse them rather than silently serve nothing.
        /// </summary>
        public static IReadOnlyList<string> Parse(string query, out IReadOnlyList<string> unknown)
        {
            var groups = (query ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(g => g.Trim().ToLowerInvariant())
                .Where(g => g.Length > 0)
                .Distinct()
                .ToList();

            unknown = groups.Where(g => !IsKnown(g)).ToList();
            return groups;
        }
    }
}
