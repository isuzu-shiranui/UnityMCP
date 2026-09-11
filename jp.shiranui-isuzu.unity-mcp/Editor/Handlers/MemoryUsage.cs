using System;
using System.Collections.Generic;

using Newtonsoft.Json.Linq;

using UnityEditor;

using UnityEngine;
using UnityEngine.Profiling;

namespace UnityMCP.Editor.Handlers
{
    /// <summary>
    /// What the loaded objects weigh, split by whether the project owns them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The split is the point. An Editor holds its own render targets and icon atlases, and in a
    /// bench project those are the five largest textures in memory; a report that mixes them with
    /// the project's own art answers nothing a developer can act on.
    /// </para>
    /// <para>
    /// These are the sizes of what is loaded now, not of what is on disk and not of what a player
    /// would hold. The Editor keeps textures in forms a build does not.
    /// </para>
    /// </remarks>
    internal static class MemoryUsage
    {
        /// <summary>The types a memory question is about, in the order they usually matter.</summary>
        private static readonly (string Name, Type Type)[] Kinds =
        {
            ("Texture", typeof(Texture)),
            ("Mesh", typeof(Mesh)),
            ("AnimationClip", typeof(AnimationClip)),
            ("AudioClip", typeof(AudioClip)),
            ("Material", typeof(Material)),
            ("Shader", typeof(Shader)),
            ("Font", typeof(Font)),
        };

        public static IReadOnlyList<string> KindNames
        {
            get
            {
                var names = new List<string>();

                foreach (var (name, _) in Kinds)
                {
                    names.Add(name);
                }

                return names;
            }
        }

        internal sealed class Entry
        {
            public string Path;
            public string Name;
            public string Type;
            public long Bytes;
            public bool InProject;
        }

        /// <summary>
        /// An object the project owns is one the AssetDatabase can name a path for under Assets or
        /// Packages. Everything else is a scene instance, a runtime copy, or the Editor's own.
        /// </summary>
        private static bool Owned(string path) =>
            !string.IsNullOrEmpty(path)
            && (path.StartsWith("Assets/", StringComparison.Ordinal)
                || path.StartsWith("Packages/", StringComparison.Ordinal));

        private static bool Asked(IReadOnlyList<string> kinds, string name)
        {
            if (kinds == null || kinds.Count == 0)
            {
                return true;
            }

            foreach (var kind in kinds)
            {
                if (string.Equals(kind, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public static List<Entry> Collect(IReadOnlyList<string> kinds)
        {
            var entries = new List<Entry>();

            foreach (var (name, type) in Kinds)
            {
                if (!Asked(kinds, name))
                {
                    continue;
                }

                // Fully qualified: there is a UnityMCP.Editor.Resources namespace, and the short
                // name binds to it from inside the package.
                foreach (var loaded in UnityEngine.Resources.FindObjectsOfTypeAll(type))
                {
                    var path = AssetDatabase.GetAssetPath(loaded);

                    entries.Add(new Entry
                    {
                        Path = path,
                        Name = loaded.name,
                        Type = name,
                        Bytes = Profiler.GetRuntimeMemorySizeLong(loaded),
                        InProject = Owned(path),
                    });
                }
            }

            return entries;
        }

        /// <summary>One row per type, so the reply says where the weight is before what it is.</summary>
        public static JArray ByType(List<Entry> entries)
        {
            var totals = new Dictionary<string, (long Bytes, int Count, long ProjectBytes, int ProjectCount)>();

            foreach (var entry in entries)
            {
                totals.TryGetValue(entry.Type, out var running);

                totals[entry.Type] = (
                    running.Bytes + entry.Bytes,
                    running.Count + 1,
                    running.ProjectBytes + (entry.InProject ? entry.Bytes : 0),
                    running.ProjectCount + (entry.InProject ? 1 : 0));
            }

            var rows = new List<JObject>();

            foreach (var pair in totals)
            {
                rows.Add(new JObject
                {
                    ["type"] = pair.Key,
                    ["bytes"] = pair.Value.Bytes,
                    ["count"] = pair.Value.Count,
                    ["projectBytes"] = pair.Value.ProjectBytes,
                    ["projectCount"] = pair.Value.ProjectCount,
                });
            }

            rows.Sort((a, b) => b["bytes"].Value<long>().CompareTo(a["bytes"].Value<long>()));
            return new JArray(rows);
        }

        public static JObject Describe(Entry entry) => new()
        {
            ["path"] = string.IsNullOrEmpty(entry.Path) ? null : entry.Path,
            ["name"] = entry.Name,
            ["type"] = entry.Type,
            ["bytes"] = entry.Bytes,
            ["inProject"] = entry.InProject,
        };
    }
}
