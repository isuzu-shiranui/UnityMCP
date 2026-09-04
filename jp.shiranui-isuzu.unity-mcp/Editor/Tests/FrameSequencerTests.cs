using System;
using System.Collections;
using System.Collections.Generic;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityEditor;

using UnityEngine.TestTools;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// <see cref="FrameSequencer"/> exists so a tool can span Editor frames without blocking the
    /// main thread. These cover the properties that makes true: one step per frame, the payload
    /// reaching the item, and every exit path settling the item rather than leaving a request
    /// waiting on a sequence that is gone.
    /// </summary>
    [TestFixture]
    internal sealed class FrameSequencerTests
    {
        /// <summary>How many frames a test waits before calling a sequence stuck.</summary>
        private const int FrameBudget = 60;

        [TearDown]
        public void TearDown()
        {
            // A sequence left behind by a failing test would keep advancing in this Editor and
            // show up as a failure in whatever runs next.
            FrameSequencer.CancelAll("Test teardown.");
        }

        [UnityTest]
        public IEnumerator EachStepCostsAFrameAndTheDonePayloadIsTheResult()
        {
            var ticks = 0;
            EditorApplication.CallbackFunction counter = () => ticks++;
            EditorApplication.update += counter;

            var ranOnTick = new List<int>();

            try
            {
                var item = FrameSequencer.Run(ThreeSteps(() => ticks, ranOnTick), "frames_three");

                Assert.That(item.IsCompleted, Is.False, "A sequence must not run inside the call that starts it.");
                Assert.That(FrameSequencer.ActiveCount, Is.EqualTo(1));

                for (var frame = 0; frame < FrameBudget && !item.IsCompleted; frame++)
                {
                    yield return null;
                }

                Assert.That(item.IsCompleted, Is.True, "The sequence never finished.");
                Assert.That(item.Error, Is.Null);
                Assert.That(item.Result["steps"].Value<int>(), Is.EqualTo(3));
                Assert.That(FrameSequencer.ActiveCount, Is.Zero);

                Assert.That(ranOnTick.Count, Is.EqualTo(3));
                Assert.That(ranOnTick, Is.Unique,
                    "Two steps sharing a frame means the Editor never got the tick between them, which is the whole reason for sequencing.");
            }
            finally
            {
                EditorApplication.update -= counter;
            }
        }

        [UnityTest]
        public IEnumerator AThrowingStepFailsTheItem()
        {
            var item = FrameSequencer.Run(ThrowsOnTheSecondStep(), "frames_throws");

            for (var frame = 0; frame < FrameBudget && !item.IsCompleted; frame++)
            {
                yield return null;
            }

            Assert.That(item.IsCompleted, Is.True, "A step that threw must still release the waiting request.");
            Assert.That(item.Error, Is.InstanceOf<InvalidOperationException>());
            Assert.That(item.Error.Message, Is.EqualTo("step blew up"));
            Assert.That(FrameSequencer.ActiveCount, Is.Zero, "A failed sequence must stop advancing.");
        }

        [Test]
        public void CancelAllFailsPendingSequences()
        {
            var item = FrameSequencer.Run(NeverEnds(), "frames_forever");

            Assert.That(FrameSequencer.ActiveCount, Is.EqualTo(1));

            FrameSequencer.CancelAll("Domain reload.");

            Assert.That(item.IsCompleted, Is.True);
            var error = item.Error as McpToolException;
            Assert.That(error, Is.Not.Null, "Cancellation must arrive as an error the client can read.");
            Assert.That(error.Code, Is.EqualTo("cancelled"));
            Assert.That(error.HttpStatus, Is.EqualTo(409));
            Assert.That(error.Message, Is.EqualTo("Domain reload."));
            Assert.That(FrameSequencer.ActiveCount, Is.Zero);
        }

        private static IEnumerator<FrameStep> ThreeSteps(Func<int> currentTick, ICollection<int> ranOnTick)
        {
            ranOnTick.Add(currentTick());
            yield return FrameStep.Wait();

            ranOnTick.Add(currentTick());
            yield return FrameStep.Wait();

            ranOnTick.Add(currentTick());
            yield return FrameStep.Done(new JObject { ["steps"] = 3 });
        }

        private static IEnumerator<FrameStep> ThrowsOnTheSecondStep()
        {
            yield return FrameStep.Wait();
            throw new InvalidOperationException("step blew up");
        }

        private static IEnumerator<FrameStep> NeverEnds()
        {
            while (true)
            {
                yield return FrameStep.Wait();
            }
        }
    }
}
