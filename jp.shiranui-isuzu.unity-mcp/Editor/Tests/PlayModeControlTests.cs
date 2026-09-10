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

        /// <summary>
        /// Stepping takes a count, and refuses one it cannot honour.
        /// </summary>
        /// <remarks>
        /// A frame at a time is a call per frame: watching a slow animation finish took 1,255 of
        /// them in one hands-on run. Measured at about 2.5 ms a frame, so the cap is a couple of
        /// seconds of the main thread rather than a limit on what can be watched.
        /// </remarks>
        [Test]
        public void SteppingRefusesACountItCannotHonour()
        {
            foreach (var count in new[] { 0, -1, 5000 })
            {
                var thrown = Assert.Throws<McpToolException>(
                    () => PlayModeControl.Control(ToolArgs.Of(("action", "step"), ("count", count))));

                Assert.That(thrown.Message, Does.Contain("1000"));
            }
        }

        /// <summary>
        /// Anything measured in seconds needed a second call through reflect_read to ask Time.time.
        /// </summary>
        [Test]
        public void TheStatusSaysHowFarIntoTheRunItIs()
        {
            var status = PlayModeControl.Control(ToolArgs.Of(("action", "status")));

            Assert.That(status["time"], Is.Not.Null);
            Assert.That(status["time"].Value<double>(), Is.GreaterThanOrEqualTo(0d));
        }

        /// <summary>
        /// Playing and running are not the same thing, and the status has to separate them.
        /// </summary>
        /// <remarks>
        /// An Editor without focus stops ticking, so isPlaying stays true while nothing advances.
        /// Two readings of frameCount are what tell the two states apart, so it has to be there
        /// alongside isPlaying rather than only when something is running.
        /// </remarks>
        [Test]
        public void TheStatusReportsTheFrameItIsOn()
        {
            var status = PlayModeControl.Control(ToolArgs.Of(("action", "status")));

            Assert.That(status["frameCount"], Is.Not.Null);
            Assert.That(status["frameCount"].Value<int>(), Is.GreaterThanOrEqualTo(0));
            Assert.That(status["isPlaying"], Is.Not.Null);
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

        /// <summary>
        /// A refusal that comes while the requested play mode is still on its way says which.
        /// </summary>
        /// <remarks>
        /// Entering play mode is deferred by a frame, so a caller acting on the reply to
        /// play_mode_play calls pause before the Editor is there. "Cannot pause outside of play
        /// mode" reads as though the request had never happened, and a hands-on run stalled on
        /// exactly that.
        /// </remarks>
        [Test]
        public void RefusingWhilePlayModeIsStillOnItsWaySaysThatRatherThanNothingWasAsked()
        {
            Assert.That(EditorApplication.isPlaying, Is.False, "this fixture only covers edit mode");

            PlayModeControl.Control(new JObject { ["action"] = "play" });

            foreach (var action in new[] { "pause", "unpause", "step" })
            {
                var reply = PlayModeControl.Control(ToolArgs.Of(("action", action), ("count", 1)));
                var error = reply["error"]?.Value<string>();

                Assert.That(error, Is.Not.Null, $"{action} cannot happen yet");
                Assert.That(error, Does.Contain("next"),
                    $"{action} has to say play mode is on its way: {error}");
                Assert.That(error, Does.Not.Contain("outside of play mode"),
                    $"{action} must not tell the caller it never asked: {error}");
            }
        }

        [Test]
        public void RefusingWithNothingPendingSaysHowToStartPlayMode()
        {
            // Another fixture's deferred work would otherwise read as a play mode on its way.
            FrameSequencer.CancelAll("test");

            var reply = PlayModeControl.Control(new JObject { ["action"] = "pause" });

            Assert.That(reply["error"]?.Value<string>(), Does.Contain("play_mode_play"));
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
        /// <summary>
        /// A step and the look that always follows it, in one call.
        /// </summary>
        /// <remarks>
        /// Watching something over time is a step and a look, over and over. One scenario made
        /// twenty-one steps and forty-six reads behind them, each read a round trip spent asking
        /// where the thing had got to. Outside play mode the step still refuses, and the paths
        /// are not read: the reply has to stay one answer, not a refusal with data attached.
        /// </remarks>
        [Test]
        public void PathsAreNotReadWhenTheStepItselfIsRefused()
        {
            var reply = PlayModeControl.Control(ToolArgs.Of(
                ("action", "step"),
                ("count", 1),
                ("paths", new JArray("UnityEngine.Time/frameCount"))));

            Assert.That(reply["error"], Is.Not.Null, "nothing is playing, so the step cannot happen");
            Assert.That(reply["reads"], Is.Null, "and a refusal must not carry readings with it");
        }

        /// <summary>
        /// What an Animator is doing at the frame reached, in the same reply as the step.
        /// </summary>
        /// <remarks>
        /// A state's progress is not a property, so 'paths' cannot reach it and the look after
        /// each step was a whole animator_inspect: twenty-one of them behind twenty-one steps in
        /// one run, each carrying the controller asset again for the few live numbers at the end.
        /// Outside play mode there is nothing running to report, and the refusal has to stay one
        /// answer rather than a refusal with data attached.
        /// </remarks>
        [Test]
        public void AnimatorsAreNotReportedWhenTheStepItselfIsRefused()
        {
            var reply = PlayModeControl.Control(ToolArgs.Of(
                ("action", "step"),
                ("count", 1),
                ("animators", new JArray("/Turnstile"))));

            Assert.That(reply["error"], Is.Not.Null, "nothing is playing, so the step cannot happen");
            Assert.That(reply["animators"], Is.Null);
        }

    }
}
