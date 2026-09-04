namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Classifies whether a command or endpoint can be safely retried on connection failure.
    /// </summary>
    public enum McpIdempotency
    {
        /// <summary>
        /// Repeating the operation leaves the project as one call would, so a client may retry it
        /// after a connection failure. That is weaker than having no side effect: a tool that
        /// writes to a path the caller named, or reads a property whose getter instantiates, is
        /// still Safe as long as the second call lands where the first one did.
        /// </summary>
        Safe,

        /// <summary>
        /// The operation may have side effects and must not be retried automatically
        /// after a post-handshake connection failure.
        /// </summary>
        Unsafe
    }
}
