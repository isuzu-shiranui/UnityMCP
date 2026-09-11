using System;
using System.Collections.Generic;

using Newtonsoft.Json.Linq;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;
using UnityMCP.Editor.Handlers;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Answering "what is using all the memory".
    /// </summary>
    internal static class MemoryTools
    {
        /// <summary>Objects named individually. The per-type totals carry the whole picture.</summary>
        private const int DefaultTop = 10;

        private const int MaxTop = 200;

        [McpTool(
            "memory_usage",
            "What the loaded objects weigh, largest first, split by whether the project owns them. " +
            "That split is what makes the answer usable: an Editor holds its own render targets " +
            "and icon atlases, and those are routinely the largest textures in memory while " +
            "telling a developer nothing. The reply leads with a row per type, then names the " +
            "heaviest individual assets with their paths. These are the sizes of what is loaded " +
            "now, not of the files on disk and not of what a built player would hold - the Editor " +
            "keeps textures in forms a build does not, so read the figures as a ranking rather " +
            "than as a budget.",
            Idempotency = McpIdempotency.Safe,
            MaxResultSizeChars = 60000)]
        public static JObject Usage(
            [McpArg("types", "Which types to weigh. Omit for all of them.")]
            string[] types = null,
            [McpArg("scope", "'project' names only assets under Assets or Packages, 'all' includes " +
                             "the Editor's own objects and runtime copies, 'other' only those.")]
            string scope = "project",
            [McpArg("top", "How many individual objects to name.")]
            int top = DefaultTop,
            [McpArg("fields", "Comma-separated keys to keep on each named object.")]
            string fields = null)
        {
            if (top < 0 || top > MaxTop)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'top' takes 0 to {MaxTop}; {top} is outside that. The per-type totals are "
                    + "where the answer is, and naming hundreds of assets buries them.");
            }

            var wanted = Kinds(types);
            var entries = MemoryUsage.Collect(wanted);
            var byType = MemoryUsage.ByType(entries);

            var named = new List<MemoryUsage.Entry>();
            long totalBytes = 0;
            long projectBytes = 0;

            foreach (var entry in entries)
            {
                totalBytes += entry.Bytes;

                if (entry.InProject)
                {
                    projectBytes += entry.Bytes;
                }

                if (Wanted(entry, scope))
                {
                    named.Add(entry);
                }
            }

            named.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

            if (named.Count > top)
            {
                named.RemoveRange(top, named.Count - top);
            }

            var result = ListResponseBuilder.Build(
                named, 0, top <= 0 ? 1 : top, MemoryUsage.Describe,
                ListResponseBuilder.ParseFieldsParam(fields), new[] { "path", "bytes" });

            result["byType"] = byType;
            result["totalBytes"] = totalBytes;
            result["projectBytes"] = projectBytes;
            result["scope"] = scope;
            result["note"] = "Sizes of what is loaded now. The Editor's own objects are included in "
                + "the totals and separated by 'projectBytes'; a built player holds less.";

            return result;
        }

        private static bool Wanted(MemoryUsage.Entry entry, string scope) => scope switch
        {
            "project" => entry.InProject,
            "other" => !entry.InProject,
            "all" => true,
            _ => throw new McpToolException(
                "invalid_params",
                $"'scope' takes 'project', 'other' or 'all'; '{scope}' is none of them."),
        };

        /// <exception cref="McpToolException"><c>invalid_params</c> for a type nothing is weighed for.</exception>
        private static List<string> Kinds(string[] types)
        {
            var known = MemoryUsage.KindNames;

            if (types == null || types.Length == 0)
            {
                return new List<string>(known);
            }

            var wanted = new List<string>();

            foreach (var type in types)
            {
                var match = string.Empty;

                foreach (var name in known)
                {
                    if (string.Equals(name, type, StringComparison.OrdinalIgnoreCase))
                    {
                        match = name;
                        break;
                    }
                }

                if (match.Length == 0)
                {
                    throw new McpToolException(
                        "invalid_params",
                        $"Nothing is weighed for '{type}'. 'types' takes {string.Join(", ", known)}.");
                }

                wanted.Add(match);
            }

            return wanted;
        }
    }
}
