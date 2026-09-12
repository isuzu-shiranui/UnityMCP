using System;
using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

using UnityMCP.Editor.Core;


namespace UnityMCP.Editor.Handlers
{
    internal static class InspectorAccess
    {
        private const int MaxProperties = 100;

        public static JObject Access(JObject parameters)
        {
            try
            {
                var mode = parameters["mode"]?.ToString() ?? "read";
                var instanceId = parameters["instanceId"]?.Value<long?>();
                var objectPath = parameters["objectPath"]?.ToString();
                var componentType = parameters["componentType"]?.ToString();
                var componentIndex = parameters["componentIndex"]?.Value<int>() ?? 0;
                var propertyPath = parameters["propertyPath"]?.ToString();
                var value = parameters["value"];

                // The shared resolver rather than GameObject.Find: Find skips anything switched
                // off, does not take the /Name[1] form the hierarchy reports for repeated
                // siblings, and does not see into an open prefab stage. These three tools are
                // the ones most often pointed at something the caller has just deactivated.
                var acrossPaths = ManyPaths(parameters["objectPaths"]);

                if (acrossPaths != null)
                {
                    return WriteAcross(acrossPaths, componentType, componentIndex, parameters);
                }

                var go = Tools.ObjectResolve.Object(objectPath, instanceId);

                var offset = parameters["offset"]?.Value<int>() ?? 0;
                var limit = parameters["limit"]?.Value<int>() ?? 0;
                var fields = ListResponseBuilder.ParseFieldsParam(parameters["fields"]?.ToString());

                // The components view answers a listing only. A read or a write with no component
                // named targets the GameObject itself, which is what those tools promise; sending
                // them here answered a write with a component list and wrote nothing.
                if (mode == "list" && string.IsNullOrEmpty(componentType))
                {
                    var detail = parameters["detail"]?.ToString() ?? "standard";
                    return ListComponents(
                        go, offset, limit <= 0 ? int.MaxValue : limit, fields, detail);
                }

                SerializedObject serializedObject;
                string target;

                if (string.IsNullOrEmpty(componentType))
                {
                    serializedObject = new SerializedObject(go);
                    target = "GameObject";
                }
                else
                {
                    var component = FindComponent(go, componentType, componentIndex);
                    if (component == null)
                    {
                        return new JObject
                        {
                            ["error"] = $"Component '{componentType}' (index {componentIndex}) not found on '{go.name}'"
                        };
                    }

                    serializedObject = new SerializedObject(component);
                    target = componentType;
                }

                if (mode == "write")
                {
                    return parameters["values"] is JObject many
                        ? WriteProperties(serializedObject, many, target)
                        : WriteProperty(serializedObject, propertyPath, value, target);
                }

                // Read mode
                if (string.IsNullOrEmpty(propertyPath))
                {
                    return ListProperties(serializedObject, target, offset, limit, fields);
                }

                return ReadProperty(serializedObject, propertyPath, target);
            }
            catch (McpToolException)
            {
                // A refusal answers the request. Folded into the failure below it reads as the
                // Editor being unreadable, and a caller retries rather than correcting what it
                // sent.
                throw;
            }
            catch (Exception e)
            {
                return new JObject { ["error"] = $"InspectorAccess error: {e.Message}" };
            }
        }

        /// <summary>The paths a caller named, or null when it named one object as usual.</summary>
        private static string[] ManyPaths(JToken token)
        {
            if (token is not JArray array || array.Count == 0)
            {
                return null;
            }

            return array.Select(t => t.ToString()).ToArray();
        }

        /// <summary>
        /// The same edit across several objects, the way the Inspector edits a multi-selection.
        /// </summary>
        /// <remarks>
        /// One SerializedObject over many targets is Unity's own answer to this, so the write
        /// lands on all of them in a single undo step rather than one call and one step each.
        /// Swapping a material on three hundred objects cost two hundred and ninety-nine calls
        /// that differed only in which object they named.
        /// <para>
        /// Every object is resolved before anything is written: an unresolvable path costs a
        /// refusal, not a scene where some of the selection changed and the rest did not.
        /// </para>
        /// </remarks>
        private static JObject WriteAcross(
            string[] paths, string componentType, int componentIndex, JObject parameters)
        {
            if (parameters["mode"]?.ToString() != "write")
            {
                throw new McpToolException(
                    "invalid_params",
                    "'object_paths' writes to several objects at once; it has no meaning for a read.");
            }

            if (paths.Length > MaxTargets)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'object_paths' takes at most {MaxTargets} objects at a time; "
                    + $"{paths.Length} were given.");
            }

