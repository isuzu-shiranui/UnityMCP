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
                var gameObjectPath = parameters["gameObjectPath"]?.ToString();
                var componentType = parameters["componentType"]?.ToString();
                var componentIndex = parameters["componentIndex"]?.Value<int>() ?? 0;
                var propertyPath = parameters["propertyPath"]?.ToString();
                var value = parameters["value"];

                // Find GameObject
                GameObject go = null;
                if (instanceId.HasValue)
                {
                    var obj = EntityIdCompat.Find(instanceId.Value);
                    go = obj as GameObject;
                }

                if (go == null && !string.IsNullOrEmpty(gameObjectPath))
                {
                    go = GameObject.Find(gameObjectPath);
                }

                if (go == null)
                {
                    if (!instanceId.HasValue && string.IsNullOrEmpty(gameObjectPath))
                        return new JObject { ["error"] = "Either instanceId or gameObjectPath is required" };
                    return new JObject { ["error"] = "GameObject not found" };
                }

                // No componentType: list all components (context-economy aware).
                if (string.IsNullOrEmpty(componentType))
                {
                    var listOffset = parameters["offset"]?.Value<int>() ?? 0;
                    var listLimit = parameters["limit"]?.Value<int>() ?? int.MaxValue;
                    var listFields = ListResponseBuilder.ParseFieldsParam(parameters["fields"]?.ToString());
                    var detail = parameters["detail"]?.ToString() ?? "standard";
                    return ListComponents(go, listOffset, listLimit, listFields, detail);
                }

                // Find the target component
                var component = FindComponent(go, componentType, componentIndex);
                if (component == null)
                {
                    return new JObject
                    {
                        ["error"] = $"Component '{componentType}' (index {componentIndex}) not found on '{go.name}'"
                    };
                }

                var serializedObject = new SerializedObject(component);

                if (mode == "write")
                {
                    return WriteProperty(serializedObject, propertyPath, value, componentType);
                }

                // Read mode
                if (string.IsNullOrEmpty(propertyPath))
                {
                    return ListProperties(serializedObject, componentType);
                }

                return ReadProperty(serializedObject, propertyPath, componentType);
            }
            catch (Exception e)
            {
                return new JObject { ["error"] = $"InspectorAccess error: {e.Message}" };
            }
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
                    allComponents.Add(new JObject { ["type"] = "null", ["index"] = 0 });
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
                        var iterator = so.GetIterator();
                        var enterChildren = true;
                        var count = 0;
                        while (iterator.NextVisible(enterChildren) && count < MaxProperties)
                        {
                            enterChildren = false;
                            properties.Add(BuildPropertyInfo(iterator));
                            count++;
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

        private static JObject ListProperties(SerializedObject serializedObject, string componentType)
        {
            var properties = new JArray();
            var iterator = serializedObject.GetIterator();
            var enterChildren = true;
            var count = 0;

            while (iterator.NextVisible(enterChildren) && count < MaxProperties)
            {
                enterChildren = false;
                properties.Add(BuildPropertyInfo(iterator));
                count++;
            }

            return new JObject
            {
                ["component"] = componentType,
                ["properties"] = properties
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
                ["property"] = BuildPropertyInfo(prop)
            };
        }

        private static JObject WriteProperty(SerializedObject serializedObject, string propertyPath,
            JToken value, string componentType)
        {
            if (string.IsNullOrEmpty(propertyPath))
            {
                return new JObject { ["error"] = "propertyPath is required for write mode" };
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

            var writeError = SetPropertyValue(prop, value);
            if (writeError != null)
            {
                return new JObject { ["error"] = writeError };
            }

            serializedObject.ApplyModifiedProperties();

            // Re-read to return updated value
            serializedObject.Update();
            prop = serializedObject.FindProperty(propertyPath);

            return new JObject
            {
                ["component"] = componentType,
                ["property"] = BuildPropertyInfo(prop),
                ["written"] = true
            };
        }

        private static JObject BuildPropertyInfo(SerializedProperty prop)
        {
            return new JObject
            {
                ["path"] = prop.propertyPath,
                ["type"] = prop.propertyType.ToString(),
                ["value"] = GetPropertyValue(prop)
            };
        }

        private static JToken GetPropertyValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return prop.intValue;

                case SerializedPropertyType.Float:
                    return prop.floatValue;

                case SerializedPropertyType.Boolean:
                    return prop.boolValue;

                case SerializedPropertyType.String:
                    return prop.stringValue;

                case SerializedPropertyType.Enum:
                    var enumNames = prop.enumDisplayNames;
                    var enumIndex = prop.enumValueIndex;
                    return new JObject
                    {
                        ["index"] = enumIndex,
                        ["name"] = enumIndex >= 0 && enumIndex < enumNames.Length
                            ? enumNames[enumIndex]
                            : "Unknown"
                    };

                case SerializedPropertyType.Vector2:
                    var v2 = prop.vector2Value;
                    return new JObject { ["x"] = v2.x, ["y"] = v2.y };

                case SerializedPropertyType.Vector3:
                    var v3 = prop.vector3Value;
                    return new JObject { ["x"] = v3.x, ["y"] = v3.y, ["z"] = v3.z };

                case SerializedPropertyType.Vector4:
                    var v4 = prop.vector4Value;
                    return new JObject { ["x"] = v4.x, ["y"] = v4.y, ["z"] = v4.z, ["w"] = v4.w };

                case SerializedPropertyType.Color:
                    var c = prop.colorValue;
                    return new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };

                case SerializedPropertyType.Quaternion:
                    var q = prop.quaternionValue;
                    return new JObject { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w };

                case SerializedPropertyType.Rect:
                    var r = prop.rectValue;
                    return new JObject
                    {
                        ["x"] = r.x, ["y"] = r.y, ["width"] = r.width, ["height"] = r.height
                    };

                case SerializedPropertyType.Bounds:
                    var b = prop.boundsValue;
                    return new JObject
                    {
                        ["center"] = new JObject
                        {
                            ["x"] = b.center.x, ["y"] = b.center.y, ["z"] = b.center.z
                        },
                        ["size"] = new JObject
                        {
                            ["x"] = b.size.x, ["y"] = b.size.y, ["z"] = b.size.z
                        }
                    };

                case SerializedPropertyType.ObjectReference:
                    var objRef = prop.objectReferenceValue;
                    return new JObject
                    {
                        ["instanceId"] = EntityIdCompat.WireObjectReferenceId(prop),
                        ["name"] = objRef != null ? objRef.name : null,
                        ["type"] = objRef != null ? objRef.GetType().Name : null
                    };

                case SerializedPropertyType.ArraySize:
                    return prop.intValue;

                default:
                    return new JObject { ["type"] = prop.propertyType.ToString() };
            }
        }

        private static string SetPropertyValue(SerializedProperty prop, JToken value)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    prop.intValue = value.Value<int>();
                    return null;

                case SerializedPropertyType.Float:
                    prop.floatValue = value.Value<float>();
                    return null;

                case SerializedPropertyType.Boolean:
                    prop.boolValue = value.Value<bool>();
                    return null;

                case SerializedPropertyType.String:
                    prop.stringValue = value.Value<string>();
                    return null;

                case SerializedPropertyType.Enum:
                    if (value.Type == JTokenType.Integer)
                    {
                        prop.enumValueIndex = value.Value<int>();
                    }
                    else
                    {
                        var name = value.Value<string>();
                        var names = prop.enumDisplayNames;
                        var found = false;
                        for (var i = 0; i < names.Length; i++)
                        {
                            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                            {
                                prop.enumValueIndex = i;
                                found = true;
                                break;
                            }
                        }
                        if (!found)
                        {
                            return $"Enum value '{name}' not found. Valid values: {string.Join(", ", names)}";
                        }
                    }
                    return null;

                case SerializedPropertyType.Vector2:
                    prop.vector2Value = new Vector2(
                        value["x"].Value<float>(),
                        value["y"].Value<float>());
                    return null;

                case SerializedPropertyType.Vector3:
                    prop.vector3Value = new Vector3(
                        value["x"].Value<float>(),
                        value["y"].Value<float>(),
                        value["z"].Value<float>());
                    return null;

                case SerializedPropertyType.Vector4:
                    prop.vector4Value = new Vector4(
                        value["x"].Value<float>(),
                        value["y"].Value<float>(),
                        value["z"].Value<float>(),
                        value["w"].Value<float>());
                    return null;

                case SerializedPropertyType.Color:
                    prop.colorValue = new Color(
                        value["r"].Value<float>(),
                        value["g"].Value<float>(),
                        value["b"].Value<float>(),
                        value["a"].Value<float>());
                    return null;

                case SerializedPropertyType.Quaternion:
                    prop.quaternionValue = new Quaternion(
                        value["x"].Value<float>(),
                        value["y"].Value<float>(),
                        value["z"].Value<float>(),
                        value["w"].Value<float>());
                    return null;

                case SerializedPropertyType.Rect:
                    prop.rectValue = new Rect(
                        value["x"].Value<float>(),
                        value["y"].Value<float>(),
                        value["width"].Value<float>(),
                        value["height"].Value<float>());
                    return null;

                case SerializedPropertyType.Bounds:
                    prop.boundsValue = new Bounds(
                        new Vector3(
                            value["center"]["x"].Value<float>(),
                            value["center"]["y"].Value<float>(),
                            value["center"]["z"].Value<float>()),
                        new Vector3(
                            value["size"]["x"].Value<float>(),
                            value["size"]["y"].Value<float>(),
                            value["size"]["z"].Value<float>()));
                    return null;

                case SerializedPropertyType.ObjectReference:
                    return "Writing ObjectReference is not supported";

                default:
                    return $"Writing property type '{prop.propertyType}' is not supported";
            }
        }
    }
}
