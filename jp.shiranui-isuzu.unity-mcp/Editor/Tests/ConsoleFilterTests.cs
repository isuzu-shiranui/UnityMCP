using System;
using System.Reflection;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityEditor;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// The Console window's own filter applies to the API the console tools read through, so a
    /// window with its Log toggle off or text in its search box makes them answer with fewer
    /// entries, or none, and say nothing about it. A reader cannot tell that apart from a quiet
    /// project.
    /// </summary>
    [TestFixture]
    internal sealed class ConsoleFilterTests
    {
        private const int LogLevelLog = 128;

        private static readonly Type LogEntries =
            typeof(EditorWindow).Assembly.GetType("UnityEditor.LogEntries");

        private const int Collapse = 1;
        private const int LogLevelWarning = 256;
        private const int LogLevelError = 512;

        private int? savedFlags;

        private static MethodInfo Method(string name)
        {
            return LogEntries?.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
        }

        /// <summary>Every severity shown and nothing collapsed, whatever the machine's Console had.</summary>
        /// <remarks>
        /// The toggles live in EditorPrefs that every Editor on the machine shares, so a Warning
        /// toggle switched off in an Editor someone is working in starts this one with warnings
        /// withheld, and a check made while the window should show everything finds them hidden.
        /// </remarks>
        [SetUp]
        public void ShowEverything()
        {
            var flags = LogEntries?.GetProperty("consoleFlags", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var setFlag = Method("SetConsoleFlag");

            if (flags == null || setFlag == null)
            {
                return;
            }

            this.savedFlags = (int)flags.GetValue(null);

            foreach (var bit in new[] { LogLevelLog, LogLevelWarning, LogLevelError })
            {
                setFlag.Invoke(null, new object[] { bit, true });
            }

            setFlag.Invoke(null, new object[] { Collapse, false });
        }

        [TearDown]
        public void RestoreTheConsole()
        {
            var setFlag = Method("SetConsoleFlag");

            if (this.savedFlags is not int saved || setFlag == null)
            {
                return;
            }

            foreach (var bit in new[] { Collapse, LogLevelLog, LogLevelWarning, LogLevelError })
            {
                setFlag.Invoke(null, new object[] { bit, (saved & bit) != 0 });
            }

            this.savedFlags = null;
        }

        [Test]
        public void TheWindowsSeverityToggleChangesWhatGetCountReports()
        {
            var getCount = Method("GetCount");
            var setFlag = Method("SetConsoleFlag");
            var byType = Method("GetCountsByType");

            if (getCount == null || setFlag == null || byType == null)
            {
                Assert.Ignore("UnityEditor.LogEntries does not expose the methods this covers on this Editor.");
            }

            UnityEngine.Debug.Log("ConsoleFilterTests: an entry to count.");

            var visible = (int)getCount.Invoke(null, null);
            int filtered;

            try
            {
                setFlag.Invoke(null, new object[] { LogLevelLog, false });
                filtered = (int)getCount.Invoke(null, null);
            }
            finally
            {
                setFlag.Invoke(null, new object[] { LogLevelLog, true });
            }

            Assert.That(filtered, Is.LessThan(visible),
                "with the Log toggle off the window reports fewer entries; if this stops being true the tools no longer need to report the filter");
            Assert.That((int)getCount.Invoke(null, null), Is.EqualTo(visible), "the toggle was not restored");
        }

        [Test]
        public void GetCountsByTypeIgnoresTheWindowsFilter()
        {
            var getCount = Method("GetCount");
            var setFlag = Method("SetConsoleFlag");
            var byType = Method("GetCountsByType");

            if (getCount == null || setFlag == null || byType == null)
            {
                Assert.Ignore("UnityEditor.LogEntries does not expose the methods this covers on this Editor.");
            }

            UnityEngine.Debug.Log("ConsoleFilterTests: an entry to count.");

            var unfilteredBefore = SumByType(byType);
            int unfilteredWhileHidden;

            try
            {
                setFlag.Invoke(null, new object[] { LogLevelLog, false });
                unfilteredWhileHidden = SumByType(byType);
            }
            finally
            {
                setFlag.Invoke(null, new object[] { LogLevelLog, true });
            }

            // This is what lets the tools detect the filter without touching the user's window.
            Assert.That(unfilteredWhileHidden, Is.EqualTo(unfilteredBefore),
                "GetCountsByType has to stay unfiltered; the console tools compare it against GetCount to find hidden rows");
        }

        [Test]
        public void TheCountToolSaysHowManyEntriesTheWindowIsHiding()
        {
            var getCount = Method("GetCount");
            var setFlag = Method("SetConsoleFlag");

            if (getCount == null || setFlag == null)
            {
                Assert.Ignore("UnityEditor.LogEntries does not expose the methods this covers on this Editor.");
            }

            UnityEngine.Debug.Log("ConsoleFilterTests: an entry to hide.");

            var handler = new UnityMCP.Editor.Handlers.ConsoleCommandHandler();

            var unfiltered = handler.Execute("getCount", new JObject());
            Assert.That(unfiltered["hiddenByConsoleFilter"], Is.Null,
                "nothing is hidden while the window shows everything");

            JObject filtered;

            try
            {
                setFlag.Invoke(null, new object[] { LogLevelLog, false });
                filtered = handler.Execute("getCount", new JObject());
            }
            finally
            {
                setFlag.Invoke(null, new object[] { LogLevelLog, true });
            }

            Assert.That(filtered["hiddenByConsoleFilter"], Is.Not.Null,
                "a caller given a smaller number has to be told the window is filtering");
            Assert.That(filtered["hiddenByConsoleFilter"].Value<int>(), Is.GreaterThan(0));
            Assert.That(filtered["note"]?.Value<string>(), Does.Contain("Console window is filtering"));
        }

        [Test]
        public void TheReadToolSaysHowManyEntriesTheWindowIsHiding()
        {
            var getCount = Method("GetCount");
            var setFlag = Method("SetConsoleFlag");

            if (getCount == null || setFlag == null)
            {
                Assert.Ignore("UnityEditor.LogEntries does not expose the methods this covers on this Editor.");
            }

            UnityEngine.Debug.Log("ConsoleFilterTests: an entry the read tool should account for.");

            var unfiltered = UnityMCP.Editor.Handlers.LogReader.ReadLogs(new JObject { ["limit"] = 5 });
            Assert.That(unfiltered["hiddenByConsoleFilter"], Is.Null,
                "nothing is hidden while the window shows everything");

            JObject filtered;

            try
            {
                setFlag.Invoke(null, new object[] { LogLevelLog, false });
                filtered = UnityMCP.Editor.Handlers.LogReader.ReadLogs(new JObject { ["limit"] = 5 });
            }
            finally
            {
                setFlag.Invoke(null, new object[] { LogLevelLog, true });
            }

            Assert.That(filtered["hiddenByConsoleFilter"], Is.Not.Null,
                "the read tool withholds entries under the same filter the count tool reports");
            Assert.That(filtered["hiddenByConsoleFilter"].Value<int>(), Is.GreaterThan(0));
            Assert.That(filtered["note"]?.Value<string>(), Does.Contain("Console window is filtering"));
        }

        /// <summary>
        /// total came from the filtered GetCount while errors and warnings came from the
        /// unfiltered GetCountsByType, so a filtered Console reported a total smaller than the
        /// severities it reported alongside it.
        /// </summary>
        [Test]
        public void TheReadToolsTotalIsNotContradictedByItsOwnSeverityCounts()
        {
            var setFlag = Method("SetConsoleFlag");

            if (setFlag == null)
            {
                Assert.Ignore("UnityEditor.LogEntries does not expose the methods this covers on this Editor.");
            }

            UnityEngine.Debug.LogWarning("ConsoleFilterTests: a warning that survives the Log toggle.");

            JObject filtered;

            try
            {
                setFlag.Invoke(null, new object[] { LogLevelLog, false });
                filtered = UnityMCP.Editor.Handlers.LogReader.ReadLogs(new JObject { ["limit"] = 5 });
            }
            finally
            {
                setFlag.Invoke(null, new object[] { LogLevelLog, true });
            }

            var total = filtered["total"].Value<int>();
            var hidden = filtered["hiddenByConsoleFilter"]?.Value<int>() ?? 0;
            var bySeverity = filtered["errors"].Value<int>() + filtered["warnings"].Value<int>();

            Assert.That(total + hidden, Is.GreaterThanOrEqualTo(bySeverity),
                "a reply that reports fewer entries in total than it reports by severity, and "
                + "does not say the window is filtering, cannot be acted on");
        }

        private static int SumByType(MethodInfo byType)
        {
            var counts = new object[] { 0, 0, 0 };
            byType.Invoke(null, counts);
            return (int)counts[0] + (int)counts[1] + (int)counts[2];
        }
    }
}
