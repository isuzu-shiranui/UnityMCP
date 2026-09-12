using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityEngine;
using UnityEngine.UI;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Ugui.Tests
{
    internal sealed class UiHitTestTests
    {
        private readonly List<GameObject> created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in created)
            {
                Object.DestroyImmediate(go);
            }

            created.Clear();
        }

        // AddComponent brings in whatever a component requires; the GameObject constructor's type
        // list does not, and an Image without its CanvasRenderer behaves nothing like a real one.
        private Image NewImage(string name)
        {
            var go = new GameObject(name);
            created.Add(go);
            go.AddComponent<RectTransform>();
            go.AddComponent<Canvas>();
            return go.AddComponent<Image>();
        }

        [Test]
        public void AGraphicThatCanBeHitReportsNoReason()
        {
            var image = NewImage("Plain");
            image.canvas.gameObject.AddComponent<GraphicRaycaster>();

            Assert.That(UiHitTest.Blockers(image), Is.Empty);
        }

        [Test]
        public void RaycastTargetOffIsReportedByName()
        {
            var image = NewImage("Decoration");
            image.canvas.gameObject.AddComponent<GraphicRaycaster>();
            image.raycastTarget = false;

            // The setting is called Raycast Target in the Inspector, and a reader searching for
            // what to change needs the label rather than the field name.
            Assert.That(UiHitTest.Blockers(image), Has.Some.Contains("Raycast Target is off"));
        }

        [Test]
        public void ACanvasWithNoRaycasterIsReported()
        {
            var image = NewImage("Orphan");

            Assert.That(UiHitTest.Blockers(image), Has.Some.Contains("no GraphicRaycaster"));
        }

        [Test]
        public void ACanvasGroupThatBlocksNothingIsReportedWithTheObjectCarryingIt()
        {
            var image = NewImage("Faded");
            image.canvas.gameObject.AddComponent<GraphicRaycaster>();
            var group = image.gameObject.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;

            // Naming the object matters: the group is usually several levels above the element
            // the caller is asking about.
            Assert.That(
                UiHitTest.Blockers(image),
                Has.Some.Contains("CanvasGroup on 'Faded'").And.Some.Contains("Blocks Raycasts off"));
        }

        [Test]
        public void AnInactiveObjectIsReported()
        {
            var image = NewImage("Hidden");
            image.canvas.gameObject.AddComponent<GraphicRaycaster>();
            image.gameObject.SetActive(false);

            Assert.That(UiHitTest.Blockers(image), Has.Some.Contains("not active"));
        }

        [Test]
        public void AGroupThatIgnoresItsParentsStopsTheWalk()
        {
            var parent = new GameObject("Parent");
            created.Add(parent);
            parent.AddComponent<RectTransform>();
            parent.AddComponent<Canvas>();
            parent.AddComponent<GraphicRaycaster>();
            var parentGroup = parent.AddComponent<CanvasGroup>();
            parentGroup.blocksRaycasts = false;

            var child = new GameObject("Child");
            child.transform.SetParent(parent.transform, false);
            child.AddComponent<RectTransform>();
            child.AddComponent<Image>();
            var childGroup = child.AddComponent<CanvasGroup>();
            childGroup.ignoreParentGroups = true;

            // The runtime stops at the first group that ignores its parents, so reporting the
            // parent's setting here would send the caller to change something with no effect.
            Assert.That(
                UiHitTest.Blockers(child.GetComponent<Image>()).Where(r => r.Contains("Parent")),
                Is.Empty);
        }

        [Test]
        public void AnObjectWithNoRectTransformIsRefusedRatherThanGivenAPointOfZero()
        {
            var go = new GameObject("Empty");
            created.Add(go);

            var thrown = Assert.Throws<McpToolException>(() => UiHitTest.CentreOf(go));
            Assert.That(thrown.Code, Is.EqualTo("invalid_params"));
            Assert.That(thrown.Message, Does.Contain("RectTransform"));
        }

        [Test]
        public void WithNoEventSystemTheVerdictSaysSoInsteadOfReportingAnEmptyScene()
        {
            // Edit mode never has an active EventSystem, so this is also the shape a caller sees
            // when they reach the handler outside play mode.
            var answer = UiHitTest.Run(new Vector2(10, 10), null, 20, true);

            // Value<T>() resolves to Linq's extension over JToken once System.Linq is in scope, so
            // every read here casts instead.
            Assert.That(answer["eventSystem"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That((string)answer["verdict"], Does.Contain("EventSystem"));
            Assert.That(((JArray)answer["hits"]).Count, Is.Zero);
        }

        [Test]
        public void OutsidePlayModeTheToolRefusesRatherThanReportingAnEmptyScreen()
        {
            // Every point reports nothing under it outside play mode, which reads exactly like a
            // correctly-answered question about a broken UI.
            var thrown = Assert.Throws<McpToolException>(() => UiTools.HitTest());
            Assert.That(thrown.Code, Is.EqualTo("conflict"));
            Assert.That(thrown.HttpStatus, Is.EqualTo(409));
            Assert.That(thrown.Message, Does.Contain("play_mode_play"));
        }

        [Test]
        public void APositionThatIsNotTwoNumbersIsRefused()
        {
            var thrown = Assert.Throws<McpToolException>(
                () => UiTools.Point(new double[] { 1, 2, 3 }, false, null));

            Assert.That(thrown.Code, Is.EqualTo("invalid_params"));
            Assert.That(thrown.Message, Does.Contain("two numbers"));
        }

        [Test]
        public void ANormalizedPositionIsScaledByTheScreen()
        {
            var point = UiTools.Point(new double[] { 0.5, 0.25 }, true, null);

            Assert.That(point.x, Is.EqualTo(Screen.width * 0.5f).Within(0.01f));
            Assert.That(point.y, Is.EqualTo(Screen.height * 0.25f).Within(0.01f));
        }

        [Test]
        public void APositionInPixelsIsUsedAsGiven()
        {
            var point = UiTools.Point(new double[] { 0.5, 0.25 }, false, null);

            Assert.That(point, Is.EqualTo(new Vector2(0.5f, 0.25f)));
        }

        [Test]
        public void AnElementThatCannotBeHitIsExplainedEvenWhenNothingElseWasHitEither()
        {
            var image = NewImage("Wanted");
            image.canvas.gameObject.AddComponent<GraphicRaycaster>();
            image.raycastTarget = false;

            // Turning Raycast Target off usually empties the hit list as well, and reporting only
            // "nothing is under this point" would hide the one fact the caller asked for.
            var verdict = UiHitTest.Verdict(
                new List<UnityEngine.EventSystems.RaycastResult>(), image.gameObject, null);

            Assert.That(verdict, Does.Contain("cannot be hit").And.Contain("Raycast Target is off"));
        }

        [Test]
        public void AnElementWithNoGraphicIsToldThatRatherThanBeingCalledOutOfFrame()
        {
            var go = new GameObject("NoGraphic");
            created.Add(go);
            go.AddComponent<RectTransform>();

            var verdict = UiHitTest.Verdict(
                new List<UnityEngine.EventSystems.RaycastResult>(), go, null);

            Assert.That(verdict, Does.Contain("no Graphic"));
        }

        [Test]
        public void ThePointAndScreenSizeComeBackSoTheCallerKnowsWhichSpaceTheyAreIn()
        {
            var answer = UiHitTest.Run(new Vector2(12, 34), null, 20, false);

            // A point means nothing without the size it was measured against: the Game view and
            // the target display are routinely different.
            Assert.That((float)answer["point"][0], Is.EqualTo(12f));
            Assert.That((float)answer["point"][1], Is.EqualTo(34f));
            Assert.That((int)answer["screen"]["width"], Is.EqualTo(Screen.width));
            Assert.That((int)answer["screen"]["height"], Is.EqualTo(Screen.height));
        }
    }
}
