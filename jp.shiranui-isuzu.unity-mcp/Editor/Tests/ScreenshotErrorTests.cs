using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Handlers;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// A capture refusal has to reach the caller as its own code. Reported as a generic failure it
    /// reads as a fault in the Editor, and a client that retries safe calls repeats it.
    /// </summary>
    [TestFixture]
    internal sealed class ScreenshotErrorTests
    {
        [Test]
        public void ACaptureFailureIsAToolExceptionSoItsCodeReachesTheCaller()
        {
            var failure = new McpScreenshotException("window_occluded", "Something is in front.", 409);

            Assert.That(failure, Is.InstanceOf<McpToolException>(),
                "ToolInvoker only preserves the code and status of an McpToolException; anything else becomes tool_failed with a 500.");
            Assert.That(failure.Code, Is.EqualTo("window_occluded"));
            Assert.That(failure.HttpStatus, Is.EqualTo(409));
        }

        [Test]
        public void ARefusalIsNotReportedAsAServerFault()
        {
            var failure = new McpScreenshotException("window_minimized", "It is minimized.", 400);

            Assert.That(failure.HttpStatus, Is.LessThan(500),
                "a condition the caller can correct must not be reported as a server fault");
        }

        /// <summary>
        /// A camera aimed away from the subject is refused before the picture is encoded.
        /// </summary>
        /// <remarks>
        /// The refusal is worth a few hundred bytes and the picture tens of thousands, and a
        /// valid image of the wrong place is indistinguishable from a change that did nothing:
        /// one run compared two captures of a set a thousand units away and concluded from
        /// identical pixels that enabling a light had no effect.
        /// </remarks>
        [Test]
        public void AskingForAnObjectTheCameraCannotSeeIsRefusedBeforeAnythingIsRendered()
        {
            var eye = new GameObject("FrameTestCamera");
            var subject = new GameObject("FrameTestSubject");

            try
            {
                eye.AddComponent<Camera>();
                eye.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

                // Behind a camera that looks down +z.
                subject.transform.position = new Vector3(0f, 0f, -500f);

                var error = Assert.Throws<McpScreenshotException>(() => ScreenshotCapture.Capture(
                    new JObject
                    {
                        ["view"] = "game",
                        ["camera"] = ObjectResolve.PathOf(eye),
                        ["focus"] = ObjectResolve.PathOf(subject),
                    }));

                Assert.That(error.Code, Is.EqualTo("not_in_frame"));
                Assert.That(error.Message, Does.Contain("FrameTestSubject"));
                Assert.That(error.HttpStatus, Is.LessThan(500));
            }
            finally
            {
                Object.DestroyImmediate(subject);
                Object.DestroyImmediate(eye);
            }
        }

        /// <summary>
        /// What is in frame is decided by what would be drawn, not by where the parent sits.
        /// </summary>
        /// <remarks>
        /// A root at the origin holding content a thousand units away spans both, so a box seeded
        /// with the parent's own position passes the frustum test on the empty half and reports a
        /// centre that is in neither place.
        /// </remarks>
        [Test]
        public void AnEmptyParentIsJudgedByWhereItsRenderersAre()
        {
            var eye = new GameObject("FrameTestCamera");
            var root = new GameObject("FrameTestRoot");
            GameObject far = null;

            try
            {
                eye.AddComponent<Camera>();
                eye.transform.SetPositionAndRotation(new Vector3(0f, 0f, -10f), Quaternion.identity);

                root.transform.position = Vector3.zero;

                far = GameObject.CreatePrimitive(PrimitiveType.Cube);
                far.name = "FrameTestFar";
                far.transform.SetParent(root.transform);
                far.transform.position = new Vector3(1000f, 0f, 0f);

                var error = Assert.Throws<McpScreenshotException>(() => ScreenshotCapture.Capture(
                    new JObject
                    {
                        ["view"] = "game",
                        ["camera"] = ObjectResolve.PathOf(eye),
                        ["focus"] = ObjectResolve.PathOf(root),
                    }));

                Assert.That(error.Code, Is.EqualTo("not_in_frame"));
            }
            finally
            {
                if (far != null)
                {
                    Object.DestroyImmediate(far);
                }

                Object.DestroyImmediate(root);
                Object.DestroyImmediate(eye);
            }
        }
    }
}
