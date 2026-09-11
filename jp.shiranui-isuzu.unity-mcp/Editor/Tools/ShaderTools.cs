using System;
using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;

using UnityEditor.Rendering;
using UnityEditor.SceneManagement;

using UnityEngine;
using UnityEngine.Rendering;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;
using UnityMCP.Editor.Handlers;

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
                        // Counting continues so `truncated` stays honest; only the message list
                        // stops growing.
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
            "material's 'propertyCount' rather than its values; name a 'slot' to get that one " +
            "slot's properties. A slot whose shader is missing, unsupported, or Unity's stand-in error " +
            "shader is called out in 'shaderProblem', which is the magenta case. A material that is " +
            "not an asset is reported with a null path rather than left out. Reading many objects " +
            "goes in 'object_paths' rather than a call each. Which objects share one material is a " +
            "different question, and reflect_read with 'paths' over each renderer's sharedMaterial " +
            "answers it in a fraction of the bytes, telling two materials apart by instanceId when " +
            "their names are the same.",
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
            int? slot = null,
            [McpArg("property", "Only report properties whose name contains this text, ignoring " +
                                "case.")]
            string property = null,
            [McpArg("object_paths", "Several objects to read in one call, up to 50. Each is read " +
                                    "the way 'object_path' reads one, and the reply is keyed by " +
                                    "the path given; an object that cannot be read carries its own " +
                                    "error and the rest still come back. Alternative to " +
                                    "'object_path'.")]
            string[] objectPaths = null,
            [McpArg("group", "Report one description per distinct set of materials rather than " +
                             "one per object, with the objects sharing it listed beside it, and " +
                             "their material names and asset paths. Objects drawn with the same " +
                             "settings come back as one group however many there are. Only means " +
                             "something with 'object_paths'.")]
            bool group = false)
        {
            if (objectPaths != null && objectPaths.Length > 0)
            {
                return ReadObjects(objectPaths, path, objectPath, slot, property, group);
            }

            if (string.IsNullOrWhiteSpace(objectPath))
            {
                if (slot.HasValue)
                {
                    throw new McpToolException(
                        "invalid_params",
                        "'slot' only means something with 'object_path': a material asset has no slots.");
                }

                return Describe(RequireMaterial(path), null, true, property);
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                throw new McpToolException(
                    "invalid_params",
                    "Pass 'path' or 'object_path', not both: one names a material asset, the other a " +
                    "scene object whose renderer holds materials.");
            }

            return ReadObject(objectPath, slot, property, slot.HasValue);
        }

        /// <summary>The most objects one call reads.</summary>
        /// <remarks>
        /// The same cap reflect_read's 'paths' takes, for the same reason: each one resolves an
        /// object and reads its renderer on the main thread.
        /// </remarks>
        private const int MaxObjects = 50;

        /// <summary>
        /// Several objects' materials in one reply, keyed by the path each was asked for.
        /// </summary>
        /// <remarks>
        /// One object at a time is what a comparison across a scene actually costs: three hundred
        /// bricks went out as three hundred calls and 744 KB. Saying so in the description did not
        /// change that; an argument does.
        /// </remarks>
        private static JObject ReadObjects(
            string[] objectPaths, string path, string objectPath, int? slot, string property, bool group)
        {
            if (!string.IsNullOrWhiteSpace(path) || !string.IsNullOrWhiteSpace(objectPath))
            {
                throw new McpToolException(
                    "invalid_params",
                    "'object_paths' replaces 'object_path' and cannot be combined with 'path'.");
            }

            if (objectPaths.Length > MaxObjects)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'object_paths' takes at most {MaxObjects} at a time; {objectPaths.Length} " +
                    "were given. Each one resolves an object and reads its renderer on the " +
                    "Editor's main thread.");
            }

            var reads = new JObject();

            foreach (var one in objectPaths)
            {
                try
                {
                    // Grouping compares descriptions, so they carry the property values: without
                    // them two materials that differ only in colour described, and grouped, alike.
                    reads[one] = ReadObject(one, slot, property, slot.HasValue || group);
                }
                catch (McpToolException e)
                {
                    // The one that failed says why, and the rest of the batch still answers. A
                    // whole reply lost to one bad path is a batch that cannot be trusted with a
                    // list the caller did not hand-check.
                    reads[one] = new JObject { ["objectPath"] = one, ["error"] = e.Message };
                }
            }

            return group ? Grouped(reads) : new JObject { ["reads"] = reads };
        }

        /// <summary>
        /// One entry per distinct set of materials, with the objects using it listed beside it.
        /// </summary>
        /// <remarks>
        /// Asking whether three hundred objects share a material means comparing three hundred
        /// descriptions that are word for word the same, and sending them all back cost 738 KB
        /// where the answer is one shape and a list of paths. Grouped by content rather than by
        /// instance: two materials with the same settings are interchangeable, which is what the
        /// question is really about, and reflect_read over sharedMaterial is what counts instances.
        /// <para>
        /// The unit is the renderer's whole set, not one material: an object drawn with two
        /// materials groups with objects carrying those same two, and not with an object carrying
        /// only one of them. 'distinct' counts those sets.
        /// </para>
        /// </remarks>
        private static JObject Grouped(JObject reads)
        {
            var groups = new List<(string Key, JObject Shape, JArray Objects, JArray Names, JArray Paths)>();
            var failed = new JObject();

            foreach (var pair in reads)
            {
                var entry = (JObject)pair.Value;

                if (entry["error"] != null)
                {
                    failed[pair.Key] = entry["error"];
                    continue;
                }

                // The materials alone, not the renderer around them: two objects drawn with the
                // same material are the answer being asked for whether or not one of them carries
                // a second slot the read did not touch.
                var shape = new JObject { ["slots"] = entry["slots"]?.DeepClone() };

                var key = KeyOf(shape);
                var found = groups.FindIndex(g => string.Equals(g.Key, key, StringComparison.Ordinal));

                if (found < 0)
                {
                    // The stripped shape is what gets reported: the first member's own name and
                    // asset path describe one material out of however many the group holds, and a
                    // caller that read that path back would edit one object and believe it edited
                    // all of them. Both are collected across the members instead.
                    groups.Add((key, StripIdentity(shape), new JArray { pair.Key },
                                FieldOf(shape, "name", new JArray()),
                                FieldOf(shape, "path", new JArray())));
                }
                else
                {
                    groups[found].Objects.Add(pair.Key);
                    FieldOf(shape, "name", groups[found].Names);
                    FieldOf(shape, "path", groups[found].Paths);
                }
            }

            var reported = new JArray();

            foreach (var (_, shape, objects, names, paths) in groups)
            {
                // Values are read for every object because the grouping compares them, but a
                // group of one shares its settings with nobody: publishing its whole property
                // list makes the grouped reply larger than the plain one it was meant to shrink.
                if (objects.Count == 1)
                {
                    Summarise(shape);
                }

                var one = new JObject
                {
                    ["objects"] = objects,
                    ["count"] = objects.Count,
                    ["names"] = names,
                };

                if (paths.Count > 0)
                {
                    one["paths"] = paths;
                }

                foreach (var field in shape)
                {
                    one[field.Key] = field.Value;
                }

                reported.Add(one);
            }

            var result = new JObject
            {
                ["distinct"] = reported.Count,
                ["groups"] = reported,
            };

            if (failed.Count > 0)
            {
                result["failed"] = failed;
            }

            return result;
        }

        /// <summary>What a material is named and where it lives, which no two of them share.</summary>
        /// <remarks>
        /// Three hundred bricks set up identically carry three hundred materials called
        /// BrickMaterial_0 to BrickMaterial_299, so a key taken over the whole description put
        /// every one of them in a group of its own and saved nothing. Whether they can share one
        /// material is a question about their settings.
        /// </remarks>
        private static readonly string[] Identity = { "name", "path", "note", "slot" };

        /// <summary>Replaces each slot's property list with its length, as an ungrouped read reports it.</summary>
        private static void Summarise(JObject shape)
        {
            foreach (var slot in shape["slots"] as JArray ?? new JArray())
            {
                if (slot is not JObject entry || entry["properties"] is not JArray properties)
                {
                    continue;
                }

                if (entry["propertyCount"] == null)
                {
                    entry["propertyCount"] = properties.Count;
                }

                entry.Remove("properties");
            }
        }

        /// <summary>The description without the fields that name one material rather than describe it.</summary>
        private static JObject StripIdentity(JObject shape)
        {
            var stripped = (JObject)shape.DeepClone();

            foreach (var slot in stripped["slots"] as JArray ?? new JArray())
            {
                foreach (var field in Identity)
                {
                    (slot as JObject)?.Remove(field);
                }
            }

            return stripped;
        }

        /// <summary>The description with the identifying fields taken out, as a comparable string.</summary>
        private static string KeyOf(JObject shape) =>
            StripIdentity(shape).ToString(Newtonsoft.Json.Formatting.None);

        /// <summary>
        /// Adds this member's value for one identifying field to the group's, so the grouping
        /// hides none of them.
        /// </summary>
        private static JArray FieldOf(JObject shape, string field, JArray collected)
        {
            foreach (var slot in shape["slots"] as JArray ?? new JArray())
            {
                var value = (slot as JObject)?[field]?.ToString();

                if (!string.IsNullOrEmpty(value) && !collected.Any(n => n.ToString() == value))
                {
                    collected.Add(value);
                }
            }

            return collected;
        }

        private static JObject ReadObject(string objectPath, int? slot, string property, bool withProperties)
        {
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

                slots.Add(Describe(materials[i], i, withProperties, property));
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
        /// <param name="nameFilter">
        /// Reports only the properties whose name contains this, ignoring case. Matched as a
        /// substring rather than exactly, because a caller after "_Color" also wants
        /// "_ShadowColor" and cannot know the spelling a shader chose.
        /// </param>
        private static JObject Describe(
            Material material, int? slotIndex, bool includeProperties, string nameFilter = null)
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

            var filtering = !string.IsNullOrWhiteSpace(nameFilter);

            for (var i = 0; includeProperties && i < propertyCount; i++)
            {
                var propertyName = shader.GetPropertyName(i);

                if (filtering
                    && propertyName.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

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

                // The total stays in a narrowed reply: without it a caller cannot tell an empty
                // filter from a material that has nothing.
                // The total is what a narrowed reply cannot be read without; the number
                // returned is the length of the array beside it.
                if (filtering)
                {
                    described["propertyCount"] = propertyCount;
                }
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

        /// <summary>Shaders examined by one call, before `limit` narrows what comes back.</summary>
        private const int MaxShadersChecked = 200;

        /// <summary>Materials named per shader. The count beside them is the whole number.</summary>
        private const int MaxMaterialsNamed = 5;

        [McpTool(
            "shader_batching_check",
            "Whether the SRP Batcher can keep a shader's material data on the GPU, and what stops " +
            "it when it cannot. This is the answer to 'why are there still thousands of SetPass " +
            "calls': one incompatible shader breaks the batch for everything drawn with it, and " +
            "nothing in the Editor reports it outside the Shader Inspector, one shader at a time. " +
            "The reason names the offending shader variable, which is what has to be moved or " +
            "declared. Pass 'scope' as 'scene' to check everything the open scenes draw with, or " +
            "name one shader or material. Only a scriptable render pipeline has a batcher to be " +
            "compatible with, so this is refused on the built-in pipeline.",
            Idempotency = McpIdempotency.Safe,
            MaxResultSizeChars = 60000)]
        public static JObject BatchingCheck(
            [McpArg("path", "Asset path of a shader or a material, e.g. 'Assets/Art/Toon.shader'.")]
            string path = null,
            [McpArg("name", "Shader name as it appears in the Shader declaration, e.g. " +
                            "'Universal Render Pipeline/Lit'.")]
            string name = null,
            [McpArg("object_path", "A scene object; every shader its renderers draw with is checked.")]
            string objectPath = null,
            [McpArg("instance_id", "Address that object by instance id instead.")]
            long? instanceId = null,
            [McpArg("scope", "'scene' checks every shader the open scenes draw with.")]
            string scope = null,
            [McpArg("incompatible_only", "Leave out the shaders that are already compatible.")]
            bool incompatibleOnly = false,
            [McpArg("limit", "How many shaders to report.")]
            int limit = 50,
            [McpArg("offset", "Where to start, for paging.")]
            int offset = 0,
            [McpArg("fields", "Comma-separated keys to keep on each row.")]
            string fields = null)
        {
            // The arguments are judged before the pipeline is, so a malformed call is told what is
            // wrong with it whether or not this project could have answered.
            RequireOneSource(path, name, objectPath, instanceId, scope);
            SrpBatcherCheck.RequireSupport();

            var users = Sources(path, name, objectPath, instanceId, scope);
            var shaders = new List<Shader>(users.Keys);
            shaders.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

            var bounded = shaders.Count > MaxShadersChecked;

            if (bounded)
            {
                shaders.RemoveRange(MaxShadersChecked, shaders.Count - MaxShadersChecked);
            }

            var rows = new List<JObject>();
            var compatible = 0;
            var incompatible = 0;

            foreach (var shader in shaders)
            {
                var row = SrpBatcherCheck.Check(shader);

                if (row["compatible"] is { } verdict && verdict.Value<bool>())
                {
                    compatible++;

                    if (incompatibleOnly)
                    {
                        continue;
                    }
                }
                else if (row["checked"] is { } ran && ran.Value<bool>())
                {
                    incompatible++;
                }

                var materials = users[shader];

                if (materials.Count > 0)
                {
                    // One shader is routinely worn by a hundred materials, and listing them all
                    // buries the sentence that says what to fix. The count is what sizes the
                    // problem; the names are a sample to find it by.
                    row["usedByCount"] = materials.Count;
                    row["usedBy"] = new JArray(materials.Take(MaxMaterialsNamed));

                    if (materials.Count > MaxMaterialsNamed)
                    {
                        row["usedBySample"] = true;
                    }
                }

                rows.Add(row);
            }

            var result = ListResponseBuilder.Build(
                rows, offset, limit, row => row,
                ListResponseBuilder.ParseFieldsParam(fields), new[] { "shader" });

            result["pipeline"] = GraphicsSettings.currentRenderPipeline.GetType().Name;
            result["compatible"] = compatible;
            result["incompatible"] = incompatible;

            if (bounded)
            {
                result["note"] = $"Stopped after {MaxShadersChecked} shaders; the scene draws with more.";
            }

            return result;
        }

        /// <summary>
        /// Checks that the call names exactly one set of shaders. Naming two is refused rather than
        /// one being picked, because which one was used would not be visible in the reply.
        /// </summary>
        /// <exception cref="McpToolException"><c>invalid_params</c>.</exception>
        internal static void RequireOneSource(
            string path, string name, string objectPath, long? instanceId, string scope)
        {
            var given = new List<string>();

            if (!string.IsNullOrWhiteSpace(path)) { given.Add("path"); }
            if (!string.IsNullOrWhiteSpace(name)) { given.Add("name"); }
            if (!string.IsNullOrWhiteSpace(objectPath) || instanceId.HasValue) { given.Add("object_path"); }
            if (!string.IsNullOrWhiteSpace(scope)) { given.Add("scope"); }

            if (given.Count == 0)
            {
                throw new McpToolException(
                    "invalid_params",
                    "Name what to check: 'path' for a shader or material asset, 'name' for a shader "
                    + "by its declared name, 'object_path' for a scene object, or scope 'scene' for "
                    + "everything the open scenes draw with.");
            }

            if (given.Count > 1)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{string.Join("' and '", given)}' name different sets of shaders; pass one.");
            }

            if (!string.IsNullOrWhiteSpace(scope)
                && !string.Equals(scope, "scene", StringComparison.OrdinalIgnoreCase))
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'scope' takes 'scene'; '{scope}' is not it.");
            }
        }

        /// <summary>The shaders one call is about, each with the materials that brought it in.</summary>
        private static Dictionary<Shader, SortedSet<string>> Sources(
            string path, string name, string objectPath, long? instanceId, string scope)
        {
            var found = new Dictionary<Shader, SortedSet<string>>();

            if (!string.IsNullOrWhiteSpace(scope))
            {
                return SrpBatcherCheck.InScene();
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                found[RequireShaderByName(name)] = new SortedSet<string>();
                return found;
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                var normalised = path.Replace('\\', '/');
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(normalised);

                if (shader != null)
                {
                    found[shader] = new SortedSet<string>();
                    return found;
                }

                var material = RequireMaterial(path);

                if (material.shader == null)
                {
                    throw new McpToolException(
                        "not_found",
                        $"The material at '{path}' has no shader assigned.");
                }

                found[material.shader] = new SortedSet<string> { material.name };
                return found;
            }

            var go = ObjectResolve.Object(objectPath, instanceId);

            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || material.shader == null)
                    {
                        continue;
                    }

                    if (!found.TryGetValue(material.shader, out var set))
                    {
                        set = new SortedSet<string>();
                        found[material.shader] = set;
                    }

                    set.Add(material.name);
                }
            }

            if (found.Count == 0)
            {
                throw new McpToolException(
                    "not_found",
                    $"Nothing under '{objectPath ?? instanceId.ToString()}' draws with a shader: it "
                    + "has no Renderer, or every material slot is empty.");
            }

            return found;
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

        [McpTool(
            "material_create",
            "Create a material asset. Without this the only way in is the Assets/Create menu, " +
            "which lands an unnamed material in whatever folder the Project window happens to be " +
            "showing and finishes only once the rename field is dismissed. Set its properties " +
            "afterwards with material_set, and hang it on a renderer with inspect_write.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Create Material",
            Group = "rendering")]
        public static JObject MaterialCreate(
            [McpArg("path", "Where to write the .mat, e.g. Assets/Art/Wood.mat. Missing folders " +
                            "under Assets/ are created. '.mat' is added when it is left off.",
                    Required = true)]
            string path = null,
            [McpArg("shader", "Shader name as shader_info reports it, e.g. 'Standard' or " +
                              "'Universal Render Pipeline/Lit'. Omit for the render pipeline's own.")]
            string shader = null,
            [McpArg("overwrite", "Replace an existing material at this path rather than refusing.")]
            bool overwrite = false)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new McpToolException("invalid_params", "'path' is required.");
            }

            var target = path.Replace('\\', '/');

            if (!target.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            {
                target += ".mat";
            }

            if (!target.StartsWith("Assets/", StringComparison.Ordinal))
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{target}' is outside Assets/. A material has to live in the project.");
            }

            var existing = AssetDatabase.LoadAssetAtPath<Material>(target);
            AssetTools.RefuseIncompatibleAsset(target, existing);

            if (!overwrite && existing != null)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{target}' already exists. Pass overwrite to replace it, or use material_set "
                    + "to change the one that is there.");
            }

            var chosen = Resolve(shader);

            EnsureFolder(target);

            if (existing != null)
            {
                AssetTools.ReplaceAssetContents(existing, new Material(chosen), target);

                return new JObject
                {
                    ["path"] = target,
                    ["shader"] = chosen.name,
                    ["created"] = false,
                    ["replaced"] = true,
                };
            }

            var material = new Material(chosen);

            AssetDatabase.CreateAsset(material, target);
            AssetDatabase.SaveAssetIfDirty(material);

            return new JObject
            {
                ["path"] = target,
                ["shader"] = chosen.name,
                ["created"] = true,
            };
        }

        /// <summary>The shader a caller named, or the one this project's pipeline draws with.</summary>
        private static Shader Resolve(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                var found = Shader.Find(name);

                if (found == null)
                {
                    throw new McpToolException(
                        "not_found",
                        $"No shader named '{name}'. shader_info lists what a project has, and the "
                        + "name is the one inside the shader rather than its file name.");
                }

                return found;
            }

            // A URP or HDRP project has no working Standard, and a Built-in one has no Lit, so
            // the pipeline is asked before either name is guessed at.
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            var fromPipeline = pipeline == null ? null : pipeline.defaultShader;

            if (fromPipeline != null)
            {
                return fromPipeline;
            }

            var standard = Shader.Find("Standard");

            if (standard == null)
            {
                throw new McpToolException(
                    "not_found",
                    "This project has neither a render pipeline default shader nor 'Standard'. "
                    + "Name a shader explicitly.");
            }

            return standard;
        }

        /// <summary>Creates the folders a path needs, the way the Project window would.</summary>
        private static void EnsureFolder(string assetPath)
        {
            var parts = assetPath.Split('/');
            var built = parts[0];

            for (var i = 1; i < parts.Length - 1; i++)
            {
                var next = built + "/" + parts[i];

                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(built, parts[i]);
                }

                built = next;
            }
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
