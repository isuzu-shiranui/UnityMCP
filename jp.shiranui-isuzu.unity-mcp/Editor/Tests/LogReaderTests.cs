using Newtonsoft.Json.Linq;

using NUnit.Framework;
using UnityMCP.Editor.Core;
using UnityMCP.Editor.Handlers;

namespace UnityMCP.Editor.Tests
{
    public class LogReaderTests
    {
        [TestCase(256, "E", TestName = "ScriptingErrorIsAnError")]
        [TestCase(2048, "E", TestName = "CompileErrorIsAnError")]
        [TestCase(131072, "E", TestName = "ScriptingExceptionIsAnError")]
        [TestCase(1, "E", TestName = "PlainErrorFlagIsAnError")]
        [TestCase(512, "W", TestName = "ScriptingWarningIsAWarning")]
        [TestCase(4096, "W", TestName = "CompileWarningIsAWarning")]
        [TestCase(1024, "L", TestName = "ScriptingLogIsALog")]
        [TestCase(4, "L", TestName = "PlainLogFlagIsALog")]
        [TestCase(1024 | 16384, "L", TestName = "LineNumberFlagDoesNotChangeTheKind")]

        // Bit 13 is kStickyLog, an entry the Console keeps when the user clears it by hand. The
        // compilation pipeline sets it beside kScriptingWarning for asmdef and versionDefines
        // problems, and beside kScriptingError for compiler errors, so it decides nothing on its
        // own and the flag beside it has to.
        [TestCase(512 | 8192, "W", TestName = "AStickyCompilationWarningStaysAWarning")]
        [TestCase(256 | 8192, "E", TestName = "AStickyCompilationErrorStaysAnError")]
        [TestCase(8192, "L", TestName = "TheStickyFlagAloneIsNotAnError")]
        public void ModeFlagsClassifyLikeTheConsole(int mode, string expected)
        {
            Assert.That(LogReader.GetTypeChar(mode), Is.EqualTo(expected));
        }

        /// <summary>
        /// Unity's own kErrorLogFlags, from EditorMonoConsole.h. Anything this set does not carry
        /// is not an error, whatever a flag's name suggests.
        /// </summary>
        [Test]
        public void TheErrorSetMatchesTheEditorsOwn()
        {
            const int unityErrorFlags = 16 | 2 | 1 | 2048 | 256 | 64 | 2097152 | 131072;

            for (var bit = 0; bit < 24; bit++)
            {
                var mode = 1 << bit;
                var unityCallsItAnError = (mode & unityErrorFlags) != 0;

                Assert.That(
                    LogReader.GetTypeChar(mode) == "E",
                    Is.EqualTo(unityCallsItAnError),
                    $"bit {bit} ({mode}) is classified differently from the Editor's own set");
            }
        }

        /// <summary>
        /// The filter compares against four names. One that matches none of them narrows nothing,
        /// so a caller asking for "ERROR" is handed every log line and reads it as the errors.
        /// </summary>
        [TestCase("ERROR")]
        [TestCase("errors")]
        [TestCase("fatal")]
        public void ASeverityTheFilterDoesNotKnowIsRefused(string severity)
        {
            var thrown = Assert.Throws<McpToolException>(
                () => LogReader.ReadLogs(ToolArgs.Of(("type", severity))));

            Assert.That(thrown.Code, Is.EqualTo("invalid_params"));
            Assert.That(thrown.Message, Does.Contain("all, error, warning or log"));
        }

        /// <summary>
        /// The count beside the list describes the list, not the console behind it.
        /// </summary>
        /// <remarks>
        /// Asking for errors in a console holding thirteen plain logs answered
        /// <c>logs: [], total: 13</c>, which reads as thirteen entries the reply declined to
        /// show. Every other paged tool counts what the request matched, and the page's own
        /// truncated/next describe the filtered list, so this was the one field that ignored the
        /// filter.
        /// </remarks>
        [Test]
        public void TheTotalCountsWhatTheFilterMatchedRatherThanTheWholeConsole()
        {
            UnityEngine.Debug.Log("LogReaderTests: a plain log, so one entry is not an error.");

            var everything = LogReader.ReadLogs(ToolArgs.Of(("type", "all"), ("limit", 1)));
            var errorsOnly = LogReader.ReadLogs(ToolArgs.Of(("type", "error"), ("limit", 1)));

            Assert.That(everything["total"].Value<int>(), Is.GreaterThan(errorsOnly["total"].Value<int>()),
                "the entry just written matches 'all' and not 'error', so the two totals differ");

            Assert.That(everything["inConsole"].Value<int>(), Is.EqualTo(errorsOnly["inConsole"].Value<int>()),
                "what the console holds is the same for both reads");
        }
    }
}
