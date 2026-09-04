using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
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
    /// Argument binding and reply serialization live here rather than in each tool, which is
    /// what lets a tool method be an ordinary typed C# function, which in turn is what makes
    /// its schema derivable from the signature.
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

        private static readonly MethodInfo CannotReadMethod =
            typeof(McpParameterBinding).GetMethod(nameof(McpParameterBinding.CannotRead));

        private static readonly MethodInfo ReadArgumentMethod = Helper(nameof(ReadArgument));
        private static readonly MethodInfo MissingArgumentMethod = Helper(nameof(MissingArgument));
        private static readonly MethodInfo CoerceStringMethod = Helper(nameof(CoerceString));
        private static readonly MethodInfo CoerceBooleanMethod = Helper(nameof(CoerceBoolean));
        private static readonly MethodInfo CoerceByteMethod = Helper(nameof(CoerceByte));
        private static readonly MethodInfo CoerceSByteMethod = Helper(nameof(CoerceSByte));
        private static readonly MethodInfo CoerceInt16Method = Helper(nameof(CoerceInt16));
        private static readonly MethodInfo CoerceUInt16Method = Helper(nameof(CoerceUInt16));
        private static readonly MethodInfo CoerceInt32Method = Helper(nameof(CoerceInt32));
        private static readonly MethodInfo CoerceUInt32Method = Helper(nameof(CoerceUInt32));
        private static readonly MethodInfo CoerceInt64Method = Helper(nameof(CoerceInt64));
        private static readonly MethodInfo CoerceUInt64Method = Helper(nameof(CoerceUInt64));
        private static readonly MethodInfo CoerceSingleMethod = Helper(nameof(CoerceSingle));
        private static readonly MethodInfo CoerceDoubleMethod = Helper(nameof(CoerceDouble));
        private static readonly MethodInfo CoerceDecimalMethod = Helper(nameof(CoerceDecimal));
        private static readonly MethodInfo CoerceEnumMethod = Helper(nameof(CoerceEnum));
        private static readonly MethodInfo CoerceArrayMethod = Helper(nameof(CoerceArray));
        private static readonly MethodInfo CoerceListMethod = Helper(nameof(CoerceList));
        private static readonly MethodInfo CoerceJsonMethod = Helper(nameof(CoerceJson));
        private static readonly MethodInfo CoerceObjectMethod = Helper(nameof(CoerceObject));

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
                    return new JObject
                    {
                        ["dry_run"] = true,
                        ["tool"] = descriptor.Name,
                        ["would_execute"] = true,
                        ["arguments"] = DescribePreviewArguments(descriptor, arguments),
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
                if (descriptor.Direct != null)
                {
                    returnValue = descriptor.Direct(arguments);
                }
                else
                {
                    var plan = descriptor.BindPlan;

                    returnValue = plan.Compiled != null
                        ? plan.Compiled(arguments)
                        : descriptor.Method.Invoke(null, BindArguments(plan, arguments));
                }
            }
            catch (McpToolException)
            {
                // A refused argument and a tool's own refusal both already carry the code and
                // status the caller acts on.
                throw;
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
            catch (Exception ex)
            {
                // The compiled delegate calls the method directly, so nothing wraps a failure
                // inside the tool body in a TargetInvocationException.
                throw new McpToolException("tool_failed", $"{descriptor.Name} threw {ex.GetType().Name}: {ex.Message}", 500);
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

        // ── the per-descriptor plan ───────────────────────────────────────────────────

        /// <summary>
        /// Resolves each parameter to a <see cref="BindKind"/> and compiles the descriptor's
        /// invoker. Called once per descriptor, behind <see cref="McpToolDescriptor.BindPlan"/>.
        /// </summary>
        internal static ToolBindPlan CreateBindPlan(McpToolDescriptor descriptor)
        {
            var parameters = descriptor.Parameters;
            var bindings = new McpParameterBinding[parameters.Count];

            for (var i = 0; i < bindings.Length; i++)
            {
                var parameter = parameters[i];
                bindings[i] = new McpParameterBinding(
                    descriptor.Name,
                    parameter.Name,
                    parameter.Parameter.ParameterType,
                    parameter.Required,
                    parameter.DefaultValue);
            }

            try
            {
                return new ToolBindPlan(bindings, BuildInvoker(descriptor, bindings).Compile(), null);
            }
            catch (Exception ex)
            {
                // A signature the expression builder cannot express still has to be callable, so
                // the descriptor keeps the reflection path. EveryLiveToolCompiles asserts that no
                // shipped tool reaches this.
                return new ToolBindPlan(bindings, null, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds <c>arguments =&gt; Tool(coerce(arguments["a"]), coerce(arguments["b"]), ...)</c>.
        /// </summary>
        /// <remarks>
        /// Value-type arguments reach the method as themselves rather than through an
        /// <c>object[]</c>, so a call allocates nothing beyond the result. Only the return value
        /// is boxed, which a uniform delegate signature cannot avoid.
        /// </remarks>
        internal static Expression<Func<JObject, object>> BuildInvoker(
            McpToolDescriptor descriptor, McpParameterBinding[] bindings)
        {
            var method = descriptor.Method;
            var methodParameters = method.GetParameters();

            if (methodParameters.Length != bindings.Length)
            {
                throw new InvalidOperationException(
                    $"'{descriptor.Name}' has {methodParameters.Length} parameters but {bindings.Length} bindings.");
            }

            var argumentsParameter = Expression.Parameter(typeof(JObject), "arguments");
            var callArguments = new Expression[bindings.Length];

            for (var i = 0; i < bindings.Length; i++)
            {
                PrepareElementCoercer(bindings[i]);
                callArguments[i] = BindExpression(argumentsParameter, bindings[i], methodParameters[i].ParameterType);
            }

            Expression body = Expression.Call(null, method, callArguments);

            body = method.ReturnType == typeof(void)
                ? (Expression)Expression.Block(typeof(object), body, Expression.Constant(null, typeof(object)))
                : Expression.Convert(body, typeof(object));

            return Expression.Lambda<Func<JObject, object>>(body, argumentsParameter);
        }

        /// <summary>
        /// One argument: read the token, coerce it, or fall back to the default and refuse when
        /// the argument is required.
        /// </summary>
        private static Expression BindExpression(
            ParameterExpression arguments, McpParameterBinding binding, Type parameterType)
        {
            var token = Expression.Variable(typeof(JToken), "token");
            var bindingConstant = Expression.Constant(binding, typeof(McpParameterBinding));

            Expression coerced = Expression.Call(CoercionMethod(binding), token, bindingConstant);
            if (coerced.Type != parameterType)
            {
                coerced = Expression.Convert(coerced, parameterType);
            }

            var absent = binding.Required
                ? (Expression)Expression.Throw(Expression.Call(MissingArgumentMethod, bindingConstant), parameterType)
                : DefaultExpression(binding, parameterType);

            return Expression.Block(
                parameterType,
                new[] { token },
                Expression.Assign(
                    token,
                    Expression.Call(ReadArgumentMethod, arguments, Expression.Constant(binding.Name))),
                Expression.Condition(
                    Expression.ReferenceNotEqual(token, Expression.Constant(null, typeof(JToken))),
                    Guarded(coerced, bindingConstant),
                    absent,
                    parameterType));
        }

        /// <summary>
        /// Turns whatever the coercion throws into the same <c>invalid_params</c> the boxing path
        /// reports, naming the argument and its declared type.
        /// </summary>
        private static Expression Guarded(Expression coerced, Expression bindingConstant)
        {
            var error = Expression.Parameter(typeof(Exception), "error");

            return Expression.TryCatch(
                coerced,
                Expression.Catch(typeof(McpToolException), Expression.Rethrow(coerced.Type)),
                Expression.Catch(
                    error,
                    Expression.Throw(Expression.Call(bindingConstant, CannotReadMethod, error), coerced.Type)));
        }

        /// <summary>
        /// The value an omitted optional argument binds to, as a constant of the parameter's type.
        /// </summary>
        /// <remarks>
        /// A struct parameter declared <c>= default</c> carries a null in metadata, and an enum
        /// parameter can carry its underlying integer, so the stored value is normalised to the
        /// parameter's own type before it becomes a constant.
        /// </remarks>
        private static Expression DefaultExpression(McpParameterBinding binding, Type parameterType)
        {
            var value = binding.DefaultValue;

            if (value == null)
            {
                return Expression.Default(parameterType);
            }

            var target = binding.Underlying;

            if (!target.IsInstanceOfType(value))
            {
                value = target.IsEnum
                    ? Enum.ToObject(target, value)
                    : Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
            }

            Expression constant = Expression.Constant(value, target);
            return target == parameterType ? constant : (Expression)Expression.Convert(constant, parameterType);
        }

        private static MethodInfo CoercionMethod(McpParameterBinding binding)
        {
            switch (binding.Kind)
            {
                case BindKind.JsonToken:
                    return CoerceJsonMethod.MakeGenericMethod(binding.Underlying);
                case BindKind.String:
                    return CoerceStringMethod;
                case BindKind.Boolean:
                    return CoerceBooleanMethod;
                case BindKind.Enum:
                    return CoerceEnumMethod.MakeGenericMethod(binding.Underlying);
                case BindKind.Byte:
                    return CoerceByteMethod;
                case BindKind.SByte:
                    return CoerceSByteMethod;
                case BindKind.Int16:
                    return CoerceInt16Method;
                case BindKind.UInt16:
                    return CoerceUInt16Method;
                case BindKind.Int32:
                    return CoerceInt32Method;
                case BindKind.UInt32:
                    return CoerceUInt32Method;
                case BindKind.Int64:
                    return CoerceInt64Method;
                case BindKind.UInt64:
                    return CoerceUInt64Method;
                case BindKind.Single:
                    return CoerceSingleMethod;
                case BindKind.Double:
                    return CoerceDoubleMethod;
                case BindKind.Decimal:
                    return CoerceDecimalMethod;
                case BindKind.Array:
                    return CoerceArrayMethod.MakeGenericMethod(binding.Underlying.GetElementType());
                case BindKind.List:
                    return CoerceListMethod.MakeGenericMethod(binding.Underlying.GetGenericArguments()[0]);
                default:
                    return CoerceObjectMethod.MakeGenericMethod(binding.Underlying);
            }
        }

        /// <summary>
        /// Gives an array or list binding the typed delegate its elements are coerced through.
        /// </summary>
        private static void PrepareElementCoercer(McpParameterBinding binding)
        {
            var element = binding.Element;

            if (element == null || element.Coercer != null)
            {
                return;
            }

            PrepareElementCoercer(element);

            var signature = typeof(Func<,,>).MakeGenericType(
                typeof(JToken), typeof(McpParameterBinding), element.DeclaredType);

            element.Coercer = Delegate.CreateDelegate(signature, CoercionMethod(element));
        }

        private static MethodInfo Helper(string name)
        {
            return typeof(ToolInvoker).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        }

        // ── typed coercion ────────────────────────────────────────────────────────────

        /// <summary>
        /// The argument's token, or null when it is absent or explicitly null.
        /// </summary>
        internal static JToken ReadArgument(JObject arguments, string name)
        {
            if (!arguments.TryGetValue(name, out var token) || token == null)
            {
                return null;
            }

            return token.Type == JTokenType.Null || token.Type == JTokenType.Undefined ? null : token;
        }

        internal static McpToolException MissingArgument(McpParameterBinding binding)
        {
            return new McpToolException("invalid_params", binding.MissingArgumentMessage);
        }

        internal static string CoerceString(JToken token, McpParameterBinding binding)
        {
            return token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None);
        }

        internal static bool CoerceBoolean(JToken token, McpParameterBinding binding)
        {
            return CoerceBoolValue(token);
        }

        internal static byte CoerceByte(JToken token, McpParameterBinding binding)
        {
            return TryExactInt64(token, out var exact) ? checked((byte)exact) : checked((byte)WholeNumber(token));
        }

        internal static sbyte CoerceSByte(JToken token, McpParameterBinding binding)
        {
            return TryExactInt64(token, out var exact) ? checked((sbyte)exact) : checked((sbyte)WholeNumber(token));
        }

        internal static short CoerceInt16(JToken token, McpParameterBinding binding)
        {
            return TryExactInt64(token, out var exact) ? checked((short)exact) : checked((short)WholeNumber(token));
        }

        internal static ushort CoerceUInt16(JToken token, McpParameterBinding binding)
        {
            return TryExactInt64(token, out var exact) ? checked((ushort)exact) : checked((ushort)WholeNumber(token));
        }

        internal static int CoerceInt32(JToken token, McpParameterBinding binding)
        {
            return TryExactInt64(token, out var exact) ? checked((int)exact) : checked((int)WholeNumber(token));
        }

        internal static uint CoerceUInt32(JToken token, McpParameterBinding binding)
        {
            return TryExactInt64(token, out var exact) ? checked((uint)exact) : checked((uint)WholeNumber(token));
        }

        internal static long CoerceInt64(JToken token, McpParameterBinding binding)
        {
            return TryExactInt64(token, out var exact) ? exact : checked((long)WholeNumber(token));
        }

        internal static ulong CoerceUInt64(JToken token, McpParameterBinding binding)
        {
            return TryExactInt64(token, out var exact) ? checked((ulong)exact) : checked((ulong)WholeNumber(token));
        }

        internal static float CoerceSingle(JToken token, McpParameterBinding binding)
        {
            return (float)CoerceDoubleValue(token);
        }

        internal static double CoerceDouble(JToken token, McpParameterBinding binding)
        {
            return CoerceDoubleValue(token);
        }

        internal static decimal CoerceDecimal(JToken token, McpParameterBinding binding)
        {
            return (decimal)CoerceDoubleValue(token);
        }

        internal static T CoerceEnum<T>(JToken token, McpParameterBinding binding)
            where T : struct
        {
            if (token.Type == JTokenType.Integer)
            {
                return (T)Enum.ToObject(binding.Underlying, token.Value<long>());
            }

            var name = token.Value<string>();

            if (!string.IsNullOrEmpty(name))
            {
                var names = binding.EnumNames;
                var values = (T[])binding.EnumValues;

                for (var i = 0; i < names.Length; i++)
                {
                    if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                    {
                        return values[i];
                    }
                }
            }

            throw new McpToolException(
                "invalid_params",
                $"'{name}' is not a valid value. Expected one of: {binding.EnumNamesJoined}.");
        }

        internal static T[] CoerceArray<T>(JToken token, McpParameterBinding binding)
        {
            var element = binding.Element;
            var coerce = (Func<JToken, McpParameterBinding, T>)element.Coercer;

            if (token is JArray array)
            {
                var items = new T[array.Count];
                for (var i = 0; i < items.Length; i++)
                {
                    items[i] = coerce(array[i], element);
                }

                return items;
            }

            // A single value where an array is expected is a common client slip; treat it
            // as a one-element array rather than failing the whole call.
            return new[] { coerce(token, element) };
        }

        internal static List<T> CoerceList<T>(JToken token, McpParameterBinding binding)
        {
            var element = binding.Element;
            var coerce = (Func<JToken, McpParameterBinding, T>)element.Coercer;

            if (token is JArray array)
            {
                var items = new List<T>(array.Count);
                for (var i = 0; i < array.Count; i++)
                {
                    items.Add(coerce(array[i], element));
                }

                return items;
            }

            return new List<T>(1) { coerce(token, element) };
        }

        internal static T CoerceJson<T>(JToken token, McpParameterBinding binding)
            where T : JToken
        {
            return (T)token;
        }

        internal static T CoerceObject<T>(JToken token, McpParameterBinding binding)
        {
            return token.ToObject<T>(ResultSerializer);
        }

        // ── the boxing path: dry runs, and any descriptor that did not compile ─────────

        /// <summary>
        /// Maps the JSON argument object onto the method's parameter array.
        /// </summary>
        private static object[] BindArguments(ToolBindPlan plan, JObject arguments)
        {
            var bindings = plan.Parameters;
            var bound = new object[bindings.Length];

            for (var i = 0; i < bindings.Length; i++)
            {
                var binding = bindings[i];
                var token = ReadArgument(arguments, binding.Name);

                if (token == null)
                {
                    if (binding.Required)
                    {
                        throw MissingArgument(binding);
                    }

                    bound[i] = binding.DefaultValue;
                    continue;
                }

                try
                {
                    bound[i] = Coerce(token, binding.DeclaredType);
                }
                catch (McpToolException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw binding.CannotRead(ex);
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
                return CoerceBoolValue(token);
            }

            if (underlying.IsEnum)
            {
                return CoerceEnumValue(token, underlying);
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

        private static object CoerceEnumValue(JToken token, Type enumType)
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
            var integral = IsIntegral(targetType);

            if (integral && TryExactInt64(token, out var exact))
            {
                return Convert.ChangeType(exact, targetType, CultureInfo.InvariantCulture);
            }

            var value = integral ? WholeNumber(token) : CoerceDoubleValue(token);

            return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }

        // ── the conversions both paths share ──────────────────────────────────────────

        private static bool CoerceBoolValue(JToken token)
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

        /// <summary>
        /// The token's exact 64-bit value, when it has one.
        /// </summary>
        /// <remarks>
        /// An integral target never goes through a double. A Unity 6.5 EntityId is about 5.7e17,
        /// above the 2^53 a double holds exactly, and rounding one names a different object.
        /// </remarks>
        private static bool TryExactInt64(JToken token, out long value)
        {
            if (token.Type == JTokenType.Integer)
            {
                value = token.Value<long>();
                return true;
            }

            if (token.Type == JTokenType.String &&
                long.TryParse(token.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            value = 0L;
            return false;
        }

        private static double CoerceDoubleValue(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return token.Value<double>();
                case JTokenType.Boolean:
                    return token.Value<bool>() ? 1d : 0d;
                case JTokenType.String:
                    var text = token.Value<string>();
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    {
                        throw new McpToolException("invalid_params", $"'{text}' is not a number.");
                    }

                    return parsed;
                default:
                    throw new McpToolException("invalid_params", $"{token.Type} is not a number.");
            }
        }

        private static double WholeNumber(JToken token)
        {
            var value = CoerceDoubleValue(token);

            if (value != Math.Floor(value))
            {
                throw new McpToolException(
                    "invalid_params",
                    $"{value.ToString(CultureInfo.InvariantCulture)} is not a whole number.");
            }

            return value;
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

            return CoerceBoolValue(token);
        }

        /// <summary>
        /// The <c>arguments</c> a dry run reports back.
        /// </summary>
        /// <remarks>
        /// A method tool binds first: a preview whose arguments do not even bind is not a useful
        /// preview, and the caller has to hear about that now. A direct tool has no bindings, so
        /// its arguments are echoed minus the two flags the invoker consumes itself.
        /// </remarks>
        private static JObject DescribePreviewArguments(McpToolDescriptor descriptor, JObject arguments)
        {
            if (descriptor.Direct != null)
            {
                var echoed = (JObject)arguments.DeepClone();
                echoed.Remove("confirm");
                echoed.Remove("dry_run");
                return echoed;
            }

            var plan = descriptor.BindPlan;
            return DescribeBoundArguments(plan, BindArguments(plan, arguments));
        }

        private static JObject DescribeBoundArguments(ToolBindPlan plan, object[] bound)
        {
            var described = new JObject();
            for (var i = 0; i < bound.Length; i++)
            {
                described[plan.Parameters[i].Name] = ToToken(bound[i]);
            }

            return described;
        }

        /// <summary>
        /// Wraps the method's return value into the object the envelope expects.
        /// </summary>
        private static JObject SerializeResult(McpToolDescriptor descriptor, object returnValue)
        {
            // A direct tool has no return type to inspect, so returning null is how it says it
            // produced no payload.
            if (descriptor.Method != null ? descriptor.Method.ReturnType == typeof(void) : returnValue == null)
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
    }
}
