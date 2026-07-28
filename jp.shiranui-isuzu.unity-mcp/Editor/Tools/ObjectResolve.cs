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
    /// </remarks>
    internal static class ObjectResolve
    {
        /// <summary>
        /// Resolves a GameObject from a path, an instance id, or both.
        /// </summary>
        public static GameObject Object(string path, int? instanceId, string argumentName = "object_path")
        {
            if (instanceId.HasValue)
            {
                var byId = EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;

                if (byId != null)
                {
                    return byId;
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
                    $"Either '{argumentName}' or 'instance_id' is required.");
            }

            var segments = path.Split('/').Where(s => s.Length > 0).ToArray();

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

        private static IEnumerable<GameObject> Children(GameObject go)
        {
            foreach (Transform child in go.transform)
            {
                yield return child.gameObject;
            }
        }

        private static GameObject MatchSegment(IEnumerable<GameObject> level, string segment)
        {
            var name = segment;
            var wanted = 0;

            var bracket = segment.LastIndexOf('[');

            if (bracket > 0 && segment.EndsWith("]") &&
                int.TryParse(segment.Substring(bracket + 1, segment.Length - bracket - 2), out var parsed))
            {
                name = segment.Substring(0, bracket);
                wanted = parsed;
            }

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

            return duplicates > 1 ? $"{t.name}[{index}]" : t.name;
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
            var available = level.Select(g => g.name).Distinct().Take(12).ToArray();
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
