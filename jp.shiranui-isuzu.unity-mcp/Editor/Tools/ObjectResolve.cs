using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using UnityEditor;
using UnityEditor.SceneManagement;

using UnityEngine;
using UnityEngine.SceneManagement;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Turns a hierarchy path or an instance id into the object it names, and back again.
    /// </summary>
    /// <remarks>
    /// Shared rather than duplicated per tool, because "which object did you mean" is the one
    /// question every authoring tool has to answer identically. Two things it fixes:
    /// <para>
    /// <c>GameObject.Find</c> only sees active objects, so the obvious implementation cannot
    /// reach anything a caller has just deactivated — or anything that was inactive when they
    /// looked at it. This walks the scene roots instead.
    /// </para>
    /// <para>
    /// Sibling names repeat constantly in real scenes. Refusing on ambiguity would make the
    /// tools unusable, and silently taking the first match makes them unpredictable, so a path
    /// carries an index only where one is needed: <c>/Canvas/Button[1]/Text</c>. Paths written
    /// by hand without indices still resolve, to the first match.
    /// </para>
    /// <para>
    /// A name can contain the characters a path is written with. A '/' in a name, and the '[' of
    /// a name that ends like an index, carry a backslash, and a backslash in a name is doubled.
    /// A backslash before any other character is an ordinary character, so a path typed without
    /// escapes resolves unless one of its names needs them.
    /// </para>
    /// </remarks>
    internal static class ObjectResolve
    {
        /// <summary>
        /// Resolves a GameObject from a path, an instance id, or both.
        /// </summary>
        /// <param name="idArgumentName">
        /// Wire name of the argument carrying <paramref name="instanceId"/>, named in the refusal
        /// when neither was supplied. Null where the caller's tool offers no id for this object,
        /// so the refusal does not send them to an argument that would address something else.
        /// </param>
        public static GameObject Object(
            string path,
            long? instanceId,
            string argumentName = "object_path",
            string idArgumentName = "instance_id")
        {
            if (instanceId.HasValue)
            {
                var found = EntityIdCompat.Find(instanceId.Value);

                if (found is GameObject byId)
                {
                    return byId;
                }

                // A live id for something else is not an expired id, and saying so sends the
                // caller to re-read a hierarchy that will never contain it. A material's id was
                // answered with "ids do not survive a domain reload" while the material was open
                // in front of them.
                if (found is Component component)
                {
                    return component.gameObject;
                }

                if (found != null)
                {
                    throw new McpToolException(
                        "invalid_params",
                        $"Instance id {instanceId.Value} is a {found.GetType().Name} named "
                        + $"'{found.name}', not a GameObject. This argument takes an object in the "
                        + "scene; an asset is named by its path under Assets/ or Packages/.");
                }

                throw new McpToolException(
                    "not_found",
                    $"No GameObject has instance id {instanceId.Value}. Instance ids do not survive a " +
                    "domain reload or a scene change; re-read the hierarchy to get current ones.");
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new McpToolException(
                    "invalid_params",
                    string.IsNullOrEmpty(idArgumentName)
                        ? $"'{argumentName}' is required."
                        : $"Either '{argumentName}' or '{idArgumentName}' is required.");
            }

            var segments = Segments(path).ToArray();

            if (segments.Length == 0)
            {
                throw new McpToolException("invalid_params", $"'{path}' does not name anything.");
            }

            IEnumerable<GameObject> level = SceneRoots();
            GameObject current = null;

            for (var depth = 0; depth < segments.Length; depth++)
            {
                var match = MatchSegment(level, segments[depth]);

                if (match == null)
                {
                    if (!path.StartsWith("/", StringComparison.Ordinal))
                    {
                        var named = SceneRoots().SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                            .Where(t => t.name == Unescape(path)).Select(t => t.gameObject).Take(11).ToArray();
                        if (named.Length == 1) return named[0];
                        if (named.Length > 1) throw new McpToolException("conflict", $"Several objects are named '{path}': "
                            + string.Join(", ", named.Take(10).Select(PathOf)) + (named.Length > 10 ? ", ..." : ""));
                    }
                    throw new McpToolException("not_found", NotFoundMessage(path, segments, depth, level));
                }

                current = match;
                level = Children(current);
            }

            return current;
        }

        /// <summary>
        /// The path that <see cref="Object"/> will resolve back to this object.
        /// </summary>
        /// <remarks>
        /// An index is appended only where the name alone is ambiguous among its siblings, so
        /// the common case stays readable and the ambiguous case stays exact.
        /// </remarks>
        public static string PathOf(GameObject go)
        {
            if (go == null)
            {
                return null;
            }

            var parts = new List<string>();

            for (var t = go.transform; t != null; t = t.parent)
            {
                parts.Add(Segment(t));
            }

            parts.Reverse();

            var builder = new StringBuilder();

            foreach (var part in parts)
            {
                builder.Append('/').Append(part);
            }

            return builder.ToString();
        }

        /// <summary>Path construction shared only within one synchronous hierarchy read.</summary>
        internal sealed class PathBatch
        {
            /// <summary>Groups up to this size compare every pair of names instead of counting them in a dictionary.</summary>
            private const int PairwiseLimit = 8;

            private readonly Dictionary<Transform, string> segments = new();
            private readonly Dictionary<Transform, string> paths = new();
            private readonly HashSet<Transform> parents = new();
            private bool rootsRead;

            public string PathOf(GameObject go)
            {
                if (go == null) return null;
                return PathOf(go.transform);
            }

            private string PathOf(Transform transform)
            {
                if (this.paths.TryGetValue(transform, out var path)) return path;
                var parent = transform.parent;
                if (parent == null && !this.rootsRead)
                {
                    this.rootsRead = true;
                    this.Index(SceneRoots().Select(root => root.transform).ToArray());
                }
                else if (parent != null && this.parents.Add(parent))
                {
                    this.Index(ChildrenOf(parent));
                }

                // Preserve SceneRoots' PrefabStage behavior even for an object outside that set.
                var segment = this.segments.TryGetValue(transform, out var named) ? named : Segment(transform);
                path = (parent == null ? "" : this.PathOf(parent)) + "/" + segment;
                this.paths[transform] = path;
                return path;
            }

            /// <remarks>
            /// Each name is read once, because Object.name is a native call that returns a new
            /// string. Hashing strings is the largest cost left on the Editor's Mono, so a large
            /// group keeps each name's tally and numbers a sibling through it instead of looking the
            /// name up again.
            /// </remarks>
            private void Index(Transform[] siblings)
            {
                var names = new string[siblings.Length];

                for (var i = 0; i < siblings.Length; i++)
                {
                    names[i] = siblings[i].name;
                }

                if (siblings.Length <= PairwiseLimit)
                {
                    for (var i = 0; i < siblings.Length; i++)
                    {
                        var before = 0;
                        var repeats = false;

                        for (var j = 0; j < siblings.Length; j++)
                        {
                            if (j != i && string.Equals(names[j], names[i]))
                            {
                                repeats = true;

                                if (j < i)
                                {
                                    before++;
                                }
                            }
                        }

                        this.segments[siblings[i]] = repeats ? Escape(names[i]) + Suffix(before) : Escape(names[i]);
                    }

                    return;
                }

                var tallies = new Tally[siblings.Length];
                var byName = new Dictionary<string, Tally>(siblings.Length, StringComparer.Ordinal);

                for (var i = 0; i < siblings.Length; i++)
                {
                    if (!byName.TryGetValue(names[i], out var tally))
                    {
                        tally = new Tally();
                        byName.Add(names[i], tally);
                    }

                    tally.Count++;
                    tallies[i] = tally;
                }

                for (var i = 0; i < siblings.Length; i++)
                {
                    var escaped = Escape(names[i]);
                    this.segments[siblings[i]] = tallies[i].Count == 1 ? escaped : escaped + Suffix(tallies[i].Next++);
                }
            }

            /// <summary>
            /// The children read by index. Enumerating a Transform allocates an enumerator that
            /// makes two native calls for every child.
            /// </summary>
            private static Transform[] ChildrenOf(Transform parent)
            {
                var children = new Transform[parent.childCount];

                for (var i = 0; i < children.Length; i++)
                {
                    children[i] = parent.GetChild(i);
                }

                return children;
            }

            /// <summary>How many siblings share a name, and the index the next of them takes.</summary>
            private sealed class Tally
            {
                public int Count;
                public int Next;
            }
        }

        /// <summary>Finds a component on an object, by type name.</summary>
        public static Component Component(GameObject go, string typeName, int index = 0)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                throw new McpToolException("invalid_params", "'component_type' is required.");
            }

            var matches = go.GetComponents<Component>()
                .Where(c => c != null && TypeMatches(c.GetType(), typeName))
                .ToArray();

            if (matches.Length == 0)
            {
                var present = string.Join(", ", go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name));

                throw new McpToolException(
                    "not_found",
                    $"'{go.name}' has no component matching '{typeName}'. It has: {present}.");
            }

            if (index < 0 || index >= matches.Length)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{go.name}' has {matches.Length} component(s) matching '{typeName}'; index {index} is out of range.");
            }

            return matches[index];
        }

        /// <summary>Roots of every loaded scene, including prefab stages.</summary>
        public static IEnumerable<GameObject> SceneRoots()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();

            if (stage != null)
            {
                // While a prefab is open for editing its contents are not in any loaded scene's
                // root list, and acting on the scene behind it is never what was meant.
                yield return stage.prefabContentsRoot;

                yield break;
            }

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);

                if (!scene.isLoaded)
                {
                    continue;
                }

                foreach (var root in scene.GetRootGameObjects())
                {
                    yield return root;
                }
            }
        }

        /// <summary>
        /// A path's segments, split at the slashes that are not part of a name. Escapes are kept,
        /// so a segment can still tell an index from a name that ends like one.
        /// </summary>
        internal static List<string> Segments(string path)
        {
            var segments = new List<string>();
            var current = new StringBuilder();

            for (var i = 0; i < path.Length; i++)
            {
                if (IsEscape(path, i))
                {
                    current.Append(path, i, 2);
                    i++;
                }
                else if (path[i] == '/')
                {
                    if (current.Length > 0)
                    {
                        segments.Add(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(path[i]);
                }
            }

            if (current.Length > 0)
            {
                segments.Add(current.ToString());
            }

            return segments;
        }

        internal static GameObject Relative(GameObject root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            if (path.StartsWith("/", StringComparison.Ordinal))
                throw new McpToolException("invalid_params", "Prefab object_path must be relative to the root, without a leading '/'.");
            var current = root;
            foreach (var segment in Segments(path))
            {
                current = MatchSegment(Children(current), segment);
                if (current == null) throw new McpToolException("not_found", $"No prefab child at '{path}'.");
            }
            return current;
        }

        private static IEnumerable<GameObject> Children(GameObject go)
        {
            foreach (Transform child in go.transform)
            {
                yield return child.gameObject;
            }
        }

        private static GameObject MatchSegment(IEnumerable<GameObject> level, string segment)
        {
            var bracket = IndexBracket(segment, out var wanted);
            var name = Unescape(bracket > 0 ? segment.Substring(0, bracket) : segment);
            var seen = 0;

            foreach (var candidate in level)
            {
                if (candidate.name != name)
                {
                    continue;
                }

                if (seen == wanted)
                {
                    return candidate;
                }

                seen++;
            }

            return null;
        }

        private static string Segment(Transform t)
        {
            var siblings = t.parent == null
                ? SceneRootsOf(t)
                : t.parent.Cast<Transform>();

            var index = 0;
            var duplicates = 0;

            foreach (var sibling in siblings)
            {
                if (sibling.name != t.name)
                {
                    continue;
                }

                if (sibling == t)
                {
                    index = duplicates;
                }

                duplicates++;
            }

            var name = Escape(t.name);

            return duplicates > 1 ? $"{name}[{index}]" : name;
        }

        private static readonly char[] PathSyntax = { '\\', '/' };

        /// <summary>A name as a path segment writes it.</summary>
        /// <remarks>
        /// Few names end in ']', so the last character is checked first, and then one scan looks for
        /// both characters. The Editor's Mono does not vectorize IndexOf, so a separate scan for each
        /// character costs it close to three times as much.
        /// </remarks>
        private static string Escape(string name)
        {
            if ((name.Length == 0 || name[name.Length - 1] != ']') && name.IndexOfAny(PathSyntax) < 0)
            {
                return name;
            }

            var builder = new StringBuilder(name.Length + 2);

            foreach (var c in name)
            {
                if (c == '\\' || c == '/')
                {
                    builder.Append('\\');
                }

                builder.Append(c);
            }

            var escaped = builder.ToString();
            var bracket = IndexBracket(escaped, out _);

            return bracket > 0 ? escaped.Insert(bracket, "\\") : escaped;
        }

        private static readonly string[] Suffixes = new string[256];

        /// <summary>The '[n]' after a repeated name. Hierarchy reads run on the main thread, so the cache takes no lock.</summary>
        private static string Suffix(int index)
        {
            if (index >= Suffixes.Length)
            {
                return "[" + index + "]";
            }

            return Suffixes[index] ??= "[" + index + "]";
        }

        private static string Unescape(string text)
        {
            if (text.IndexOf('\\') < 0)
            {
                return text;
            }

            var builder = new StringBuilder(text.Length);

            for (var i = 0; i < text.Length; i++)
            {
                if (IsEscape(text, i))
                {
                    i++;
                }

                builder.Append(text[i]);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Where the '[n]' that picks among same-named siblings starts, or -1 when the segment
        /// ends in no unescaped one.
        /// </summary>
        private static int IndexBracket(string segment, out int index)
        {
            index = 0;

            if (!segment.EndsWith("]", StringComparison.Ordinal))
            {
                return -1;
            }

            var bracket = -1;

            for (var i = 0; i < segment.Length; i++)
            {
                if (IsEscape(segment, i))
                {
                    i++;
                }
                else if (segment[i] == '[')
                {
                    bracket = i;
                }
            }

            return bracket > 0
                   && int.TryParse(segment.Substring(bracket + 1, segment.Length - bracket - 2), out index)
                ? bracket
                : -1;
        }

        /// <summary>Whether the character at <paramref name="i"/> is a backslash escaping the next one.</summary>
        private static bool IsEscape(string text, int i)
        {
            if (text[i] != '\\' || i + 1 >= text.Length)
            {
                return false;
            }

            var next = text[i + 1];

            return next == '\\' || next == '/' || next == '[';
        }

        private static IEnumerable<Transform> SceneRootsOf(Transform t)
        {
            return SceneRoots().Select(go => go.transform);
        }

        private static bool TypeMatches(System.Type type, string typeName)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                if (t.Name == typeName || t.FullName == typeName)
                {
                    return true;
                }
            }

            return false;
        }

        private static string NotFoundMessage(string path, string[] segments, int depth, IEnumerable<GameObject> level)
        {
            var available = level.Select(g => Escape(g.name)).Distinct().Take(12).ToArray();
            var where = depth == 0
                ? "among the scene roots"
                : $"under '{string.Join("/", segments.Take(depth))}'";

            var listing = available.Length == 0
                ? "nothing is there"
                : string.Join(", ", available);

            return $"'{path}' does not resolve: no '{segments[depth]}' {where}. Found: {listing}. " +
                   "Paths come from scene_browse_hierarchy; inactive objects are included.";
        }
    }
}
