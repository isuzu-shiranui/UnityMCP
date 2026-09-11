namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Classifies whether a command or endpoint can be safely retried on connection failure.
    /// </summary>
    public enum McpIdempotency
    {
        /// <summary>
        /// A read-only operation that may be retried after a connection failure. The tool catalog
        /// publishes both readOnlyHint and idempotentHint for this value. File writes and arbitrary
        /// property getters must use Unsafe, even if a particular call happens to be repeatable.
        /// </summary>
        Safe,

        /// <summary>
        /// The operation may have side effects and must not be retried automatically
        /// after a post-handshake connection failure.
        /// </summary>
        Unsafe
    }
}
