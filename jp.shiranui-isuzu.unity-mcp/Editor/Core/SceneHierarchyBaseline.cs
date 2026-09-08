using System;
using System.Collections.Generic;

using Newtonsoft.Json.Linq;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// The states <c>scene_browse_hierarchy</c> has described, each under an id the caller keeps.
    /// </summary>
    /// <remarks>
    /// A snapshot is never consumed and never advances on its own. Two clients browsing the same
    /// scene hold two ids and each diffs against what it was actually given, so neither can eat
    /// the other's changes; a reply that never arrives leaves the caller on the id it already
    /// had, and the retry reports the same difference rather than nothing.
    /// <para>
    /// Static and unserialised, so the store empties on a domain reload — which is also when the
    /// instance ids it is keyed by stop naming the same objects.
    /// </para>
    /// </remarks>
    internal static class SceneHierarchyBaseline
    {
        private sealed class Snapshot
        {
            public string Walk;
            public Dictionary<string, string> Nodes;
        }

        private static readonly Dictionary<string, Snapshot> Snapshots =
            new Dictionary<string, Snapshot>(StringComparer.Ordinal);

        /// <summary>Most recently used first, so the oldest snapshot is the one dropped.</summary>
        private static readonly List<string> Recent = new List<string>();

        /// <summary>
        /// Each snapshot of a large scene is on the order of 70 KB of strings, and one is taken
        /// per call rather than per filter, so the ceiling is what bounds the store.
        /// </summary>
        private const int KeepAtMost = 16;

        private static long counter;

        private static readonly object Gate = new object();

        internal sealed class Diff
        {
            public JArray Added { get; set; }
            public JArray Changed { get; set; }
            public JArray Removed { get; set; }
            public int Unchanged { get; set; }
        }

        /// <summary>Records what was returned and names it, without comparing it to anything.</summary>
        public static string Remember(string walk, IReadOnlyList<JObject> nodes)
        {
            var state = Serialise(nodes);

            lock (Gate)
            {
                counter++;
                var id = "snap-" + counter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Snapshots[id] = new Snapshot { Walk = walk, Nodes = state };
                Touch(id);
                return id;
            }
        }

        /// <summary>The walk a snapshot describes, or null when it is gone.</summary>
        public static string WalkOf(string id)
        {
            lock (Gate)
            {
                return Snapshots.TryGetValue(id, out var snapshot) ? snapshot.Walk : null;
            }
        }

        /// <summary>
        /// Compares against the snapshot the caller names and records the new state under a new
        /// id. Returns null when that snapshot is gone, which the caller has to answer by
        /// reading in full again.
        /// </summary>
        public static Diff CompareWith(string sinceId, string walk, IReadOnlyList<JObject> nodes, out string newId)
        {
            var state = Serialise(nodes);

            lock (Gate)
            {
                if (!Snapshots.TryGetValue(sinceId, out var snapshot))
                {
                    newId = null;
                    return null;
                }

                var previous = snapshot.Nodes;

                var diff = new Diff
                {
                    Added = new JArray(),
                    Changed = new JArray(),
                    Removed = new JArray(),
                };

                foreach (var node in nodes)
                {
                    var id = IdOf(node);
                    if (id == null)
                    {
                        continue;
                    }

                    if (!previous.TryGetValue(id, out var before))
                    {
                        diff.Added.Add(node);
                    }
                    else if (!string.Equals(before, state[id], StringComparison.Ordinal))
                    {
                        diff.Changed.Add(node);
                    }
                    else
                    {
                        diff.Unchanged++;
                    }
                }

                foreach (var pair in previous)
                {
                    if (!state.ContainsKey(pair.Key))
                    {
                        // The same spelling the nodes use: a number before Unity 6.5 and a string
                        // from 6.5 on. A caller keying by the id it was given has to be able to
                        // find the removal with it.
                        diff.Removed.Add(EntityIdCompat.Wire(long.Parse(
                            pair.Key, System.Globalization.CultureInfo.InvariantCulture)));
                    }
                }

                counter++;
                newId = "snap-" + counter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Snapshots[newId] = new Snapshot { Walk = walk, Nodes = state };
                Touch(sinceId);
                Touch(newId);
                return diff;
            }
        }

        /// <summary>Empties the store. For tests, which need each to start from nothing.</summary>
        internal static void Reset()
        {
            lock (Gate)
            {
                Snapshots.Clear();
                Recent.Clear();
                counter = 0;
            }
        }

        private static string IdOf(JObject node)
        {
            var token = node["instanceId"];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            var text = token.ToString();
            return text.Length == 0 ? null : text;
        }

        private static Dictionary<string, string> Serialise(IReadOnlyList<JObject> nodes)
        {
            var state = new Dictionary<string, string>(nodes.Count, StringComparer.Ordinal);

            foreach (var node in nodes)
            {
                var id = IdOf(node);
                if (id != null)
                {
                    state[id] = node.ToString(Newtonsoft.Json.Formatting.None);
                }
            }

            return state;
        }

        private static void Touch(string id)
        {
            Recent.Remove(id);
            Recent.Insert(0, id);

            while (Recent.Count > KeepAtMost)
            {
                var oldest = Recent[Recent.Count - 1];
                Recent.RemoveAt(Recent.Count - 1);
                Snapshots.Remove(oldest);
            }
        }
    }
}
