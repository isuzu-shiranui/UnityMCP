using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Newtonsoft.Json.Linq;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Reading live private state out of a running pipeline.
    /// </summary>
    /// <remarks>
    /// Render pipeline debugging spends most of its time asking what some manager's per-camera
    /// state actually contains this frame, and the answer is always behind a private field. Doing
    /// that through execute_code means writing, compiling and loading an assembly per question,
    /// which is slow, is not on the undo stack, and puts a compile error between the question and
    /// the answer. This asks directly.
    /// <para>
    /// Reads only. Fields and parameterless property getters, never methods, so a question cannot
    /// change what it is asking about.
    /// </para>
    /// </remarks>
    internal static class ReflectTools
    {
        private const BindingFlags AllStatic =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        private const BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        [McpTool(
            "reflect_read",
            "Read live state by type and member path, including private ones: " +
            "'MyPipeline.ShadowManager/ByCamera[0]/levels[2]/worldToShadow'. Segments are separated " +
            "by '/', indexers by [n] or [\"key\"]. Use this instead of execute_code when the question " +
            "is what a value currently is.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject Read(
            [McpArg("path", "Type name, then members: 'Namespace.Type/field/other[3]'.")]
            string path = null,
            [McpArg("depth", "How deep to serialise nested objects.")]
            int depth = 2,
            [McpArg("max_items", "Maximum elements to include from any one collection.")]
            int maxItems = 20,
            [McpArg("members", "Instead of a value, list the members available at this path.")]
            bool members = false)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new McpToolException(
                    "invalid_params",
                    "'path' is required, e.g. 'UnityEngine.QualitySettings/renderPipeline'.");
            }

            var current = ResolvePath(path, out var type, out var walked);

            if (members)
            {
                var owningType = current?.GetType() ?? type;

                return new JObject
                {
                    ["path"] = walked,
                    ["type"] = owningType.FullName,
                    ["members"] = new JArray(MemberNames(owningType).Cast<object>().ToArray()),
                };
            }

            return new JObject
            {
                ["path"] = walked,
                ["type"] = current?.GetType().FullName,
                ["value"] = Serialize(current, Math.Max(depth, 0), Math.Max(maxItems, 0)),
            };
        }

        [McpTool(
            "reflect_find_type",
            "Find loaded types by name, when the full name for reflect_read is not known.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject FindType(
            [McpArg("name", "Name or fragment to match, case-insensitive.")]
            string name = null,
            [McpArg("limit", "Maximum matches to return.")]
            int limit = 30)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new McpToolException("invalid_params", "'name' is required.");
            }

            var matches = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeTypes)
                .Where(t => t.FullName != null &&
                            t.FullName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(t => t.FullName.Length)
                .Take(Math.Max(limit, 0))
                .Select(t => (object)new JObject
                {
                    ["fullName"] = t.FullName,
                    ["assembly"] = t.Assembly.GetName().Name,
                })
                .ToArray();

            return new JObject { ["count"] = matches.Length, ["types"] = new JArray(matches) };
        }

        /// <summary>
        /// Walks a type-and-member path and returns whatever it lands on.
        /// </summary>
        /// <remarks>
        /// Shared with gpu_readback, which needs the same walk to reach the buffer or texture it
        /// is asked to read. Two implementations of "what does this path mean" would drift.
        /// </remarks>
        internal static object ResolvePath(string path, out Type rootType, out string walked)
        {
            var segments = SplitPath(path);
            rootType = ResolveType(segments[0]);
            object current = null;
            walked = segments[0].Raw;

            for (var i = 1; i < segments.Count; i++)
            {
                var step = segments[i];

                current = i == 1
                    ? ReadMember(rootType, null, step, walked)
                    : ReadMember(current?.GetType(), current, step, walked);

                walked += "/" + step.Raw;

                if (current == null && i < segments.Count - 1)
                {
                    throw new McpToolException("not_found", $"'{walked}' is null; cannot go further.");
                }
            }

            return current;
        }

        // ── path parsing ──

        internal sealed class Segment
        {
            public string Raw;

            public string Name;

            public string Index;
        }

        internal static List<Segment> SplitPath(string path)
        {
            var result = new List<Segment>();

            foreach (var raw in path.Split('/'))
            {
                if (raw.Length == 0)
                {
                    continue;
                }

                var segment = new Segment { Raw = raw, Name = raw };
                var open = raw.IndexOf('[');

                if (open > 0 && raw.EndsWith("]", StringComparison.Ordinal))
                {
                    segment.Name = raw.Substring(0, open);
                    segment.Index = raw.Substring(open + 1, raw.Length - open - 2).Trim('"', '\'');
                }

                result.Add(segment);
            }

            if (result.Count == 0)
            {
                throw new McpToolException("invalid_params", $"'{path}' does not name anything.");
            }

            return result;
        }

        internal static Type ResolveType(Segment segment)
        {
            var candidates = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeTypes)
                .Where(t => t.FullName == segment.Name || t.Name == segment.Name)
                .OrderBy(t => t.FullName == segment.Name ? 0 : 1)
                .ToArray();

            if (candidates.Length == 0)
            {
                throw new McpToolException(
                    "not_found",
                    $"No loaded type named '{segment.Name}'. reflect_find_type will search for it.");
            }

            if (candidates.Length > 1 && candidates[0].FullName != segment.Name)
            {
                var names = string.Join(", ", candidates.Take(5).Select(t => t.FullName));

                throw new McpToolException(
                    "invalid_params",
                    $"'{segment.Name}' is ambiguous: {names}. Use the full name.");
            }

            return candidates[0];
        }

        private static object ReadMember(Type type, object instance, Segment segment, string walked)
        {
            if (type == null)
            {
                throw new McpToolException("not_found", $"'{walked}' is null; cannot read '{segment.Name}'.");
            }

            var flags = instance == null ? AllStatic : AllInstance;
            object value = null;
            var found = false;

            for (var t = type; t != null && !found; t = t.BaseType)
            {
                var field = t.GetField(segment.Name, flags);

                if (field != null)
                {
                    value = field.GetValue(instance);
                    found = true;
                    break;
                }

                var property = t.GetProperty(segment.Name, flags);

                if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
                {
                    value = property.GetValue(instance);
                    found = true;
                }
            }

            if (!found)
            {
                var available = string.Join(", ", MemberNames(type).Take(15));

                throw new McpToolException(
                    "not_found",
                    $"'{type.FullName}' has no readable member '{segment.Name}'. Available: {available}. " +
                    "Pass members=true to list them all.");
            }

            return segment.Index == null ? value : Index(value, segment.Index, walked + "/" + segment.Raw);
        }

        private static object Index(object value, string index, string walked)
        {
            if (value == null)
            {
                throw new McpToolException("not_found", $"'{walked}' is null; cannot index it.");
            }

            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (string.Equals(entry.Key?.ToString(), index, StringComparison.Ordinal))
                    {
                        return entry.Value;
                    }
                }

                var keys = string.Join(", ", dictionary.Keys.Cast<object>().Take(10).Select(k => k?.ToString()));

                throw new McpToolException(
                    "not_found",
                    $"No key '{index}' in {walked}. Keys: {keys}.");
            }

            if (!int.TryParse(index, out var position))
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{index}' is not a number, and {walked} is not a dictionary.");
            }

            if (value is IList list)
            {
                if (position < 0 || position >= list.Count)
                {
                    throw new McpToolException(
                        "invalid_params",
                        $"Index {position} is out of range; {walked} has {list.Count} element(s).");
                }

                return list[position];
            }

            if (value is IEnumerable enumerable)
            {
                var i = 0;

                foreach (var item in enumerable)
                {
                    if (i++ == position)
                    {
                        return item;
                    }
                }

                throw new McpToolException("invalid_params", $"Index {position} is past the end of {walked}.");
            }

            throw new McpToolException("invalid_params", $"{walked} is not indexable.");
        }

        internal static IEnumerable<string> MemberNames(Type type)
        {
            var names = new List<string>();

            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                names.AddRange(t.GetFields(AllStatic).Select(f => f.Name));
                names.AddRange(t.GetFields(AllInstance).Select(f => f.Name));
                names.AddRange(t.GetProperties(AllStatic)
                    .Where(p => p.CanRead && p.GetIndexParameters().Length == 0).Select(p => p.Name));
                names.AddRange(t.GetProperties(AllInstance)
                    .Where(p => p.CanRead && p.GetIndexParameters().Length == 0).Select(p => p.Name));
            }

            return names.Distinct().OrderBy(n => n, StringComparer.Ordinal);
        }

        // ── serialisation ──

        internal static JToken Serialize(object value, int depth, int maxItems)
        {
            switch (value)
            {
                case null:
                    return JValue.CreateNull();

                case string s:
                    return s;

                case bool b:
                    return b;

                case float f:
                    return f;

                case double d:
                    return d;

                case decimal m:
                    return m;

                case Enum e:
                    return e.ToString();
            }

            var type = value.GetType();

            if (type.IsPrimitive)
            {
                return JToken.FromObject(value);
            }

            if (value is Vector2 v2) return new JObject { ["x"] = v2.x, ["y"] = v2.y };
            if (value is Vector3 v3) return new JObject { ["x"] = v3.x, ["y"] = v3.y, ["z"] = v3.z };
            if (value is Vector4 v4) return new JObject { ["x"] = v4.x, ["y"] = v4.y, ["z"] = v4.z, ["w"] = v4.w };
            if (value is Color c) return new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };

            if (value is Matrix4x4 matrix)
            {
                var rows = new JArray();

                for (var r = 0; r < 4; r++)
                {
                    rows.Add(new JArray(
                        (object)matrix[r, 0], (object)matrix[r, 1], (object)matrix[r, 2], (object)matrix[r, 3]));
                }

                return rows;
            }

            if (value is UnityEngine.Object unityObject)
            {
                return new JObject
                {
                    ["name"] = unityObject.name,
                    ["type"] = type.FullName,
                    ["instanceId"] = unityObject.GetInstanceID(),
                };
            }

            if (depth <= 0)
            {
                // Not silently truncated to null: a caller that wanted more can ask for more.
                return new JObject { ["type"] = type.FullName, ["truncated"] = "depth" };
            }

            if (value is IDictionary dictionary)
            {
                var o = new JObject();
                var n = 0;

                foreach (DictionaryEntry entry in dictionary)
                {
                    if (n++ >= maxItems)
                    {
                        o["__truncated"] = $"{dictionary.Count} entries";
                        break;
                    }

                    o[entry.Key?.ToString() ?? "null"] = Serialize(entry.Value, depth - 1, maxItems);
                }

                return o;
            }

            if (value is IEnumerable sequence)
            {
                var a = new JArray();
                var n = 0;
                var more = false;

                foreach (var item in sequence)
                {
                    if (n++ >= maxItems)
                    {
                        more = true;
                        break;
                    }

                    a.Add(Serialize(item, depth - 1, maxItems));
                }

                if (!more)
                {
                    return a;
                }

                return new JObject { ["items"] = a, ["truncated"] = $"more than {maxItems}" };
            }

            var result = new JObject { ["__type"] = type.FullName };

            foreach (var field in type.GetFields(AllInstance).Take(60))
            {
                try
                {
                    result[field.Name] = Serialize(field.GetValue(value), depth - 1, maxItems);
                }
                catch (Exception e)
                {
                    result[field.Name] = $"<{e.GetType().Name}>";
                }
            }

            return result;
        }

        internal static Type[] SafeTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types.Where(t => t != null).ToArray();
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }
    }
}
