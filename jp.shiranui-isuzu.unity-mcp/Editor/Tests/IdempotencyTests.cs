using NUnit.Framework;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// The two idempotency classes must stay distinct: the retry policy on the client and the
    /// annotations on the MCP entry both branch on them.
    /// </summary>
    [TestFixture]
    internal sealed class IdempotencyTests
    {
        [Test]
        public void McpIdempotency_EnumReachable()
        {
            Assert.AreNotEqual(McpIdempotency.Safe, McpIdempotency.Unsafe);
        }
    }
}
