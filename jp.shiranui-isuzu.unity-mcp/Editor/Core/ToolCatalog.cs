using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using Newtonsoft.Json.Linq;

using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Discovers <see cref="McpToolAttribute"/> methods across the loaded assemblies and
    /// derives each tool's JSON Schema from its method signature.
    /// <para>
    /// Deriving the schema is what keeps a tool defined in one place. A schema written by
    /// hand on the client is a second definition of the same tool and is free to drift from
    /// the method it describes — advertising a tool nothing implements, or routing to an
    /// endpoint the Editor never registered — and neither failure shows up until a call is
    /// made.
    /// </para>
    /// </summary>
    internal sealed class ToolCatalog
    {
        /// <summary>
        /// MCP tool-name grammar. Dots are not permitted, so a <c>prefix.action</c> name
        /// has to be spelled <c>prefix_action</c> here.
        /// </summary>
        private static readonly Regex NamePattern = new(@"^[a-z][a-z0-9_]{0,63}$", RegexOptions.Compiled);

        /// <summary>
        /// Parameter names the invoker injects itself. A tool method that declares one of
        /// these would be shadowed at call time, so the catalog rejects it at discovery.
        /// </summary>
        private static readonly HashSet<string> ReservedParameterNames = new(StringComparer.Ordinal)
        {
            "confirm",
            "dry_run",
            "target",
        };

        private readonly Dictionary<string, McpToolDescriptor> tools = new(StringComparer.Ordinal);

        /// <summary>Whether <paramref name="name"/> satisfies the MCP tool-name grammar.</summary>
        internal static bool IsValidName(string name)
        {
            return !string.IsNullOrEmpty(name) && NamePattern.IsMatch(name);
        }

        /// <summary>Whether the invoker injects an argument of this name itself.</summary>
        internal static bool IsReservedParameterName(string name)
        {
            return ReservedParameterNames.Contains(name);
        }

        // Rendered responses, keyed by shape and group set. A catalog never changes after it is
        // built (a refresh builds a new one), so nothing here ever has to be invalidated.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> renderedUtf8 =
            new(StringComparer.Ordinal);

        /// <summary>All discovered tools, ordered by name.</summary>
        public IEnumerable<McpToolDescriptor> Tools => this.tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal);

        /// <summary>Number of successfully registered tools.</summary>
        public int Count => this.tools.Count;

        /// <summary>
        /// Problems found during discovery — duplicate names, bad grammar, non-static
        /// methods. These are surfaced rather than swallowed: a tool that silently fails
        /// to register looks identical to a tool that was never written.
        /// </summary>
        public IReadOnlyList<string> Errors { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// Scans the current AppDomain and builds the catalog.
        /// </summary>
        /// <param name="extra">
        /// Tools that have no backing method, registered after the discovered ones so an
        /// attribute tool always wins a name collision.
        /// </param>
        public static ToolCatalog Build(IReadOnlyList<McpToolDescriptor> extra = null)
        {
            return Build(extra == null ? null : (Func<ToolCatalog, List<string>, IReadOnlyList<McpToolDescriptor>>)((_, __) => extra));
        }

        /// <summary>
        /// Scans the current AppDomain and builds the catalog, asking <paramref name="defined"/>
        /// for the method-less tools once the attribute tools are registered.
        /// </summary>
        /// <param name="defined">
        /// Receives the catalog holding only the attribute tools, and the shared error list, and
        /// returns the descriptors to add. Definitions that chain or shadow attribute tools need
        /// to see them before they can be validated, and scanning the AppDomain twice to give them
        /// a finished catalog would double the cost of every rebuild.
        /// </param>
        public static ToolCatalog Build(Func<ToolCatalog, List<string>, IReadOnlyList<McpToolDescriptor>> defined)
        {
            var errors = new List<string>();
            var types = new List<Type>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (ShouldSkipAssembly(assembly))
                {
                    continue;
                }

                try
                {
                    types.AddRange(assembly.GetTypes());
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // A partially loadable assembly still yields its resolvable types.
                    types.AddRange(ex.Types.Where(t => t != null));
                }
                catch (Exception ex)
                {
                    errors.Add($"Failed to scan assembly {assembly.GetName().Name}: {ex.Message}");
                }
            }

            return BuildFromTypes(types.Where(t => !IsTestFixtureType(t)), errors, defined);
        }

        /// <summary>
        /// True when the type is an NUnit fixture or is nested inside one.
        /// <para>
        /// Fixtures deliberately declare malformed tools to prove the catalog rejects them.
        /// Those must never reach the live catalog or every domain reload would log their
        /// rejection as a real authoring error. Matching on the attribute's name rather than
        /// its type keeps NUnit out of the runtime assembly's references, and unlike an
        /// assembly-name check it holds however the test assembly is named.
        /// </para>
        /// </summary>
        private static bool IsTestFixtureType(Type type)
        {
            for (var current = type; current != null; current = current.DeclaringType)
            {
                foreach (var attribute in current.GetCustomAttributes(false))
                {
                    if (attribute.GetType().Name == "TestFixtureAttribute")
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Builds a catalog from an explicit type list.
        /// <para>
        /// Tests use this so their fixture tools are never visible to <see cref="Build"/>'s
        /// AppDomain scan — otherwise every test tool would show up in the live
        /// <c>/tools</c> catalog whenever the test assembly is loaded.
        /// </para>
        /// </summary>
        public static ToolCatalog BuildFromTypes(
            IEnumerable<Type> types,
            List<string> errors = null,
            IReadOnlyList<McpToolDescriptor> extra = null)
        {
            return BuildFromTypes(
                types,
                errors,
                extra == null ? null : (Func<ToolCatalog, List<string>, IReadOnlyList<McpToolDescriptor>>)((_, __) => extra));
        }

        /// <summary>
        /// <see cref="BuildFromTypes(IEnumerable{Type}, List{string}, IReadOnlyList{McpToolDescriptor})"/>
        /// with the method-less tools produced against the attribute tools; see
        /// <see cref="Build(Func{ToolCatalog, List{string}, IReadOnlyList{McpToolDescriptor}})"/>.
        /// </summary>
        public static ToolCatalog BuildFromTypes(
            IEnumerable<Type> types,
            List<string> errors,
            Func<ToolCatalog, List<string>, IReadOnlyList<McpToolDescriptor>> defined)
        {
            var catalog = new ToolCatalog();
            errors ??= new List<string>();

            foreach (var type in types)
            {
                // Instance methods are scanned too, purely so that an attribute placed on
                // one produces an error instead of vanishing — a tool that silently fails
                // to register looks identical to a tool that was never written.
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                           BindingFlags.Static | BindingFlags.Instance |
                                           BindingFlags.DeclaredOnly;

                foreach (var method in type.GetMethods(flags))
                {
                    var attribute = method.GetCustomAttribute<McpToolAttribute>();
                    if (attribute == null)
                    {
                        continue;
                    }

                    if (!method.IsStatic)
                    {
                        errors.Add(
                            $"[McpTool] on {type.FullName}.{method.Name} is ignored: tool methods must be static.");
                        continue;
                    }

                    catalog.TryRegister(attribute, method, errors);
                }
            }

            var extra = defined?.Invoke(catalog, errors);

            if (extra != null)
            {
                foreach (var descriptor in extra)
                {
                    catalog.TryRegisterDefined(descriptor, errors);
                }
            }

            catalog.Errors = errors;
            return catalog;
        }

        /// <summary>Looks up a tool by name.</summary>
        public bool TryGet(string name, out McpToolDescriptor descriptor)
        {
            return this.tools.TryGetValue(name ?? string.Empty, out descriptor);
        }

        /// <summary>
        /// Renders the whole catalog for the <c>/tools</c> endpoint. The TypeScript server
        /// consumes this at startup and registers MCP tools from it, so this payload is
        /// the entire tool surface.
        /// </summary>
        public JObject ToJson(IReadOnlyList<string> groups = null)
        {
            return new JObject
            {
                ["tools"] = new JArray(this.Select(groups).Select(t => t.ToCatalogEntry()).Cast<object>().ToArray()),
            };
        }

        /// <summary>
        /// The <c>tools</c> array of <see cref="ToJson"/> as text, from each descriptor's cached
        /// rendering. What <c>GET /tools</c> and <c>tools/list</c> write, since building the tree
        /// only to serialise it again is most of what those requests would otherwise cost.
        /// </summary>
        public string ToolsArrayJson(IReadOnlyList<string> groups, bool mcpShape)
        {
            var buffer = new System.Text.StringBuilder(8192);
            buffer.Append('[');

            var first = true;
            foreach (var descriptor in this.Select(groups))
            {
                if (!first)
                {
                    buffer.Append(',');
                }

                first = false;
                buffer.Append(mcpShape ? descriptor.McpEntryJson : descriptor.CatalogEntryJson);
            }

            buffer.Append(']');
            return buffer.ToString();
        }

        /// <summary>
        /// <see cref="ToolsArrayJson"/> as UTF-8, cached per group set, so a request for the
        /// list allocates nothing after the first one for that set.
        /// </summary>
        public byte[] ToolsArrayUtf8(IReadOnlyList<string> groups, bool mcpShape)
        {
            var key = (mcpShape ? "mcp|" : "rest|") + GroupKey(groups);
            return this.renderedUtf8.GetOrAdd(key, _ => System.Text.Encoding.UTF8.GetBytes(this.ToolsArrayJson(groups, mcpShape)));
        }

        /// <summary>
        /// The whole <c>GET /tools</c> success envelope as UTF-8, or null when discovery reported
        /// errors, which the envelope has to carry and which are not worth caching for.
        /// </summary>
        public byte[] CatalogEnvelopeUtf8(IReadOnlyList<string> groups)
        {
            if (this.Errors.Count > 0)
            {
                return null;
            }

            var key = "envelope|" + GroupKey(groups);
            return this.renderedUtf8.GetOrAdd(key, _ =>
            {
                var array = this.ToolsArrayJson(groups, mcpShape: false);
                return System.Text.Encoding.UTF8.GetBytes("{\"status\":\"success\",\"result\":{\"tools\":" + array + "}}");
            });
        }

        private static string GroupKey(IReadOnlyList<string> groups)
        {
            return groups == null || groups.Count == 0
                ? "*"
                : string.Join(",", groups.OrderBy(g => g, StringComparer.Ordinal));
        }

        /// <summary>The tools in the given groups, or every tool when the list is null or empty.</summary>
        public IEnumerable<McpToolDescriptor> Select(IReadOnlyList<string> groups)
        {
            return groups == null || groups.Count == 0
                ? this.Tools
                : this.Tools.Where(t => groups.Contains(t.Group));
        }

        private static bool ShouldSkipAssembly(Assembly assembly)
        {
            var name = assembly.FullName;

            return name.StartsWith("System.", StringComparison.Ordinal) ||
                   name.StartsWith("Unity.", StringComparison.Ordinal) ||
                   name.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
                   name.StartsWith("UnityEditor.", StringComparison.Ordinal) ||
                   name.StartsWith("mscorlib,", StringComparison.Ordinal) ||
                   name.StartsWith("netstandard,", StringComparison.Ordinal) ||
                   name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal);
        }

        private void TryRegister(McpToolAttribute attribute, MethodInfo method, List<string> errors)
        {
            var origin = $"{method.DeclaringType?.FullName}.{method.Name}";

            if (string.IsNullOrEmpty(attribute.Name) || !NamePattern.IsMatch(attribute.Name))
            {
                errors.Add(
                    $"[McpTool] on {origin} has invalid name '{attribute.Name}'. " +
                    "Names must match ^[a-z][a-z0-9_]{0,63}$ (dots are not valid in MCP tool names).");
                return;
            }

            if (string.IsNullOrWhiteSpace(attribute.Description))
            {
                errors.Add($"[McpTool] '{attribute.Name}' on {origin} has an empty description.");
                return;
            }

            if (this.tools.TryGetValue(attribute.Name, out var existing))
            {
                errors.Add(
                    $"Duplicate tool name '{attribute.Name}': {origin} collides with {existing.Origin}.");
                return;
            }

            if (method.IsGenericMethodDefinition)
            {
                errors.Add($"[McpTool] '{attribute.Name}' on {origin} is generic; generic tool methods are not supported.");
                return;
            }

            // Undo grouping calls Undo.IncrementCurrentGroup, which only the main thread may do.
            if (!attribute.MainThread && !string.IsNullOrEmpty(attribute.UndoGroup))
            {
                errors.Add(
                    $"[McpTool] '{attribute.Name}' on {origin} sets UndoGroup with MainThread = false; " +
                    "Undo is main-thread only, so one of the two has to go.");
                return;
            }

            if (!string.IsNullOrEmpty(attribute.Group) && !McpToolGroups.IsKnown(attribute.Group))
            {
                errors.Add(
                    $"[McpTool] '{attribute.Name}' on {origin} names unknown group '{attribute.Group}'. " +
                    $"Known: {string.Join(", ", McpToolGroups.Known)}.");
                return;
            }

            var parameters = new List<McpToolParameter>();
            var properties = new JObject();
            var required = new JArray();

            foreach (var parameterInfo in method.GetParameters())
            {
                if (parameterInfo.IsOut || parameterInfo.ParameterType.IsByRef)
                {
                    errors.Add($"[McpTool] '{attribute.Name}' on {origin} uses ref/out parameter '{parameterInfo.Name}'.");
                    return;
                }

                var arg = parameterInfo.GetCustomAttribute<McpArgAttribute>();
                var wireName = string.IsNullOrEmpty(arg?.Name) ? parameterInfo.Name : arg.Name;

                if (ReservedParameterNames.Contains(wireName))
                {
                    errors.Add(
                        $"[McpTool] '{attribute.Name}' on {origin} declares reserved parameter '{wireName}'. " +
                        "confirm and dry_run are added by the invoker to destructive tools, and target is the client's routing key.");
                    return;
                }

                if (properties.ContainsKey(wireName))
                {
                    errors.Add($"[McpTool] '{attribute.Name}' on {origin} has duplicate parameter name '{wireName}'.");
                    return;
                }

                // A parameter with a compile-time default is optional; one without is required.
                // An explicit [McpArg(Required = true)] can force a defaulted parameter to be mandatory.
                var isRequired = (arg?.Required ?? false) || !parameterInfo.HasDefaultValue;

                var schema = BuildParameterSchema(parameterInfo.ParameterType);
                if (!string.IsNullOrEmpty(arg?.Description))
                {
                    schema["description"] = arg.Description;
                }

                if (parameterInfo.HasDefaultValue && parameterInfo.DefaultValue != null)
                {
                    schema["default"] = JToken.FromObject(parameterInfo.DefaultValue);
                }

                properties[wireName] = schema;
                if (isRequired)
                {
                    required.Add(wireName);
                }

                var defaultValue = parameterInfo.HasDefaultValue
                    ? parameterInfo.DefaultValue
                    : DefaultOf(parameterInfo.ParameterType);

                parameters.Add(new McpToolParameter(wireName, parameterInfo, isRequired, defaultValue));
            }

            if (attribute.Destructive)
            {
                AppendConfirmationProperties(properties);
            }

            var inputSchema = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
            };

            if (required.Count > 0)
            {
                inputSchema["required"] = required;
            }

            if (!TryBuildExamples(attribute, origin, errors, out var examples))
            {
                return;
            }

            if (examples != null)
            {
                inputSchema["examples"] = examples;
            }

            this.tools[attribute.Name] = new McpToolDescriptor(attribute, method, parameters, inputSchema);
        }

        /// <summary>
        /// The two flags the invoker injects into every destructive tool's schema.
        /// </summary>
        internal static void AppendConfirmationProperties(JObject properties)
        {
            properties["confirm"] = new JObject
            {
                ["type"] = "boolean",
                ["description"] = "Must be true to actually perform this destructive operation.",
                ["default"] = false,
            };
            properties["dry_run"] = new JObject
            {
                ["type"] = "boolean",
                ["description"] = "Report what the operation would affect without performing it.",
                ["default"] = false,
            };
        }

        /// <summary>
        /// Registers a tool that carries its own body instead of a discovered method, under the
        /// same rules an attribute tool has to pass.
        /// </summary>
        /// <remarks>
        /// A tool loaded from outside the assembly is the one most likely to be malformed, and a
        /// client cannot tell a tool that failed to register from one that was never written, so
        /// every refusal names the tool and where it came from.
        /// </remarks>
        private void TryRegisterDefined(McpToolDescriptor descriptor, List<string> errors)
        {
            var origin = descriptor.Origin;

            if (string.IsNullOrEmpty(descriptor.Name) || !NamePattern.IsMatch(descriptor.Name))
            {
                errors.Add(
                    $"Defined tool from {origin} has invalid name '{descriptor.Name}'. " +
                    "Names must match ^[a-z][a-z0-9_]{0,63}$ (dots are not valid in MCP tool names).");
                return;
            }

            if (string.IsNullOrWhiteSpace(descriptor.Description))
            {
                errors.Add($"Defined tool '{descriptor.Name}' from {origin} has an empty description.");
                return;
            }

            if (this.tools.TryGetValue(descriptor.Name, out var existing))
            {
                errors.Add(
                    $"Defined tool '{descriptor.Name}' from {origin} collides with {existing.Origin}; " +
                    "the earlier registration wins.");
                return;
            }

            if (descriptor.InputSchema?["properties"] is JObject properties)
            {
                foreach (var property in properties)
                {
                    // confirm and dry_run are the invoker's own flags, which a destructive tool
                    // publishes precisely because the invoker reads them.
                    if (descriptor.Destructive && (property.Key == "confirm" || property.Key == "dry_run"))
                    {
                        continue;
                    }

                    if (ReservedParameterNames.Contains(property.Key))
                    {
                        errors.Add(
                            $"Defined tool '{descriptor.Name}' from {origin} declares reserved parameter " +
                            $"'{property.Key}'. confirm and dry_run are added by the invoker to destructive tools, and target is the client's routing key.");
                        return;
                    }
                }
            }

            if (!McpToolGroups.IsKnown(descriptor.Group))
            {
                errors.Add(
                    $"Defined tool '{descriptor.Name}' from {origin} names unknown group '{descriptor.Group}'. " +
                    $"Known: {string.Join(", ", McpToolGroups.Known)}.");
                return;
            }

            // Undo grouping calls Undo.IncrementCurrentGroup, which only the main thread may do.
            if (!descriptor.MainThread && !string.IsNullOrEmpty(descriptor.UndoGroup))
            {
                errors.Add(
                    $"Defined tool '{descriptor.Name}' from {origin} sets UndoGroup with MainThread = false; " +
                    "Undo is main-thread only, so one of the two has to go.");
                return;
            }

            this.tools[descriptor.Name] = descriptor;
        }

        /// <summary>
        /// Parses the tool's declared examples into the array the schema publishes, or null when it
        /// declares none.
        /// </summary>
        /// <remarks>
        /// Parsed here rather than passed through as text so a malformed example is caught while the
        /// catalogue is being built, where the message names the tool. Shipped to a client, it would
        /// instead be an invalid schema that the client either rejects wholesale or, worse, quietly
        /// learns the wrong shape from.
        /// </remarks>
        private static bool TryBuildExamples(
            McpToolAttribute attribute, string origin, List<string> errors, out JArray examples)
        {
            examples = null;

            if (attribute.Examples == null || attribute.Examples.Length == 0)
            {
                return true;
            }

            var parsedAll = new JArray();

            foreach (var text in attribute.Examples)
            {
                try
                {
                    parsedAll.Add(JObject.Parse(text));
                }
                catch (Exception ex)
                {
                    // Refused the same way a malformed name is: the tool does not register, and the
                    // message names it. Publishing the tool with the example dropped would hide an
                    // authoring mistake behind a tool that still appears to work.
                    errors.Add(
                        $"[McpTool] '{attribute.Name}' on {origin} has an example that is not a JSON " +
                        $"object: {ex.Message}");
                    return false;
                }
            }

            examples = parsedAll;
            return true;
        }

        /// <summary>
        /// Maps a CLR parameter type onto a JSON Schema fragment.
        /// </summary>
        private static JObject BuildParameterSchema(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;

            if (underlying == typeof(string))
            {
                return new JObject { ["type"] = "string" };
            }

            if (underlying == typeof(bool))
            {
                return new JObject { ["type"] = "boolean" };
            }

            if (underlying.IsEnum)
            {
                return new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray(Enum.GetNames(underlying).Cast<object>().ToArray()),
                };
            }

            if (underlying == typeof(long) || underlying == typeof(ulong))
            {
                // A 64-bit value does not survive a JSON number through JavaScript, so a string is
                // an accepted spelling; Unity 6.5 instance ids arrive that way (see EntityIdCompat).
                return new JObject { ["type"] = new JArray("integer", "string") };
            }

            if (underlying == typeof(byte) || underlying == typeof(sbyte) ||
                underlying == typeof(short) || underlying == typeof(ushort) ||
                underlying == typeof(int) || underlying == typeof(uint))
            {
                return new JObject { ["type"] = "integer" };
            }

            if (underlying == typeof(float) || underlying == typeof(double) || underlying == typeof(decimal))
            {
                return new JObject { ["type"] = "number" };
            }

            if (underlying.IsArray)
            {
                return new JObject
                {
                    ["type"] = "array",
                    ["items"] = BuildParameterSchema(underlying.GetElementType()),
                };
            }

            if (underlying.IsGenericType)
            {
                var definition = underlying.GetGenericTypeDefinition();
                var arguments = underlying.GetGenericArguments();

                if (definition == typeof(List<>) || definition == typeof(IList<>) ||
                    definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyList<>))
                {
                    return new JObject
                    {
                        ["type"] = "array",
                        ["items"] = BuildParameterSchema(arguments[0]),
                    };
                }

                if (definition == typeof(Dictionary<,>) && arguments[0] == typeof(string))
                {
                    return new JObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = BuildParameterSchema(arguments[1]),
                    };
                }
            }

            if (underlying == typeof(JObject))
            {
                return new JObject { ["type"] = "object" };
            }

            if (underlying == typeof(JArray))
            {
                return new JObject { ["type"] = "array" };
            }

            // JToken and JValue accept any JSON value, and inspect_write's own examples pass a
            // number. Declaring "object" for them makes a schema-validating client refuse the
            // call before it reaches the Editor. The types are listed rather than omitted
            // because a client that requires the key would otherwise have nothing to read.
            if (typeof(JToken).IsAssignableFrom(underlying))
            {
                return new JObject
                {
                    ["type"] = new JArray("string", "number", "integer", "boolean", "object", "array", "null"),
                };
            }

            // Anything else is passed through as a JSON object and deserialized by
            // Newtonsoft at bind time.
            return new JObject { ["type"] = "object" };
        }

        private static object DefaultOf(Type type)
        {
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        /// <summary>
        /// Reports discovery errors through <paramref name="sink"/> so authoring mistakes
        /// surface in the console instead of only in the <c>/tools</c> payload.
        /// </summary>
        /// <remarks>
        /// The sink is injected rather than calling <c>Debug.LogError</c> directly because
        /// discovery runs on an HTTP worker thread, and Unity's logging takes an internal
        /// lock — the caller routes this through the main-thread pump instead.
        /// </remarks>
        public void ReportErrors(Action<string> sink)
        {
            foreach (var error in this.Errors)
            {
                sink($"[ToolCatalog] {error}");
            }
        }
    }
}
