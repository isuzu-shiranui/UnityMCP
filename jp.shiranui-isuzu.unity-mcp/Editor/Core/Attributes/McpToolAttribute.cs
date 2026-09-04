using System;

namespace UnityMCP.Editor.Core.Attributes
{
    /// <summary>
    /// Marks a static method as an MCP tool. The method becomes discoverable by
    /// <see cref="ToolCatalog"/>, which derives the tool's JSON Schema from the
    /// method signature. The Editor is the only place a tool is defined, so there is
    /// no second definition on the client to keep in step.
    /// </summary>
    /// <remarks>
    /// Modelled on <c>[CliCommand]</c> from Unity's <c>com.unity.pipeline</c> package.
    /// The defaults are deliberately conservative: a tool is assumed to mutate state
    /// (<see cref="McpIdempotency.Unsafe"/>) and to require the Editor main thread
    /// unless it says otherwise, because getting either wrong silently is worse than
    /// being slow.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class McpToolAttribute : Attribute
    {
        /// <summary>
        /// The tool name exposed over MCP, e.g. <c>console_get_logs</c>.
        /// Must match <c>^[a-z][a-z0-9_]{0,63}$</c> — the MCP tool-name grammar
        /// forbids dots, so a <c>prefix.action</c> name has to be spelled <c>prefix_action</c>.
        /// <see cref="ToolCatalog"/> rejects names that do not conform.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Human-readable description surfaced to the model in <c>tools/list</c>.
        /// This is the model's only cue for when to reach for the tool, so it should
        /// describe the situation it is for, not just restate the name.
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Whether the tool may be retried automatically after a connection failure.
        /// Defaults to <see cref="McpIdempotency.Unsafe"/>: retrying a mutation twice
        /// is a real bug, retrying a query twice is free, so the unsafe default only
        /// costs read-only tools an explicit annotation.
        /// </summary>
        public McpIdempotency Idempotency { get; set; } = McpIdempotency.Unsafe;

        /// <summary>
        /// Whether the tool must run on the Editor main thread. Defaults to <c>true</c>.
        /// <para>
        /// Setting this to <c>false</c> is what keeps a tool answerable while the Editor
        /// is blocked in a "Hold on" modal: off-thread tools never enter the main-thread
        /// queue, so they still respond when the main thread is wedged. Only set it for
        /// work that touches no Unity API — reading a file, reporting server state.
        /// </para>
        /// </summary>
        public bool MainThread { get; set; } = true;

        /// <summary>
        /// Marks the tool as destructive. Destructive tools refuse to run unless the
        /// caller passes <c>confirm: true</c>, and support <c>dry_run: true</c> to report
        /// what they would touch. Both parameters are injected into the schema by
        /// <see cref="ToolCatalog"/>; the method itself does not declare them.
        /// </summary>
        public bool Destructive { get; set; }

        /// <summary>
        /// When set, the invocation is wrapped in an Undo group with this name so the
        /// whole tool call collapses into a single Ctrl+Z step for the human at the
        /// keyboard. Leave null for tools that make no undoable scene/asset changes.
        /// </summary>
        public string UndoGroup { get; set; }

        /// <summary>
        /// Worked examples of a complete argument object, each written as a JSON object literal.
        /// They are published as the input schema's <c>examples</c>, which is where a model looks
        /// to see the shape rather than infer it.
        /// </summary>
        /// <remarks>
        /// Worth the effort only where the shape is not obvious from the parameter list: nested
        /// values, or arguments that constrain each other so that some combinations are wrong.
        /// A tool taking three strings gains nothing. Each entry is parsed when the catalogue is
        /// built and a malformed one fails discovery, so a broken example cannot reach a client.
        /// </remarks>
        public string[] Examples { get; set; }

        /// <summary>
        /// Keeps this tool's definition loaded rather than deferred behind tool search.
        /// </summary>
        /// <remarks>
        /// Clients with more tools than fit comfortably in context defer tool definitions and
        /// discover them by searching, which costs a round trip before the first call. For the
        /// handful of tools that are wanted on almost every turn — reading the console, browsing
        /// the hierarchy — paying that repeatedly is worse than the context they occupy. Set this
        /// sparingly: marking everything defeats the mechanism and puts the whole catalogue back
        /// in the prompt. Surfaced as <c>anthropic/alwaysLoad</c> in the tool's <c>_meta</c>.
        /// </remarks>
        public bool AlwaysLoad { get; set; }

        /// <summary>
        /// Raises the size at which this tool's text result is spilled to a file instead of
        /// being returned inline, in characters. Zero leaves the client's default in place.
        /// </summary>
        /// <remarks>
        /// For tools whose useful answer is genuinely large — a deep hierarchy, a long log tail —
        /// the default truncation loses the part the caller asked for. This does nothing for image
        /// results, so it will not help a screenshot. Surfaced as
        /// <c>anthropic/maxResultSizeChars</c> in the tool's <c>_meta</c>.
        /// </remarks>
        public int MaxResultSizeChars { get; set; }

        /// <summary>
        /// The group a client can ask for to receive a subset of the tools: one of
        /// <c>diagnostics</c>, <c>authoring</c>, <c>rendering</c>, <c>timeline</c>, <c>build</c>,
        /// <c>code</c>, <c>input</c>. Null derives it from the name prefix, which is right for
        /// almost every tool.
        /// </summary>
        public string Group { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="McpToolAttribute"/> class.
        /// </summary>
        /// <param name="name">Tool name; see <see cref="Name"/> for the accepted grammar.</param>
        /// <param name="description">Description surfaced to the model.</param>
        public McpToolAttribute(string name, string description)
        {
            this.Name = name;
            this.Description = description;
        }
    }
}
