using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Newtonsoft.Json.Linq;

using UnityEditor;

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
    /// Fields and parameterless property getters, never methods. That is not the same as being
    /// free of side effects: a getter runs arbitrary code, and <c>Renderer.material</c> and
    /// <c>MeshFilter.mesh</c> are documented mutators that swap in a fresh instance when read.
    /// There is no allowlist, so a path can change the scene it is asking about.
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
            "by '/', indexers by [n] or [\"key\"]. The first segment may also be an instance root: " +
            "'@scene:/Canvas/Button[1]' (a hierarchy path, then optionally a component type name), " +
            "'@id:<instanceId>', '@selection', '@sceneview:camera' or '@type:Ns.Type'. " +
            "Use this instead of execute_code when the question is what a value currently is. " +
            "Reading a member runs its getter, and a few Unity getters change the scene as a side " +
            "effect: Renderer.material and MeshFilter.mesh each replace the shared asset with a " +
            "fresh instance. Read sharedMaterial and sharedMesh instead when you only want to look. " +
            "A Collider's 'bounds' is the physics engine's copy of the box and lags the Transform " +
            "until physics next runs, so this syncs before reading one; Renderer.bounds never " +
            "lagged and is unaffected.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject Read(
            // Not Required: either this or 'paths' answers the call, and the framework's own
            // check runs before the one that knows that.
            [McpArg("path", "Type name or instance root, then members: 'Namespace.Type/field/other[3]', '@selection/transform/position'. Several at once go in 'paths'.")]
            string path = null,
            [McpArg("paths", "Read several paths in one call, up to 50, instead of 'path'. The " +
                             "reply keys each result by the path it was asked for, and one that " +
                             "cannot be read carries its own 'error' rather than failing the " +
                             "others. Every other argument applies to all of them. One path at a " +
                             "time is a round trip each: three objects' bounds cost fourteen " +
                             "calls once. " +
                             "This is also how to ask what a set of objects has in common — " +
                             "reading 360 renderers' sharedMaterial in batches of 50 took 8 calls " +
                             "and told 301 materials apart by the instanceId each reference " +
                             "carries, where a material tool asked per object took 361.")]
            string[] paths = null,
            [McpArg("depth", "How deep to serialise nested objects.")]
            int depth = 2,
            [McpArg("max_items", "Maximum elements to include from any one collection.")]
            int maxItems = 20,
            [McpArg("members", "Instead of a value, list the members available at this path.")]
            bool members = false)
        {
            var many = paths != null && paths.Length > 0;

            if (many && !string.IsNullOrWhiteSpace(path))
            {
                throw new McpToolException(
                    "invalid_params",
                    "'path' and 'paths' are alternatives; pass one of them.");
            }

            if (!many)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new McpToolException(
                        "invalid_params",
                        "'path' is required, e.g. 'UnityEngine.QualitySettings/renderPipeline'. "
                        + "Several at once go in 'paths'.");
                }

                return One(path, depth, maxItems, members);
            }

            if (paths.Length > MaxPaths)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'paths' takes at most {MaxPaths} at a time; {paths.Length} were given. Each "
                    + "one runs a getter on the Editor's main thread.");
            }

            var reads = new JObject();

            foreach (var one in paths)
            {
                // A path that cannot be read is that entry's answer rather than the call's. The
                // others were asked for in the same breath and are still worth having, which is
                // the shape a probe definition already returns.
                try
                {
                    reads[one] = One(one, depth, maxItems, members);
                }
                catch (McpToolException ex)
                {
                    reads[one] = new JObject { ["path"] = one, ["error"] = ex.Message };
                }
            }

            return new JObject { ["reads"] = reads };
        }

        /// <summary>The most paths one call will walk.</summary>
        private const int MaxPaths = 50;

        /// <summary>The same batched read other tools reach for after changing something.</summary>
        /// <remarks>
        /// Shared rather than duplicated, so a path means the same thing wherever it is written
        /// and one that cannot be read costs its own entry rather than the whole reply.
        /// </remarks>
        internal static JObject Many(string[] paths, int depth = 2, int maxItems = 20)
        {
            var reads = new JObject();

            foreach (var one in paths)
            {
                try
                {
                    reads[one] = One(one, depth, maxItems, members: false);
                }
                catch (McpToolException ex)
                {
                    reads[one] = new JObject { ["path"] = one, ["error"] = ex.Message };
                }
            }

            return reads;
        }

        /// <summary>One path, resolved and serialised.</summary>
        private static JObject One(string path, int depth, int maxItems, bool members)
        {
            var current = ResolvePath(path, out var type, out var walked);

            // The physics engine holds its own copy of every collider's box and does not take a
            // Transform change until the next physics step. Nothing steps in the Editor, so a
            // moved or rescaled collider answers with the box it had before the move, and nothing
            // in the reply says so. Renderer.bounds does not lag this way.
            if (current is Bounds && walked.EndsWith("/bounds", StringComparison.Ordinal))
            {
                Physics.SyncTransforms();
                current = ResolvePath(path, out type, out walked);
            }

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
            [McpArg("name", "Name or fragment to match, case-insensitive.", Required = true)]
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
            var current = ResolveRoot(segments, out rootType, out var consumed, out walked);
            var staticRoot = current == null;

            for (var i = consumed; i < segments.Count; i++)
            {
                var step = segments[i];

                current = i == consumed && staticRoot
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

        /// <summary>
        /// Resolves the first segment of a path: a type name (statics are then read from it) or an
        /// instance root written with an <c>@</c> prefix, whose members are then read from the
        /// instance.
        /// </summary>
        /// <remarks>
        /// <c>@scene:</c> takes as many segments as still name a child object, so the hierarchy
        /// path needs no terminator, then a component type name if the next segment is one. The
        /// same component step applies to every root that yields a GameObject.
        /// </remarks>
        /// <param name="consumed">How many leading segments the root used.</param>
        /// <returns>The instance to read from, or null when the root is a type.</returns>
        internal static object ResolveRoot(
            List<Segment> segments, out Type rootType, out int consumed, out string walked)
        {
            var first = segments[0];
            consumed = 1;
            walked = first.Raw;

            if (!first.Raw.StartsWith("@", StringComparison.Ordinal))
            {
                rootType = ResolveType(first);
                return null;
            }

            var colon = first.Raw.IndexOf(':');
            var root = colon < 0 ? first.Raw : first.Raw.Substring(0, colon);
            var rest = colon < 0 ? string.Empty : first.Raw.Substring(colon + 1);
            object instance;

            switch (root)
            {
                case "@type":
                    if (rest.Length == 0)
                    {
                        throw new McpToolException("invalid_params", "'@type:' needs a type name after the colon.");
                    }

                    rootType = ResolveType(new Segment { Raw = rest, Name = rest });
                    return null;

                case "@scene":
                    instance = ResolveSceneObject(segments, rest, ref consumed, ref walked);
                    break;

                case "@id":
                    if (!long.TryParse(rest, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var id))
                    {
                        throw new McpToolException("invalid_params", $"'@id:{rest}' is not an instance id.");
                    }

                    instance = EntityIdCompat.Find(id);

                    if (instance == null)
                    {
                        throw new McpToolException(
                            "not_found",
                            $"No object has instance id {id}. Instance ids do not survive a domain reload or a " +
                            "scene change; re-read the hierarchy to get current ones.");
                    }

                    break;

                case "@selection":
                    instance = Selection.activeObject;

                    if (instance == null)
                    {
                        throw new McpToolException("not_found", "Nothing is selected.");
                    }

                    break;

                case "@sceneview":
                    var sceneView = SceneView.lastActiveSceneView;

                    if (sceneView == null)
                    {
                        throw new McpToolException("not_found", "No Scene View has been active in this session.");
                    }

                    if (rest == "camera")
                    {
                        instance = sceneView.camera;

                        if (instance == null)
                        {
                            throw new McpToolException("not_found", "The Scene View has no camera yet.");
                        }
                    }
                    else if (rest.Length == 0)
                    {
                        instance = sceneView;
                    }
                    else
                    {
                        throw new McpToolException(
                            "invalid_params", $"'@sceneview:{rest}' is not known; use '@sceneview' or '@sceneview:camera'.");
                    }

                    break;

                default:
                    throw new McpToolException(
                        "invalid_params",
                        $"'{first.Raw}' is not a root. Roots are a type name, '@type:Ns.Type', " +
                        "'@scene:/Path/To/Object', '@id:<instanceId>', '@selection' and '@sceneview:camera'.");
            }

            if (instance is GameObject gameObject && consumed < segments.Count &&
                TryComponent(gameObject, segments[consumed].Name, out var component))
            {
                walked += "/" + segments[consumed].Raw;
                consumed++;
                instance = component;
            }

            rootType = instance.GetType();
            return instance;
        }

        private static object ResolveSceneObject(List<Segment> segments, string rest, ref int consumed, ref string walked)
        {
            var scenePath = rest.Trim('/');
            GameObject found = null;

            if (scenePath.Length > 0)
            {
                found = ObjectResolve.Object("/" + scenePath, null, "path", null);
            }

            while (consumed < segments.Count)
            {
                var candidate = scenePath.Length == 0 ? segments[consumed].Raw : scenePath + "/" + segments[consumed].Raw;
                GameObject child;

                try
                {
                    child = ObjectResolve.Object("/" + candidate, null, "path", null);
                }
                catch (McpToolException)
                {
                    break;
                }

                found = child;
                scenePath = candidate;
                walked += "/" + segments[consumed].Raw;
                consumed++;
            }

            if (found == null)
            {
                throw new McpToolException(
                    "not_found",
                    $"'@scene:{rest}' names no object. Write the hierarchy path after the colon or as the " +
                    "following segments: '@scene:/Canvas/Button[1]/transform'.");
            }

            return found;
        }

        private static bool TryComponent(GameObject gameObject, string typeName, out Component component)
        {
            component = null;

            if (string.IsNullOrEmpty(typeName))
            {
                return false;
            }

            foreach (var candidate in gameObject.GetComponents<Component>())
            {
                if (candidate == null)
                {
                    continue;
                }

                for (var t = candidate.GetType(); t != null; t = t.BaseType)
                {
                    if (t.Name == typeName || t.FullName == typeName)
                    {
                        component = candidate;
                        return true;
                    }
                }
            }

            return false;
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

        /// <summary>Whether an object of this name is in a loaded scene.</summary>
        /// <remarks>
        /// Only reached from a failure, so walking every root costs nothing anyone waits on. It
        /// includes objects that are switched off, because one of those is exactly what someone
        /// is reaching for when they go looking with reflect_read.
        /// </remarks>
        private static bool InScene(string name)
        {
            try
            {
                foreach (var transform in UnityEngine.Resources.FindObjectsOfTypeAll<Transform>())
                {
                    if (transform != null
                        && transform.gameObject.scene.IsValid()
                        && string.Equals(transform.name, name, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                // Nothing to add to the message, which is all this decides.
            }

            return false;
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
                // A first segment with no prefix is read as a type name. Someone who wrote a
                // hierarchy path instead was sent to reflect_find_type to search for a type that
                // was never going to exist, three separate times, so the object they did name is
                // looked for before that advice is given.
                throw new McpToolException(
                    "not_found",
                    InScene(segment.Name)
                        ? $"'{segment.Name}' is an object in the scene, not a type. Mark a "
                          + $"hierarchy path as one: '@scene:/{segment.Name}/...'. A path with no "
                          + "prefix names a type."
                        : $"No loaded type named '{segment.Name}'. reflect_find_type will search for it.");
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

        /// <summary>
        /// Ceilings the caller cannot raise. depth bounds how deep the walk goes and max_items how
        /// wide each level is, but they multiply, so a value either one allows can still be the
        /// product of the two. The node budget is what actually bounds the reply.
        /// </summary>
        private const int MaxDepth = 4;
        private const int MaxItemsCeiling = 200;
        private const int MaxNodes = 2000;

        /// <summary>
        /// A string is returned ahead of every depth and collection check, so nothing else can
        /// shorten a serialised blob or a shader's source held in a field.
        /// </summary>
        private const int MaxStringLength = 4000;

        /// <summary>Fields read from one object. Their order is unspecified, so which ones survive is too.</summary>
        private const int MaxFields = 60;

        internal static JToken Serialize(object value, int depth, int maxItems)
        {
            var budget = MaxNodes;

            return Serialize(
                value,
                Math.Min(Math.Max(depth, 0), MaxDepth),
                Math.Min(Math.Max(maxItems, 0), MaxItemsCeiling),
                ref budget);
        }

        private static JToken Serialize(object value, int depth, int maxItems, ref int budget)
        {
            if (budget-- <= 0)
            {
                return new JObject { ["truncated"] = "budget" };
            }

            switch (value)
            {
                case null:
                    return JValue.CreateNull();

                case string s:
                    return s.Length <= MaxStringLength
                        ? s
                        : s.Substring(0, MaxStringLength) + $"… ({s.Length} characters)";

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

            // Bounds stores a half-size and calls it m_Extents. Reflected field by field that is
            // what comes back, and a caller comparing it against a BoxCollider's 'size' — which is
            // a full size — is out by two with both numbers looking reasonable. The names the API
            // uses are given instead, size among them.
            if (value is Bounds bounds)
            {
                return new JObject
                {
                    ["center"] = Serialize(bounds.center, depth, maxItems, ref budget),
                    ["size"] = Serialize(bounds.size, depth, maxItems, ref budget),
                    ["extents"] = Serialize(bounds.extents, depth, maxItems, ref budget),
                    ["min"] = Serialize(bounds.min, depth, maxItems, ref budget),
                    ["max"] = Serialize(bounds.max, depth, maxItems, ref budget),
                };
            }

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
                    ["instanceId"] = EntityIdCompat.WireIdOf(unityObject),
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

                    o[entry.Key?.ToString() ?? "null"] = Serialize(entry.Value, depth - 1, maxItems, ref budget);
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

                    a.Add(Serialize(item, depth - 1, maxItems, ref budget));
                }

                if (!more)
                {
                    return a;
                }

                return new JObject { ["items"] = a, ["truncated"] = $"more than {maxItems}" };
            }

            var result = new JObject { ["__type"] = type.FullName };
            var fields = type.GetFields(AllInstance);

            if (fields.Length > MaxFields)
            {
                result["__truncated"] = $"{fields.Length} fields";
            }

            foreach (var field in fields.Take(MaxFields))
            {
                try
                {
                    result[field.Name] = Serialize(field.GetValue(value), depth - 1, maxItems, ref budget);
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
