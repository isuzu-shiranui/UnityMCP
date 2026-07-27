using System;
using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.TestRunner
{
    /// <summary>
    /// Runs Unity's own test suites and reports what happened.
    /// </summary>
    /// <remarks>
    /// Lives in its own assembly, constrained to <c>UNITY_INCLUDE_TESTS</c>, so that a project
    /// without the test framework simply does not see these two tools rather than failing to
    /// compile the whole package. Declaring <c>com.unity.test-framework</c> as a dependency
    /// would have been simpler, but it would push the framework onto every consumer to make an
    /// optional feature available.
    /// </remarks>
    internal static class TestRunnerTools
    {
        /// <summary>
        /// Reading these off the main thread is the entire point — a run holds the main thread
        /// for its whole duration, so anything that has to be dispatched there cannot report on
        /// a run while it is happening. The state therefore lives in plain statics, written
        /// only from the callbacks (which arrive on the main thread) and read under a lock.
        /// </summary>
        private static readonly object Gate = new object();

        private static TestRunSnapshot current = new TestRunSnapshot();

        /// <summary>
        /// Statics do not survive a domain reload, and a PlayMode run causes several. The
        /// snapshot is mirrored into SessionState so it can be restored on load.
        /// </summary>
        private const string SessionKey = "UnityMCP.LastTestRun";

        private static TestRunnerApi api;

        private sealed class TestRunSnapshot
        {
            public string Status = "idle";

            public string Mode;

            public string StartedAt;

            public string CompletedAt;

            public int Passed;

            public int Failed;

            public int Skipped;

            public int Inconclusive;

            /// <summary>
            /// Names of the tests seen so far, so progress can be reported before the run ends.
            /// A set rather than a count because the callbacks fire more than once per test —
            /// see <see cref="Callbacks.TestFinished"/>.
            /// </summary>
            public HashSet<string> Finished = new HashSet<string>();

            public double DurationSeconds;

            public JArray Results = new JArray();

            public JObject ToJson()
            {
                return new JObject
                {
                    ["status"] = this.Status,
                    ["mode"] = this.Mode,
                    ["startedAt"] = this.StartedAt,
                    ["completedAt"] = this.CompletedAt,
                    ["passed"] = this.Passed,
                    ["failed"] = this.Failed,
                    ["skipped"] = this.Skipped,
                    ["inconclusive"] = this.Inconclusive,
                    ["finishedCount"] = this.Finished.Count,
                    ["durationSeconds"] = this.DurationSeconds,
                    ["results"] = this.Results,
                };
            }

            public static TestRunSnapshot FromJson(JObject json)
            {
                return new TestRunSnapshot
                {
                    Status = (string)json["status"] ?? "idle",
                    Mode = (string)json["mode"],
                    StartedAt = (string)json["startedAt"],
                    CompletedAt = (string)json["completedAt"],
                    Passed = (int?)json["passed"] ?? 0,
                    Failed = (int?)json["failed"] ?? 0,
                    Skipped = (int?)json["skipped"] ?? 0,
                    Inconclusive = (int?)json["inconclusive"] ?? 0,
                    // finishedCount is deliberately not restored. It is live progress, and a
                    // run that survived a domain reload is reported as interrupted anyway, so
                    // "how far the previous domain got" is not something a caller can act on.
                    DurationSeconds = (double?)json["durationSeconds"] ?? 0d,
                    Results = json["results"] as JArray ?? new JArray(),
                };
            }
        }

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            Restore();
            EnsureRegistered();
        }

        /// <summary>
        /// Registers the callbacks for this domain.
        /// </summary>
        /// <remarks>
        /// Every domain load, deliberately. Registrations do not carry across a reload — an
        /// attempt to register only once per Editor session left a reloaded domain receiving
        /// no callbacks at all, so a run started after a recompile never reported that it had
        /// finished. The cost of re-registering is that a callback can be delivered more than
        /// once, which is why <see cref="Callbacks.TestFinished"/> counts by name and
        /// <see cref="Callbacks.RunStarted"/> does not clear anything.
        /// </remarks>
        private static void EnsureRegistered()
        {
            if (api != null)
            {
                return;
            }

            api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(new Callbacks());
        }

        private static void Restore()
        {
            var raw = SessionState.GetString(SessionKey, string.Empty);

            if (raw.Length == 0)
            {
                return;
            }

            try
            {
                lock (Gate)
                {
                    current = TestRunSnapshot.FromJson(JObject.Parse(raw));

                    // A run that was in flight when the domain reloaded cannot be observed any
                    // more: the callbacks belonged to the old domain. Saying "running" forever
                    // would be worse than admitting the outcome is unknown.
                    if (current.Status == "running")
                    {
                        current.Status = "interrupted";
                    }
                }
            }
            catch
            {
                // A malformed snapshot is not worth failing a domain reload over.
            }
        }

        private static void Persist()
        {
            try
            {
                SessionState.SetString(SessionKey, current.ToJson().ToString(Newtonsoft.Json.Formatting.None));
            }
            catch
            {
                // Losing the mirror only costs durability across the next reload.
            }
        }

        private sealed class Callbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
                lock (Gate)
                {
                    // Only initialises a run this package did not start — one launched from the
                    // Test Runner window. A run started through the tool is already marked
                    // running by the time Execute is called, and resetting here as well would
                    // throw away progress if this callback is delivered late or twice.
                    if (current.Status != "running")
                    {
                        current = new TestRunSnapshot
                        {
                            Status = "running",
                            StartedAt = DateTime.UtcNow.ToString("o"),
                        };
                    }

                    Persist();
                }
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                lock (Gate)
                {
                    current.Status = "completed";
                    current.CompletedAt = DateTime.UtcNow.ToString("o");
                    current.DurationSeconds = result.Duration;
                    current.Passed = result.PassCount;
                    current.Failed = result.FailCount;
                    current.Skipped = result.SkipCount;
                    current.Inconclusive = result.InconclusiveCount;
                    current.Results = new JArray(Flatten(result).Cast<object>().ToArray());

                    Persist();
                }
            }

            public void TestStarted(ITestAdaptor test)
            {
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.Test.IsSuite)
                {
                    return;
                }

                lock (Gate)
                {
                    // Counted by name rather than incremented. The callbacks registered by each
                    // domain outlive that domain, so after a couple of reloads every callback
                    // fires more than once and a counter reports two or three times the tests
                    // that ran — observed as 22 finished for an 11-test run. Nothing else is
                    // affected, because the other numbers come from the result tree at
                    // RunFinished, and re-adding a name is harmless.
                    current.Finished.Add(result.Test.FullName);
                }
            }
        }

        private static IEnumerable<JObject> Flatten(ITestResultAdaptor result)
        {
            if (!result.Test.IsSuite)
            {
                yield return new JObject
                {
                    ["name"] = result.Test.Name,
                    ["fullName"] = result.Test.FullName,
                    ["status"] = result.TestStatus.ToString().ToLowerInvariant(),
                    ["durationSeconds"] = result.Duration,
                    ["message"] = string.IsNullOrEmpty(result.Message) ? null : result.Message,
                    ["stackTrace"] = string.IsNullOrEmpty(result.StackTrace) ? null : result.StackTrace,
                };

                yield break;
            }

            if (result.Children == null)
            {
                yield break;
            }

            foreach (var child in result.Children)
            {
                foreach (var leaf in Flatten(child))
                {
                    yield return leaf;
                }
            }
        }

        [McpTool(
            "test_run",
            "Start Unity's test runner. Returns as soon as the run is queued, because a run holds " +
            "the main thread for its whole duration; poll test_results for progress and the outcome.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Run(
            [McpArg("mode", "Which suite to run: 'edit' or 'play'. PlayMode runs enter Play Mode and reload the domain.")]
            string mode = "edit",
            [McpArg("assembly", "Restrict to one test assembly, by name.")]
            string assembly = null,
            [McpArg("filter", "Restrict to tests whose full name matches this regular expression.")]
            string filter = null,
            [McpArg("category", "Restrict to one NUnit category.")]
            string category = null)
        {
            TestMode testMode;

            switch ((mode ?? "edit").Trim().ToLowerInvariant())
            {
                case "edit":
                case "editmode":
                    testMode = TestMode.EditMode;
                    break;

                case "play":
                case "playmode":
                    testMode = TestMode.PlayMode;
                    break;

                default:
                    throw new McpToolException(
                        "invalid_params",
                        $"'{mode}' is not a test mode. Use 'edit' or 'play'.");
            }

            lock (Gate)
            {
                if (current.Status == "running")
                {
                    return new JObject
                    {
                        ["started"] = false,
                        ["message"] = "A test run is already in progress; poll test_results instead.",
                    };
                }

                // Marked running here, synchronously, rather than waiting for the RunStarted
                // callback. The documented workflow is to call this and then poll, and between
                // Execute and the first callback test_results would otherwise still be
                // reporting the previous run — a caller polling promptly reads "completed" and
                // takes the last run's results for this one's.
                current = new TestRunSnapshot
                {
                    Status = "running",
                    Mode = testMode.ToString(),
                    StartedAt = DateTime.UtcNow.ToString("o"),
                };

                Persist();
            }

            var testFilter = new Filter { testMode = testMode };

            if (!string.IsNullOrEmpty(assembly))
            {
                testFilter.assemblyNames = new[] { assembly };
            }

            if (!string.IsNullOrEmpty(filter))
            {
                testFilter.groupNames = new[] { filter };
            }

            if (!string.IsNullOrEmpty(category))
            {
                testFilter.categoryNames = new[] { category };
            }

            EnsureRegistered();

            api.Execute(new ExecutionSettings(testFilter));

            return new JObject
            {
                ["started"] = true,
                ["mode"] = testMode.ToString(),
                ["message"] =
                    "Test run queued. The main thread is busy for the duration, so poll test_results " +
                    "(which does not need the main thread) rather than any other tool.",
            };
        }

        [McpTool(
            "test_results",
            "Report the state of the current or most recent test run: counts, and every failure with " +
            "its message. Answers while a run is in progress, when tools that need the main thread cannot.",
            Idempotency = McpIdempotency.Safe,
            MainThread = false)]
        public static JObject Results(
            [McpArg("include_passed", "Include passing tests as well as failures.")]
            bool includePassed = false,
            [McpArg("limit", "Maximum test entries to return.")]
            int limit = 50)
        {
            JObject snapshot;

            lock (Gate)
            {
                snapshot = current.ToJson();
            }

            var results = (snapshot["results"] as JArray ?? new JArray())
                .OfType<JObject>()
                .Where(r => includePassed || (string)r["status"] != "passed")
                .ToList();

            var truncated = results.Count > Math.Max(limit, 0);

            if (truncated)
            {
                results = results.Take(Math.Max(limit, 0)).ToList();
            }

            snapshot["results"] = new JArray(results.Cast<object>().ToArray());
            snapshot["truncated"] = truncated;

            return snapshot;
        }
    }
}
