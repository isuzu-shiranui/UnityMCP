using System;
using System.Linq;
using System.Reflection;

using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using Unity.Profiling;

namespace UnityMCP.Editor.Tests
{
    [TestFixture]
    internal sealed class TestResultsTests
    {
        private Type tools;
        private FieldInfo current;
        private object previous;
        private string previousSession;
        private Func<bool, int, JObject> results;

        [SetUp]
        public void SetUp()
        {
            // The optional TestRunner assembly is not a dependency of the main test assembly.
            this.tools = Assembly.Load("UnityMCP.Editor.TestRunner")
                .GetType("UnityMCP.Editor.TestRunner.TestRunnerTools", true);
            this.current = this.tools.GetField("current", BindingFlags.Static | BindingFlags.NonPublic);
            this.previous = this.current.GetValue(null);
            this.previousSession = SessionState.GetString("UnityMCP.LastTestRun", string.Empty);
            this.results = (Func<bool, int, JObject>)Delegate.CreateDelegate(
                typeof(Func<bool, int, JObject>), this.tools.GetMethod("Results"));
        }

        [TearDown]
        public void TearDown()
        {
            this.current.SetValue(null, this.previous);
            SessionState.SetString("UnityMCP.LastTestRun", this.previousSession);
        }

        [TestCase(false, -1, 0, true)]
        [TestCase(false, 0, 0, true)]
        [TestCase(false, 2, 2, true)]
        [TestCase(false, 3, 3, false)]
        [TestCase(true, 2, 2, true)]
        [TestCase(true, int.MaxValue, 4, false)]
        public void LimitedResultsKeepCountsOrderAndPersistedSnapshot(
            bool includePassed, int limit, int count, bool truncated)
        {
            this.Seed("passed", "skipped", "failed", "inconclusive");
            var persisted = SessionState.GetString("UnityMCP.LastTestRun", string.Empty);
            var response = this.results(includePassed, limit);
            var expected = includePassed ? new[] { "Case0", "Case1", "Case2", "Case3" }
                : new[] { "Case1", "Case2", "Case3" };

            Assert.That(response["results"].Select(r => (string)r["name"]), Is.EqualTo(expected.Take(count)));
            Assert.That((bool)response["truncated"], Is.EqualTo(truncated));
            foreach (var status in new[] { "passed", "skipped", "failed", "inconclusive" })
            {
                Assert.That((int)response[status], Is.EqualTo(1));
            }

            // Editing the returned JSON must not mutate the source or a later response.
            if (count > 0)
            {
                response["results"][0]["name"] = "changed by caller";
            }

            this.tools.GetMethod("Persist", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            Assert.That(SessionState.GetString("UnityMCP.LastTestRun", string.Empty), Is.EqualTo(persisted));
            this.tools.GetMethod("Restore", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            Assert.That(this.results(true, int.MaxValue)["results"].Select(r => (string)r["name"]),
                Is.EqualTo(new[] { "Case0", "Case1", "Case2", "Case3" }));
        }

        [Test]
        public void AllPassingRunHasNoTruncatedFailuresEvenWithZeroLimit()
        {
            this.Seed("passed", "passed");
            var response = this.results(false, 0);
            Assert.That((int)response["passed"], Is.EqualTo(2));
            Assert.That((JArray)response["results"], Is.Empty);
            Assert.That((bool)response["truncated"], Is.False);
        }

        [Test]
        public void LimitedResultsDoNotAllocateInProportionToTheWholeRun()
        {
            var small = this.AllocationFor(100);
            var large = this.AllocationFor(1000);
            Assert.That(large, Is.LessThan(small * 2),
                "Returning the same five rows from ten times as many tests should not clone the whole snapshot.");
        }

        private long AllocationFor(int count)
        {
            this.Seed(Enumerable.Repeat("failed", count).ToArray());
            this.results(false, 5);
            using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1,
                ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.CollectOnlyOnCurrentThread))
            {
                for (var i = 0; i < 5; i++)
                {
                    this.results(false, 5);
                }

                recorder.Stop();
                Assert.That(recorder.Count, Is.GreaterThan(0), "GC.Alloc recording must be available.");
                var allocations = recorder.GetSample(0).Count;
                Assert.That(allocations, Is.GreaterThan(0));
                return allocations;
            }
        }

        private void Seed(params string[] statuses)
        {
            var snapshot = new JObject
            {
                ["status"] = "completed",
                ["passed"] = statuses.Count(s => s == "passed"),
                ["failed"] = statuses.Count(s => s == "failed"),
                ["skipped"] = statuses.Count(s => s == "skipped"),
                ["inconclusive"] = statuses.Count(s => s == "inconclusive"),
                ["results"] = new JArray(statuses.Select((s, i) => new JObject
                {
                    ["name"] = "Case" + i,
                    ["status"] = s,
                })),
            };
            var state = this.current.FieldType.GetMethod("FromJson").Invoke(null, new object[] { snapshot });
            this.current.SetValue(null, state);
            this.tools.GetMethod("Persist", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        }
    }
}
