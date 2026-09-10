using System;
using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

using UnityObject = UnityEngine.Object;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// The lighting and environment a scene carries outside any GameObject.
    /// </summary>
    /// <remarks>
    /// RenderSettings is a scene-wide singleton, so nothing addresses it by path and inspect_write
    /// cannot reach it. Sky, ambient light and fog are most of what a room looks like before a
    /// single object is placed.
    /// </remarks>
    internal static class SceneSettingsTools
    {
        /// <summary>
        /// The settings this tool carries, each with how to read it and how to write it.
        /// </summary>
        /// <remarks>
        /// A table rather than a parameter per setting: the schema is sent to a model on every
        /// turn, and twenty named arguments cost more there than they save at the call.
        /// </remarks>
        private static readonly Dictionary<string, Setting> Settings =
            new Dictionary<string, Setting>(StringComparer.OrdinalIgnoreCase)
            {
                ["skybox"] = Setting.Asset<Material>(
                    () => RenderSettings.skybox, m => RenderSettings.skybox = (Material)m),
                ["sun"] = Setting.Asset<Light>(
                    () => RenderSettings.sun, l => RenderSettings.sun = (Light)l),

                ["ambientMode"] = Setting.Enum<AmbientMode>(
                    () => RenderSettings.ambientMode, v => RenderSettings.ambientMode = (AmbientMode)v),
                ["ambientLight"] = Setting.Colour(
                    () => RenderSettings.ambientLight, c => RenderSettings.ambientLight = c),
                ["ambientSkyColor"] = Setting.Colour(
                    () => RenderSettings.ambientSkyColor, c => RenderSettings.ambientSkyColor = c),
                ["ambientEquatorColor"] = Setting.Colour(
                    () => RenderSettings.ambientEquatorColor, c => RenderSettings.ambientEquatorColor = c),
                ["ambientGroundColor"] = Setting.Colour(
                    () => RenderSettings.ambientGroundColor, c => RenderSettings.ambientGroundColor = c),
                ["ambientIntensity"] = Setting.Number(
                    () => RenderSettings.ambientIntensity, v => RenderSettings.ambientIntensity = v),

                ["fog"] = Setting.Flag(() => RenderSettings.fog, v => RenderSettings.fog = v),
                ["fogMode"] = Setting.Enum<FogMode>(
                    () => RenderSettings.fogMode, v => RenderSettings.fogMode = (FogMode)v),
                ["fogColor"] = Setting.Colour(
                    () => RenderSettings.fogColor, c => RenderSettings.fogColor = c),
                ["fogDensity"] = Setting.Number(
                    () => RenderSettings.fogDensity, v => RenderSettings.fogDensity = v),
                ["fogStartDistance"] = Setting.Number(
                    () => RenderSettings.fogStartDistance, v => RenderSettings.fogStartDistance = v),
                ["fogEndDistance"] = Setting.Number(
                    () => RenderSettings.fogEndDistance, v => RenderSettings.fogEndDistance = v),

                ["reflectionIntensity"] = Setting.Number(
                    () => RenderSettings.reflectionIntensity, v => RenderSettings.reflectionIntensity = v),
                ["reflectionBounces"] = Setting.Whole(
                    () => RenderSettings.reflectionBounces, v => RenderSettings.reflectionBounces = v),
                ["defaultReflectionMode"] = Setting.Enum<DefaultReflectionMode>(
                    () => RenderSettings.defaultReflectionMode,
                    v => RenderSettings.defaultReflectionMode = (DefaultReflectionMode)v),
                ["customReflection"] = Setting.Asset<Texture>(
                    () => RenderSettings.customReflectionTexture,
                    t => RenderSettings.customReflectionTexture = (Texture)t),

                ["haloStrength"] = Setting.Number(
                    () => RenderSettings.haloStrength, v => RenderSettings.haloStrength = v),
                ["flareStrength"] = Setting.Number(
                    () => RenderSettings.flareStrength, v => RenderSettings.flareStrength = v),
            };

        private static readonly System.Reflection.MethodInfo TheObject =
            typeof(RenderSettings).GetMethod(
                "GetRenderSettings",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        [McpTool(
            "scene_settings",
            "Read or change the scene's lighting and environment: skybox, ambient light, fog and " +
            "reflection. These live on RenderSettings, which has no path, so inspect_write cannot " +
            "reach them. A change marks the scene dirty and is kept by scene_save.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Scene Settings",
            Group = "rendering")]
        public static JObject SceneSettings(
            [McpArg("property", "Which setting to read or change. Omit to read them all.")]
            string property = null,
            [McpArg("value", "The new value. A colour takes {r,g,b,a}, an enum its name or index, " +
                             "and skybox, sun and customReflection take an asset path under " +
                             "Assets/ or Packages/, a scene path for the sun, or an empty string " +
                             "to clear. Omit to read rather than change.")]
            JToken value = null)
        {
            if (string.IsNullOrWhiteSpace(property))
            {
                if (value != null)
                {
                    throw new McpToolException(
                        "invalid_params",
                        "'value' needs a 'property' to change. Without one this reads every setting.");
                }

                var all = new JObject();

                foreach (var pair in Settings.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    all[pair.Key] = pair.Value.Read();
                }

                return new JObject { ["scene"] = SceneName(), ["settings"] = all };
            }

            if (!Settings.TryGetValue(property, out var setting))
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{property}' is not a scene setting. It has: "
                    + string.Join(", ", Settings.Keys.OrderBy(k => k, StringComparer.Ordinal)) + ".");
            }

            if (value == null)
            {
                return new JObject
                {
                    ["scene"] = SceneName(),
                    ["property"] = property,
                    ["value"] = setting.Read(),
                };
            }

            var recorded = Record();

            var problem = setting.Write(value);

            if (problem != null)
            {
                throw new McpToolException("invalid_params", problem);
            }

            // Without this the change is live but lost the moment the scene reloads, and a caller
            // who then saves gets a file that does not match what they were looking at.
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene());

            var written = new JObject
            {
                ["scene"] = SceneName(),
                ["property"] = property,
                ["value"] = setting.Read(),
                ["written"] = true,
            };

            // The tool declares an undo group, so a caller assumes Ctrl+Z takes this back. An empty
            // group is skipped instead, undoing whatever came before it, which is worse than
            // knowing the change is permanent.
            if (!recorded)
            {
                written["undoable"] = false;
            }

            return written;
        }

        /// <summary>
        /// Puts the scene's render settings on the undo stack before they are changed.
        /// </summary>
        /// <remarks>
        /// RenderSettings is a static facade over an object Unity keeps to itself, and Undo needs
        /// the object. Without this a change made from here cannot be taken back with Ctrl+Z,
        /// which every other scene-editing tool here allows.
        /// </remarks>
        private static bool Record()
        {
            var target = TheObject == null ? null : TheObject.Invoke(null, null) as UnityObject;

            if (target == null)
            {
                return false;
            }

            Undo.RecordObject(target, "MCP Scene Settings");
            return true;
        }

        private static string SceneName()
        {
            return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        }

        /// <summary>One setting: how it reads, and what it accepts.</summary>
        private sealed class Setting
        {
            private Func<JToken> read;
            private Func<JToken, string> write;

            public JToken Read() => this.read();

            public string Write(JToken value) => this.write(value);

            public static Setting Number(Func<float> get, Action<float> set) => new Setting
            {
                read = () => get(),
                write = value => value.Type == JTokenType.Float || value.Type == JTokenType.Integer
                    ? Apply(() => set(value.Value<float>()))
                    : "This setting takes a number.",
            };

            public static Setting Whole(Func<int> get, Action<int> set) => new Setting
            {
                read = () => get(),
                write = value => value.Type == JTokenType.Integer
                    ? Apply(() => set(value.Value<int>()))
                    : "This setting takes a whole number.",
            };

            public static Setting Flag(Func<bool> get, Action<bool> set) => new Setting
            {
                read = () => get(),
                write = value => value.Type == JTokenType.Boolean
                    ? Apply(() => set(value.Value<bool>()))
                    : "This setting takes true or false.",
            };

            public static Setting Colour(Func<Color> get, Action<Color> set) => new Setting
            {
                read = () =>
                {
                    var c = get();
                    return new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };
                },
                write = value =>
                {
                    if (!(value is JObject o) || o["r"] == null || o["g"] == null || o["b"] == null)
                    {
                        return "A colour takes {r, g, b} and optionally a.";
                    }

                    return Apply(() => set(new Color(
                        o["r"].Value<float>(), o["g"].Value<float>(), o["b"].Value<float>(),
                        o["a"] == null ? 1f : o["a"].Value<float>())));
                },
            };

            public static Setting Enum<T>(Func<object> get, Action<object> set) where T : struct
            {
                return new Setting
                {
                    read = () => get().ToString(),
                    write = value =>
                    {
                        if (value.Type == JTokenType.Integer)
                        {
                            var index = value.Value<int>();

                            if (!System.Enum.IsDefined(typeof(T), index))
                            {
                                return Choices<T>(index.ToString());
                            }

                            return Apply(() => set(System.Enum.ToObject(typeof(T), index)));
                        }

                        var name = value.Value<string>();

                        foreach (var candidate in System.Enum.GetNames(typeof(T)))
                        {
                            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                            {
                                return Apply(() => set(System.Enum.Parse(typeof(T), candidate)));
                            }
                        }

                        return Choices<T>(name);
                    },
                };
            }

            /// <summary>
            /// A setting pointing at an asset. Named by path rather than by identity, because a
            /// path is what a caller has.
            /// </summary>
            public static Setting Asset<T>(Func<UnityObject> get, Action<UnityObject> set)
                where T : UnityObject
            {
                return new Setting
                {
                    read = () =>
                    {
                        var current = get();

                        if (current == null)
                        {
                            return JValue.CreateNull();
                        }

                        // A scene object has no asset path, and an empty one written back would
                        // clear the reference rather than restore it.
                        var assetPath = AssetDatabase.GetAssetPath(current);
                        var component = current as Component;

                        return new JObject
                        {
                            ["name"] = current.name,
                            ["type"] = current.GetType().Name,
                            ["path"] = string.IsNullOrEmpty(assetPath)
                                ? (component == null
                                    ? JValue.CreateNull()
                                    : (JToken)ObjectResolve.PathOf(component.gameObject))
                                : assetPath,
                        };
                    },
                    write = value =>
                    {
                        var text = value.Type == JTokenType.Null ? string.Empty : value.Value<string>();

                        if (string.IsNullOrEmpty(text))
                        {
                            return Apply(() => set(null));
                        }

                        if (text.StartsWith("Assets/", StringComparison.Ordinal)
                            || text.StartsWith("Packages/", StringComparison.Ordinal))
                        {
                            var asset = AssetDatabase.LoadAssetAtPath<T>(text);

                            return asset == null
                                ? $"No {typeof(T).Name} at '{text}'."
                                : Apply(() => set(asset));
                        }

                        // A Light lives in the scene, not in the asset database, and may be
                        // switched off, which GameObject.Find does not see.
                        GameObject go;

                        try
                        {
                            go = ObjectResolve.Object(text, null, "value", null);
                        }
                        catch (McpToolException)
                        {
                            return $"'{text}' is not an asset path under Assets/ or Packages/, "
                                   + "nor a scene path.";
                        }

                        var component = go.GetComponent(typeof(T)) as T;

                        return component == null
                            ? $"'{text}' carries no {typeof(T).Name}."
                            : Apply(() => set(component));
                    },
                };
            }

            private static string Choices<T>(string given) where T : struct
            {
                return $"'{given}' is not one of: "
                       + string.Join(", ", System.Enum.GetNames(typeof(T))) + ".";
            }

            private static string Apply(Action action)
            {
                action();
                return null;
            }
        }
    }
}
