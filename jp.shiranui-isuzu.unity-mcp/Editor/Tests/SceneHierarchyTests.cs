using NUnit.Framework;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEngine;
using UnityEngine.SceneManagement;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Handlers;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Covers the shape <c>scene_browse_hierarchy</c> returns. A correct <c>total</c> says
    /// nothing about the payload, so every assertion here reads the tree rather than a count.
    /// </summary>
    [TestFixture]
    internal sealed class SceneHierarchyTests
    {
        private const string NamePrefix = "SHTest";

        [Test]
        public void BatchedPathsMatchSinglePathsAcrossDuplicateNamesAndRoots()
        {
            var other = new GameObject(this.root.name);
            var a = new GameObject("Duplicate");
            a.transform.SetParent(this.root.transform);
            var b = new GameObject("Duplicate");
            b.transform.SetParent(this.root.transform);
            b.SetActive(false);
            try
            {
                var objects = this.root.GetComponentsInChildren<Transform>(true)
                    .Select(t => t.gameObject).Concat(new[] { other }).ToArray();
                var batch = new UnityMCP.Editor.Tools.ObjectResolve.PathBatch();
                foreach (var go in objects.Reverse())
                    Assert.That(batch.PathOf(go), Is.EqualTo(UnityMCP.Editor.Tools.ObjectResolve.PathOf(go)));

                b.transform.SetSiblingIndex(0);
                var nextRead = new UnityMCP.Editor.Tools.ObjectResolve.PathBatch();
                foreach (var go in objects)
                    Assert.That(nextRead.PathOf(go), Is.EqualTo(UnityMCP.Editor.Tools.ObjectResolve.PathOf(go)),
                        "a new browse must observe reordered siblings");

                var filtered = SceneHierarchy.Browse(ToolArgs.Of(("name", "Duplicate"), ("activeOnly", true)));
                var node = FindByName(ChildrenOf(FindNode(filtered, this.root.name)), "Duplicate");
                Assert.That(node, Is.Not.Null);
                Assert.That(node["path"].ToString(), Is.EqualTo(UnityMCP.Editor.Tools.ObjectResolve.PathOf(a)),
                    "inactive siblings excluded by the filter still determine path indices");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(a);
                UnityEngine.Object.DestroyImmediate(b);
                UnityEngine.Object.DestroyImmediate(other);
            }
        }
        private const string RootName = "SHTestRoot";
        private const string ChildAName = "SHTestChildA";
        private const string ChildBName = "SHTestChildB";
        private const string GrandchildName = "SHTestGrandchild";

        private GameObject root;

        [SetUp]
        public void SetUp()
        {
            // The baseline outlives a single test, so each one starts from nothing.
            SceneHierarchyBaseline.Reset();

            this.root = new GameObject(RootName);

            var childA = new GameObject(ChildAName);
            childA.transform.SetParent(this.root.transform);

            var grandchild = new GameObject(GrandchildName);
            grandchild.transform.SetParent(childA.transform);
            grandchild.AddComponent<SphereCollider>();

            var childB = new GameObject(ChildBName);
            childB.transform.SetParent(this.root.transform);
            childB.AddComponent<SphereCollider>();
        }

        [TearDown]
        public void TearDown()
        {
            if (this.root != null)
            {
                UnityEngine.Object.DestroyImmediate(this.root);
                this.root = null;
            }
        }

        [Test]
        public void APreResetSnapshotCannotBecomeAnotherSnapshotsId()
        {
            var nodes = new[] { new JObject { ["instanceId"] = 1, ["name"] = "before" } };
            var oldId = SceneHierarchyBaseline.Remember("same-walk", nodes);
            SceneHierarchyBaseline.Reset();
            var newId = SceneHierarchyBaseline.Remember("same-walk", nodes);

            Assert.That(newId, Is.Not.EqualTo(oldId));
            Assert.That(SceneHierarchyBaseline.WalkOf(oldId), Is.Null);
            Assert.That(SceneHierarchyBaseline.CompareWith(oldId, "same-walk", nodes, out var replacementId), Is.Null);
            Assert.That(replacementId, Is.Null);
        }

        /// <summary>
        /// A layer nobody named is still the answer to "why is this not drawn", so it has to be
        /// identifiable. Reported as the empty string it said only "not Default".
        /// </summary>
        [Test]
        public void AnUnnamedLayerIsReportedByItsNumber()
        {
            var go = new GameObject("UnnamedLayerObject") { layer = 31 };

            try
            {
                var browsed = SceneHierarchy.Browse(ToolArgs.Of(("name", "UnnamedLayerObject")));

                var node = ((JArray)browsed["scenes"])
                    .SelectMany(scene => (JArray)scene["gameObjects"])
                    .First(n => n["name"].ToString() == "UnnamedLayerObject");

                Assert.That(node["layer"].Value<int>(), Is.EqualTo(31));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Browse_Unfiltered_NestsDescendantsUnderTheirParent()
        {
            var result = SceneHierarchy.Browse(ToolArgs.Of(
                ("sceneIndex", ActiveSceneIndex()),
                ("maxDepth", 5)));

            var rootNode = FindByName(SceneObjects(result), RootName);
            Assert.IsNotNull(rootNode, "the probe root is missing from the top level");

            var childA = FindByName(ChildrenOf(rootNode), ChildAName);
            Assert.IsNotNull(childA, "the root came back without a children array");

            var grandchild = FindByName(ChildrenOf(childA), GrandchildName);
            Assert.IsNotNull(grandchild, "the grandchild is missing from the children of its parent");
            Assert.AreEqual(
                "/" + RootName + "/" + ChildAName + "/" + GrandchildName,
                (string)grandchild["path"]);
        }

        [Test]
        public void Browse_Unfiltered_KeepsDescendantsOffTheTopLevel()
        {
            var result = SceneHierarchy.Browse(ToolArgs.Of(
                ("sceneIndex", ActiveSceneIndex()),
                ("maxDepth", 5)));

            var top = SceneObjects(result);

            Assert.IsNull(FindByName(top, ChildAName), "a child was reported as a root");
            Assert.IsNull(FindByName(top, GrandchildName), "a grandchild was reported as a root");
        }

        [Test]
        public void Browse_MaxDepthOne_StopsBelowTheFirstLevel()
        {
            var result = SceneHierarchy.Browse(ToolArgs.Of(
                ("sceneIndex", ActiveSceneIndex()),
                ("maxDepth", 1)));

            var rootNode = FindByName(SceneObjects(result), RootName);
            var childA = FindByName(ChildrenOf(rootNode), ChildAName);

            Assert.IsNotNull(childA, "depth 1 must still reach the direct children");
            Assert.IsNotNull(FindByName(ChildrenOf(rootNode), ChildBName));
            Assert.IsNull(
                FindByName(ChildrenOf(childA), GrandchildName),
                "depth 1 descended past the first level");
        }

        [Test]
        public void Browse_ComponentFilter_ReturnsMatchingDescendantsNotOnlyTheAncestor()
        {
            var result = SceneHierarchy.Browse(ToolArgs.Of(
                ("sceneIndex", ActiveSceneIndex()),
                ("component", "SphereCollider"),
                ("maxDepth", 5)));

            var rootNode = FindByName(SceneObjects(result), RootName);
            Assert.IsNotNull(rootNode, "the ancestor leading to the matches is missing");

            var childA = FindByName(ChildrenOf(rootNode), ChildAName);
            Assert.IsNotNull(childA, "the ancestor of the matching grandchild is missing");
            Assert.IsNotNull(
                FindByName(ChildrenOf(childA), GrandchildName),
                "the matching grandchild is missing");
            Assert.IsNotNull(
                FindByName(ChildrenOf(rootNode), ChildBName),
                "the matching child is missing");
        }

        [Test]
        public void Browse_NameFilter_CountsEveryNodeAndKeepsTheTree()
        {
            var result = SceneHierarchy.Browse(ToolArgs.Of(
                ("sceneIndex", ActiveSceneIndex()),
                ("name", NamePrefix),
                ("maxDepth", 5)));

            Assert.AreEqual(4, (int)result["total"]);

            var top = SceneObjects(result);
            Assert.AreEqual(1, top.Count, "only the probe root belongs at the top level");

            var rootNode = FindByName(top, RootName);
            Assert.AreEqual(2, ChildrenOf(rootNode).Count);

            var childA = FindByName(ChildrenOf(rootNode), ChildAName);
            Assert.IsNotNull(FindByName(ChildrenOf(childA), GrandchildName));
        }

        [Test]
        public void Browse_PageStartingBelowTheRoot_PromotesTheOrphanAndKeepsItsSubtree()
        {
            var result = SceneHierarchy.Browse(ToolArgs.Of(
                ("sceneIndex", ActiveSceneIndex()),
                ("name", NamePrefix),
                ("maxDepth", 5),
                ("offset", 1),
                ("limit", 2)));

            Assert.AreEqual(4, (int)result["total"], "total must count the walk, not the page");
            Assert.IsTrue((bool)result["truncated"]);

            var top = SceneObjects(result);
            Assert.AreEqual(1, top.Count, "only the orphaned child belongs at the top level");

            var childA = FindByName(top, ChildAName);
            Assert.IsNotNull(childA, "the child whose parent fell outside the page was not promoted");
            Assert.IsNotNull(
                FindByName(ChildrenOf(childA), GrandchildName),
                "the promoted child lost the subtree that stayed inside the page");
        }

        [Test]
        public void Browse_PageCoveringOnlyTheRoot_OmitsChildrenOutsideTheWindow()
        {
            var result = SceneHierarchy.Browse(ToolArgs.Of(
                ("sceneIndex", ActiveSceneIndex()),
                ("name", NamePrefix),
                ("maxDepth", 5),
                ("offset", 0),
                ("limit", 1)));

            Assert.AreEqual(4, (int)result["total"]);

            var top = SceneObjects(result);
            Assert.AreEqual(1, top.Count);
            Assert.IsNull(
                ChildrenOf(FindByName(top, RootName)),
                "nodes outside the page must not be reported");
        }

        [Test]
        public void Browse_WithFieldsFilter_StillNests()
        {
            var result = SceneHierarchy.Browse(ToolArgs.Of(
                ("sceneIndex", ActiveSceneIndex()),
                ("name", NamePrefix),
                ("maxDepth", 5),
                ("fields", "name")));

            var rootNode = FindByName(SceneObjects(result), RootName);
            var childA = FindByName(ChildrenOf(rootNode), ChildAName);

            Assert.IsNotNull(childA, "the fields allowlist must not drop the nesting");
            Assert.IsNotNull(FindByName(ChildrenOf(childA), GrandchildName));
            Assert.IsNull(childA["path"], "the fields allowlist must still drop unlisted keys");
        }

        private static JObject Full()
        {
            return SceneHierarchy.Browse(ToolArgs.Of(("name", NamePrefix)));
        }

        private static JObject Since(string snapshotId)
        {
            return SceneHierarchy.Browse(ToolArgs.Of(("name", NamePrefix), ("since", snapshotId)));
        }

        private static string SnapshotOf(JObject result)
        {
            return result["snapshotId"]?.ToString();
        }

        private static int Count(JObject result, string key)
        {
            return (result[key] as JArray)?.Count ?? -1;
        }

        [Test]
        public void EveryReplyNamesTheStateItDescribes()
        {
            var full = Full();

            Assert.That(SnapshotOf(full), Is.Not.Null.And.Not.Empty,
                "without an id the caller has nothing to ask for a difference from");
        }

        [TestCase(1, 0)]
        [TestCase(0, 1)]
        public void APartialSnapshotCannotReportUnseenExistingNodesAsAdded(int limit, int offset)
        {
            var page = SceneHierarchy.Browse(ToolArgs.Of(
                ("name", NamePrefix), ("limit", limit), ("offset", offset)));
            var snapshot = SnapshotOf(page);
            Assert.That(snapshot, Is.Not.Null.And.Not.Empty, "pages still identify their snapshot");
            var result = Since(snapshot);
            // The way out is named as well as the refusal: a scene too large to return whole is
            // narrowed with a filter, which keeps the snapshot complete for what it selects.
            Assert.That(result["error"]?.ToString(),
                        Does.Contain("only a page").And.Contain("Take a full one").And.Contain("max_depth"));
            Assert.That(result["added"], Is.Null);
        }

        [Test]
        public void ALimitThatIncludesTheWholeWalkStillMakesAFullSnapshot()
        {
            var page = SceneHierarchy.Browse(ToolArgs.Of(("name", NamePrefix), ("limit", int.MaxValue)));
            var result = Since(SnapshotOf(page));
            Assert.That(result["error"], Is.Null);
            Assert.That(Count(result, "added"), Is.Zero);
            Assert.That(Count(result, "changed"), Is.Zero);
        }

        [Test]
        public void AStillSceneDiffersFromItsOwnSnapshotInNothing()
        {
            var result = Since(SnapshotOf(Full()));

            Assert.That(Count(result, "added"), Is.Zero);
            Assert.That(Count(result, "changed"), Is.Zero);
            Assert.That(Count(result, "removed"), Is.Zero);
            Assert.That(result["unchanged"].Value<int>(), Is.EqualTo(result["total"].Value<int>()));
        }

        [Test]
        public void OneClientReadingDoesNotConsumeTheChangesAnotherIsWaitingFor()
        {
            // The failure this pins: with one baseline per filter, the second read would move the
            // state the first caller compares against, and it would never hear about the change.
            var a = SnapshotOf(Full());
            var b = SnapshotOf(Full());

            var added = new GameObject(NamePrefix + "Shared");
            try
            {
                var forB = Since(b);
                var forA = Since(a);

                Assert.That(Count(forB, "added"), Is.EqualTo(1), "B has to see it");
                Assert.That(Count(forA, "added"), Is.EqualTo(1), "and B reading must not eat it for A");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(added);
            }
        }

        [Test]
        public void AReplyThatNeverArrivedLeavesTheOldSnapshotUsable()
        {
            // A caller that did not receive an answer retries with the id it still holds, and has
            // to be told the same difference rather than nothing.
            var held = SnapshotOf(Full());

            var added = new GameObject(NamePrefix + "Lost");
            try
            {
                var first = Since(held);
                var retry = Since(held);

                Assert.That(Count(first, "added"), Is.EqualTo(1));
                Assert.That(Count(retry, "added"), Is.EqualTo(1), "the snapshot must not have moved");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(added);
            }
        }

        [Test]
        public void ReorderingSiblingsIsAChangeRatherThanNothing()
        {
            // A path carries an index only where a sibling name repeats, so without the sibling
            // index a swap of two differently-named siblings was invisible.
            var snapshot = SnapshotOf(Full());
            GameObject.Find(RootName + "/" + ChildBName).transform.SetSiblingIndex(0);

            var result = Since(snapshot);

            Assert.That(Count(result, "changed"), Is.GreaterThan(0), "the reorder has to be reported");
            Assert.That(Count(result, "removed"), Is.Zero);
            Assert.That(Count(result, "added"), Is.Zero);
        }

        [Test]
        public void ARenameIsOneChangedNodeRatherThanARemovalAndAnAddition()
        {
            var snapshot = SnapshotOf(Full());

            // A leaf: renaming a parent rewrites the path of everything under it, and those are
            // real changes too. This isolates the renamed object itself.
            GameObject.Find(RootName + "/" + ChildBName).name = ChildBName + "Renamed";

            var result = Since(snapshot);

            // Following objects by path would report this as the old one disappearing and a new
            // one arriving, which is what instance ids are here to avoid.
            Assert.That(Count(result, "changed"), Is.EqualTo(1));
            Assert.That(Count(result, "removed"), Is.Zero);
            Assert.That(Count(result, "added"), Is.Zero);
        }

        [Test]
        public void ADestroyedObjectIsNamedInRemovedTheWayTheNodesNameIt()
        {
            var full = Full();
            var childB = FindByName(ChildrenOf(FindByName(SceneObjects(full), RootName)), ChildBName);
            var goneId = childB["instanceId"];

            UnityEngine.Object.DestroyImmediate(GameObject.Find(RootName + "/" + ChildBName));

            var result = Since(SnapshotOf(full));
            var removed = (JArray)result["removed"];

            Assert.That(removed.Count, Is.EqualTo(1));
            // Same JSON type the nodes carry, so a caller keyed by the id it was given can find
            // the removal with it. Before Unity 6.5 that is a number, not a string.
            Assert.That(removed[0].Type, Is.EqualTo(goneId.Type),
                "removed ids have to be spelled the way node ids are");
            Assert.That(removed[0].ToString(), Is.EqualTo(goneId.ToString()));
        }

        [Test]
        public void AChangedNodeReplacesRatherThanMergesIntoWhatWasHeld()
        {
            // A caller applying a shallow merge would keep the old tag and the old active, both
            // of which are gone from the node precisely because they went back to their default.
            var childB = GameObject.Find(RootName + "/" + ChildBName);
            childB.SetActive(false);
            childB.tag = "Player";

            var snapshot = SnapshotOf(Full());
            childB.SetActive(true);
            childB.tag = "Untagged";

            var changed = (JArray)Since(snapshot)["changed"];
            var node = FindByName(changed, ChildBName);

            Assert.That(node, Is.Not.Null, "going back to the default is still a change");
            Assert.That(node["active"], Is.Null, "the node carries no active, meaning true");
            Assert.That(node["tag"], Is.Null, "the node carries no tag, meaning Untagged");
        }

        [Test]
        public void RemovedNamesWhatLeftTheResultRatherThanWhatWasDestroyed()
        {
            // Renaming an object out of the filter puts it in `removed` while it sits in the
            // scene. A caller deleting its own record of the object on that word would be wrong.
            var snapshot = SnapshotOf(Full());
            var stillThere = GameObject.Find(RootName + "/" + ChildBName);
            stillThere.name = "OutOfTheFilter";

            try
            {
                var result = Since(snapshot);

                Assert.That(Count(result, "removed"), Is.EqualTo(1));
                Assert.That(stillThere, Is.Not.Null, "and the object is still in the scene");
                Assert.That(GameObject.Find("/" + RootName + "/OutOfTheFilter"), Is.Not.Null);
            }
            finally
            {
                stillThere.name = ChildBName;
            }
        }

        [Test]
        public void ADiffCarriesTheParentAndSceneTheTreeWouldHaveShown()
        {
            // A diff arrives flat, so the two things the tree says by its shape have to be on
            // the node instead.
            var snapshot = SnapshotOf(Full());
            var added = new GameObject(NamePrefix + "Flat");
            added.transform.SetParent(this.root.transform);

            try
            {
                var node = FindByName((JArray)Since(snapshot)["added"], NamePrefix + "Flat");

                Assert.That(node, Is.Not.Null);
                Assert.That(node["parentInstanceId"]?.ToString(),
                    Is.EqualTo(EntityIdCompat.WireIdOf(this.root).ToString()),
                    "without this the caller can only re-nest by parsing paths");
                Assert.That(node["scene"], Is.Not.Null,
                    "a root moving between two open scenes changes nothing else about it");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(added);
            }
        }

        [Test]
        public void TheTreeLeavesOutWhatItsShapeAlreadySays()
        {
            // Carried on every node these were 40% of the reply, and the tree states both: the
            // scene by its grouping, the parent by the nesting.
            var node = FindByName(SceneObjects(Full()), RootName);

            Assert.That(node["parentInstanceId"], Is.Null);
            Assert.That(node["scene"], Is.Null);
        }

        [Test]
        public void ASnapshotIsNotComparedAgainstADifferentWalk()
        {
            // A shallower walk leaves out everything below its depth. Diffed against a deeper
            // snapshot it would report all of that as removed, naming objects that are still in
            // the scene — a confident wrong answer rather than an error.
            var deep = SnapshotOf(SceneHierarchy.Browse(
                ToolArgs.Of(("name", NamePrefix), ("maxDepth", 12))));

            var result = SceneHierarchy.Browse(ToolArgs.Of(
                ("name", NamePrefix), ("maxDepth", 1), ("since", deep)));

            Assert.That(result["error"], Is.Not.Null);
            Assert.That(result["removed"], Is.Null);
        }

        [Test]
        public void ASnapshotIsComparedAgainstTheSameWalkWithoutComplaint()
        {
            var snapshot = SnapshotOf(SceneHierarchy.Browse(
                ToolArgs.Of(("name", NamePrefix), ("maxDepth", 12))));

            var result = SceneHierarchy.Browse(ToolArgs.Of(
                ("name", NamePrefix), ("maxDepth", 12), ("since", snapshot)));

            Assert.That(result["error"], Is.Null);
            Assert.That(Count(result, "removed"), Is.Zero);
        }

        [Test]
        public void AnExpiredSnapshotIsAnsweredWithTheTreeRatherThanARoundTrip()
        {
            // A snapshot goes on every domain reload, and the walk that answers the diff has
            // already run by the time the miss is known. An error here costs a round trip for a
            // reply the caller has to be given anyway.
            var result = SceneHierarchy.Browse(ToolArgs.Of(
                ("name", NamePrefix), ("since", "snap-does-not-exist")));

            Assert.That(result["error"], Is.Null);
            Assert.That(result["scenes"], Is.Not.Null, "the tree stands in for the diff");
            Assert.That(result["snapshotId"], Is.Not.Null, "and it can be diffed from next time");
            Assert.That(result["sinceExpired"]?.ToString(), Is.EqualTo("snap-does-not-exist"),
                "the reply has to say why it is a tree");
            Assert.That(result["added"], Is.Null, "an empty diff would read as a still scene");
        }

        [Test]
        public void APagedDiffIsRefusedRatherThanAnsweredWrongly()
        {
            // The snapshot would hold one window and the next call another, so an object pushed
            // out of the window by an earlier insertion would read as removed while it is still
            // in the scene.
            var snapshot = SnapshotOf(Full());
            var limited = SceneHierarchy.Browse(ToolArgs.Of(("since", snapshot), ("limit", 10)));
            var skipped = SceneHierarchy.Browse(ToolArgs.Of(("since", snapshot), ("offset", 5)));

            Assert.That(limited["error"], Is.Not.Null);
            Assert.That(skipped["error"], Is.Not.Null);
            Assert.That(limited["added"], Is.Null);
        }

        [Test]
        public void AFieldsAllowlistCannotDropTheKeyTheDiffIsBuiltOn()
        {
            // Without instanceId every node fails to match itself, and the call reports a still
            // scene however much moved.
            var snapshot = SnapshotOf(SceneHierarchy.Browse(
                ToolArgs.Of(("name", NamePrefix), ("fields", "name"))));

            var added = new GameObject(NamePrefix + "Added");
            try
            {
                var result = SceneHierarchy.Browse(ToolArgs.Of(
                    ("name", NamePrefix), ("since", snapshot), ("fields", "name")));

                Assert.That(Count(result, "added"), Is.EqualTo(1), "the new object has to be seen");
                Assert.That(((JArray)result["added"])[0]["instanceId"], Is.Not.Null,
                    "instanceId is kept even though the allowlist did not name it");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(added);
            }
        }

        private static int ActiveSceneIndex()
        {
            var active = SceneManager.GetActiveScene();

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i) == active)
                {
                    return i;
                }
            }

            return 0;
        }

        [Test]
        public void AnObjectWhoseScriptsAllResolveSaysSoByLeavingTheCountOut()
        {
            // A missing script cannot be synthesised in a test: it needs a serialised reference to
            // a type the domain no longer has. What can be pinned is the reading of an absent key,
            // which is what the description promises: no `missingScripts` means none are missing.
            var result = SceneHierarchy.Browse(ToolArgs.Of(("name", NamePrefix)));
            var nodes = SceneObjects(result);
            var node = FindByName(nodes, RootName);

            Assert.That(node["missingScripts"], Is.Null, "a zero count is carried by the key's absence");
        }

        [Test]
        public void KeysSittingAtTheirDefaultAreLeftOutOfEveryNode()
        {
            // These four are the bulk of a large response and say nothing. The contract is that a
            // caller reads their absence as the default, so a node built from an untouched
            // GameObject has to carry none of them.
            var result = SceneHierarchy.Browse(ToolArgs.Of(("name", NamePrefix)));
            var node = FindByName(SceneObjects(result), RootName);

            Assert.That(node["active"], Is.Null, "active true is the default");
            Assert.That(node["tag"], Is.Null, "Untagged is the default");
            Assert.That(node["layer"], Is.Null, "Default is the default layer");
            Assert.That(node["name"], Is.Not.Null, "name is never dropped");
            Assert.That(node["path"], Is.Not.Null, "path is never dropped");
            Assert.That(node["instanceId"], Is.Not.Null, "instanceId is never dropped");
            Assert.That(node["id"], Is.Null, "id duplicated instanceId and is gone");
        }

        [Test]
        public void TheMissingScriptFilterMatchesNothingWhenEveryScriptResolves()
        {
            // The scene the fixture builds has no broken component, so the filter must come back
            // empty rather than falling through to every object, which is what a filter that is
            // read but never applied would do.
            var result = SceneHierarchy.Browse(ToolArgs.Of(
                ("name", NamePrefix),
                ("missingScripts", true)));

            Assert.That(result["total"].Value<int>(), Is.Zero);
            Assert.That(SceneObjects(result).Count, Is.Zero);
        }

        private static JArray SceneObjects(JObject result)
        {
            Assert.IsNull(result["error"], (string)result["error"]);

            var scenes = result["scenes"] as JArray;
            Assert.IsNotNull(scenes, "the response carries no scenes array");

            // A walk that matches nothing reports no scene at all, so an empty array here is an
            // answer rather than a missing scene. Only the fixture's own scene is ever open.
            Assert.LessOrEqual(scenes.Count, 1, "more scenes came back than the fixture opens");

            return scenes.Count == 0 ? new JArray() : (JArray)scenes[0]["gameObjects"];
        }

        private static JObject FindByName(JArray nodes, string name)
        {
            if (nodes == null)
            {
                return null;
            }

            foreach (var node in nodes)
            {
                if ((string)node["name"] == name)
                {
                    return (JObject)node;
                }
            }

            return null;
        }

        private static JArray ChildrenOf(JObject node)
        {
            return node?["children"] as JArray;
        }
    /// <summary>
        /// A filtered node says how many of its children the filter kept out.
        /// </summary>
        /// <remarks>
        /// Filtering by a parent's name answered with the parent and nothing under it, which
        /// reads as a leaf: a hands-on run took that for "the filter does not show children" and
        /// fetched the whole scene instead of the three objects it was after.
        /// </remarks>
        [Test]
        public void AMatchedParentSaysHowManyChildrenTheFilterLeftOut()
        {
            var parent = new GameObject("FilterParentProbe");

            try
            {
                for (var i = 0; i < 3; i++)
                {
                    new GameObject("FilterChildProbe" + i).transform.SetParent(parent.transform);
                }

                var reply = SceneHierarchy.Browse(ToolArgs.Of(("name", "FilterParentProbe")));
                var node = FindNode(reply, "FilterParentProbe");

                Assert.That(node, Is.Not.Null, "the filter has to match the parent itself");
                Assert.That(node["children"], Is.Null, "its children do not match, so they are not here");
                Assert.That(node["childrenNotShown"].Value<int>(), Is.EqualTo(3),
                    "and the reply has to say that there are three of them");
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        /// <summary>
        /// One branch, rooted where the caller asked, and nothing from the rest of the scene.
        /// </summary>
        /// <remarks>
        /// Without this the objects under a known object could only be reached by taking the
        /// whole scene and finding them in it, which on a real scene is hundreds of thousands of
        /// tokens for the sake of one subtree.
        /// </remarks>
        [Test]
        public void ABranchCanBeReadOnItsOwn()
        {
            var reply = SceneHierarchy.Browse(ToolArgs.Of(("objectPath", "/" + RootName)));
            var scenes = (JArray)reply["scenes"];
            var roots = (JArray)((JObject)scenes[0])["gameObjects"];

            Assert.That(scenes.Count, Is.EqualTo(1), "only the scene the object is in");
            Assert.That(roots.Count, Is.EqualTo(1), "the object asked for is the only root");
            Assert.That(((JObject)roots[0])["name"].Value<string>(), Is.EqualTo(RootName));

            // The branch and nothing else: the fixture's root, its two children and the one
            // grandchild, with none of whatever else the scene holds.
            Assert.That(reply["total"].Value<int>(), Is.EqualTo(4));
            Assert.That(reply.ToString(), Does.Contain(GrandchildName), "and what is under it");
        }

        /// <summary>A snapshot of one branch is not a snapshot of the scene.</summary>
        [Test]
        public void ABranchSnapshotIsNotComparedAgainstTheWholeScene()
        {
            var whole = SceneHierarchy.Browse(ToolArgs.Of());
            var snapshot = whole["snapshotId"].Value<string>();

            var refused = SceneHierarchy.Browse(ToolArgs.Of(
                ("objectPath", "/" + RootName), ("since", snapshot)));

            Assert.That(refused["error"]?.ToString(), Does.Contain("different arguments"));
        }

        private static JObject FindNode(JObject reply, string name)
        {
            foreach (var scene in (JArray)reply["scenes"])
            {
                foreach (JObject node in (JArray)scene["gameObjects"])
                {
                    if (node["name"].Value<string>() == name)
                    {
                        return node;
                    }
                }
            }

            return null;
        }
    }
}
