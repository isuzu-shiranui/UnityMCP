using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityEditor;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Handlers;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Entering and leaving play mode is deferred so the response is written before the domain
    /// reload, and the Editor has to keep ticking until the deferred work runs.
    /// </summary>
    /// <remarks>
    /// <c>EditorApplication.delayCall</c> does not do that. An Editor without focus stops ticking
    /// once the request that woke it is answered, so the callback waits for a frame that never
    /// comes: the call reported that play mode would start and it never did, which is the state
    /// an agent drives the Editor in. The loop waker watches the dispatcher queue and the frame
    /// sequencer, so the work has to be in one of them.
    /// <para>
    /// The sequence is cancelled here rather than allowed to run. A test that entered play mode
    /// would take the rest of the EditMode run with it.
    /// </para>
    /// </remarks>
    [TestFixture]
    internal sealed class PlayModeControlTests
    {
        [TearDown]
        public void TearDown()
        {
            FrameSequencer.CancelAll("PlayModeControlTests finished");
        }

        [Test]
        public void AskingToPlayLeavesWorkTheLoopWakerCanSee()
        {
            Assert.That(EditorApplication.isPlaying, Is.False, "this fixture only covers edit mode");

            var before = FrameSequencer.ActiveCount;

            var reply = PlayModeControl.Control(new JObject { ["action"] = "play" });

            Assert.That(reply["deferred"]?.Value<bool>(), Is.True,
                "the transition is deferred so the response is written before the domain reload");

            Assert.That(FrameSequencer.ActiveCount, Is.GreaterThan(before),
                "the deferred work has to sit where the loop waker looks, or an Editor without "
                + "focus never reaches the frame that runs it");

            FrameSequencer.CancelAll("test");

            Assert.That(EditorApplication.isPlaying, Is.False,
                "cancelling has to leave the Editor where it was");
        }

        [Test]
        public void AskingToStopWhileNotPlayingSchedulesNothing()
        {
            var before = FrameSequencer.ActiveCount;

            var reply = PlayModeControl.Control(new JObject { ["action"] = "stop" });

            Assert.That(reply["message"]?.Value<string>(), Does.Contain("Not in play mode"));
            Assert.That(FrameSequencer.ActiveCount, Is.EqualTo(before),
                "there is nothing to defer when the Editor is already where it was asked to be");
        }
    }
}
