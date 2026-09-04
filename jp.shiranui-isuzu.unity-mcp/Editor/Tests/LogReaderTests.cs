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
        public void ModeFlagsClassifyLikeTheConsole(int mode, string expected)
        {
            Assert.That(LogReader.GetTypeChar(mode), Is.EqualTo(expected));
        }
    }
}
