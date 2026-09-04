using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Covers the two defects <see cref="McpMainThreadDispatcher"/> exists to fix: work that
    /// ran after its request had already given up, and a queue lock held across execution.
    /// </summary>
    [TestFixture]
    internal sealed class MainThreadDispatcherTests
    {
        private McpMainThreadDispatcher dispatcher;

        [SetUp]
        public void SetUp()
        {
            this.dispatcher = new McpMainThreadDispatcher();
        }

        [Test]
        public void PumpRunsQueuedWork()
        {
            var item = this.dispatcher.Submit(() => new JObject { ["value"] = 7 });

            Assert.That(item.IsCompleted, Is.False, "Work must not run until the pump does.");

            this.dispatcher.Pump();

            Assert.That(item.IsCompleted, Is.True);
            Assert.That(item.Result["value"].Value<int>(), Is.EqualTo(7));
            Assert.That(item.Error, Is.Null);
        }

        [Test]
        public void AbandonedWorkNeverRuns()
        {
            // A request that gives up must not leave its action queued: the side effect would
            // land anyway and a retry would produce it twice.
            var ran = false;
            var item = this.dispatcher.Submit(() =>
            {
                ran = true;
                return new JObject();
            });

            Assert.That(item.TryAbandon(), Is.True);

            this.dispatcher.Pump();

            Assert.That(ran, Is.False, "Abandoning a pending item must guarantee it never executes.");
            Assert.That(item.WasAbandoned, Is.True);
            Assert.That(item.IsCompleted, Is.True, "An abandoned item must release anyone waiting on it.");
        }

        [Test]
        public void AbandonFailsOnceWorkHasRun()
        {
            var item = this.dispatcher.Submit(() => new JObject());
            this.dispatcher.Pump();

            Assert.That(item.TryAbandon(), Is.False,
                "Reporting a successful cancel after the work ran would misrepresent its side effects.");
            Assert.That(item.WasAbandoned, Is.False);
        }

        [Test]
        public void ExceptionsAreCapturedNotThrown()
        {
            var item = this.dispatcher.Submit(() => throw new InvalidOperationException("nope"));

            Assert.DoesNotThrow(() => this.dispatcher.Pump(), "A failing item must not break the pump.");

            Assert.That(item.IsCompleted, Is.True);
            Assert.That(item.Error, Is.InstanceOf<InvalidOperationException>());
            Assert.That(item.Error.Message, Is.EqualTo("nope"));
        }

        [Test]
        public void OneFailingItemDoesNotStopTheRest()
        {
            var failing = this.dispatcher.Submit(() => throw new InvalidOperationException("boom"));
            var following = this.dispatcher.Submit(() => new JObject { ["ok"] = true });

            this.dispatcher.Pump();

            Assert.That(failing.Error, Is.Not.Null);
            Assert.That(following.IsCompleted, Is.True);
            Assert.That(following.Result["ok"].Value<bool>(), Is.True);
        }

        [Test]
        public void WaitTimesOutUntilPumped()
        {
            var item = this.dispatcher.Submit(() => new JObject());

            Assert.That(item.Wait(50), Is.False);

            this.dispatcher.Pump();

            Assert.That(item.Wait(0), Is.True);
        }

        [Test]
        public void SubmitIsNotBlockedByRunningWork()
        {
            // Holding the queue lock across execution stalls every other worker thread trying
            // to enqueue. Enqueueing must stay cheap during execution.
            var started = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);

            this.dispatcher.Submit(() =>
            {
                started.Set();
                release.Wait(5000);
                return new JObject();
            });

            var pump = Task.Run(() => this.dispatcher.Pump());

            Assert.That(started.Wait(5000), Is.True, "The slow item should have started.");

            var stopwatch = Stopwatch.StartNew();
            var second = this.dispatcher.Submit(() => new JObject());
            stopwatch.Stop();

            release.Set();
            Assert.That(pump.Wait(5000), Is.True);

            Assert.That(second, Is.Not.Null);
            Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(500),
                "Submitting while another item runs must not wait on the executing item.");
        }

        [Test]
        public void PumpYieldsInsteadOfDrainingUnboundedBacklog()
        {
            for (var i = 0; i < 10; i++)
            {
                this.dispatcher.Submit(() =>
                {
                    Thread.Sleep(20);
                    return new JObject();
                });
            }

            this.dispatcher.Pump();

            Assert.That(this.dispatcher.PendingCount, Is.GreaterThan(0),
                "A long backlog must be spread across frames rather than freezing the Editor in one.");
        }

        [Test]
        public void DrainAndFailReleasesPendingWaiters()
        {
            var ran = false;
            var item = this.dispatcher.Submit(() =>
            {
                ran = true;
                return new JObject();
            });

            this.dispatcher.DrainAndFail("server stopped");

            Assert.That(item.IsCompleted, Is.True, "Shutdown must not leave requests blocked for their full window.");
            Assert.That(item.Error, Is.Not.Null);
            Assert.That(item.Error.Message, Is.EqualTo("server stopped"));

            this.dispatcher.Pump();

            Assert.That(ran, Is.False, "Drained work must not execute afterwards.");
            Assert.That(this.dispatcher.PendingCount, Is.Zero);
        }

        [Test]
        public void DeferredItemIsRunningUntilCompleted()
        {
            var item = McpMainThreadDispatcher.CreateDeferred();

            Assert.That(item.IsCompleted, Is.False);
            Assert.That(item.WasAbandoned, Is.False);
            Assert.That(item.TryAbandon(), Is.False,
                "A deferred item has no queue entry to cancel, so a successful cancel would misreport it.");
            Assert.That(this.dispatcher.PendingCount, Is.Zero, "A deferred item must not enter the queue.");

            item.Complete(new JObject { ["value"] = 3 });

            Assert.That(item.IsCompleted, Is.True);
            Assert.That(item.Result["value"].Value<int>(), Is.EqualTo(3));
            Assert.That(item.Error, Is.Null);
        }

        [Test]
        public void DeferredCompleteIsIdempotent()
        {
            // The sequence that owns the item and the shutdown path that cancels it can both
            // settle it, and whichever lost the race must not overwrite the answer already sent.
            var item = McpMainThreadDispatcher.CreateDeferred();

            item.Complete(new JObject { ["value"] = 1 });
            item.Complete(new JObject { ["value"] = 2 });
            item.Fail(new InvalidOperationException("late"));

            Assert.That(item.Result["value"].Value<int>(), Is.EqualTo(1));
            Assert.That(item.Error, Is.Null);
        }

        [Test]
        public void DeferredFailCarriesTheError()
        {
            var item = McpMainThreadDispatcher.CreateDeferred();

            item.Fail(new InvalidOperationException("no window"));

            Assert.That(item.IsCompleted, Is.True);
            Assert.That(item.Error, Is.InstanceOf<InvalidOperationException>());
            Assert.That(item.Error.Message, Is.EqualTo("no window"));
            Assert.That(item.Result, Is.Null);
        }

        [Test]
        public void PendingCountTracksQueue()
        {
            Assert.That(this.dispatcher.PendingCount, Is.Zero);

            this.dispatcher.Submit(() => new JObject());
            this.dispatcher.Submit(() => new JObject());

            Assert.That(this.dispatcher.PendingCount, Is.EqualTo(2));

            this.dispatcher.Pump();

            Assert.That(this.dispatcher.PendingCount, Is.Zero);
        }
    }
}
