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
    /// This replaces the v2 pairing of <c>IMcpCommandHandler</c> (C#, untyped
    /// <c>JObject</c> parameters, dispatch by internal <c>switch</c>) with a hand-written
    /// zod schema on the TypeScript side. Those two definitions drifted apart in practice
    /// — the v2 TS resource handlers routed to an endpoint the Editor never registered,
    /// and the TS README advertised seven tools that had no implementation. Generating the
    /// schema from the signature removes the second definition entirely, so there is
    /// nothing left to drift.
    /// </para>
    /// </summary>
    internal sealed class ToolCatalog
    {
        /// <summary>
        /// MCP tool-name grammar. Dots are not permitted, so the v2 <c>prefix.action</c>
        /// convention has to be spelled <c>prefix_action</c> here.
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
        public static ToolCatalog Build()
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

            return BuildFromTypes(types.Where(t => !IsTestFixtureType(t)), errors);
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
        public static ToolCatalog BuildFromTypes(IEnumerable<Type> types, List<string> errors = null)
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
        public JObject ToJson()
        {
            return new JObject
            {
                ["tools"] = new JArray(this.Tools.Select(t => t.ToCatalogEntry()).Cast<object>().ToArray()),
            };
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
                    $"Duplicate tool name '{attribute.Name}': {origin} collides with " +
                    $"{existing.Method.DeclaringType?.FullName}.{existing.Method.Name}.");
                return;
            }

            if (method.IsGenericMethodDefinition)
            {
                errors.Add($"[McpTool] '{attribute.Name}' on {origin} is generic; generic tool methods are not supported.");
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
                        "confirm/dry_run/target are injected by the invoker.");
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

            if (typeof(JToken).IsAssignableFrom(underlying))
            {
                return new JObject { ["type"] = "object" };
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
