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
                var objectPath = parameters["objectPath"]?.ToString();

                // A limit of zero or less means every node, which is what an omitted limit
                // becomes; offset skips that many nodes of the flattened traversal, and fields
                // is an allowlist applied to the projected keys.
                var limit = parameters["limit"]?.Value<int?>() ?? 0;
                var offset = Math.Max(0, parameters["offset"]?.Value<int>() ?? 0);
                var fieldsFilter = ListResponseBuilder.ParseFieldsParam(parameters["fields"]?.ToString());

                var since = parameters["since"]?.ToString();
                var diffing = !string.IsNullOrEmpty(since);

                // Identity is what a diff is built on, and every reply is one a later call can
                // diff against, so the allowlist never drops it. Left out, each node would fail
                // to match itself and the comparison would report a still scene however much
                // had moved. It goes to the builder as alwaysKeep rather than into the filter,
                // so it cannot stand in for a field name the caller got wrong.

                // Two different walks describe two different sets of objects. Comparing across
                // them reports everything the narrower one leaves out as removed, which is a
                // confident wrong answer about objects that are still in the scene.
                var walk = WalkOf(nameFilter, componentFilter, tagFilter, maxDepth,
                    activeOnly, missingScriptsOnly, sceneIndex, objectPath, fieldsFilter);

                string expired = null;

                if (diffing)
                {
                    var takenUnder = SceneHierarchyBaseline.WalkOf(since);

                    if (takenUnder == null)
                    {
                        // The snapshot is gone, and a snapshot goes on every domain reload, which
                        // is every compile and every play-mode transition. The walk about to
                        // happen is the same one a fresh read would do, so refusing here would
                        // spend a round trip to be asked for a reply already in hand.
                        expired = since;
                        diffing = false;
                    }
                    else if (SceneHierarchyBaseline.IsPartial(since))
                    {
                        return new JObject
                        {
                            ["error"] = $"snapshot '{since}' covers only a page. Take a full one by reading without since, limit and offset. A scene too large to return whole narrows with 'name', 'component', 'tag', 'max_depth' or 'fields' instead: those keep the snapshot complete for what they select, where limit and offset leave it a page."
                        };
                    }
                    else if (!string.Equals(takenUnder, walk, StringComparison.Ordinal))
                    {
                        return new JObject
                        {
                            ["error"] = $"snapshot '{since}' was taken with different arguments ({takenUnder}); ask for the difference with the same ones, or read without 'since' to take a new snapshot"
                        };
                    }
                }

                // A window over the traversal cannot be diffed. The snapshot would hold one page
                // and the next call another, so an object pushed out of the window by an earlier
                // insertion would be reported as removed while it is still there.
                if (diffing && (limit > 0 || offset > 0))
                {
                    return new JObject
                    {
                        ["error"] = "'since' covers the whole filtered set and cannot be paged; drop limit and offset, or narrow with a filter instead"
                    };
                }

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

                void Walk(Transform root, int si)
                {
                    if (hasFilter)
                    {
                        var tree = BuildTreeNode(root, 0, maxDepth);
                        MarkMatches(tree, nameFilter, componentFilter, tagFilter, activeOnly, missingScriptsOnly);
                        CollectFilteredFlat(tree, si, -1, flat);
                    }
                    else
                    {
                        CollectFlat(root, 0, maxDepth, si, -1, flat);
                    }
                }

                if (!string.IsNullOrEmpty(objectPath))
                {
                    // One branch rather than every root. Without this, the objects under a known
                    // object could only be reached by taking the whole tree and finding them in
                    // it, which on a real scene is the whole scene for the sake of one subtree.
                    var start = UnityMCP.Editor.Tools.ObjectResolve.Object(
                        objectPath, null, "object_path", null);

                    Walk(start.transform, SceneIndexOf(start.scene));
                }
                else
                {
                    for (var si = startIndex; si < endIndex; si++)
                    {
                        var scene = SceneManager.GetSceneAt(si);
                        if (!scene.isLoaded) continue;

                        foreach (var root in scene.GetRootGameObjects())
                        {
                            Walk(root.transform, si);
                        }
                    }
                }

                // 2. Apply offset/limit and project to JObjects via ListResponseBuilder.
                var total = flat.Count;
                var effectiveLimit = limit <= 0 ? int.MaxValue : limit;
                JObject page;
                projecting = flat;
                var paths = new UnityMCP.Editor.Tools.ObjectResolve.PathBatch();
                try
                {
                    page = ListResponseBuilder.Build(
                        flat,
                        offset,
                        effectiveLimit,
                        node => ProjectFlatNode(node, paths),
                        fieldsFilter,
                        IdentityField
                    );
                }
                finally
                {
                    projecting = null;
                }

                if (diffing)
                {
                    return Changes(since, walk, page, total);
                }

                // A snapshot of what is about to be described, named so the next call can ask
                // for the difference from it. Taken before the page is re-nested, because the
                // rebuild moves the nodes into the tree, and before the two keys below are
                // dropped, because a comparison has to see everything that can change.
                var snapshotId = SceneHierarchyBaseline.Remember(walk, Peek(page),
                    partial: offset > 0 || page["truncated"].Value<bool>());

                // The tree says both of these already: `scenes` groups by scene and `children`
                // names the parent. Carried per node they were 40% of the response.
                DropWhatTheTreeAlreadySays(page);

                // 3. Rebuild the scenes[] structure from the page, preserving
                //    parent→children where both ends survived the paging window.
                var scenes = RebuildScenesFromPage(flat, offset, page, total);

                var result = new JObject
                {
                    ["snapshotId"] = snapshotId,
                    ["scenes"] = scenes,
                    ["sceneCount"] = endIndex - startIndex,
                    ["total"] = total,
                    // `truncated`/`next` are kept at top level for the envelope
                    // writer to hoist onto the outer envelope.
                    ["truncated"] = page["truncated"],
                    ["next"] = page["next"]
                };

                if (expired != null)
                {
                    // Why this is a tree when a difference was asked for. Without it the reply
                    // reads as the caller having forgotten to pass 'since'.
                    result["sinceExpired"] = expired;
                }

                return result;
            }
            catch (McpToolException)
            {
                // A refusal answers the request. Folded into the failure below it reads as the
                // Editor being unreadable, and a caller retries rather than correcting what it
                // sent.
                throw;
            }
            catch (Exception e)
            {
                return new JObject { ["error"] = $"Failed to browse scene hierarchy: {e.Message}" };
            }
        }

        /// <summary>
        /// The page as a diff against the snapshot the caller named. Flat rather than nested: a
        /// diff is a list of what moved, and nesting it would carry back the parents that did
        /// not move, which is the cost this mode exists to avoid.
        /// </summary>
        private static JObject Changes(string since, string walk, JObject page, int total)
        {
            var nodes = Detach(page);
            var diff = SceneHierarchyBaseline.CompareWith(since, walk, nodes, out var snapshotId);

            if (diff == null)
            {
                return new JObject
                {
                    ["error"] = $"no snapshot '{since}'. It expired, or the Editor reloaded its scripts since it was taken; read without 'since' to take a new one."
                };
            }

            return new JObject
            {
                ["snapshotId"] = snapshotId,
                ["since"] = since,
                ["added"] = diff.Added,
                ["changed"] = diff.Changed,
                ["removed"] = diff.Removed,
                ["unchanged"] = diff.Unchanged,
                ["total"] = total,
                ["truncated"] = page["truncated"],
                ["next"] = page["next"],
            };
        }

        /// <summary>
        /// What a snapshot covers, so a later call can be told it is asking about a different
        /// set of objects rather than being handed a diff between two of them.
        /// </summary>
        private static string WalkOf(
            string name, string component, string tag, int maxDepth,
            bool activeOnly, bool missingScriptsOnly, int? sceneIndex, string objectPath,
            string[] fields)
        {
            var joined = fields == null ? string.Empty : string.Join(",", fields);
            return $"name={name}|component={component}|tag={tag}|maxDepth={maxDepth}" +
                   $"|activeOnly={activeOnly}|missingScripts={missingScriptsOnly}" +
                   $"|sceneIndex={sceneIndex}|objectPath={objectPath}|fields={joined}";
        }

        /// <summary>Which open scene this one is, so a subtree reports the index its nodes carry.</summary>
        private static int SceneIndexOf(Scene scene)
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i) == scene)
                {
                    return i;
                }
            }

            return 0;
        }

        /// <summary>
        /// Removes the keys a nested reply does not need. They stay in the snapshot, so a later
        /// diff still notices a root moving between two open scenes or a node changing parent
        /// without changing its path.
        /// </summary>
        private static void DropWhatTheTreeAlreadySays(JObject page)
        {
            var items = page["items"] as JArray;
            if (items == null)
            {
                return;
            }

            foreach (var item in items)
            {
                var node = (JObject)item;
                node.Remove("parentInstanceId");
                node.Remove("scene");
            }
        }

        /// <summary>The page's nodes, left where they are: the tree rebuild still needs them.</summary>
        private static List<JObject> Peek(JObject page)
        {
            var nodes = new List<JObject>();
            var items = page["items"] as JArray;

            if (items != null)
            {
                foreach (var item in items)
                {
                    nodes.Add((JObject)item);
                }
            }

            return nodes;
        }

        /// <summary>
        /// The page's nodes, off the array that owns them. Newtonsoft copies a token that already
        /// belongs to a container when it is added to a second one, so leaving them parented
        /// would put copies into the response.
        /// </summary>
        private static List<JObject> Detach(JObject page)
        {
            var nodes = new List<JObject>();
            var items = page["items"] as JArray;

            if (items == null)
            {
                return nodes;
            }

            foreach (var item in items)
            {
                nodes.Add((JObject)item);
            }

            items.RemoveAll();
            return nodes;
        }

        private sealed class FlatNode
        {
            public GameObject Go;
            public int SceneIndex;
            public int ParentIndex; // index into the flat list, or -1 for roots.

            /// <summary>Children a filter kept out of the reply.</summary>
            /// <remarks>
            /// A filter matching a parent returns it with no children at all, which reads as a
            /// leaf: asking for "Platform" answered with Platforms and nothing under it, and the
            /// caller fetched the whole scene rather than the three plates it was after.
            /// </remarks>
            public int ChildrenNotShown;
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
            var mine = new FlatNode
            {
                Go = node.Go,
                SceneIndex = sceneIndex,
                ParentIndex = parentIndex
            };

            flat.Add(mine);

            foreach (var child in node.Children)
            {
                if (!child.Matched && !child.AncestorOfMatch)
                {
                    mine.ChildrenNotShown++;
                    continue;
                }

                CollectFilteredFlat(child, sceneIndex, myIndex, flat);
            }
        }

        /// <summary>Kept on every node however the caller narrows the reply.</summary>
        /// <remarks>
        /// Not <c>[ThreadStatic]</c>: an initialiser on such a field runs only on the thread that
        /// first touched the class, and this is read from the Editor's main thread, where it would
        /// be null.
        /// </remarks>
        private static readonly string[] IdentityField = { "instanceId" };

        /// <summary>
        /// The flat list the current page is being projected from. <see cref="ProjectFlatNode"/>
        /// needs it to name a node's parent, and the projector signature takes one node.
        /// </summary>
        [ThreadStatic]
        private static List<FlatNode> projecting;

        private static JObject ProjectFlatNode(FlatNode n, UnityMCP.Editor.Tools.ObjectResolve.PathBatch paths)
        {
            // The nested structure is rebuilt in RebuildScenesFromPage, so we
            // emit only the node-level keys here. ListResponseBuilder applies
            // the `fieldsFilter` allowlist after this projection.
            var go = n.Go;
            var node = new JObject
            {
                ["name"] = go.name,
                // The identifier every authoring tool takes. Without it a caller who has just
                // browsed the hierarchy has to guess at the path of the thing they are looking
                // at, and guesses fail on any name that repeats among siblings.
                ["path"] = paths.PathOf(go),
                ["instanceId"] = EntityIdCompat.WireIdOf(go),
                // A path carries an index only where a sibling name repeats, so without this a
                // reorder is invisible — and it decides draw order under a Canvas.
                ["siblingIndex"] = go.transform.GetSiblingIndex(),
                // A diff arrives flat. Without this the caller can only rebuild the tree by
                // parsing paths, which is ambiguous when a name contains a slash and says
                // nothing when a parent is replaced by another of the same name.
                ["parentInstanceId"] = n.ParentIndex >= 0 && projecting != null
                    ? EntityIdCompat.WireIdOf(projecting[n.ParentIndex].Go)
                    : JValue.CreateNull(),
                // Moving a root from one open scene to another changes nothing else about it.
                // The path is the stable name; the index shifts as scenes open and close.
                ["scene"] = SceneManager.GetSceneAt(n.SceneIndex).path,
            };

            // Keys at their default are left out. On a scene of any size they are most of the
            // response and they carry nothing: a caller reads an absent key as the default.
            if (!go.activeSelf)
            {
                node["active"] = false;
            }

            if (!string.Equals(go.tag, "Untagged", StringComparison.Ordinal))
            {
                node["tag"] = go.tag;
            }

            // An unnamed layer has no name to give, and reporting the empty string tells the
            // caller only that it is not Default — which is the moment they most need to know
            // which one it is, because an object on an unnamed layer is usually one nothing draws.
            var layer = LayerMask.LayerToName(go.layer);

            if (string.IsNullOrEmpty(layer))
            {
                node["layer"] = go.layer;
            }
            else if (!string.Equals(layer, "Default", StringComparison.Ordinal))
            {
                node["layer"] = layer;
            }

            // Never empty: every GameObject carries a Transform.
            node["components"] = GetComponentNames(go, out var missing);

            if (n.ChildrenNotShown > 0)
            {
                node["childrenNotShown"] = n.ChildrenNotShown;
            }

            if (missing > 0)
            {
                node["missingScripts"] = missing;
            }

            return node;
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

        private static JArray GetComponentNames(GameObject go, out int missing)
        {
            var arr = new JArray();
            missing = 0;
            foreach (var comp in go.GetComponents<Component>())
            {
                // A component that reads as null is a MonoBehaviour whose script Unity cannot
                // resolve: the class was renamed, or the package that declared it was removed.
                // Naming it is the difference between an agent explaining a broken avatar and
                // reporting a null it cannot account for.
                arr.Add(comp != null ? comp.GetType().Name : MissingScript);
                if (comp == null) missing++;
            }
            return arr;
        }
    }
}
