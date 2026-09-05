using System;
using System.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Covers <see cref="McpJobRegistry"/>: the handle a caller gets when main-thread work
    /// outlives its request's synchronous wait.
    /// </summary>
    [TestFixture]
    internal sealed class JobRegistryTests
    {
        private McpMainThreadDispatcher dispatcher;
        private McpJobRegistry registry;

        [SetUp]
        public void SetUp()
        {
            this.dispatcher = new McpMainThreadDispatcher();
            this.registry = new McpJobRegistry();
        }

        [Test]
        public void IdIsReadableAndDerivedFromLabel()
        {
            var item = this.dispatcher.Submit(() => new JObject());

            var id = this.registry.Track(item, "/command:console.getLogs");

            // Readable ids measurably reduce mis-quoting by models compared to raw GUIDs.
            Assert.That(id, Does.StartWith("command_console_getlogs-"));
            Assert.That(id, Does.Not.Contain("/"));
            Assert.That(id, Does.Not.Contain(":"));
        }

        [Test]
        public void IdsAreUniquePerTrack()
        {
            var first = this.registry.Track(this.dispatcher.Submit(() => new JObject()), "/execute_code");
            var second = this.registry.Track(this.dispatcher.Submit(() => new JObject()), "/execute_code");

            Assert.That(first, Is.Not.EqualTo(second));
        }

        [Test]
        public void EmptyLabelStillProducesAnId()
        {
            var id = this.registry.Track(this.dispatcher.Submit(() => new JObject()), "///");

            Assert.That(id, Does.StartWith("job-"));
        }

        [Test]
        public void StatusIsRunningUntilPumped()
        {
            var item = this.dispatcher.Submit(() => new JObject());
            var id = this.registry.Track(item, "/execute_code");

            Assert.That(this.registry.TryGet(id, out var entry), Is.True);
            Assert.That(entry.Status, Is.EqualTo("running"));
            Assert.That(this.registry.RunningCount, Is.EqualTo(1));

            this.dispatcher.Pump();

            Assert.That(entry.Status, Is.EqualTo("completed"));
            Assert.That(this.registry.RunningCount, Is.Zero);
        }

        [Test]
        public void CompletedJobCarriesItsResult()
        {
            var item = this.dispatcher.Submit(() => new JObject { ["value"] = 42 });
            var id = this.registry.Track(item, "/execute_code");
            this.dispatcher.Pump();

            this.registry.TryGet(id, out var entry);
            var detail = entry.ToDetailJson();

            Assert.That(detail["status"].Value<string>(), Is.EqualTo("completed"));
            Assert.That(detail["result"]["value"].Value<int>(), Is.EqualTo(42));
            Assert.That(detail["error"], Is.Null);
        }

        [Test]
        public void FailedJobCarriesItsError()
        {
            var item = this.dispatcher.Submit(() => throw new InvalidOperationException("exploded"));
            var id = this.registry.Track(item, "/execute_code");
            this.dispatcher.Pump();

            this.registry.TryGet(id, out var entry);
            var detail = entry.ToDetailJson();

            Assert.That(detail["status"].Value<string>(), Is.EqualTo("failed"));
            Assert.That(detail["error"].Value<string>(), Is.EqualTo("exploded"));
            Assert.That(detail["result"], Is.Null);
        }

        [Test]
        public void CancelledJobReportsCancelled()
        {
            var item = this.dispatcher.Submit(() => new JObject());
            var id = this.registry.Track(item, "/execute_code");

            Assert.That(item.TryAbandon(), Is.True);
            this.registry.TryGet(id, out var entry);

            Assert.That(entry.Status, Is.EqualTo("cancelled"));
        }

        [Test]
        public void RunningJobOmitsResultKeys()
        {
            var item = this.dispatcher.Submit(() => new JObject());
            var id = this.registry.Track(item, "/execute_code");

            this.registry.TryGet(id, out var entry);
            var detail = entry.ToDetailJson();

            Assert.That(detail["status"].Value<string>(), Is.EqualTo("running"));
            Assert.That(detail["result"], Is.Null);
            Assert.That(detail["error"], Is.Null);
        }

        [Test]
        public void UnknownIdIsNotFound()
        {
            Assert.That(this.registry.TryGet("no_such_job-99", out _), Is.False);
            Assert.That(this.registry.TryGet(null, out _), Is.False);
        }

        [Test]
        public void ListReportsEveryTrackedJob()
        {
            this.registry.Track(this.dispatcher.Submit(() => new JObject()), "/execute_code");
            this.registry.Track(this.dispatcher.Submit(() => new JObject()), "/inspect");

            var listed = this.registry.ToJson();

            Assert.That(listed.Count, Is.EqualTo(2));
            Assert.That(listed.Select(j => j["label"].Value<string>()),
                Is.EquivalentTo(new[] { "/execute_code", "/inspect" }));
        }

        [Test]
        public void JobFollowsADeferredResultToTheInnerItem()
        {
            // A queued tool that outlives the window is tracked by its queue item. When that
            // item settles with a DeferredToolResult, the work has only started; the job must
            // stay running until the inner item settles and then carry the inner result.
            var inner = McpMainThreadDispatcher.CreateDeferred();
            var outer = this.dispatcher.Submit(() => new DeferredToolResult(inner));
            var id = this.registry.Track(outer, "/input_replay");

            this.dispatcher.Pump();

            Assert.That(outer.IsCompleted, Is.True);
            Assert.That(this.registry.TryGet(id, out var entry), Is.True);
            Assert.That(entry.Status, Is.EqualTo("running"), "The marker must not read as a finished job.");
            Assert.That(this.registry.RunningCount, Is.EqualTo(1));
            Assert.That(entry.ToDetailJson()["result"], Is.Null);

            inner.Complete(new JObject { ["frames"] = 12 });

            Assert.That(entry.Status, Is.EqualTo("completed"));
            Assert.That(entry.ToDetailJson()["result"]["frames"].Value<int>(), Is.EqualTo(12));
            Assert.That(this.registry.RunningCount, Is.Zero);
        }

        [Test]
        public void JobFollowsAFailedInnerItem()
        {
            var inner = McpMainThreadDispatcher.CreateDeferred();
            var outer = this.dispatcher.Submit(() => new DeferredToolResult(inner));
            var id = this.registry.Track(outer, "/input_replay");
            this.dispatcher.Pump();

            inner.Fail(new McpToolException("cancelled", "Server stopped.", 409));

            this.registry.TryGet(id, out var entry);
            Assert.That(entry.Status, Is.EqualTo("failed"));
            Assert.That(entry.ToDetailJson()["error"].Value<string>(), Is.EqualTo("Server stopped."));
        }

        [Test]
        public void RunningJobsAreNeverEvicted()
        {
            // Losing the handle to work that is still going to mutate the project is worse
            // than an oversized registry, so the cap must only claim finished entries.
            // The item is submitted to a dispatcher this test never pumps, so it stays
            // genuinely pending while the filler jobs churn through the cap.
            var idle = new McpMainThreadDispatcher();
            var running = idle.Submit(() => new JObject());
            var runningId = this.registry.Track(running, "/long_running");

            for (var i = 0; i < 400; i++)
            {
                var item = this.dispatcher.Submit(() => new JObject());
                this.registry.Track(item, "/filler");
                this.dispatcher.Pump();
            }

            Assert.That(this.registry.TryGet(runningId, out var entry), Is.True,
                "A still-running job must survive eviction pressure.");
            Assert.That(entry.Status, Is.EqualTo("running"));
        }

        /// <summary>
        /// Answered inline, a McpToolException reaches the caller with its code and its status,
        /// which is how a refusal is told apart from a fault. Through a job only the message used
        /// to survive, so the same refusal read as a generic failure depending on how busy the
        /// main thread had been.
        /// </summary>
        [Test]
        public void AFailedJobKeepsTheCodeAndStatusItFailedWith()
        {
            var item = this.dispatcher.Submit(() =>
                throw new McpToolException("window_occluded", "Another application is in front.", 409));
            var id = this.registry.Track(item, "capture_screenshot");
            this.dispatcher.Pump();

            this.registry.TryGet(id, out var entry);
            var detail = entry.ToDetailJson();

            Assert.That(detail["status"].Value<string>(), Is.EqualTo("failed"));
            Assert.That(detail["errorCode"].Value<string>(), Is.EqualTo("window_occluded"));
            Assert.That(detail["httpStatus"].Value<int>(), Is.EqualTo(409));
            Assert.That(detail["error"].Value<string>(), Does.Contain("in front"));
        }

        [Test]
        public void AJobThatFailedWithSomethingElseIsAnInternalError()
        {
            var item = this.dispatcher.Submit(() => throw new InvalidOperationException("something gave way"));
            var id = this.registry.Track(item, "whatever");
            this.dispatcher.Pump();

            this.registry.TryGet(id, out var entry);
            var detail = entry.ToDetailJson();

            Assert.That(detail["errorCode"].Value<string>(), Is.EqualTo("internal_error"));
            Assert.That(detail["httpStatus"].Value<int>(), Is.EqualTo(500));
        }

        /// <summary>
        /// A handler that reports failure by returning it has to read the same way whether the
        /// call was answered inline or became a job, or the verdict depends on how warm Roslyn
        /// happened to be.
        /// </summary>
        [Test]
        public void AHandlerReportedFailureIsAFailedJob()
        {
            var item = this.dispatcher.Submit(
                () => new JObject { ["error"] = "the snippet did not compile" });
            var id = this.registry.Track(item, "execute_code");
            this.dispatcher.Pump();

            this.registry.TryGet(id, out var entry);
            var detail = entry.ToDetailJson();

            Assert.That(detail["status"].Value<string>(), Is.EqualTo("failed"),
                "a job whose result reports a failure is a failed job");
            Assert.That(detail["error"].Value<string>(), Does.Contain("did not compile"));
            Assert.That(detail["errorCode"].Value<string>(), Is.EqualTo("invalid_params"));
            Assert.That(detail["result"], Is.Not.Null, "the caller still gets what the tool returned");
        }

        [Test]
        public void AnOrdinaryResultIsStillACompletedJob()
        {
            var item = this.dispatcher.Submit(
                () => new JObject { ["errorCount"] = 0, ["warningCount"] = 2 });
            var id = this.registry.Track(item, "console_get_count");
            this.dispatcher.Pump();

            this.registry.TryGet(id, out var entry);
            var detail = entry.ToDetailJson();

            Assert.That(detail["status"].Value<string>(), Is.EqualTo("completed"));
            Assert.That(detail["error"], Is.Null);
        }
    }
}
