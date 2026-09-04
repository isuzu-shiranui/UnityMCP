using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// <see cref="ToolCallRunner"/> decides where a call runs and when it becomes a job. Both the
    /// REST route and the MCP endpoint go through it, so these are the only tests of that logic.
    /// </summary>
    [TestFixture]
    internal sealed class ToolCallRunnerTests
    {
        private static class Tools
        {
            [McpTool("runner_inline", "Answers off the main thread.", Idempotency = McpIdempotency.Safe, MainThread = false)]
            public static int Inline([McpArg("value", "Echoed")] int value) => value;

            [McpTool("runner_main", "Needs the main thread.", Idempotency = McpIdempotency.Safe)]
            public static int Main([McpArg("value", "Echoed")] int value) => value;

            [McpTool("runner_throws", "Always fails.", Idempotency = McpIdempotency.Safe, MainThread = false)]
            public static void Throws() => throw new McpToolException("boom", "It broke.", 418);

            /// <summary>The item <see cref="Deferred"/> hands back; the test settles it.</summary>
            public static McpMainThreadDispatcher.WorkItem Pending;

            [McpTool("runner_deferred", "Answers on a later frame.", Idempotency = McpIdempotency.Safe, MainThread = false)]
            public static JObject Deferred() => new DeferredToolResult(Pending);
        }

        private McpMainThreadDispatcher dispatcher;
        private McpJobRegistry jobs;
        private ToolCatalog catalog;

        [SetUp]
        public void SetUp()
        {
            this.dispatcher = new McpMainThreadDispatcher();
            this.jobs = new McpJobRegistry();
            this.catalog = ToolCatalog.BuildFromTypes(new[] { typeof(Tools) });
        }

        private McpToolDescriptor Tool(string name)
        {
            Assert.That(this.catalog.TryGet(name, out var descriptor), Is.True);
            return descriptor;
        }

        [Test]
        public void OffThreadToolCompletesWithoutThePump()
        {
            var runner = new ToolCallRunner(this.dispatcher, this.jobs, () => 250);

            var outcome = runner.Run(this.Tool("runner_inline"), new JObject { ["value"] = 4 });

            Assert.That(outcome.State, Is.EqualTo(ToolCallOutcome.Kind.Completed));
            Assert.That(outcome.Result["result"].Value<int>(), Is.EqualTo(4));
            Assert.That(this.dispatcher.PendingCount, Is.EqualTo(0), "Off-thread tools must not touch the queue.");
        }

        [Test]
        public void MainThreadToolBecomesAJobWhenNothingPumps()
        {
            var runner = new ToolCallRunner(this.dispatcher, this.jobs, () => 100);

            var outcome = runner.Run(this.Tool("runner_main"), new JObject { ["value"] = 1 });

            Assert.That(outcome.State, Is.EqualTo(ToolCallOutcome.Kind.Running));
            Assert.That(outcome.JobId, Does.StartWith("runner_main"));
            Assert.That(this.jobs.TryGet(outcome.JobId, out var entry), Is.True);
            Assert.That(entry.Status, Is.EqualTo("running"));

            // The work was not cancelled: pumping later finishes it and the job carries the result.
            this.dispatcher.Pump();
            Assert.That(entry.Status, Is.EqualTo("completed"));
            Assert.That(entry.Item.Result["result"].Value<int>(), Is.EqualTo(1));
        }

        [Test]
        public void ToolExceptionIsReportedAsFailedWithItsOwnCode()
        {
            var runner = new ToolCallRunner(this.dispatcher, this.jobs, () => 250);

            var outcome = runner.Run(this.Tool("runner_throws"), new JObject());

            Assert.That(outcome.State, Is.EqualTo(ToolCallOutcome.Kind.Failed));
            var error = outcome.Error as McpToolException;
            Assert.That(error, Is.Not.Null);
            Assert.That(error.Code, Is.EqualTo("boom"));
            Assert.That(error.HttpStatus, Is.EqualTo(418));
        }

        [Test]
        public void DeferredResultIsUnwrappedWhenItArrivesInTime()
        {
            var item = McpMainThreadDispatcher.CreateDeferred();
            Tools.Pending = item;

            var settle = Task.Run(() =>
            {
                Thread.Sleep(50);
                item.Complete(new JObject { ["clicked"] = true });
            });

            var runner = new ToolCallRunner(this.dispatcher, this.jobs, () => 2000);
            var outcome = runner.Run(this.Tool("runner_deferred"), new JObject());

            Assert.That(settle.Wait(5000), Is.True);
            Assert.That(outcome.State, Is.EqualTo(ToolCallOutcome.Kind.Completed));
            Assert.That(outcome.Result["clicked"].Value<bool>(), Is.True,
                "The marker object must never reach the caller in place of the real result.");
            Assert.That(this.jobs.ToJson().Count(), Is.EqualTo(0),
                "A call answered inside its window must not leave a job behind.");
        }

        [Test]
        public void DeferredResultThatOutlivesTheWindowBecomesAJob()
        {
            Tools.Pending = McpMainThreadDispatcher.CreateDeferred();

            var runner = new ToolCallRunner(this.dispatcher, this.jobs, () => 100);
            var outcome = runner.Run(this.Tool("runner_deferred"), new JObject());

            Assert.That(outcome.State, Is.EqualTo(ToolCallOutcome.Kind.Running));
            Assert.That(outcome.JobId, Does.StartWith("runner_deferred"));
            Assert.That(this.jobs.TryGet(outcome.JobId, out var entry), Is.True);
            Assert.That(entry.Status, Is.EqualTo("running"));

            Tools.Pending.Complete(new JObject { ["clicked"] = true });

            Assert.That(entry.Status, Is.EqualTo("completed"));
            Assert.That(entry.Item.Result["clicked"].Value<bool>(), Is.True);
        }

        [Test]
        public void NullArgumentsAreTreatedAsEmpty()
        {
            var runner = new ToolCallRunner(this.dispatcher, this.jobs, () => 250);

            var outcome = runner.Run(this.Tool("runner_throws"), null);

            Assert.That(outcome.State, Is.EqualTo(ToolCallOutcome.Kind.Failed));
            Assert.That(this.jobs.ToJson().Count(), Is.EqualTo(0));
        }
    }
}
