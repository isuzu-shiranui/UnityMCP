using System.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityEditor;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// One tool call must be one undo step.
    /// </summary>
    /// <remarks>
    /// This exists because the opposite shipped. <see cref="ToolInvoker"/> captured
    /// <c>Undo.GetCurrentGroup()</c> without incrementing first, so every call in a session
    /// shared a group index and each collapse merged everything recorded since — a single
    /// Ctrl+Z reversed the whole conversation. Every existing invoker test passed throughout,
    /// because a single call cannot observe it. The distinguishing case is two calls, so that
    /// is what is asserted here.
    /// </remarks>
    [TestFixture]
    internal sealed class UndoGroupingTests
    {
        private static class Tools
        {
            [McpTool("t_undo_move", "Moves an object, on the undo stack.",
                     Idempotency = McpIdempotency.Unsafe, UndoGroup = "Test Move")]
            public static float Move(
                [McpArg("name", "Object name")] string name,
                [McpArg("x", "New x")] float x)
            {
                var go = GameObject.Find(name);
                Undo.RecordObject(go.transform, "Test Move");
                go.transform.localPosition = new Vector3(x, 0f, 0f);
                return go.transform.localPosition.x;
            }

            [McpTool("t_undo_none", "Moves an object without declaring an undo group.",
                     Idempotency = McpIdempotency.Unsafe)]
            public static float MoveUngrouped(
                [McpArg("name", "Object name")] string name,
                [McpArg("x", "New x")] float x)
            {
                var go = GameObject.Find(name);
                go.transform.localPosition = new Vector3(x, 0f, 0f);
                return go.transform.localPosition.x;
            }
        }

        private ToolCatalog catalog;

        private GameObject target;

        [SetUp]
        public void SetUp()
        {
            this.catalog = ToolCatalog.BuildFromTypes(new[] { typeof(Tools) });

            Undo.ClearAll();
            this.target = new GameObject("UndoGroupingTarget");
            Undo.RegisterCreatedObjectUndo(this.target, "create target");
            Undo.IncrementCurrentGroup();
        }

        [TearDown]
        public void TearDown()
        {
            if (this.target != null)
            {
                Object.DestroyImmediate(this.target);
            }

            Undo.ClearAll();
        }

        private void Move(float x)
        {
            var descriptor = this.catalog.Tools.Single(t => t.Name == "t_undo_move");
            ToolInvoker.Invoke(descriptor, new JObject { ["name"] = "UndoGroupingTarget", ["x"] = x });
        }

        private float X => this.target.transform.localPosition.x;

        [Test]
        public void ThreeCallsTakeThreeUndoSteps()
        {
            this.Move(1f);
            this.Move(2f);
            this.Move(3f);
            Assert.That(this.X, Is.EqualTo(3f));

            Undo.PerformUndo();
            Assert.That(this.X, Is.EqualTo(2f), "one undo reversed more than the last call");

            Undo.PerformUndo();
            Assert.That(this.X, Is.EqualTo(1f));

            Undo.PerformUndo();
            Assert.That(this.X, Is.EqualTo(0f), "the earliest call did not come back");
        }

        [Test]
        public void RedoReappliesOneCallAtATime()
        {
            this.Move(1f);
            this.Move(2f);

            Undo.PerformUndo();
            Assert.That(this.X, Is.EqualTo(1f));

            Undo.PerformRedo();
            Assert.That(this.X, Is.EqualTo(2f));
        }

        [Test]
        public void EachCallGetsItsOwnGroupIndex()
        {
            this.Move(1f);
            var first = Undo.GetCurrentGroup();

            this.Move(2f);
            var second = Undo.GetCurrentGroup();

            Assert.That(second, Is.GreaterThan(first),
                "consecutive calls shared a group, which is what made one undo reverse both");
        }

        [Test]
        public void TheGroupIsNamedAfterTheTool()
        {
            this.Move(1f);

            Assert.That(Undo.GetCurrentGroupName(), Is.EqualTo("Test Move"));
        }

        [Test]
        public void AToolWithoutAnUndoGroupDoesNotTouchTheStack()
        {
            this.Move(1f);

            var before = Undo.GetCurrentGroup();
            var descriptor = this.catalog.Tools.Single(t => t.Name == "t_undo_none");
            ToolInvoker.Invoke(descriptor, new JObject { ["name"] = "UndoGroupingTarget", ["x"] = 9f });

            Assert.That(Undo.GetCurrentGroup(), Is.EqualTo(before));
            Assert.That(this.X, Is.EqualTo(9f));

            // The ungrouped write is not undoable, so undoing takes the value back past it to
            // where the last grouped call left it.
            Undo.PerformUndo();
            Assert.That(this.X, Is.EqualTo(0f));
        }
    }
}
