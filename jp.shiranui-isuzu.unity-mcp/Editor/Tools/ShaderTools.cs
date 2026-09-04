using System;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;

using UnityEditor.Rendering;
using UnityEditor.SceneManagement;

using UnityEngine;
using UnityEngine.Rendering;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Shaders and materials: what they compiled to, and what they are set to.
    /// </summary>
    internal static class ShaderTools
    {
        [McpTool(
            "shader_errors",
            "Report shader compilation errors and warnings. A shader that fails to compile does not " +
            "stop the Editor or show up in the console after the fact — it renders magenta and stays " +
            "quiet, so this has to be asked for. Omit the path to check every shader under Assets. " +
            "Shaders in packages are not checked by that sweep, including the project's own local " +
            "packages, so name the path to check one of those.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject Errors(
            [McpArg("path", "Shader asset path. Omit to check every shader under Assets; shaders in " +
                            "packages are only checked when named here.")]
            string path = null,
            [McpArg("include_warnings", "Report warnings as well as errors.")]
            bool includeWarnings = false,
            [McpArg("limit", "Maximum messages to return.")]
            int limit = 50)
        {
            var shaders = string.IsNullOrWhiteSpace(path)
                ? AssetDatabase.FindAssets("t:Shader")
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => p.StartsWith("Assets/", StringComparison.Ordinal))
                    .Select(AssetDatabase.LoadAssetAtPath<Shader>)
                    .Where(s => s != null)
                    .ToArray()
                : new[] { RequireShader(path) };

            var messages = new JArray();
            var errorCount = 0;
            var warningCount = 0;

            foreach (var shader in shaders)
            {
                var count = ShaderUtil.GetShaderMessageCount(shader);

                if (count == 0)
                {
                    continue;
                }

                foreach (var message in ShaderUtil.GetShaderMessages(shader))
                {
                    var isError = message.severity == ShaderCompilerMessageSeverity.Error;

                    if (isError)
                    {
                        errorCount++;
                    }
                    else
                    {
                        warningCount++;
                    }

                    if (!isError && !includeWarnings)
                    {
                        continue;
                    }

                    if (messages.Count >= Math.Max(limit, 0))
                    {
                        continue;
                    }

                    messages.Add(new JObject
                    {
                        ["shader"] = AssetDatabase.GetAssetPath(shader),
                        ["severity"] = isError ? "error" : "warning",
                        ["message"] = message.message,
                        ["messageDetails"] = Text(message.messageDetails),
                        ["file"] = message.file,
                        ["line"] = message.line,
                        ["platform"] = message.platform.ToString(),
                    });
                }
            }

            return new JObject
            {
                ["shadersChecked"] = shaders.Length,
                ["errorCount"] = errorCount,
                ["warningCount"] = warningCount,
                ["clean"] = errorCount == 0,
                // Against what the caller asked to see, not against everything found: with
                // include_warnings off, warnings are counted but never eligible, and comparing
                // to their total reports a truncation that did not happen.
                ["truncated"] = messages.Count < (includeWarnings ? errorCount + warningCount : errorCount),
                ["messages"] = messages,
            };
        }

        [McpTool(
            "shader_info",
            "Describe a shader asset: every property with its type and flags, the keyword space, " +
            "the render queue, and how many subshaders and passes it has. Reach for this when the " +
            "question is what a shader offers — which property name to set, which keyword exists — " +
            "and for material_read when the question is what one material currently holds. Passes " +
            "are only counted here, not named or described.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject Info(
            [McpArg("path", "Shader asset path, e.g. Assets/Shaders/Toon.shader.")]
            string path = null,
            [McpArg("name", "Shader name as written in the Shader declaration, instead of a path.")]
            string name = null)
        {
            var shader = string.IsNullOrWhiteSpace(path)
                ? RequireShaderByName(name)
                : RequireShader(path);

            var properties = new JArray();

            for (var i = 0; i < shader.GetPropertyCount(); i++)
            {
                properties.Add(new JObject
                {
                    ["name"] = shader.GetPropertyName(i),
                    ["description"] = shader.GetPropertyDescription(i),
                    ["type"] = shader.GetPropertyType(i).ToString(),
                    ["flags"] = shader.GetPropertyFlags(i).ToString(),
                });
            }

            return new JObject
            {
                ["name"] = shader.name,
                ["path"] = AssetDatabase.GetAssetPath(shader),
                ["isSupported"] = shader.isSupported,
                ["renderQueue"] = shader.renderQueue,
                ["maximumLOD"] = shader.maximumLOD,
                ["passCount"] = shader.passCount,
                ["subshaderCount"] = shader.subshaderCount,
                ["propertyCount"] = shader.GetPropertyCount(),
                ["properties"] = properties,
                ["keywordSpace"] = new JArray(shader.keywordSpace.keywordNames.Cast<object>().ToArray()),
                ["messageCount"] = ShaderUtil.GetShaderMessageCount(shader),
            };
        }

        [McpTool(
            "material_read",
            "Report a material's shader, every property's current value, its enabled keywords and its " +
            "render queue. This is the state a frame is actually drawn from, as opposed to what the " +
            "shader declares as defaults. Name a material asset with 'path', or name a scene object " +
            "with 'object_path' to read what its Renderer actually draws with, one entry per material " +
            "slot; that is the short way in from 'why is this object magenta', because it does not need " +
            "the material asset path dug out of the renderer first. Reading every slot reports each " +
            "material's 'propertyCount' rather than its values, since a renderer can carry dozens of " +
            "materials of a few hundred properties each; name a 'slot' to get that one slot's " +
            "properties. A slot whose shader is missing, unsupported, or Unity's stand-in error " +
            "shader is called out in 'shaderProblem', which is the magenta case. A material that is " +
            "not an asset is reported with a null path rather than left out.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject MaterialRead(
            [McpArg("path", "Material asset path, e.g. Assets/Art/Wood.mat. Omit when reading through " +
                            "'object_path'.")]
            string path = null,
            [McpArg("object_path", "Hierarchy path of a GameObject, from scene_browse_hierarchy. Reads " +
                                   "the materials its Renderer draws with, instead of one material asset.")]
            string objectPath = null,
            [McpArg("slot", "With 'object_path', read one material slot by index instead of all of " +
                            "them, which is also what returns that material's property values.")]
            int? slot = null)
        {
            if (string.IsNullOrWhiteSpace(objectPath))
            {
                if (slot.HasValue)
                {
                    throw new McpToolException(
                        "invalid_params",
                        "'slot' only means something with 'object_path': a material asset has no slots.");
                }

                return Describe(RequireMaterial(path), null, true);
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                throw new McpToolException(
                    "invalid_params",
                    "Pass 'path' or 'object_path', not both: one names a material asset, the other a " +
                    "scene object whose renderer holds materials.");
            }

            var go = ObjectResolve.Object(objectPath, null, "object_path", null);
            var renderer = RequireRenderer(go);
            var materials = renderer.sharedMaterials;

            var first = 0;
            var last = materials.Length;

            if (slot.HasValue)
            {
                first = RequireSlot(renderer, slot);
                last = first + 1;
            }

            var slots = new JArray();
            var broken = new JArray();

            for (var i = first; i < last; i++)
            {
                if (ShaderProblem(materials[i]) != null)
                {
                    broken.Add(i);
                }

                slots.Add(Describe(materials[i], i, slot.HasValue));
            }

            return new JObject
            {
                ["objectPath"] = ObjectResolve.PathOf(go),
                ["renderer"] = renderer.GetType().Name,
                ["slotCount"] = materials.Length,
                ["shaderProblem"] = Text(broken.Count == 0
                    ? null
                    : $"{broken.Count} of the {slots.Count} slot(s) read here cannot draw: see " +
                      "'shaderProblem' on each. This is what makes the object magenta."),
                ["brokenSlots"] = broken,
                ["slots"] = slots,
            };
        }

        [McpTool(
            "material_set",
            "Set one property on a material, or toggle one of its keywords. Name a material asset with " +
            "'path', or reach one through a scene object with 'object_path' and 'slot'. Through a " +
            "renderer this writes the shared material, the same asset the Inspector edits, so every " +
            "renderer using it changes and no per-renderer copy is made: asking a renderer for its own " +
            "copy would leave a material belonging to no .mat file embedded in the scene the next time " +
            "it is saved. An asset's .mat file is written to disk before this returns, and Undo puts the " +
            "material back in memory but not on disk, so until something saves again the file still " +
            "holds the change the Editor no longer shows. A material that is not an asset lives in the " +
            "scene instead, and its change is only kept once that scene is saved.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Material Edit")]
        public static JObject MaterialSet(
            [McpArg("path", "Material asset path. Omit when writing through 'object_path'.")]
            string path = null,
            [McpArg("object_path", "Hierarchy path of a GameObject, from scene_browse_hierarchy. Writes " +
                                   "to a material on its Renderer, instead of naming a material asset.")]
            string objectPath = null,
            [McpArg("slot", "Which material slot to write, with 'object_path'. Required once the " +
                            "renderer has more than one, so an omitted slot never writes to a material " +
                            "nobody named.")]
            int? slot = null,
            [McpArg("property", "Shader property name, e.g. _BaseColor. Omit when toggling a keyword.")]
            string property = null,
            [McpArg("value", "New value: a number, a string for textures, or {r,g,b,a}, {x,y,z,w} " +
                             "or [x,y,z,w] for a colour or vector.")]
            JToken value = null,
            [McpArg("keyword", "Shader keyword to toggle instead of setting a property.")]
            string keyword = null,
            [McpArg("enabled", "Whether the keyword should be on.")]
            bool enabled = true,
            [McpArg("render_queue", "Override the render queue; -1 puts it back to the shader's.")]
            int? renderQueue = null)
        {
            Material material;
            Renderer renderer = null;
            var slotIndex = 0;

            if (string.IsNullOrWhiteSpace(objectPath))
            {
                if (slot.HasValue)
                {
                    throw new McpToolException(
                        "invalid_params",
                        "'slot' only means something with 'object_path': a material asset has no slots.");
                }

                material = RequireMaterial(path);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    throw new McpToolException(
                        "invalid_params",
                        "Pass 'path' or 'object_path', not both: one names a material asset, the other a " +
                        "scene object whose renderer holds materials.");
                }

                var go = ObjectResolve.Object(objectPath, null, "object_path", null);
                renderer = RequireRenderer(go);
                slotIndex = RequireSlot(renderer, slot);
                material = renderer.sharedMaterials[slotIndex];

                if (material == null)
                {
                    throw new McpToolException(
                        "not_found",
                        $"Slot {slotIndex} of '{go.name}' is empty, so there is no material to write to.");
                }
            }

            Undo.RecordObject(material, "MCP Material Edit");
            var changes = new JArray();

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                if (enabled)
                {
                    material.EnableKeyword(keyword);
                }
                else
                {
                    material.DisableKeyword(keyword);
                }

                changes.Add($"keyword {keyword} = {enabled}");
            }

            if (!string.IsNullOrWhiteSpace(property))
            {
                if (!material.HasProperty(property))
                {
                    throw new McpToolException(
                        "not_found",
                        $"'{material.shader.name}' has no property '{property}'. material_read lists them.");
                }

                if (value == null || value.Type == JTokenType.Null)
                {
                    throw new McpToolException("invalid_params", "'value' is required when setting a property.");
                }

                changes.Add($"{property} = {ApplyProperty(material, property, value)}");
            }

            if (renderQueue.HasValue)
            {
                material.renderQueue = renderQueue.Value;
                changes.Add($"render_queue = {renderQueue.Value}");
            }

            if (changes.Count == 0)
            {
                throw new McpToolException(
                    "invalid_params",
                    "Nothing to do: pass 'property' with 'value', 'keyword', or 'render_queue'.");
            }

            EditorUtility.SetDirty(material);

            var assetPath = AssetDatabase.GetAssetPath(material);
            var isAsset = !string.IsNullOrEmpty(assetPath);
            var notes = new JArray();

            if (isAsset)
            {
                AssetDatabase.SaveAssetIfDirty(material);
            }
            else
            {
                notes.Add("This material is not an asset: it is stored in the scene, so the change is " +
                          "only kept once the scene is saved.");

                if (renderer != null && renderer.gameObject.scene.IsValid())
                {
                    EditorSceneManager.MarkSceneDirty(renderer.gameObject.scene);
                }
            }

            var result = new JObject
            {
                ["path"] = Text(isAsset ? assetPath : null),
                ["material"] = material.name,
                ["savedToDisk"] = isAsset,
                ["changed"] = changes,
            };

            if (renderer != null)
            {
                result["objectPath"] = ObjectResolve.PathOf(renderer.gameObject);
                result["slot"] = slotIndex;
                notes.Add("Written to the shared material, so every renderer using it draws with the " +
                          "change. No per-renderer copy was made.");
            }

            result["notes"] = notes;

            return isAsset ? result : EditorNotes.SceneChange(result);
        }

        /// <summary>
        /// One material's shader, values and keywords, with the reason it cannot draw when there is one.
        /// </summary>
        /// <param name="includeProperties">
        /// Whether to read every property's value. A shader like lilToon declares a few hundred, so
        /// a renderer with many slots answers with the count alone until one slot is asked for.
        /// </param>
        private static JObject Describe(Material material, int? slotIndex, bool includeProperties)
        {
            var problem = ShaderProblem(material);

            if (material == null)
            {
                var empty = WithSlot(slotIndex, new JObject
                {
                    ["name"] = null,
                    ["path"] = null,
                    ["isAsset"] = false,
                    ["shader"] = null,
                    ["shaderProblem"] = Text(problem),
                });

                if (includeProperties)
                {
                    empty["properties"] = new JArray();
                }
                else
                {
                    empty["propertyCount"] = 0;
                }

                return empty;
            }

            var assetPath = AssetDatabase.GetAssetPath(material);
            var isAsset = !string.IsNullOrEmpty(assetPath);
            var shader = material.shader;
            var propertyCount = shader == null ? 0 : shader.GetPropertyCount();
            var properties = new JArray();

            for (var i = 0; includeProperties && i < propertyCount; i++)
            {
                var propertyName = shader.GetPropertyName(i);
                var type = shader.GetPropertyType(i);

                JToken value;

                switch (type)
                {
                    case ShaderPropertyType.Color:
                        var c = material.GetColor(propertyName);
                        value = new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };
                        break;

                    case ShaderPropertyType.Vector:
                        var v = material.GetVector(propertyName);
                        value = new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z, ["w"] = v.w };
                        break;

                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        value = material.GetFloat(propertyName);
                        break;

                    case ShaderPropertyType.Int:
                        value = material.GetInteger(propertyName);
                        break;

                    case ShaderPropertyType.Texture:
                        var texture = material.GetTexture(propertyName);
                        value = texture == null ? null : (JToken)AssetDatabase.GetAssetPath(texture);
                        break;

                    default:
                        value = null;
                        break;
                }

                properties.Add(new JObject
                {
                    ["name"] = propertyName,
                    ["type"] = type.ToString(),
                    ["value"] = value,
                });
            }

            var described = WithSlot(slotIndex, new JObject
            {
                ["name"] = material.name,
                ["path"] = Text(isAsset ? assetPath : null),
                ["isAsset"] = isAsset,
                ["note"] = Text(isAsset
                    ? null
                    : "This material is not an asset: it was created in memory or is stored inside " +
                      "the scene, so there is no .mat file and material_read with a 'path' cannot " +
                      "reach it."),
                ["shader"] = Text(shader == null ? null : shader.name),
                ["shaderPath"] = Text(shader == null ? null : AssetDatabase.GetAssetPath(shader)),
                ["shaderIsSupported"] = shader != null && shader.isSupported,
                ["shaderProblem"] = Text(problem),
                ["renderQueue"] = material.renderQueue,
                // The material's own queue is -1 when it just follows the shader. Reporting both
                // saves a round trip when a sorting problem is being chased.
                ["renderQueueFromShader"] = shader != null && material.renderQueue == shader.renderQueue,
                ["enabledKeywords"] = new JArray(material.enabledKeywords.Select(k => (object)k.name).ToArray()),
                ["shaderKeywords"] = new JArray(material.shaderKeywords.Cast<object>().ToArray()),
                ["passCount"] = material.passCount,
            });

            if (includeProperties)
            {
                described["properties"] = properties;
            }
            else
            {
                described["propertyCount"] = propertyCount;
            }

            return described;
        }

        /// <summary>
        /// A string as a JSON value, where null becomes a JSON null.
        /// </summary>
        /// <remarks>
        /// Json.NET's implicit string conversion builds a String-typed JValue even from a null
        /// string. It writes as null either way, so the wire looks right, but a reader that tests
        /// JTokenType sees String for one absent field and Null for another, depending only on
        /// whether the expression happened to carry a JToken cast.
        /// </remarks>
        private static JToken Text(string value)
        {
            return value == null ? JValue.CreateNull() : (JToken)value;
        }

        /// <summary>
        /// Puts the slot index first, and leaves it out entirely when a material asset was named
        /// directly and there is no slot to report.
        /// </summary>
        private static JObject WithSlot(int? slotIndex, JObject description)
        {
            if (slotIndex.HasValue)
            {
                description.AddFirst(new JProperty("slot", slotIndex.Value));
            }

            return description;
        }

        /// <summary>
        /// Why a material cannot draw, or null when it can.
        /// </summary>
        /// <remarks>
        /// Unity substitutes <c>Hidden/InternalErrorShader</c> for a shader it could not load and
        /// says nothing further about it, so a material that reports that name has lost its real
        /// shader rather than been authored with this one.
        /// </remarks>
        private static string ShaderProblem(Material material)
        {
            if (material == null)
            {
                return "The slot has no material, so this submesh draws with the error shader (magenta).";
            }

            var shader = material.shader;

            if (shader == null)
            {
                return $"'{material.name}' has no shader at all, so it draws magenta.";
            }

            if (shader.name == "Hidden/InternalErrorShader")
            {
                return $"'{material.name}' resolves to Hidden/InternalErrorShader, which Unity puts in " +
                       "place of a shader it could not load: the real shader is missing from the project, " +
                       "or its package is not installed. This is what draws magenta.";
            }

            if (!shader.isSupported)
            {
                return $"'{shader.name}' did not compile or is unsupported on this graphics API, so " +
                       $"'{material.name}' draws magenta. shader_errors on that shader reports why.";
            }

            return null;
        }

        private static Renderer RequireRenderer(GameObject go)
        {
            var renderer = go.GetComponent<Renderer>();

            if (renderer != null)
            {
                return renderer;
            }

            var below = go.GetComponentsInChildren<Renderer>(true)
                .Where(r => r.gameObject != go)
                .Select(r => ObjectResolve.PathOf(r.gameObject))
                .Take(8)
                .ToArray();

            var hint = below.Length == 0
                ? "Nothing under it has one either."
                : "Renderers under it: " + string.Join(", ", below) + ".";

            throw new McpToolException(
                "not_found",
                $"'{go.name}' has no Renderer, so it has no materials. {hint}");
        }

        private static int RequireSlot(Renderer renderer, int? slot)
        {
            var count = renderer.sharedMaterials.Length;

            if (count == 0)
            {
                throw new McpToolException(
                    "not_found",
                    $"'{renderer.name}' has a {renderer.GetType().Name} with no material slots.");
            }

            if (!slot.HasValue)
            {
                if (count == 1)
                {
                    return 0;
                }

                throw new McpToolException(
                    "invalid_params",
                    $"'{renderer.name}' has {count} material slots, so 'slot' is required (0..{count - 1}). " +
                    "material_read with the same object_path lists them.");
            }

            if (slot.Value < 0 || slot.Value >= count)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{renderer.name}' has {count} material slot(s), so slot {slot.Value} does not exist " +
                    $"(0..{count - 1}).");
            }

            return slot.Value;
        }

        private static string ApplyProperty(Material material, string property, JToken value)
        {
            var index = material.shader.FindPropertyIndex(property);
            var type = index >= 0 ? material.shader.GetPropertyType(index) : ShaderPropertyType.Float;

            switch (type)
            {
                case ShaderPropertyType.Color:
                    var c = ReadVector4(value, "value");
                    material.SetColor(property, new Color(c.x, c.y, c.z, c.w));
                    return c.ToString();

                case ShaderPropertyType.Vector:
                    var v = ReadVector4(value, "value");
                    material.SetVector(property, v);
                    return v.ToString();

                case ShaderPropertyType.Int:
                    material.SetInteger(property, value.Value<int>());
                    return value.ToString();

                case ShaderPropertyType.Texture:
                    var texturePath = value.ToString();
                    var texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);

                    if (texture == null)
                    {
                        throw new McpToolException("not_found", $"No texture at '{texturePath}'.");
                    }

                    material.SetTexture(property, texture);
                    return texturePath;

                default:
                    material.SetFloat(property, value.Value<float>());
                    return value.ToString();
            }
        }

        private static Vector4 ReadVector4(JToken token, string argumentName)
        {
            if (token is JObject o)
            {
                float Axis(params string[] keys)
                {
                    foreach (var key in keys)
                    {
                        if (o[key] != null && o[key].Type != JTokenType.Null)
                        {
                            return o[key].Value<float>();
                        }
                    }

                    return 0f;
                }

                return new Vector4(Axis("x", "r"), Axis("y", "g"), Axis("z", "b"), Axis("w", "a"));
            }

            if (token is JArray a && a.Count >= 3)
            {
                return new Vector4(
                    a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>(),
                    a.Count > 3 ? a[3].Value<float>() : 1f);
            }

            throw new McpToolException(
                "invalid_params",
                $"'{argumentName}' must be {{x,y,z,w}}, {{r,g,b,a}} or an array for this property type.");
        }

        private static Shader RequireShader(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new McpToolException("invalid_params", "'path' or 'name' is required.");
            }

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path.Replace('\\', '/'));

            if (shader == null)
            {
                throw new McpToolException(
                    "not_found",
                    $"No shader at '{path}'. asset_find with type 'Shader' will list them.");
            }

            return shader;
        }

        private static Shader RequireShaderByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new McpToolException("invalid_params", "'path' or 'name' is required.");
            }

            var shader = Shader.Find(name);

            if (shader == null)
            {
                throw new McpToolException(
                    "not_found",
                    $"Shader.Find could not resolve '{name}'. The name is the one in the Shader " +
                    "declaration, not the file name.");
            }

            return shader;
        }

        private static Material RequireMaterial(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new McpToolException("invalid_params", "'path' is required.");
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(path.Replace('\\', '/'));

            if (material == null)
            {
                throw new McpToolException(
                    "not_found",
                    $"No material at '{path}'. asset_find with type 'Material' will list them.");
            }

            return material;
        }
    }
}
