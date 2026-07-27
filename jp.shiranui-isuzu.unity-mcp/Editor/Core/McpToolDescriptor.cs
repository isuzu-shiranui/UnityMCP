using System.Collections.Generic;
using System.Reflection;

using Newtonsoft.Json.Linq;

using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// One discovered <see cref="McpToolAttribute"/> method, resolved into everything the
    /// server needs at call time: how to describe it (<see cref="InputSchema"/>), where to
    /// run it (<see cref="MainThread"/>) and how to bind its arguments (<see cref="Parameters"/>).
    /// </summary>
    internal sealed class McpToolDescriptor
    {
        /// <summary>Tool name as exposed over MCP.</summary>
        public string Name { get; }

        /// <summary>Description surfaced to the model.</summary>
        public string Description { get; }

        /// <summary>Retry classification, published through <c>/health</c> and <c>/tools</c>.</summary>
        public McpIdempotency Idempotency { get; }

        /// <summary>Whether the call must be marshalled onto the Editor main thread.</summary>
        public bool MainThread { get; }

        /// <summary>Whether the call requires <c>confirm: true</c> and supports <c>dry_run</c>.</summary>
        public bool Destructive { get; }

        /// <summary>Undo group name, or null when the tool makes no undoable changes.</summary>
        public string UndoGroup { get; }

        /// <summary>The static method to invoke.</summary>
        public MethodInfo Method { get; }

        /// <summary>Bound parameters, in the order <see cref="Method"/> declares them.</summary>
        public IReadOnlyList<McpToolParameter> Parameters { get; }

        /// <summary>
        /// JSON Schema for the tool's arguments, generated from the method signature.
        /// This is what the TypeScript server forwards verbatim into <c>tools/list</c>,
        /// which is why there is no hand-written schema anywhere in the TS package.
        /// </summary>
        public JObject InputSchema { get; }

        public McpToolDescriptor(
            McpToolAttribute attribute,
            MethodInfo method,
            IReadOnlyList<McpToolParameter> parameters,
            JObject inputSchema)
        {
            this.Name = attribute.Name;
            this.Description = attribute.Description;
            this.Idempotency = attribute.Idempotency;
            this.MainThread = attribute.MainThread;
            this.Destructive = attribute.Destructive;
            this.UndoGroup = attribute.UndoGroup;
            this.Method = method;
            this.Parameters = parameters;
            this.InputSchema = inputSchema;
        }

        /// <summary>
        /// Renders the descriptor as one entry of the <c>/tools</c> catalog.
        /// </summary>
        public JObject ToCatalogEntry()
        {
            var entry = new JObject
            {
                ["name"] = this.Name,
                ["description"] = this.Description,
                ["inputSchema"] = (JObject)this.InputSchema.DeepClone(),
                ["idempotency"] = this.Idempotency == McpIdempotency.Safe ? "safe" : "unsafe",
                ["mainThread"] = this.MainThread,
                ["destructive"] = this.Destructive,
            };

            return entry;
        }
    }

    /// <summary>
    /// One parameter of a tool method, paired with the schema-relevant facts
    /// <see cref="ToolInvoker"/> needs to bind an incoming JSON value to it.
    /// </summary>
    internal sealed class McpToolParameter
    {
        /// <summary>JSON property name (may differ from <see cref="ParameterInfo.Name"/>).</summary>
        public string Name { get; }

        /// <summary>Reflection info for the underlying parameter.</summary>
        public ParameterInfo Parameter { get; }

        /// <summary>Whether the caller must supply this argument.</summary>
        public bool Required { get; }

        /// <summary>
        /// Value to use when the caller omits an optional argument — the parameter's
        /// compile-time default, or <c>null</c>/<c>default(T)</c> when it has none.
        /// </summary>
        public object DefaultValue { get; }

        public McpToolParameter(string name, ParameterInfo parameter, bool required, object defaultValue)
        {
            this.Name = name;
            this.Parameter = parameter;
            this.Required = required;
            this.DefaultValue = defaultValue;
        }
    }
}
