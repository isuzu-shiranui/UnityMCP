using System;

namespace UnityMCP.Editor.Core.Attributes
{
    /// <summary>
    /// Describes a single parameter of an <see cref="McpToolAttribute"/> method.
    /// <see cref="ToolCatalog"/> turns the parameter's CLR type, default value and this
    /// description into one JSON Schema property.
    /// </summary>
    /// <remarks>
    /// Modelled on <c>[CliArg]</c> from Unity's <c>com.unity.pipeline</c> package.
    /// The attribute is optional: an undecorated parameter still becomes a schema
    /// property, just without a description. Decorating it is strongly preferred —
    /// the description is what stops the model guessing at the argument's meaning.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    public sealed class McpArgAttribute : Attribute
    {
        /// <summary>
        /// JSON property name. Null means "use the C# parameter name verbatim", which
        /// is the common case; set it only when the wire name must differ from the
        /// C# identifier (e.g. a name that is a C# keyword).
        /// </summary>
        public string Name { get; }

        /// <summary>Description of the argument, surfaced in the tool's JSON Schema.</summary>
        public string Description { get; }

        /// <summary>
        /// Forces the argument into the schema's <c>required</c> list.
        /// <para>
        /// Leave this alone in normal use. <see cref="ToolCatalog"/> already infers
        /// required-ness from the method signature: a parameter with no default value
        /// is required, one with a default is optional. Set it explicitly only to make
        /// a defaulted parameter mandatory on the wire.
        /// </para>
        /// </summary>
        public bool Required { get; set; }

        /// <summary>
        /// Initializes the attribute using the C# parameter name as the wire name.
        /// </summary>
        /// <param name="description">Description of the argument.</param>
        public McpArgAttribute(string description)
        {
            this.Description = description;
        }

        /// <summary>
        /// Initializes the attribute with an explicit wire name.
        /// </summary>
        /// <param name="name">JSON property name to use instead of the C# parameter name.</param>
        /// <param name="description">Description of the argument.</param>
        public McpArgAttribute(string name, string description)
        {
            this.Name = name;
            this.Description = description;
        }
    }
}
