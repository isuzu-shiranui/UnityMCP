using System;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;

using UnityEditor.Rendering;

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
            "quiet, so this has to be asked for. Omit the path to sweep every shader in the project.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject Errors(
            [McpArg("path", "Shader asset path. Omit to check every shader in the project.")]
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
                        ["messageDetails"] = string.IsNullOrEmpty(message.messageDetails) ? null : message.messageDetails,
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
                ["truncated"] = messages.Count >= Math.Max(limit, 0) && errorCount + warningCount > messages.Count,
                ["messages"] = messages,
            };
        }

        [McpTool(
            "shader_info",
            "Describe a shader: its passes, properties, keywords and render queue.",
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
            "shader declares as defaults.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject MaterialRead(
            [McpArg("path", "Material asset path, e.g. Assets/Art/Wood.mat.")]
            string path = null)
        {
            var material = RequireMaterial(path);
            var shader = material.shader;
            var properties = new JArray();

            for (var i = 0; i < shader.GetPropertyCount(); i++)
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

            return new JObject
            {
                ["path"] = AssetDatabase.GetAssetPath(material),
                ["shader"] = shader.name,
                ["shaderPath"] = AssetDatabase.GetAssetPath(shader),
                ["renderQueue"] = material.renderQueue,
                // The material's own queue is -1 when it just follows the shader. Reporting both
                // saves a round trip when a sorting problem is being chased.
                ["renderQueueFromShader"] = material.renderQueue == shader.renderQueue,
                ["enabledKeywords"] = new JArray(material.enabledKeywords.Select(k => (object)k.name).ToArray()),
                ["shaderKeywords"] = new JArray(material.shaderKeywords.Cast<object>().ToArray()),
                ["passCount"] = material.passCount,
                ["properties"] = properties,
            };
        }

        [McpTool(
            "material_set",
            "Set one property on a material, or toggle one of its keywords. Undoable.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Material Edit")]
        public static JObject MaterialSet(
            [McpArg("path", "Material asset path.")]
            string path = null,
            [McpArg("property", "Shader property name, e.g. _BaseColor. Omit when toggling a keyword.")]
            string property = null,
            [McpArg("value", "New value: a number, a string for textures, or {r,g,b,a} / {x,y,z,w}.")]
            JToken value = null,
            [McpArg("keyword", "Shader keyword to toggle instead of setting a property.")]
            string keyword = null,
            [McpArg("enabled", "Whether the keyword should be on.")]
            bool enabled = true,
            [McpArg("render_queue", "Override the render queue; -1 puts it back to the shader's.")]
            int? renderQueue = null)
        {
            var material = RequireMaterial(path);

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
                changes.Add($"renderQueue = {renderQueue.Value}");
            }

            if (changes.Count == 0)
            {
                throw new McpToolException(
                    "invalid_params",
                    "Nothing to do: pass 'property' with 'value', 'keyword', or 'render_queue'.");
            }

            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);

            return new JObject
            {
                ["path"] = AssetDatabase.GetAssetPath(material),
                ["changed"] = changes,
            };
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