            var targets = new List<UnityEngine.Object>(paths.Length);

            foreach (var path in paths)
            {
                var go = Tools.ObjectResolve.Object(path, null);

                if (string.IsNullOrEmpty(componentType))
                {
                    targets.Add(go);
                    continue;
                }

                var component = FindComponent(go, componentType, componentIndex);

                if (component == null)
                {
                    throw new McpToolException(
                        "not_found",
                        $"'{path}' has no '{componentType}' at index {componentIndex}. "
                        + "Nothing was written.");
                }

                targets.Add(component);
            }

            var serialized = new SerializedObject(targets.ToArray());
            var label = string.IsNullOrEmpty(componentType) ? "GameObject" : componentType;

            var written = parameters["values"] is JObject set
                ? WriteProperties(serialized, set, label)
                : WriteProperty(serialized, parameters["propertyPath"]?.ToString(), parameters["value"], label);

            if (written["error"] == null)
            {
                written["objects"] = paths.Length;
            }

            return written;
        }

        /// <summary>The most objects one write will reach.</summary>
        /// <remarks>
        /// A cap rather than a limit anyone should hit: it exists so a mistyped filter that
        /// selected the whole scene is a refusal instead of an edit to every object in it.
        /// </remarks>
        private const int MaxTargets = 500;

        private static int MissingScriptCount(Component[] components)
        {
            var missing = 0;

            foreach (var comp in components)
            {
                if (comp == null)
                {
                    missing++;
                }
            }

            return missing;
        }

        private static JObject ListComponents(
            GameObject go,
            int offset,
            int limit,
            string[] fieldsFilter,
            string detail)
        {
            var components = go.GetComponents<Component>();

            // Build flat list of component JObjects first, shaped by `detail`:
            //   - "summary":  only type + index
            //   - "standard": + enabled (default behaviour before R5 rework)
            //   - "full":     + full serialized-property listing (capped)
            var isSummary = string.Equals(detail, "summary", StringComparison.OrdinalIgnoreCase);
            var isFull = string.Equals(detail, "full", StringComparison.OrdinalIgnoreCase);

            var allComponents = new List<JObject>(components.Length);
            for (var i = 0; i < components.Length; i++)
            {
                var comp = components[i];
                if (comp == null)
                {
                    // Unity cannot resolve the script behind this component: the class was
                    // renamed, or the package that declared it is gone. Its serialized values are
                    // still on the object and are lost the moment someone removes it.
                    allComponents.Add(new JObject
                    {
                        ["type"] = SceneHierarchy.MissingScript,
                        ["index"] = 0,
                        ["missingScript"] = true,
                    });
                    continue;
                }

                var typeName = comp.GetType().Name;

                // Count index among same-type components before this one.
                var sameTypeIndex = 0;
                for (var j = 0; j < i; j++)
                {
                    if (components[j] != null && components[j].GetType().Name == typeName)
                        sameTypeIndex++;
                }

                var entry = new JObject
                {
                    ["type"] = typeName,
                    ["index"] = sameTypeIndex
                };

                if (!isSummary)
                {
                    var behaviour = comp as Behaviour;
                    if (behaviour != null)
                    {
                        entry["enabled"] = behaviour.enabled;
                    }
                    else
                    {
                        var renderer = comp as Renderer;
                        if (renderer != null)
                            entry["enabled"] = renderer.enabled;
                        var collider = comp as Collider;
                        if (collider != null)
                            entry["enabled"] = collider.enabled;
                    }
                }

                if (isFull)
                {
                    try
                    {
                        var so = new SerializedObject(comp);
                        var properties = new JArray();

                        // The same walk the narrowed listing uses. Left on visibility, this view
                        // dropped a HingeJoint's m_ConnectedBody while the other one showed it,
                        // so the same question got two answers depending on how it was asked.
                        foreach (var property in SerializedValues.TopLevel(so))
                        {
                            if (properties.Count >= MaxProperties)
                            {
                                break;
                            }

                            properties.Add(SerializedValues.Describe(property));
                        }

                        entry["properties"] = properties;
                    }
                    catch
                    {
                        // Component may not be a UnityEngine.Object (rare) — skip.
                    }
                }

                allComponents.Add(entry);
            }

            var page = ListResponseBuilder.Build(
                allComponents,
                offset,
                limit,
                item => item,
                fieldsFilter
            );

            return new JObject
            {
                ["gameObject"] = go.name,
                ["instanceId"] = EntityIdCompat.WireIdOf(go),
                ["components"] = page["items"],
                ["missingScripts"] = MissingScriptCount(components),
                ["truncated"] = page["truncated"],
                ["next"] = page["next"]
            };
        }

        private static Component FindComponent(GameObject go, string typeName, int index)
        {
            var components = go.GetComponents<Component>();
            var count = 0;

            foreach (var comp in components)
            {
                if (comp == null) continue;
                if (comp.GetType().Name == typeName)
                {
                    if (count == index)
                        return comp;
                    count++;
                }
            }

            return null;
        }

        private static JObject ListProperties(
            SerializedObject serializedObject, string componentType, int offset, int limit, string[] fields)
        {
            var all = new List<JObject>();

            foreach (var property in SerializedValues.TopLevel(serializedObject))
            {
                if (all.Count >= MaxProperties)
                {
                    break;
                }

                all.Add(SerializedValues.Describe(property));
            }

            var page = ListResponseBuilder.Build(all, offset, limit, item => item, fields);

            return new JObject
            {
                ["component"] = componentType,
                ["properties"] = page["items"],
                ["count"] = all.Count,
                ["truncated"] = page["truncated"],
                ["next"] = page["next"],
            };
        }

        private static JObject ReadProperty(SerializedObject serializedObject, string propertyPath,
            string componentType)
        {
            var prop = serializedObject.FindProperty(propertyPath);
            if (prop == null)
            {
                return new JObject
                {
                    ["error"] = $"Property '{propertyPath}' not found on component '{componentType}'"
                };
            }

            return new JObject
            {
                ["component"] = componentType,
                ["property"] = SerializedValues.Describe(prop)
            };
        }

        /// <summary>Writes several properties on the one component, or none of them.</summary>
        /// <remarks>
        /// All or nothing, which one SerializedObject makes free: nothing reaches the object
        /// until ApplyModifiedProperties, so a path that does not resolve costs the caller a
        /// refusal rather than a component half configured. Setting up a single
        /// ConfigurableJoint took twenty-one calls before this, one property at a time.
        /// </remarks>
        private static JObject WriteProperties(
            SerializedObject serializedObject, JObject values, string componentType)
        {
            if (values.Count == 0)
            {
                return new JObject { ["error"] = "'values' is empty; name at least one property." };
            }

            var conflict = SerializedValues.BatchPathConflict(values);
            if (conflict != null)
            {
                return new JObject { ["error"] = conflict };
            }

            var written = new JObject();

            foreach (var pair in values)
            {
                var prop = serializedObject.FindProperty(pair.Key);

                if (prop == null)
                {
                    return new JObject
                    {
                        ["error"] = $"Property '{pair.Key}' not found on component "
                                    + $"'{componentType}'. Nothing was written; inspect_list "
                                    + "names the paths this component takes.",
                    };
                }

                var failed = SerializedValues.Write(prop, pair.Value);

                if (failed != null)
                {
                    return new JObject
                    {
                        ["error"] = $"'{pair.Key}': {failed} Nothing was written.",
                    };
                }
            }

            serializedObject.ApplyModifiedProperties();

            foreach (var pair in values)
            {
                var prop = serializedObject.FindProperty(pair.Key);
                written[pair.Key] = SerializedValues.Read(prop);
            }

            return Tools.EditorNotes.SceneChange(
                new JObject
                {
                    ["component"] = componentType,
                    ["written"] = written,
                    ["count"] = written.Count,
                },
                serializedObject.targetObject);
        }

        private static JObject WriteProperty(SerializedObject serializedObject, string propertyPath,
            JToken value, string componentType)
        {
            if (string.IsNullOrEmpty(propertyPath))
            {
                return new JObject { ["error"] = "property_path is required for write mode" };
            }

            if (value == null)
            {
                return new JObject { ["error"] = "value is required for write mode" };
            }

            var prop = serializedObject.FindProperty(propertyPath);
            if (prop == null)
            {
                return new JObject
                {
                    ["error"] = $"Property '{propertyPath}' not found on component '{componentType}'"
                };
            }

            var writeError = SerializedValues.Write(prop, value);
            if (writeError != null)
            {
                return new JObject { ["error"] = writeError };
            }

            serializedObject.ApplyModifiedProperties();

            // Re-read to return updated value
            serializedObject.Update();
            prop = serializedObject.FindProperty(propertyPath);

            var written = new JObject
            {
                ["component"] = componentType,
                ["property"] = SerializedValues.Describe(prop),
                ["written"] = true
            };

            // The same key every other editing tool uses, and the one both docs and the skill
            // already name. A second key for the same fact would have to be learnt twice.
            return Tools.EditorNotes.SceneChange(written, serializedObject.targetObject);
        }

    }
}
