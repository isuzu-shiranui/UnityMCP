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
    }
}
