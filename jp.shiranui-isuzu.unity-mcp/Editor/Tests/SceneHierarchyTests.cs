using NUnit.Framework;
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
        private const string RootName = "SHTestRoot";
        private const string ChildAName = "SHTestChildA";
        private const string ChildBName = "SHTestChildB";
        private const string GrandchildName = "SHTestGrandchild";

        private GameObject root;

        [SetUp]
        public void SetUp()
        {
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
        public void EveryObjectReportsHowManyOfItsComponentsLostTheirScript()
        {
            // A missing script cannot be synthesised in a test: it needs a serialised reference to
            // a type the domain no longer has. What can be pinned is that the count is reported and
            // reads zero for objects whose components all resolve, so a caller can trust the field
            // rather than inferring absence from a name it has to match.
            var result = SceneHierarchy.Browse(ToolArgs.Of(("name", NamePrefix)));
            var nodes = SceneObjects(result);
            var node = FindByName(nodes, RootName);

            Assert.That(node["missingScripts"], Is.Not.Null, "the field has to be present to be trusted");
            Assert.That(node["missingScripts"].Value<int>(), Is.Zero);
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
    }
}
