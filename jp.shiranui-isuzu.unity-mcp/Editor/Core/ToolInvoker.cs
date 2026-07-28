using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

using UnityEditor;

using Object = UnityEngine.Object;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Binds incoming JSON arguments to a discovered tool method, applies the
    /// confirm/dry-run and Undo policies declared on the tool, invokes it, and serializes
    /// the return value.
    /// </summary>
    /// <remarks>
    /// v2 required every handler to pull its own values out of a raw <c>JObject</c> and
    /// build its own <c>JObject</c> reply. Centralising that here is what lets a tool method
    /// be an ordinary typed C# function, which in turn is what makes the schema derivable.
    /// </remarks>
    internal static class ToolInvoker
    {
        /// <summary>
        /// Serializer for tool arguments and return values.
        /// </summary>
        /// <remarks>
        /// Property names are camel-cased so a tool returning a plain C# object produces the
        /// same shape as the hand-built payloads elsewhere in the API (<c>jobId</c>,
        /// <c>queueDepth</c>, <c>inputSchema</c>) instead of leaking PascalCase C# identifiers
        /// onto the wire. Unity object graphs are cyclic, so reference loops are ignored
        /// rather than allowed to throw, and depth is bounded so a deeply nested return value
        /// cannot blow up the response.
        /// </remarks>
        private static readonly JsonSerializer ResultSerializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore,
            MaxDepth = 16,
        });

        /// <summary>
        /// Executes a tool.
        /// </summary>
        /// <param name="descriptor">The tool to run.</param>
        /// <param name="arguments">Raw arguments from the request body; may be null.</param>
        /// <returns>The tool's result, always as a JSON object.</returns>
        /// <exception cref="McpToolException">The call was malformed or refused.</exception>
        public static JObject Invoke(McpToolDescriptor descriptor, JObject arguments)
        {
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            arguments ??= new JObject();

            if (descriptor.Destructive)
            {
                var dryRun = ReadFlag(arguments, "dry_run");
                var confirmed = ReadFlag(arguments, "confirm");

                if (dryRun)
                {
                    // Bind anyway: a dry run whose arguments do not even bind is not a
                    // useful preview, and the caller should hear about that now.
                    var previewArgs = BindArguments(descriptor, arguments);
                    return new JObject
                    {
                        ["dry_run"] = true,
                        ["tool"] = descriptor.Name,
                        ["would_execute"] = true,
                        ["arguments"] = DescribeBoundArguments(descriptor, previewArgs),
                    };
                }

                if (!confirmed)
                {
                    throw new McpToolException(
                        "confirmation_required",
                        $"'{descriptor.Name}' is destructive. Pass confirm=true to execute it, " +
                        "or dry_run=true to see what it would affect.",
                        409);
                }
            }

            var boundArguments = BindArguments(descriptor, arguments);

            object returnValue;
            var undoGroup = -1;
            var usingUndo = !string.IsNullOrEmpty(descriptor.UndoGroup);

            if (usingUndo)
            {
                // Increment first. GetCurrentGroup returns whichever group is already open, so
                // without this every call in a session captures the same index and each collapse
                // merges everything recorded since — one Ctrl+Z then undoes the whole
                // conversation instead of the last call. It looks correct until a second tool
                // call happens, which is why a single-call test passed while this was wrong.
                Undo.IncrementCurrentGroup();
                undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName(descriptor.UndoGroup);
            }

            try
            {
                returnValue = descriptor.Method.Invoke(null, boundArguments);
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                if (inner is McpToolException)
                {
                    throw inner;
                }

                throw new McpToolException("tool_failed", $"{descriptor.Name} threw {inner.GetType().Name}: {inner.Message}", 500);
            }
            finally
            {
                if (usingUndo)
                {
                    // Collapse whatever the tool recorded into one Ctrl+Z step, even on failure —
                    // a half-applied change is exactly what the human needs to be able to undo.
                    Undo.CollapseUndoOperations(undoGroup);
                }
            }

            return SerializeResult(descriptor, returnValue);
        }

        /// <summary>
        /// Maps the JSON argument object onto the method's parameter array.
        /// </summary>
        private static object[] BindArguments(McpToolDescriptor descriptor, JObject arguments)
        {
            var bound = new object[descriptor.Parameters.Count];

            for (var i = 0; i < descriptor.Parameters.Count; i++)
            {
                var parameter = descriptor.Parameters[i];
                var token = arguments[parameter.Name];

                if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                {
                    if (parameter.Required)
                    {
                        throw new McpToolException(
                            "invalid_params",
                            $"'{descriptor.Name}' requires argument '{parameter.Name}'.");
                    }

                    bound[i] = parameter.DefaultValue;
                    continue;
                }

                try
                {
                    bound[i] = Coerce(token, parameter.Parameter.ParameterType);
                }
                catch (McpToolException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new McpToolException(
                        "invalid_params",
                        $"Argument '{parameter.Name}' of '{descriptor.Name}' could not be read as " +
                        $"{FriendlyTypeName(parameter.Parameter.ParameterType)}: {ex.Message}");
                }
            }

            return bound;
        }

        /// <summary>
        /// Converts a JSON token to a CLR value.
        /// <para>
        /// Deliberately tolerant about scalars arriving as strings — MCP clients routinely
        /// send <c>"3"</c> where an integer is expected, and rejecting that produces a
        /// failure the model cannot diagnose from the error text.
        /// </para>
        /// </summary>
        private static object Coerce(JToken token, Type targetType)
        {
            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (typeof(JToken).IsAssignableFrom(underlying))
            {
                return token;
            }

            if (underlying == typeof(string))
            {
                return token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None);
            }

            if (underlying == typeof(bool))
            {
                return CoerceBool(token);
            }

            if (underlying.IsEnum)
            {
                return CoerceEnum(token, underlying);
            }

            if (IsIntegral(underlying) || IsFloating(underlying))
            {
                return CoerceNumber(token, underlying);
            }

            if (underlying.IsArray)
            {
                var elementType = underlying.GetElementType();
                var items = AsArray(token);
                var array = Array.CreateInstance(elementType!, items.Count);
                for (var i = 0; i < items.Count; i++)
                {
                    array.SetValue(Coerce(items[i], elementType), i);
                }

                return array;
            }

            if (underlying.IsGenericType)
            {
                var definition = underlying.GetGenericTypeDefinition();
                var typeArguments = underlying.GetGenericArguments();

                if (definition == typeof(List<>) || definition == typeof(IList<>) ||
                    definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyList<>))
                {
                    var elementType = typeArguments[0];
                    var listType = typeof(List<>).MakeGenericType(elementType);
                    var list = (IList)Activator.CreateInstance(listType);
                    foreach (var item in AsArray(token))
                    {
                        list.Add(Coerce(item, elementType));
                    }

                    return list;
                }
            }

            return token.ToObject(underlying, ResultSerializer);
        }

        private static bool CoerceBool(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Boolean:
                    return token.Value<bool>();
                case JTokenType.Integer:
                case JTokenType.Float:
                    return token.Value<double>() != 0d;
                case JTokenType.String:
                    var text = token.Value<string>();
                    if (bool.TryParse(text, out var parsed))
                    {
                        return parsed;
                    }

                    if (string.Equals(text, "1", StringComparison.Ordinal))
                    {
                        return true;
                    }

                    if (string.Equals(text, "0", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    throw new McpToolException("invalid_params", $"'{text}' is not a boolean.");
                default:
                    throw new McpToolException("invalid_params", $"{token.Type} is not a boolean.");
            }
        }

        private static object CoerceEnum(JToken token, Type enumType)
        {
            if (token.Type == JTokenType.Integer)
            {
                return Enum.ToObject(enumType, token.Value<long>());
            }

            var name = token.Value<string>();
            if (!string.IsNullOrEmpty(name))
            {
                foreach (var candidate in Enum.GetNames(enumType))
                {
                    if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return Enum.Parse(enumType, candidate);
                    }
                }
            }

            throw new McpToolException(
                "invalid_params",
                $"'{name}' is not a valid value. Expected one of: {string.Join(", ", Enum.GetNames(enumType))}.");
        }

        private static object CoerceNumber(JToken token, Type targetType)
        {
            double value;

            switch (token.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    value = token.Value<double>();
                    break;
                case JTokenType.Boolean:
                    value = token.Value<bool>() ? 1d : 0d;
                    break;
                case JTokenType.String:
                    var text = token.Value<string>();
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    {
                        throw new McpToolException("invalid_params", $"'{text}' is not a number.");
                    }

                    break;
                default:
                    throw new McpToolException("invalid_params", $"{token.Type} is not a number.");
            }

            if (IsIntegral(targetType) && value != Math.Floor(value))
            {
                throw new McpToolException(
                    "invalid_params",
                    $"{value.ToString(CultureInfo.InvariantCulture)} is not a whole number.");
            }

            return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }

        private static IReadOnlyList<JToken> AsArray(JToken token)
        {
            if (token is JArray array)
            {
                return array.ToList();
            }

            // A single value where an array is expected is a common client slip; treat it
            // as a one-element array rather than failing the whole call.
            return new List<JToken> { token };
        }

        private static bool IsIntegral(Type type)
        {
            return type == typeof(byte) || type == typeof(sbyte) ||
                   type == typeof(short) || type == typeof(ushort) ||
                   type == typeof(int) || type == typeof(uint) ||
                   type == typeof(long) || type == typeof(ulong);
        }

        private static bool IsFloating(Type type)
        {
            return type == typeof(float) || type == typeof(double) || type == typeof(decimal);
        }

        private static bool ReadFlag(JObject arguments, string name)
        {
            var token = arguments[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            return CoerceBool(token);
        }

        private static JObject DescribeBoundArguments(McpToolDescriptor descriptor, object[] bound)
        {
            var described = new JObject();
            for (var i = 0; i < descriptor.Parameters.Count; i++)
            {
                described[descriptor.Parameters[i].Name] = ToToken(bound[i]);
            }

            return described;
        }

        /// <summary>
        /// Wraps the method's return value into the object the envelope expects.
        /// </summary>
        private static JObject SerializeResult(McpToolDescriptor descriptor, object returnValue)
        {
            if (descriptor.Method.ReturnType == typeof(void))
            {
                return new JObject { ["ok"] = true };
            }

            var token = ToToken(returnValue);

            // Object-shaped results are returned as-is so tools control their own field names;
            // scalars and arrays get wrapped, because the envelope's result must be an object.
            return token is JObject asObject
                ? asObject
                : new JObject { ["result"] = token };
        }

        private static JToken ToToken(object value)
        {
            switch (value)
            {
                case null:
                    return JValue.CreateNull();
                case JToken token:
                    return token;
                case Object unityObject:
                    // Serializing a live UnityEngine.Object produces an enormous, mostly
                    // useless graph. Return an identifying summary instead.
                    return new JObject
                    {
                        ["name"] = unityObject.name,
                        ["type"] = unityObject.GetType().Name,
                    };
                default:
                    return JToken.FromObject(value, ResultSerializer);
            }
        }

        private static string FriendlyTypeName(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type);
            return underlying != null ? $"{underlying.Name}?" : type.Name;
        }
    }
}
