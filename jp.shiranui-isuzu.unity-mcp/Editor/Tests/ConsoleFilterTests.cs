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

        private static MethodInfo Method(string name)
        {
            return LogEntries?.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
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

        private static int SumByType(MethodInfo byType)
        {
            var counts = new object[] { 0, 0, 0 };
            byType.Invoke(null, counts);
            return (int)counts[0] + (int)counts[1] + (int)counts[2];
        }
    }
}
