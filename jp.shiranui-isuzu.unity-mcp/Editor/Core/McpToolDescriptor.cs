using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

using Newtonsoft.Json;
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

        /// <summary>Whether this tool stays loaded rather than being deferred behind tool search.</summary>
        public bool AlwaysLoad { get; }

        /// <summary>Client-side inline result limit in characters, or zero for the default.</summary>
        public int MaxResultSizeChars { get; }

        /// <summary>The group a client can filter by; see <see cref="McpToolGroups"/>.</summary>
        public string Group { get; }

        /// <summary>The static method to invoke, or null when <see cref="Direct"/> carries the body.</summary>
        public MethodInfo Method { get; }

        /// <summary>
        /// The tool body for a tool that has no backing method; null when <see cref="Method"/> does.
        /// Receives the raw arguments and returns the result object, so nothing here goes through
        /// argument binding.
        /// </summary>
        public Func<JObject, JObject> Direct { get; }

        /// <summary>
        /// Where the tool came from, as named in a registration error: the declaring
        /// <c>Type.Method</c> for an attribute tool, whatever the caller supplied otherwise.
        /// </summary>
        public string Origin { get; }

        /// <summary>Bound parameters, in the order <see cref="Method"/> declares them.</summary>
        public IReadOnlyList<McpToolParameter> Parameters { get; }

        /// <summary>
        /// JSON Schema for the tool's arguments, generated from the method signature.
        /// This is what the TypeScript server forwards verbatim into <c>tools/list</c>,
        /// which is why there is no hand-written schema anywhere in the TS package.
        /// </summary>
        public JObject InputSchema { get; }

        private string cachedCatalogEntryJson;
        private string cachedMcpEntryJson;

        private readonly Lazy<ToolBindPlan> lazyBindPlan;

        /// <summary>
        /// How this tool's arguments are read and how it is called, resolved once.
        /// </summary>
        /// <remarks>
        /// Built on first call rather than at discovery: every domain reload rebuilds the
        /// catalogue, and compiling 69 invokers for the handful a session actually uses would
        /// pay for itself only in the sessions that use them all. Descriptors are shared across
        /// the HTTP worker threads, so construction is single-shot.
        /// </remarks>
        public ToolBindPlan BindPlan => this.lazyBindPlan.Value;

        /// <summary>
        /// Whether calls go through <see cref="MethodInfo.Invoke"/> because the compiled
        /// invoker could not be built for this signature. Always false for a direct tool,
        /// which has no signature to compile.
        /// </summary>
        public bool UsesReflectionFallback => this.Direct == null && this.BindPlan.UsesReflectionFallback;

        /// <summary>
        /// <see cref="ToCatalogEntry"/> rendered once as text. A descriptor never changes after
        /// discovery, and the catalogue is listed on every client connect, so re-rendering 69
        /// schemas per request only costs allocation.
        /// </summary>
        public string CatalogEntryJson => this.cachedCatalogEntryJson ??= this.ToCatalogEntry().ToString(Formatting.None);

        /// <summary><see cref="ToMcpToolEntry"/> rendered once as text.</summary>
        public string McpEntryJson => this.cachedMcpEntryJson ??= this.ToMcpToolEntry().ToString(Formatting.None);

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
            this.AlwaysLoad = attribute.AlwaysLoad;
            this.MaxResultSizeChars = attribute.MaxResultSizeChars;
            this.Group = string.IsNullOrEmpty(attribute.Group) ? McpToolGroups.Derive(attribute.Name) : attribute.Group;
            this.Method = method;
            this.Origin = $"{method.DeclaringType?.FullName}.{method.Name}";
            this.Parameters = parameters;
            this.InputSchema = inputSchema;
            this.lazyBindPlan = new Lazy<ToolBindPlan>(
                () => ToolInvoker.CreateBindPlan(this), LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>
        /// A tool whose body is a delegate rather than a discovered method.
        /// </summary>
        /// <remarks>
        /// The bind plan is empty and must stay unused: there is no method signature to bind
        /// against, so <paramref name="direct"/> reads the arguments itself.
        /// </remarks>
        public McpToolDescriptor(
            string name,
            string description,
            JObject inputSchema,
            McpIdempotency idempotency,
            bool mainThread,
            bool destructive,
            string undoGroup,
            string group,
            bool alwaysLoad,
            int maxResultSizeChars,
            string origin,
            Func<JObject, JObject> direct)
        {
            this.Name = name;
            this.Description = description;
            this.Idempotency = idempotency;
            this.MainThread = mainThread;
            this.Destructive = destructive;
            this.UndoGroup = undoGroup;
            this.AlwaysLoad = alwaysLoad;
            this.MaxResultSizeChars = maxResultSizeChars;
            this.Group = string.IsNullOrEmpty(group) ? McpToolGroups.Derive(name) : group;
            this.Method = null;
            this.Origin = origin;
            this.Direct = direct;
            this.Parameters = Array.Empty<McpToolParameter>();
            this.InputSchema = inputSchema;
            this.lazyBindPlan = new Lazy<ToolBindPlan>(
                () => new ToolBindPlan(Array.Empty<McpParameterBinding>(), compiled: null, compileError: null),
                LazyThreadSafetyMode.ExecutionAndPublication);
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
                ["group"] = this.Group,
            };

            var meta = this.BuildMeta();
            if (meta != null)
            {
                entry["_meta"] = meta;
            }

            return entry;
        }

        /// <summary>
        /// Renders the descriptor as one entry of an MCP <c>tools/list</c> result.
        /// </summary>
        /// <remarks>
        /// Annotations follow the MCP tool-annotation vocabulary: clients that honour them run
        /// <c>readOnlyHint</c> tools in parallel and gate <c>destructiveHint</c> ones behind
        /// approval. <c>openWorldHint</c> is false because every tool acts on the local Editor.
        /// </remarks>
        public JObject ToMcpToolEntry()
        {
            var safe = this.Idempotency == McpIdempotency.Safe;

            var entry = new JObject
            {
                ["name"] = this.Name,
                ["description"] = this.Description,
                ["inputSchema"] = (JObject)this.InputSchema.DeepClone(),
                ["group"] = this.Group,
                ["annotations"] = new JObject
                {
                    ["readOnlyHint"] = safe,
                    ["idempotentHint"] = safe,
                    ["destructiveHint"] = this.Destructive,
                    ["openWorldHint"] = false,
                },
            };

            var meta = this.BuildMeta();
            if (meta != null)
            {
                entry["_meta"] = meta;
            }

            return entry;
        }

        /// <summary>
        /// Client hints carried in the tool's own <c>_meta</c> so they travel with the definition.
        /// Null when the tool sets none: an empty <c>_meta</c> would be noise on every other tool.
        /// </summary>
        private JObject BuildMeta()
        {
            var meta = new JObject();

            if (this.AlwaysLoad)
            {
                meta["anthropic/alwaysLoad"] = true;
            }

            if (this.MaxResultSizeChars > 0)
            {
                meta["anthropic/maxResultSizeChars"] = this.MaxResultSizeChars;
            }

            return meta.HasValues ? meta : null;
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

    /// <summary>
    /// What a parameter's declared type resolves to, so a call is a jump table on a byte
    /// rather than a chain of <see cref="Type"/> comparisons.
    /// </summary>
    internal enum BindKind : byte
    {
        /// <summary>Anything with no dedicated conversion; deserialized by Newtonsoft.</summary>
        Object = 0,
        JsonToken,
        String,
        Boolean,
        Enum,
        Byte,
        SByte,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Single,
        Double,
        Decimal,
        Array,
        List,
    }

    /// <summary>
    /// Everything binding one argument needs, resolved once so that a call performs no type
    /// inspection: the kind to convert to, the enum's names and values, the element binding of
    /// an array or list, and the error text for a refusal.
    /// </summary>
    internal sealed class McpParameterBinding
    {
        public McpParameterBinding(
            string toolName,
            string name,
            Type declaredType,
            bool required,
            object defaultValue,
            string friendlyTypeName = null)
        {
            this.ToolName = toolName;
            this.Name = name;
            this.DeclaredType = declaredType;
            this.Underlying = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
            this.Required = required;
            this.DefaultValue = defaultValue;
            this.Kind = KindOf(this.Underlying);
            this.MissingArgumentMessage = $"'{toolName}' requires argument '{name}'.";
            this.FriendlyTypeName = friendlyTypeName ??
                                    (this.Underlying == declaredType ? declaredType.Name : $"{this.Underlying.Name}?");

            switch (this.Kind)
            {
                case BindKind.Enum:
                    this.EnumNames = Enum.GetNames(this.Underlying);
                    this.EnumValues = Enum.GetValues(this.Underlying);
                    this.EnumNamesJoined = string.Join(", ", this.EnumNames);
                    break;
                case BindKind.Array:
                    this.Element = this.ElementBinding(toolName, this.Underlying.GetElementType());
                    break;
                case BindKind.List:
                    this.Element = this.ElementBinding(toolName, this.Underlying.GetGenericArguments()[0]);
                    break;
            }
        }

        /// <summary>The tool this argument belongs to, as named in an error.</summary>
        public string ToolName { get; }

        /// <summary>JSON property name to read.</summary>
        public string Name { get; }

        /// <summary>The parameter's type as the method declares it.</summary>
        public Type DeclaredType { get; }

        /// <summary><see cref="DeclaredType"/> with <see cref="Nullable{T}"/> stripped.</summary>
        public Type Underlying { get; }

        /// <summary>Whether the caller must supply this argument.</summary>
        public bool Required { get; }

        /// <summary>Value bound when the caller omits an optional argument.</summary>
        public object DefaultValue { get; }

        public BindKind Kind { get; }

        /// <summary>Enum member names, in <see cref="EnumValues"/> order.</summary>
        public string[] EnumNames { get; }

        /// <summary>Enum members as a typed array, so matching a name allocates nothing.</summary>
        public Array EnumValues { get; }

        /// <summary>Enum names as they appear in the error listing the valid ones.</summary>
        public string EnumNamesJoined { get; }

        /// <summary>How the elements of an array or list parameter bind.</summary>
        public McpParameterBinding Element { get; }

        /// <summary>
        /// <c>Func&lt;JToken, McpParameterBinding, T&gt;</c> for an element binding, set while
        /// the owning descriptor is compiled.
        /// </summary>
        public Delegate Coercer { get; set; }

        public string MissingArgumentMessage { get; }

        /// <summary>The declared type as the caller sees it named in an error.</summary>
        public string FriendlyTypeName { get; }

        /// <summary>
        /// The refusal for a value that could not be converted.
        /// </summary>
        public McpToolException CannotRead(Exception error)
        {
            return new McpToolException(
                "invalid_params",
                $"Argument '{this.Name}' of '{this.ToolName}' could not be read as " +
                $"{this.FriendlyTypeName}: {error.Message}");
        }

        /// <summary>
        /// An element binds like the parameter that holds it, and reports failures under that
        /// parameter's name and declared type.
        /// </summary>
        private McpParameterBinding ElementBinding(string toolName, Type elementType)
        {
            return new McpParameterBinding(toolName, this.Name, elementType, true, null, this.FriendlyTypeName);
        }

        private static BindKind KindOf(Type underlying)
        {
            if (typeof(JToken).IsAssignableFrom(underlying))
            {
                return BindKind.JsonToken;
            }

            if (underlying == typeof(string))
            {
                return BindKind.String;
            }

            if (underlying == typeof(bool))
            {
                return BindKind.Boolean;
            }

            if (underlying.IsEnum)
            {
                return BindKind.Enum;
            }

            switch (Type.GetTypeCode(underlying))
            {
                case TypeCode.Byte:
                    return BindKind.Byte;
                case TypeCode.SByte:
                    return BindKind.SByte;
                case TypeCode.Int16:
                    return BindKind.Int16;
                case TypeCode.UInt16:
                    return BindKind.UInt16;
                case TypeCode.Int32:
                    return BindKind.Int32;
                case TypeCode.UInt32:
                    return BindKind.UInt32;
                case TypeCode.Int64:
                    return BindKind.Int64;
                case TypeCode.UInt64:
                    return BindKind.UInt64;
                case TypeCode.Single:
                    return BindKind.Single;
                case TypeCode.Double:
                    return BindKind.Double;
                case TypeCode.Decimal:
                    return BindKind.Decimal;
            }

            if (underlying.IsArray)
            {
                return BindKind.Array;
            }

            if (underlying.IsGenericType)
            {
                var definition = underlying.GetGenericTypeDefinition();

                if (definition == typeof(List<>) || definition == typeof(IList<>) ||
                    definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyList<>))
                {
                    return BindKind.List;
                }
            }

            return BindKind.Object;
        }
    }

    /// <summary>
    /// A descriptor's resolved bindings and its compiled invoker.
    /// </summary>
    internal sealed class ToolBindPlan
    {
        public ToolBindPlan(McpParameterBinding[] parameters, Func<JObject, object> compiled, string compileError)
        {
            this.Parameters = parameters;
            this.Compiled = compiled;
            this.CompileError = compileError;
        }

        public McpParameterBinding[] Parameters { get; }

        /// <summary>Binds and calls the tool, or null when the signature would not compile.</summary>
        public Func<JObject, object> Compiled { get; }

        /// <summary>Why <see cref="Compiled"/> is null, for a test to report.</summary>
        public string CompileError { get; }

        public bool UsesReflectionFallback => this.Compiled == null;
    }
}
