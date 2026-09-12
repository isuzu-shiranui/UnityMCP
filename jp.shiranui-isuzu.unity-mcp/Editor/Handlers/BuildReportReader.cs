using System;
using System.Collections.Generic;
using System.IO;

using Newtonsoft.Json.Linq;

using UnityEditor.Build.Reporting;

using UnityEditorInternal;

using UnityEngine;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Handlers
{
    /// <summary>
    /// What a build contained and what it weighed, read back from the report Unity writes.
    /// </summary>
    /// <remarks>
    /// The report is a serialized object beside the Library, and nothing public points at it.
    /// Every figure here comes out of that object rather than from the build folder, so the
    /// answer survives the output being deleted.
    /// </remarks>
    internal static class BuildReportReader
    {
        /// <summary>Where Unity leaves the last build's report.</summary>
        public const string LastBuildPath = "Library/LastBuild.buildreport";

        /// <exception cref="McpToolException"><c>not_found</c> when no report is there to read.</exception>
        public static BuildReport Load(string path)
        {
            var full = Path.IsPathRooted(path) ? path : Path.Combine(Directory.GetCurrentDirectory(), path);

            if (!File.Exists(full))
            {
                throw new McpToolException(
                    "not_found",
                    $"No build report at '{path}'. Unity writes one to '{LastBuildPath}' when a "
                    + "build finishes, so there is nothing to read until this project has been "
                    + "built once. build_player makes one.");
            }

            foreach (var loaded in InternalEditorUtility.LoadSerializedFileAndForget(full))
            {
                if (loaded is BuildReport report)
                {
                    return report;
                }
            }

            throw new McpToolException(
                "invalid_params",
                $"'{path}' is not a build report: it holds no BuildReport object.");
        }

        public static JObject Summarise(BuildReport report) => new()
        {
            ["result"] = report.summary.result.ToString(),
            ["platform"] = report.summary.platform.ToString(),
            ["totalBytes"] = (long)report.summary.totalSize,
            ["totalSeconds"] = Math.Round(report.summary.totalTime.TotalSeconds, 1),
            ["errors"] = report.summary.totalErrors,
            ["warnings"] = report.summary.totalWarnings,
            ["outputPath"] = report.summary.outputPath,
            ["builtAt"] = report.summary.buildEndedAt.ToString("o"),
        };

        internal sealed class Packed
        {
            public string Path;
            public string Type;
            public long Bytes;
        }

        /// <summary>Every asset the build packed, with what it weighed once packed.</summary>
        public static List<Packed> Contents(BuildReport report)
        {
            var packed = new List<Packed>();

            foreach (var bundle in report.packedAssets)
            {
                foreach (var content in bundle.contents)
                {
                    packed.Add(new Packed
                    {
                        Path = content.sourceAssetPath,
                        Type = content.type == null ? "Unknown" : content.type.Name,
                        Bytes = (long)content.packedSize,
                    });
                }
            }

            return packed;
        }

        /// <summary>
        /// Totals per type. An asset can be packed more than once, and each copy is counted, which
        /// is what makes a duplicated texture visible rather than averaged away.
        /// </summary>
        public static JArray ByType(List<Packed> packed)
        {
            var totals = new Dictionary<string, (long Bytes, int Count)>();

            foreach (var entry in packed)
            {
                totals.TryGetValue(entry.Type, out var running);
                totals[entry.Type] = (running.Bytes + entry.Bytes, running.Count + 1);
            }

            var rows = new List<JObject>();

            foreach (var pair in totals)
            {
                rows.Add(new JObject
                {
                    ["type"] = pair.Key,
                    ["bytes"] = pair.Value.Bytes,
                    ["count"] = pair.Value.Count,
                });
            }

            rows.Sort((a, b) => b["bytes"].Value<long>().CompareTo(a["bytes"].Value<long>()));
            return new JArray(rows);
        }

        /// <summary>The build steps that took the longest, which is where a slow build is.</summary>
        public static JArray SlowestSteps(BuildReport report, int take)
        {
            var steps = new List<BuildStep>(report.steps);
            steps.Sort((a, b) => b.duration.CompareTo(a.duration));

            var rows = new JArray();

            for (var i = 0; i < steps.Count && i < take; i++)
            {
                rows.Add(new JObject
                {
                    ["step"] = steps[i].name,
                    ["seconds"] = Math.Round(steps[i].duration.TotalSeconds, 2),
                });
            }

            return rows;
        }

        public static JObject Describe(Packed entry) => new()
        {
            ["path"] = string.IsNullOrEmpty(entry.Path) ? "<no source asset>" : entry.Path,
            ["type"] = entry.Type,
            ["bytes"] = entry.Bytes,
        };

        /// <summary>
        /// What changed between two builds, by asset. Nothing else in the ecosystem does this, and
        /// it is the only form of the question that answers "why did this get bigger".
        /// </summary>
        public static JObject Compare(List<Packed> before, List<Packed> after, int take)
        {
            var was = Fold(before);
            var now = Fold(after);

            var grew = new List<JObject>();
            var added = new List<JObject>();
            var removed = new List<JObject>();

            foreach (var pair in now)
            {
                if (!was.TryGetValue(pair.Key, out var previous))
                {
                    added.Add(new JObject { ["path"] = pair.Key, ["bytes"] = pair.Value });
                    continue;
                }

                if (pair.Value != previous)
                {
                    grew.Add(new JObject
                    {
                        ["path"] = pair.Key,
                        ["was"] = previous,
                        ["now"] = pair.Value,
                        ["delta"] = pair.Value - previous,
                    });
                }
            }

            foreach (var pair in was)
            {
                if (!now.ContainsKey(pair.Key))
                {
                    removed.Add(new JObject { ["path"] = pair.Key, ["bytes"] = pair.Value });
                }
            }

            grew.Sort((a, b) => Math.Abs(b["delta"].Value<long>()).CompareTo(Math.Abs(a["delta"].Value<long>())));
            added.Sort((a, b) => b["bytes"].Value<long>().CompareTo(a["bytes"].Value<long>()));
            removed.Sort((a, b) => b["bytes"].Value<long>().CompareTo(a["bytes"].Value<long>()));

            return new JObject
            {
                ["added"] = new JArray(Take(added, take)),
                ["removed"] = new JArray(Take(removed, take)),
                ["changed"] = new JArray(Take(grew, take)),
                ["addedCount"] = added.Count,
                ["removedCount"] = removed.Count,
                ["changedCount"] = grew.Count,
            };
        }

        private static Dictionary<string, long> Fold(List<Packed> packed)
        {
            var folded = new Dictionary<string, long>();

            foreach (var entry in packed)
            {
                var key = string.IsNullOrEmpty(entry.Path) ? "<no source asset>" : entry.Path;
                folded.TryGetValue(key, out var running);
                folded[key] = running + entry.Bytes;
            }

            return folded;
        }

        private static List<JObject> Take(List<JObject> rows, int take) =>
            rows.Count <= take ? rows : rows.GetRange(0, take);
    }
}
