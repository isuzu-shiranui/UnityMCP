using System.Linq;

using NUnit.Framework;

using UnityEngine;
using UnityEngine.Rendering;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Handlers;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// The parts of an intermediate-target capture that need no render: which names are accepted,
    /// how a depth sample becomes metres, and how a float readback becomes a readable grey.
    /// </summary>
    [TestFixture]
    internal sealed class CameraBufferCaptureTests
    {
        private const float Near = 0.3f;
        private const float Far = 1000f;

        [Test]
        public void EveryBufferNameResolvesWhateverItsCase()
        {
            foreach (var kind in CameraBufferCapture.Kinds)
            {
                Assert.That(CameraBufferCapture.Named(kind.Name.ToUpperInvariant()).Global, Is.EqualTo(kind.Global));
            }
        }

        [Test]
        public void AnUnknownBufferIsRefusedWithTheOnesThatExist()
        {
            var thrown = Assert.Throws<McpToolException>(() => CameraBufferCapture.Named("colour"));

            Assert.That(thrown.Code, Is.EqualTo("invalid_params"));

            foreach (var kind in CameraBufferCapture.Kinds)
            {
                Assert.That(thrown.Message, Does.Contain(kind.Name));
            }

            Assert.That(thrown.Message, Does.Contain("capture_screenshot"));
        }

        [Test]
        public void EveryKindNamesTheSettingThatProducesIt()
        {
            foreach (var kind in CameraBufferCapture.Kinds)
            {
                Assert.That(kind.Setting, Is.Not.Null.And.Not.Empty, kind.Name);
                Assert.That(kind.Global, Does.StartWith("_"), kind.Name);
            }

            Assert.That(CameraBufferCapture.Kinds.Count(k => k.IsDepth), Is.EqualTo(1));
        }

        [Test]
        public void ReversedDepthRunsFromTheNearPlaneAtOneToTheFarPlaneAtZero()
        {
            Assert.That(CameraBufferCapture.Distance(1f, Near, Far, true, false), Is.EqualTo(Near).Within(0.0001f));
            Assert.That(CameraBufferCapture.Distance(0f, Near, Far, true, false), Is.EqualTo(Far).Within(0.01f));
        }

        [Test]
        public void PlainDepthRunsTheOtherWay()
        {
            Assert.That(CameraBufferCapture.Distance(0f, Near, Far, false, false), Is.EqualTo(Near).Within(0.0001f));
            Assert.That(CameraBufferCapture.Distance(1f, Near, Far, false, false), Is.EqualTo(Far).Within(0.01f));
        }

        [Test]
        public void DepthIsHyperbolicSoHalfwayThroughTheBufferIsNotHalfwayThroughTheScene()
        {
            // Reading the buffer as if it were linear is the mistake this arithmetic exists to
            // stop: the midpoint of a reversed buffer sits at twice the near plane, not at 500 m.
            var middle = CameraBufferCapture.Distance(0.5f, Near, Far, true, false);

            Assert.That(middle, Is.EqualTo(2f * Near).Within(0.01f));
        }

        [Test]
        public void AnOrthographicCameraWritesDepthLinearly()
        {
            Assert.That(CameraBufferCapture.Distance(0.5f, Near, Far, true, true), Is.EqualTo(500.15f).Within(0.01f));
            Assert.That(CameraBufferCapture.Distance(0.5f, Near, Far, false, true), Is.EqualTo(500.15f).Within(0.01f));
        }

        [Test]
        public void TheFarPlaneIsBlackAndTheNearestSurfaceIsWhite()
        {
            var camera = new GameObject("buffer-test").AddComponent<Camera>();

            try
            {
                camera.nearClipPlane = Near;
                camera.farClipPlane = Far;

                var reversed = SystemInfo.usesReversedZBuffer;
                var depth = new Texture2D(2, 2, TextureFormat.RFloat, false);

                // One sample at the near plane, one halfway, and two at the far plane.
                depth.SetPixels(new[]
                {
                    new Color(reversed ? 1f : 0f, 0, 0, 1),
                    new Color(0.5f, 0, 0, 1),
                    new Color(reversed ? 0f : 1f, 0, 0, 1),
                    new Color(reversed ? 0f : 1f, 0, 0, 1),
                });
                depth.Apply();

                var report = CameraBufferCapture.Shade(depth, camera, out var grey);

                Assert.That((int)report["backgroundPixels"], Is.EqualTo(2));
                Assert.That((int)report["measuredPixels"], Is.EqualTo(2));
                Assert.That((double)report["nearestMetres"], Is.EqualTo(Near).Within(0.001));

                var shaded = grey.GetPixels32();

                Assert.That(shaded[0].r, Is.EqualTo(255), "The nearest surface.");
                Assert.That(shaded[2].r, Is.Zero, "The far plane.");
                Assert.That(shaded[3].r, Is.Zero, "The far plane.");
                Assert.That(shaded[1].r, Is.EqualTo(1), "The farthest measured surface.");

                UnityEngine.Object.DestroyImmediate(grey);
                UnityEngine.Object.DestroyImmediate(depth);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(camera.gameObject);
            }
        }

        [Test]
        public void ADepthBufferOfNothingButSkyReportsNoDistances()
        {
            var camera = new GameObject("buffer-test-empty").AddComponent<Camera>();

            try
            {
                camera.nearClipPlane = Near;
                camera.farClipPlane = Far;

                var far = SystemInfo.usesReversedZBuffer ? 0f : 1f;
                var depth = new Texture2D(1, 1, TextureFormat.RFloat, false);
                depth.SetPixels(new[] { new Color(far, 0, 0, 1) });
                depth.Apply();

                var report = CameraBufferCapture.Shade(depth, camera, out var grey);

                Assert.That((int)report["measuredPixels"], Is.Zero);
                Assert.That(report["nearestMetres"].Type, Is.EqualTo(Newtonsoft.Json.Linq.JTokenType.Null));

                UnityEngine.Object.DestroyImmediate(grey);
                UnityEngine.Object.DestroyImmediate(depth);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(camera.gameObject);
            }
        }

        [Test]
        public void TheBuiltInPipelineIsRefusedAndAScriptableOneIsNot()
        {
            if (GraphicsSettings.currentRenderPipeline == null)
            {
                var thrown = Assert.Throws<McpToolException>(CameraBufferCapture.RequireSupport);

                Assert.That(thrown.Code, Is.EqualTo("not_supported"));
                Assert.That(thrown.Message, Does.Contain("render_pipeline_info"));
                return;
            }

            Assert.DoesNotThrow(CameraBufferCapture.RequireSupport);
        }
    }
}
