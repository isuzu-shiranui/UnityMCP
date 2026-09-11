using System.Collections.Generic;

using NUnit.Framework;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// What an A/B capture refuses before it renders anything, and that hiding the objects is
    /// reversed exactly rather than by switching everything on.
    /// </summary>
    [TestFixture]
    internal sealed class RenderCaptureAbTests
    {
        private readonly List<GameObject> made = new();

        private GameObject Made(string name, bool withRenderer)
        {
            var made = new GameObject(name);

            if (withRenderer)
            {
                made.AddComponent<MeshFilter>();
                made.AddComponent<MeshRenderer>();
            }

            this.made.Add(made);
            return made;
        }

        [TearDown]
        public void Cleanup()
        {
            foreach (var made in this.made)
            {
                if (made != null)
                {
                    Object.DestroyImmediate(made);
                }
            }

            this.made.Clear();
        }

        [Test]
        public void HidingNothingIsRefused()
        {
            var thrown = Assert.Throws<McpToolException>(
                () => RenderTools.CaptureAb(hide: new string[0], savePathPrefix: "Temp/ab"));

            Assert.That(thrown.Code, Is.EqualTo("invalid_params"));
            Assert.That(thrown.Message, Does.Contain("capture_screenshot"));
        }

        [Test]
        public void MoreObjectsThanOneCallWillHideIsRefused()
        {
            var many = new string[51];

            for (var i = 0; i < many.Length; i++)
            {
                many[i] = "/Thing" + i;
            }

            var thrown = Assert.Throws<McpToolException>(
                () => RenderTools.CaptureAb(hide: many, savePathPrefix: "Temp/ab"));

            Assert.That(thrown.Code, Is.EqualTo("invalid_params"));
            Assert.That(thrown.Message, Does.Contain("50"));
        }

        [Test]
        public void WithNowhereToWriteThePicturesTheCallIsRefused()
        {
            var thrown = Assert.Throws<McpToolException>(
                () => RenderTools.CaptureAb(hide: new[] { "/Thing" }, savePathPrefix: "  "));

            Assert.That(thrown.Code, Is.EqualTo("invalid_params"));
            Assert.That(thrown.Message, Does.Contain("save_path_prefix"));
        }

        [Test]
        public void AnImpossibleSettleWindowIsRefused()
        {
            foreach (var frames in new[] { 0, 61 })
            {
                var thrown = Assert.Throws<McpToolException>(
                    () => RenderTools.CaptureAb(
                        hide: new[] { "/Thing" }, savePathPrefix: "Temp/ab", settleFrames: frames));

                Assert.That(thrown.Code, Is.EqualTo("invalid_params"));
                Assert.That(thrown.Message, Does.Contain("settle_frames"));
            }
        }

        [Test]
        public void AnObjectThatDrawsNothingIsRefusedRatherThanCaptured()
        {
            var empty = this.Made("McpAbNoRenderer", withRenderer: false);

            var thrown = Assert.Throws<McpToolException>(
                () => RenderTools.CaptureAb(hide: new[] { "/" + empty.name }, savePathPrefix: "Temp/ab"));

            Assert.That(thrown.Code, Is.EqualTo("invalid_params"));
            Assert.That(thrown.Message, Does.Contain("scene_browse_hierarchy"));
        }

        [Test]
        public void PuttingBackRestoresWhatEachRendererWasRatherThanSwitchingItOn()
        {
            var on = this.Made("McpAbOn", withRenderer: true).GetComponent<Renderer>();
            var off = this.Made("McpAbOff", withRenderer: true).GetComponent<Renderer>();
            off.enabled = false;

            var restore = new List<KeyValuePair<Renderer, bool>>();
            RenderTools.Hide(new[] { on, off }, restore);

            Assert.That(on.enabled, Is.False);
            Assert.That(off.enabled, Is.False);

            RenderTools.Show(restore);

            Assert.That(on.enabled, Is.True);
            Assert.That(off.enabled, Is.False, "A renderer the scene had switched off stays off.");
        }

        [Test]
        public void PuttingBackSurvivesAnObjectDestroyedWhileTheCallWasInFlight()
        {
            var doomed = this.Made("McpAbDoomed", withRenderer: true).GetComponent<Renderer>();

            var restore = new List<KeyValuePair<Renderer, bool>>();
            RenderTools.Hide(new[] { doomed }, restore);

            Object.DestroyImmediate(doomed.gameObject);

            Assert.DoesNotThrow(() => RenderTools.Show(restore));
        }
    }
}
