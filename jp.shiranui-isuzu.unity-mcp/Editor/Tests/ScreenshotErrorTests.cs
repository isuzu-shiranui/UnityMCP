using NUnit.Framework;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Handlers;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// A capture refusal has to reach the caller as its own code. Reported as a generic failure it
    /// reads as a fault in the Editor, and a client that retries safe calls repeats it.
    /// </summary>
    [TestFixture]
    internal sealed class ScreenshotErrorTests
    {
        [Test]
        public void ACaptureFailureIsAToolExceptionSoItsCodeReachesTheCaller()
        {
            var failure = new McpScreenshotException("window_occluded", "Something is in front.", 409);

            Assert.That(failure, Is.InstanceOf<McpToolException>(),
                "ToolInvoker only preserves the code and status of an McpToolException; anything else becomes tool_failed with a 500.");
            Assert.That(failure.Code, Is.EqualTo("window_occluded"));
            Assert.That(failure.HttpStatus, Is.EqualTo(409));
        }

        [Test]
        public void ARefusalIsNotReportedAsAServerFault()
        {
            var failure = new McpScreenshotException("window_minimized", "It is minimized.", 400);

            Assert.That(failure.HttpStatus, Is.LessThan(500),
                "a condition the caller can correct must not be reported as a server fault");
        }
    }
}
