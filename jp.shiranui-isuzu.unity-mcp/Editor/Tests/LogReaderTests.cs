using NUnit.Framework;
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
    }
}
