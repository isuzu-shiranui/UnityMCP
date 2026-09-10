using System;
using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;
using UnityEngine;

using UnityObject = UnityEngine.Object;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Turning a <see cref="SerializedProperty"/> into JSON and back.
    /// </summary>
    /// <remarks>
    /// A component's fields and a project setting's are the same thing to Unity, so the conversion
    /// lives here rather than beside either of them. Two copies of this type switch would drift,
    /// and the drift would show as one tool reading a value the other cannot write.
    /// </remarks>
    internal static class SerializedValues
    {
        public static JObject Describe(SerializedProperty prop)
        {
            return new JObject
            {
                ["path"] = prop.propertyPath,
                ["type"] = prop.propertyType.ToString(),
                ["value"] = Read(prop)
            };
        }

        public static JToken Read(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    // An unsigned field reports as Integer, and intValue hands back its bits read
                    // as signed: a physics layer mask of 4292870143 reads as -2097153. Written
                    // back, that number is clamped to 0 and the reply still says it was written.
                    return Unsigned(prop, out var unsignedValue) ? unsignedValue : prop.intValue;

                // A culling mask or a collision mask is where a "why is this not drawn / not hit"
                // question ends up, and the bare bitmask is unreadable: Everything is -1 and one
                // layer switched off is 2147483645. The names are what the question is about.
                case SerializedPropertyType.LayerMask:
                    return new JObject
                    {
                        ["mask"] = prop.intValue,
                        ["layers"] = LayerNames(prop.intValue),
                    };

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
                    // An array reports its length and how to reach an element: a bare type name
                    // left a caller no way to tell an empty list from one it could not read.
                    if (prop.isArray && prop.propertyType != SerializedPropertyType.String)
                    {
                        return new JObject
                        {
                            ["isArray"] = true,
                            ["length"] = prop.arraySize,
                            ["elementPath"] = prop.propertyPath + ".Array.data[0]",
                            ["lengthPath"] = prop.propertyPath + ".Array.size",
                        };
                    }

                    // A struct holds its values in its leaves, and nothing else says the leaves
                    // exist: a caller reading a HingeJoint's spring or a Light's shadow settings
                    // is answered "Generic" and has nowhere to go next.
                    var fields = Children(prop);

                    return fields == null
                        ? new JObject { ["type"] = prop.propertyType.ToString() }
                        : new JObject { ["fields"] = fields };
            }
        }

        /// <summary>
        /// Unity's per-Object header, present on every component and never what a caller wants.
        /// </summary>
        private static readonly HashSet<string> Bookkeeping = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_ObjectHideFlags",
            "m_CorrespondingSourceObject",
            "m_PrefabInstance",
            "m_PrefabAsset",
            "m_GameObject",
        };

        /// <summary>Whether a path names one of Unity's per-Object header fields.</summary>
        public static bool IsBookkeeping(string propertyPath)
        {
            return Bookkeeping.Contains(propertyPath);
        }

        /// <summary>Walks a serialized object's top-level properties.</summary>
        /// <remarks>
        /// Next rather than NextVisible: Unity marks a field invisible when the type's own
        /// Inspector draws it by hand, and a HingeJoint's m_ConnectedBody is exactly that. Listed
        /// by visibility, the one property the caller came to find is the one left out, while
        /// inspect_read and inspect_write both take it. The header fields are dropped by name
        /// instead, which is what visibility was standing in for.
        /// </remarks>
        public static IEnumerable<SerializedProperty> TopLevel(SerializedObject serialized)
        {
            var iterator = serialized.GetIterator();
            var entering = true;

            while (iterator.Next(entering))
            {
                entering = false;

                if (Bookkeeping.Contains(iterator.propertyPath))
                {
                    continue;
                }

                yield return iterator;
            }
        }

        /// <summary>The paths of a property's immediate children, or null when it has none.</summary>
        private static JArray Children(SerializedProperty prop)
        {
            if (!prop.hasChildren)
            {
                return null;
            }

            var fields = new JArray();
            var child = prop.Copy();
            var end = prop.GetEndProperty();
            var entering = true;

            while (child.NextVisible(entering) && !SerializedProperty.EqualContents(child, end))
            {
                entering = false;
                fields.Add(child.propertyPath);

                if (fields.Count == MaxFields)
                {
                    break;
                }
            }

            return fields.Count == 0 ? null : fields;
        }

        /// <summary>
        /// Enough for any struct Unity serializes. The cap is here so a type nobody anticipated
        /// cannot turn one property into a page of paths.
        /// </summary>
        private const int MaxFields = 32;

        /// <summary>
        /// Points a reference property at the object a caller named: a scene path, an asset path,
        /// an instance id, or null to clear it.
        /// </summary>
        /// <remarks>
        /// A field's accepted type is only visible through <c>SerializedProperty.type</c>, which
        /// spells it <c>PPtr&lt;$Transform&gt;</c>. Without reading it, a path naming a GameObject
        /// would be assigned to a Transform field and silently refused by Unity, leaving the
        /// property unchanged while the call reported success.
        /// </remarks>
        private static string SetObjectReference(SerializedProperty prop, JToken value)
        {
            var wanted = ReferencedTypeName(prop.type);

            if (value.Type == JTokenType.Null
                || (value.Type == JTokenType.String && string.IsNullOrEmpty(value.Value<string>())))
            {
                prop.objectReferenceValue = null;
                return null;
            }

            UnityObject resolved = null;

            if (value.Type == JTokenType.Integer)
            {
                resolved = EntityIdCompat.Find(value.Value<long>());
            }
            else
            {
                var text = value.Value<string>();

                if (string.IsNullOrWhiteSpace(text))
                {
                    return "'value' has to name an object, or be null to clear the reference.";
                }

                if (long.TryParse(text, out var id))
                {
                    resolved = EntityIdCompat.Find(id);
                }
                else if (text.StartsWith("Assets/", StringComparison.Ordinal)
                         || text.StartsWith("Packages/", StringComparison.Ordinal))
                {
                    var problem = LoadAsset(text, wanted, out resolved);

                    if (problem != null)
                    {
                        return problem;
                    }
                }
                else
                {
                    // Not GameObject.Find: it skips anything switched off, and a spare collider or
                    // a hidden outfit is exactly the thing a reference points at.
                    try
                    {
                        resolved = Tools.ObjectResolve.Object(text, null, "value", null);
                    }
                    catch (McpToolException)
                    {
                        return $"'{text}' is not a scene path, an asset path under Assets/ or "
                               + "Packages/, or an instance id.";
                    }
                }
            }

            if (resolved == null)
            {
                return "That object no longer exists.";
            }

            // A path names a GameObject; a field usually wants one of its components.
            if (resolved is GameObject named && wanted != null && wanted != "GameObject")
            {
                var component = named.GetComponent(wanted);

                if (component != null)
                {
                    resolved = component;
                }
            }

            prop.objectReferenceValue = resolved;

            // Unity drops an assignment whose type the field does not accept, without saying so.
            if (prop.objectReferenceValue == null)
            {
                return $"'{resolved.name}' is a {resolved.GetType().Name}, and this property holds "
                       + $"a {wanted ?? "different type"}. Name an object of that type, or assign "
                       + "it through execute_code: new SerializedObject(component)"
                       + ".FindProperty(path).objectReferenceValue = target, then "
                       + "ApplyModifiedProperties().";
            }

            return null;
        }

        /// <summary>The unsigned value of a property whose field has no sign, when it has one.</summary>
        /// <remarks>
        /// Reached by name because SerializedProperty.uintValue is not on every Unity this package
        /// supports, and naming it directly stops the package building on the ones without it.
        /// </remarks>
        private static bool Unsigned(SerializedProperty prop, out ulong value)
        {
            value = 0;

            if (!IsUnsigned(prop.type))
            {
                return false;
            }

            var reader = typeof(SerializedProperty).GetProperty(
                prop.type == "ulong" ? "ulongValue" : "uintValue");

            if (reader == null)
            {
                return false;
            }

            var read = reader.GetValue(prop);

            value = read is ulong wide ? wide : Convert.ToUInt64(read);
            return true;
        }

        /// <summary>Whether this field holds a number that cannot be negative.</summary>
        private static bool IsUnsigned(string fieldType)
        {
            return fieldType == "uint" || fieldType == "ulong"
                   || fieldType == "ushort" || fieldType == "UInt32" || fieldType == "UInt64";
        }

        /// <summary>Writes a whole number, honouring a field that has no sign.</summary>
        /// <remarks>
        /// intValue on an unsigned field cannot carry the top bit: Unity clamps -1 to 0 rather
        /// than storing every bit set. A physics layer mask is exactly that number, so writing one
        /// back the way it was read turned "collide with everything" into "collide with nothing",
        /// under a reply saying it was written.
        /// </remarks>
        private static string WriteInteger(SerializedProperty prop, JToken value)
        {
            if (!IsUnsigned(prop.type))
            {
                prop.intValue = value.Value<int>();
                return null;
            }

            var wanted = value.Value<long>();

            if (wanted < 0)
            {
                return $"'{prop.propertyPath}' is a {prop.type}, which cannot be negative. "
                       + $"Read it back to see the number it holds now; {wanted} would be stored "
                       + "as 0.";
            }

            var writer = typeof(SerializedProperty).GetProperty(
                prop.type == "ulong" || prop.type == "UInt64" ? "ulongValue" : "uintValue");

            if (writer == null)
            {
                // No unsigned accessor on this Unity. Anything that fits a signed int still
                // round-trips; the rest would be silently clamped, so it is refused instead.
                if (wanted > int.MaxValue)
                {
                    return $"'{prop.propertyPath}' is a {prop.type} and this Unity version has no "
                           + "unsigned accessor for it, so a value above int.MaxValue cannot be "
                           + "written here. Use execute_code for this one.";
                }

                prop.intValue = (int)wanted;
                return null;
            }

            writer.SetValue(
                prop,
                writer.PropertyType == typeof(ulong) ? (object)(ulong)wanted : (object)(uint)wanted);

            return null;
        }

        /// <summary>The names of the layers a mask includes.</summary>
        /// <remarks>
        /// The two masks worth naming in one word are the two that occur most: a camera that draws
        /// everything spells out all thirty-two layers otherwise, and twenty-six of them have no
        /// name to give. The Inspector calls these Everything and Nothing.
        /// </remarks>
        private static JArray LayerNames(int mask)
        {
            var names = new JArray();

            if (mask == -1)
            {
                names.Add("Everything");
                return names;
            }

            if (mask == 0)
            {
                names.Add("Nothing");
                return names;
            }

            for (var layer = 0; layer < 32; layer++)
            {
                if ((mask & (1 << layer)) == 0)
                {
                    continue;
                }

                var name = LayerMask.LayerToName(layer);

                // An unnamed layer is still in the mask, and saying so by number beats leaving a
                // gap the caller cannot account for.
                names.Add(string.IsNullOrEmpty(name) ? layer.ToString() : name);
            }

            return names;
        }

        /// <summary>Takes a mask as a number, or as the list of layers it should hold.</summary>
        private static string WriteLayerMask(SerializedProperty prop, JToken value)
        {
            if (value.Type == JTokenType.Integer)
            {
                prop.intValue = value.Value<int>();
                return null;
            }

            if (value is JArray wanted)
            {
                var mask = 0;

                foreach (var entry in wanted)
                {
                    var name = entry.ToString();

                    // Symmetry with what a read hands back: whatever it says has to be writable.
                    if (name == "Everything")
                    {
                        prop.intValue = -1;
                        return null;
                    }

                    if (name == "Nothing")
                    {
                        prop.intValue = 0;
                        return null;
                    }

                    var layer = int.TryParse(name, out var index) ? index : LayerMask.NameToLayer(name);

                    if (layer < 0 || layer > 31)
                    {
                        return $"'{name}' is not a layer in this project. project_settings with "
                               + "section 'tags' lists the ones that are.";
                    }

                    mask |= 1 << layer;
                }

                prop.intValue = mask;
                return null;
            }

            return "A layer mask takes a number, or the list of layer names it should hold, "
                   + "e.g. [\"Default\", \"Water\"].";
        }

        /// <summary>The asset at a path that a field of the wanted type can hold.</summary>
        /// <remarks>
        /// A PNG's main asset is its Texture2D and its Sprite is a sub-asset, so loading the path
        /// alone hands a Sprite field the one object in the file it cannot take.
        /// </remarks>
        private static string LoadAsset(string path, string wanted, out UnityObject resolved)
        {
            resolved = AssetDatabase.LoadAssetAtPath<UnityObject>(path);

            if (resolved == null)
            {
                return $"No asset at '{path}'.";
            }

            if (wanted == null || IsNamed(resolved, wanted))
            {
                return null;
            }

            // LoadAllAssets throws on a scene, and refuses to say so quietly.
            if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                return $"'{path}' is a scene, and this property holds a {wanted}.";
            }

            // LoadAllAssets rather than LoadAllAssetRepresentations: the latter returns only what
            // the Project window shows, and a blend tree or a sub-state machine is hidden.
            var matches = new List<UnityObject>();

            foreach (var candidate in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (candidate != null && candidate != resolved && IsNamed(candidate, wanted))
                {
                    matches.Add(candidate);
                }
            }

            if (matches.Count == 1)
            {
                resolved = matches[0];
                return null;
            }

            if (matches.Count > 1)
            {
                return $"'{path}' holds {matches.Count} of type {wanted}: "
                       + string.Join(", ", matches.Select(m => m.name))
                       + ". A path cannot say which; pass the instance id of the one you mean.";
            }

            // Nothing of that type in the file. Handing the main asset over anyway would be
            // rejected by Unity without a word, so say what is there.
            return $"'{path}' holds no {wanted}; its main asset is a {resolved.GetType().Name}.";
        }

        /// <summary>Whether an object is of a type of this name, base types included.</summary>
        private static bool IsNamed(UnityObject o, string typeName)
        {
            for (var type = o.GetType(); type != null; type = type.BaseType)
            {
                if (type.Name == typeName)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The type inside <c>PPtr&lt;Transform&gt;</c>, or null when it is not one.</summary>
        /// <remarks>
        /// A field declared in managed code spells it <c>PPtr&lt;$Transform&gt;</c> and one Unity
        /// serializes itself spells it <c>PPtr&lt;Transform&gt;</c>. Reading only the first form
        /// leaves every built-in component's reference fields unnarrowed and unnamed.
        /// </remarks>
        private static string ReferencedTypeName(string propertyType)
        {
            const string prefix = "PPtr<";

            if (propertyType == null
                || !propertyType.StartsWith(prefix, StringComparison.Ordinal)
                || !propertyType.EndsWith(">", StringComparison.Ordinal))
            {
                return null;
            }

            var inner = propertyType.Substring(prefix.Length, propertyType.Length - prefix.Length - 1);

            return inner.StartsWith("$", StringComparison.Ordinal) ? inner.Substring(1) : inner;
        }

        public static string Write(SerializedProperty prop, JToken value)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return WriteInteger(prop, value);

                case SerializedPropertyType.LayerMask:
                    return WriteLayerMask(prop, value);

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
                    return SetObjectReference(prop, value);

                case SerializedPropertyType.ArraySize:
                    if (value.Type != JTokenType.Integer)
                    {
                        return "An array's length takes a whole number.";
                    }

                    var length = value.Value<int>();

                    if (length < 0)
                    {
                        return "An array cannot be shorter than nothing.";
                    }

                    prop.intValue = length;
                    return null;

                default:
                    return $"Writing property type '{prop.propertyType}' is not supported. "
                           + "A whole struct or list is assigned through execute_code: "
                           + "new SerializedObject(component), then the fields under "
                           + "FindProperty(path), then ApplyModifiedProperties().";
            }
        }
    }
}
