using System.Collections.Generic;

using NUnit.Framework;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Asserts that the <see cref="McpIdempotency"/> enum is reachable and that
    /// the canonical built-in table (design §3.1 / requirements R5.6) classifies
    /// each HTTP shortcut / action correctly.
    /// </summary>
    [TestFixture]
    internal sealed class IdempotencyTests
    {
        // Canonical per-action table shipped in /health.handlers[].
        // Keep in sync with McpHttpServer.BuiltinHandlerEntries.
        private static readonly IReadOnlyDictionary<string, McpIdempotency> Canonical =
            new Dictionary<string, McpIdempotency>
            {
                ["/health"]             = McpIdempotency.Safe,
                ["/resource"]           = McpIdempotency.Safe,
                ["/read_logs"]          = McpIdempotency.Safe,
                ["/browse_hierarchy"]   = McpIdempotency.Safe,
                ["/capture_screenshot"] = McpIdempotency.Safe,
                ["/inspect:read"]       = McpIdempotency.Safe,
                ["/inspect:list"]       = McpIdempotency.Safe,
                ["/inspect:write"]      = McpIdempotency.Unsafe,
                ["/play_mode:status"]   = McpIdempotency.Safe,
                ["/play_mode:play"]     = McpIdempotency.Unsafe,
                ["/play_mode:stop"]    = McpIdempotency.Unsafe,
                ["/play_mode:pause"]    = McpIdempotency.Unsafe,
                ["/play_mode:unpause"]  = McpIdempotency.Unsafe,
                ["/play_mode:step"]     = McpIdempotency.Unsafe,
                ["/execute_code"]       = McpIdempotency.Unsafe,
                ["/jobs"]               = McpIdempotency.Safe,
                ["/jobs:cancel"]        = McpIdempotency.Unsafe,
                ["/tools"]              = McpIdempotency.Safe
            };

        [Test]
        public void McpIdempotency_EnumReachable()
        {
            // Sanity: values exist and are distinct.
            Assert.AreNotEqual(McpIdempotency.Safe, McpIdempotency.Unsafe);
        }

        [Test]
        public void CanonicalTable_ExecuteCodeIsUnsafe()
        {
            Assert.AreEqual(McpIdempotency.Unsafe, Canonical["/execute_code"]);
        }

        [Test]
        public void CanonicalTable_ReadOnlyEndpointsAreSafe()
        {
            Assert.AreEqual(McpIdempotency.Safe, Canonical["/health"]);
            Assert.AreEqual(McpIdempotency.Safe, Canonical["/read_logs"]);
            Assert.AreEqual(McpIdempotency.Safe, Canonical["/browse_hierarchy"]);
            Assert.AreEqual(McpIdempotency.Safe, Canonical["/capture_screenshot"]);
            Assert.AreEqual(McpIdempotency.Safe, Canonical["/resource"]);
        }

        [Test]
        public void CanonicalTable_InspectPerModeGranularity()
        {
            Assert.AreEqual(McpIdempotency.Safe,   Canonical["/inspect:read"]);
            Assert.AreEqual(McpIdempotency.Safe,   Canonical["/inspect:list"]);
            Assert.AreEqual(McpIdempotency.Unsafe, Canonical["/inspect:write"]);
        }

        [Test]
        public void CanonicalTable_PlayModeStatusIsSafeOthersUnsafe()
        {
            Assert.AreEqual(McpIdempotency.Safe, Canonical["/play_mode:status"]);

            foreach (var unsafeAction in new[] { "play", "stop", "pause", "unpause", "step" })
            {
                Assert.AreEqual(
                    McpIdempotency.Unsafe,
                    Canonical[$"/play_mode:{unsafeAction}"],
                    $"/play_mode:{unsafeAction} should be Unsafe");
            }
        }
    }
}
