using System;
using System.Collections.Generic;

using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Handlers
{
    internal static class SceneHierarchy
    {
        public static JObject Browse(JObject parameters)
        {
            try
            {
                var nameFilter = parameters["name"]?.ToString();
                var componentFilter = parameters["component"]?.ToString();
                var tagFilter = parameters["tag"]?.ToString();
                var maxDepth = parameters["maxDepth"]?.Value<int>() ?? 5;
                var activeOnly = parameters["activeOnly"]?.Value<bool>() ?? false;
                var missingScriptsOnly = parameters["missingScripts"]?.Value<bool>() ?? false;
                var sceneIndex = parameters["sceneIndex"]?.Value<int?>();

                // A limit of zero or less means every node, which is what an omitted limit
                // becomes; offset skips that many nodes of the flattened traversal, and fields
                // is an allowlist applied to the projected keys.
                var limit = parameters["limit"]?.Value<int?>() ?? 0;
                var offset = Math.Max(0, parameters["offset"]?.Value<int>() ?? 0);
                var fieldsFilter = ListResponseBuilder.ParseFieldsParam(parameters["fields"]?.ToString());

                var hasFilter = !string.IsNullOrEmpty(nameFilter)
                    || !string.IsNullOrEmpty(componentFilter)
                    || !string.IsNullOrEmpty(tagFilter)
                    || activeOnly
                    || missingScriptsOnly;

                var sceneCount = SceneManager.sceneCount;
                if (sceneIndex.HasValue)
                {
                    if (sceneIndex.Value < 0 || sceneIndex.Value >= sceneCount)
                    {
                        return new JObject
                        {
                            ["error"] = $"scene_index {sceneIndex.Value} out of range (0..{sceneCount - 1})"
                        };
                    }
                }

                // 1. Flatten: walk every (filtered) scene and collect node records.
                //    We walk root-first, depth-first; offset/limit apply to this
                //    flattened stream. Parent→child relations are preserved via
                //    a parent-index pointer and rebuilt into nested trees below.
                var flat = new List<FlatNode>();
                var startIndex = sceneIndex ?? 0;
                var endIndex = sceneIndex.HasValue ? sceneIndex.Value + 1 : sceneCount;

                for (var si = startIndex; si < endIndex; si++)
                {
                    var scene = SceneManager.GetSceneAt(si);
                    if (!scene.isLoaded) continue;

                    var rootObjects = scene.GetRootGameObjects();
                    foreach (var root in rootObjects)
                    {
                        if (hasFilter)
                        {
                            var tree = BuildTreeNode(root.transform, 0, maxDepth);
                            MarkMatches(tree, nameFilter, componentFilter, tagFilter, activeOnly, missingScriptsOnly);
                            CollectFilteredFlat(tree, si, -1, flat);
                        }
                        else
                        {
                            CollectFlat(root.transform, 0, maxDepth, si, -1, flat);
                        }
                    }
                }

                // 2. Apply offset/limit and project to JObjects via ListResponseBuilder.
                var total = flat.Count;
                var effectiveLimit = limit <= 0 ? int.MaxValue : limit;
                var page = ListResponseBuilder.Build(
                    flat,
                    offset,
                    effectiveLimit,
                    ProjectFlatNode,
                    fieldsFilter
                );

                // 3. Rebuild the scenes[] structure from the page, preserving
                //    parent→children where both ends survived the paging window.
                var scenes = RebuildScenesFromPage(flat, offset, page, total);

                var result = new JObject
                {
                    ["scenes"] = scenes,
                    ["sceneCount"] = endIndex - startIndex,
                    ["total"] = total,
                    // `truncated`/`next` are kept at top level for the envelope
                    // writer to hoist onto the outer envelope.
                    ["truncated"] = page["truncated"],
                    ["next"] = page["next"]
                };

                return result;
            }
            catch (Exception e)
            {
                return new JObject { ["error"] = $"Failed to browse scene hierarchy: {e.Message}" };
            }
        }

        private sealed class FlatNode
        {
            public GameObject Go;
            public int SceneIndex;
            public int ParentIndex; // index into the flat list, or -1 for roots.
        }

        private static void CollectFlat(
            Transform transform,
            int depth,
            int maxDepth,
            int sceneIndex,
            int parentIndex,
            List<FlatNode> flat)
        {
            var myIndex = flat.Count;
            flat.Add(new FlatNode
            {
                Go = transform.gameObject,
                SceneIndex = sceneIndex,
                ParentIndex = parentIndex
            });

            if (depth >= maxDepth) return;

            for (var i = 0; i < transform.childCount; i++)
            {
                CollectFlat(transform.GetChild(i), depth + 1, maxDepth, sceneIndex, myIndex, flat);
            }
        }

        private static void CollectFilteredFlat(
            TreeNode node,
            int sceneIndex,
            int parentIndex,
            List<FlatNode> flat)
        {
            // Only include nodes that are matched themselves or are ancestors
            // of a matched node.
            if (!node.Matched && !node.AncestorOfMatch) return;

            var myIndex = flat.Count;
            flat.Add(new FlatNode
            {
                Go = node.Go,
                SceneIndex = sceneIndex,
                ParentIndex = parentIndex
            });

            foreach (var child in node.Children)
            {
                CollectFilteredFlat(child, sceneIndex, myIndex, flat);
            }
        }

        private static JObject ProjectFlatNode(FlatNode n)
        {
            // The nested structure is rebuilt in RebuildScenesFromPage, so we
            // emit only the node-level keys here. ListResponseBuilder applies
            // the `fieldsFilter` allowlist after this projection.
            var go = n.Go;
            return new JObject
            {
                ["name"] = go.name,
                // The identifier every authoring tool takes. Without it a caller who has just
                // browsed the hierarchy has to guess at the path of the thing they are looking
                // at, and guesses fail on any name that repeats among siblings.
                ["path"] = UnityMCP.Editor.Tools.ObjectResolve.PathOf(go),
                ["id"] = EntityIdCompat.WireIdOf(go),
                ["instanceId"] = EntityIdCompat.WireIdOf(go),
                ["active"] = go.activeSelf,
                ["tag"] = go.tag,
                ["layer"] = LayerMask.LayerToName(go.layer),
                ["components"] = GetComponentNames(go),
                ["missingScripts"] = MissingScriptCount(go),
            };
        }

        private static JArray RebuildScenesFromPage(
            List<FlatNode> flat,
            int offset,
            JObject page,
            int total)
        {
            var items = page["items"] as JArray;
            if (items == null) return new JArray();

            // Newtonsoft copies a token that already belongs to a container when it is added
            // to a second one, so the page items must be detached from `page["items"]` before
            // they are assembled into scene trees. Assembling them while they are still
            // parented puts copies into the response, and the `children` writes below then
            // land on the originals nobody reads, which flattens the whole tree.
            var paged = new JObject[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                paged[i] = (JObject)items[i];
            }
            items.RemoveAll();

            // Project paged items to (flatIndex, JObject) pairs.
            var windowStart = offset;
            var windowEnd = Math.Min(offset + paged.Length, total);

            // Map flatIndex → its JObject within the page window.
            var pageByFlatIndex = new Dictionary<int, JObject>(paged.Length);
            for (var i = 0; i < paged.Length; i++)
            {
                pageByFlatIndex[windowStart + i] = paged[i];
            }

            // Group paged nodes by sceneIndex.
            var sceneBuckets = new Dictionary<int, List<int>>();
            for (var i = windowStart; i < windowEnd; i++)
            {
                var node = flat[i];
                if (!sceneBuckets.TryGetValue(node.SceneIndex, out var bucket))
                {
                    bucket = new List<int>();
                    sceneBuckets[node.SceneIndex] = bucket;
                }
                bucket.Add(i);
            }

            var scenes = new JArray();
            foreach (var kv in sceneBuckets)
            {
                var scene = SceneManager.GetSceneAt(kv.Key);

                // Build a lookup of children lists inside the window.
                var childrenOf = new Dictionary<int, JArray>();
                var topLevel = new JArray();

                foreach (var idx in kv.Value)
                {
                    var node = flat[idx];
                    var obj = pageByFlatIndex[idx];

                    if (node.ParentIndex >= windowStart && node.ParentIndex < windowEnd)
                    {
                        if (!childrenOf.TryGetValue(node.ParentIndex, out var arr))
                        {
                            arr = new JArray();
                            childrenOf[node.ParentIndex] = arr;
                        }
                        arr.Add(obj);
                    }
                    else
                    {
                        // Parent is outside the current page → promote to top level.
                        topLevel.Add(obj);
                    }
                }

                // Attach children to their parents.
                foreach (var pair in childrenOf)
                {
                    var parentObj = pageByFlatIndex[pair.Key];
                    parentObj["children"] = pair.Value;
                }

                scenes.Add(new JObject
                {
                    ["name"] = scene.name,
                    ["gameObjects"] = topLevel
                });
            }

            return scenes;
        }

        private sealed class TreeNode
        {
            public GameObject Go;
            public List<TreeNode> Children;
            public bool Matched;
            public bool AncestorOfMatch;
        }

        private static TreeNode BuildTreeNode(Transform transform, int depth, int maxDepth)
        {
            var node = new TreeNode
            {
                Go = transform.gameObject,
                Children = new List<TreeNode>()
            };

            if (depth < maxDepth)
            {
                for (var i = 0; i < transform.childCount; i++)
                {
                    node.Children.Add(BuildTreeNode(transform.GetChild(i), depth + 1, maxDepth));
                }
            }

            return node;
        }

        /// <summary>What a component whose script Unity cannot resolve is called in a reply.</summary>
        internal const string MissingScript = "<missing script>";

        private static int MissingScriptCount(GameObject go)
        {
            var missing = 0;

            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null)
                {
                    missing++;
                }
            }

            return missing;
        }

        private static void MarkMatches(
            TreeNode node,
            string nameFilter,
            string componentFilter,
            string tagFilter,
            bool activeOnly,
            bool missingScriptsOnly)
        {
            var go = node.Go;
            var matches = true;

            if (activeOnly && !go.activeSelf)
                matches = false;

            if (matches && missingScriptsOnly && MissingScriptCount(go) == 0)
                matches = false;

            if (matches && !string.IsNullOrEmpty(nameFilter))
            {
                if (go.name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    matches = false;
            }

            if (matches && !string.IsNullOrEmpty(componentFilter))
            {
                var found = false;
                foreach (var comp in go.GetComponents<Component>())
                {
                    if (comp != null && comp.GetType().Name == componentFilter)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                    matches = false;
            }

            if (matches && !string.IsNullOrEmpty(tagFilter))
            {
                if (!go.CompareTag(tagFilter))
                    matches = false;
            }

            node.Matched = matches;

            foreach (var child in node.Children)
            {
                MarkMatches(child, nameFilter, componentFilter, tagFilter, activeOnly, missingScriptsOnly);
            }

            foreach (var child in node.Children)
            {
                if (child.Matched || child.AncestorOfMatch)
                {
                    node.AncestorOfMatch = true;
                    break;
                }
            }
        }

        private static JArray GetComponentNames(GameObject go)
        {
            var arr = new JArray();
            foreach (var comp in go.GetComponents<Component>())
            {
                // A component that reads as null is a MonoBehaviour whose script Unity cannot
                // resolve: the class was renamed, or the package that declared it was removed.
                // Naming it is the difference between an agent explaining a broken avatar and
                // reporting a null it cannot account for.
                arr.Add(comp != null ? comp.GetType().Name : MissingScript);
            }
            return arr;
        }
    }
}
