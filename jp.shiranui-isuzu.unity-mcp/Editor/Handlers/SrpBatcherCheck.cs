using System.Collections.Generic;
using System.Reflection;

using Newtonsoft.Json.Linq;

using UnityEditor;

using UnityEngine;
using UnityEngine.Rendering;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Handlers
{
    /// <summary>
    /// Whether a shader lets the SRP Batcher keep its material data on the GPU, and what stops it.
    /// </summary>
    /// <remarks>
    /// The compatibility code and the sentence explaining it are computed by the Editor and reached
    /// by reflection; neither is reproduced here, so a new code in a later Unity explains itself
    /// rather than coming back as an unknown number.
    /// </remarks>
    internal static class SrpBatcherCheck
    {
        private const BindingFlags Internals = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static readonly MethodInfo CodeOf =
            typeof(ShaderUtil).GetMethod("GetSRPBatcherCompatibilityCode", Internals);

        private static readonly MethodInfo ReasonOf =
            typeof(ShaderUtil).GetMethod("GetSRPBatcherCompatibilityIssueReason", Internals);

        private static readonly MethodInfo ActiveSubshaderOf =
            typeof(ShaderUtil).GetMethod("GetShaderActiveSubshaderIndex", Internals);

        private static readonly MethodInfo SubshaderCountOf =
            typeof(ShaderUtil).GetMethod("GetShaderSubshaderCount", Internals);

        public static bool Available =>
            CodeOf != null && ReasonOf != null && ActiveSubshaderOf != null && SubshaderCountOf != null;

        /// <summary>
        /// Refuses unless this Editor can answer at all: the check is a property of a scriptable
        /// pipeline, and the built-in pipeline has no batcher for a shader to be compatible with.
        /// </summary>
        /// <exception cref="McpToolException"><c>not_supported</c>.</exception>
        public static void RequireSupport()
        {
            if (GraphicsSettings.currentRenderPipeline == null)
            {
                throw new McpToolException(
                    "not_supported",
                    "The SRP Batcher belongs to a scriptable render pipeline, and this project is on "
                    + "the built-in one, where no shader is compatible or incompatible. "
                    + "render_pipeline_info reports which pipeline is active.",
                    501);
            }

            if (!Available)
            {
                throw new McpToolException(
                    "not_supported",
                    "This Editor does not expose the SRP Batcher compatibility check that this tool "
                    + "reads. It is present in 2022.3 through 6000.5.",
                    501);
            }
        }

        /// <summary>
        /// The verdict for one shader. Never calls into the compatibility check without a subshader
        /// to ask about: the Editor's bounds check is compiled out of a release build, so an index
        /// past the end reads whatever follows and takes the Editor down with it.
        /// </summary>
        public static JObject Check(Shader shader)
        {
            // A shader that failed to compile reports an empty name, so the asset path is the only
            // thing left to call it by.
            var named = string.IsNullOrEmpty(shader.name)
                ? AssetDatabase.GetAssetPath(shader)
                : shader.name;

            var entry = new JObject { ["shader"] = string.IsNullOrEmpty(named) ? "<unnamed>" : named };

            if (!shader.isSupported)
            {
                // Unity substitutes an error subshader for a failed compile, so the check would run
                // and report a verdict for something that renders magenta and batches nothing.
                entry["checked"] = false;
                entry["note"] = "The shader failed to compile, so it renders magenta and a batching "
                    + "verdict would mean nothing. shader_errors reports why.";
                return entry;
            }

            var count = (int)SubshaderCountOf.Invoke(null, new object[] { shader });

            if (count <= 0)
            {
                entry["checked"] = false;
                entry["note"] = "The shader has no subshader to ask about.";
                return entry;
            }

            // A shader the Editor has not compiled yet answers "not initialised", which reads as
            // incompatible. Drawing one pass with a throwaway material is how the Shader Inspector
            // forces the compile before it asks.
            var probe = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

            try
            {
                probe.SetPass(0);

                var index = (int)ActiveSubshaderOf.Invoke(null, new object[] { shader });

                if (index < 0 || index >= count)
                {
                    entry["checked"] = false;
                    entry["note"] = $"The active subshader index {index} is outside the {count} this "
                        + "shader has, so the check was not run.";
                    return entry;
                }

                var code = (int)CodeOf.Invoke(null, new object[] { shader, index });

                entry["checked"] = true;
                entry["subshader"] = index;
                entry["compatible"] = code == 0;

                if (code != 0)
                {
                    entry["code"] = code;
                    entry["reason"] = (string)ReasonOf.Invoke(null, new object[] { shader, index, code });
                }

                return entry;
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }
        }

        /// <summary>Every shader used by the renderers in the open scenes, with what uses it.</summary>
        public static Dictionary<Shader, SortedSet<string>> InScene()
        {
            var byShader = new Dictionary<Shader, SortedSet<string>>();

            foreach (var renderer in AllRenderers())
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || material.shader == null)
                    {
                        continue;
                    }

                    if (!byShader.TryGetValue(material.shader, out var users))
                    {
                        users = new SortedSet<string>();
                        byShader[material.shader] = users;
                    }

                    users.Add(material.name);
                }
            }

            return byShader;
        }

        private static Renderer[] AllRenderers()
        {
#if UNITY_6000_5_OR_NEWER
            return Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include);
#else
#pragma warning disable CS0618
            return Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#pragma warning restore CS0618
#endif
        }
    }
}
