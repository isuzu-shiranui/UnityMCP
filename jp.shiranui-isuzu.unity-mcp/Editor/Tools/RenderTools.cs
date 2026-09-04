using System;
using System.IO;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;

using UnityEngine;
using UnityEngine.Rendering;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Answering "is the picture right", and "what is drawing it".
    /// </summary>
    internal static class RenderTools
    {
        [McpTool(
            "render_compare",
            "Compare two captured images and report how they differ, in numbers. Use this instead of " +
            "looking at both pictures: capture with save_path, toggle the thing under test, capture " +
            "again, then compare. Absolute colours are post-tonemap and not worth trusting, so what " +
            "this reports is change — how many pixels moved, by how much, and where. The bounding " +
            "box and the per-cell grid are only present when at least one pixel changed. Both " +
            "images have to be the same size.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject Compare(
            [McpArg("before", "Path to the first PNG, from capture_screenshot's save_path.")]
            string before = null,
            [McpArg("after", "Path to the second PNG.")]
            string after = null,
            [McpArg("threshold", "Largest per-channel difference, 0-255, at or below which a pixel " +
                                 "counts as unchanged. Only red, green and blue are compared; alpha " +
                                 "is ignored.")]
            int threshold = 2,
            [McpArg("grid", "Report the difference over a grid this many cells across, to localise " +
                            "it. Clamped to 1-32.")]
            int grid = 8)
        {
            var a = LoadPng(before, "before");
            Texture2D b;

            try
            {
                b = LoadPng(after, "after");
            }
            catch
            {
                // Without this the first texture outlives every failed call. A caller comparing
                // against a path that does not exist yet — polling for a capture, say — would
                // leak one texture per attempt and never learn why memory grew.
                UnityEngine.Object.DestroyImmediate(a);
                throw;
            }

            if (a.width != b.width || a.height != b.height)
            {
                // Read the sizes before destroying: interpolating them afterwards throws
                // MissingReferenceException and the caller gets tool_failed instead of the
                // explanation.
                var sizes = $"{a.width}x{a.height} and {b.width}x{b.height}";

                UnityEngine.Object.DestroyImmediate(a);
                UnityEngine.Object.DestroyImmediate(b);

                throw new McpToolException(
                    "invalid_params",
                    $"The images are different sizes ({sizes}). " +
                    "Capture both at the same size, or the comparison means nothing.");
            }

            try
            {
                var pa = a.GetPixels32();
                var pb = b.GetPixels32();

                var cells = Math.Max(1, Math.Min(grid, 32));
                var cellCounts = new int[cells * cells];

                long changed = 0;
                long sumDelta = 0;
                var maxDelta = 0;
                int minX = a.width, minY = a.height, maxX = -1, maxY = -1;

                for (var i = 0; i < pa.Length; i++)
                {
                    var dr = Math.Abs(pa[i].r - pb[i].r);
                    var dg = Math.Abs(pa[i].g - pb[i].g);
                    var db = Math.Abs(pa[i].b - pb[i].b);
                    var delta = Math.Max(dr, Math.Max(dg, db));

                    if (delta <= threshold)
                    {
                        continue;
                    }

                    changed++;
                    sumDelta += delta;
                    maxDelta = Math.Max(maxDelta, delta);

                    var x = i % a.width;
                    var y = i / a.width;

                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;

                    cellCounts[(y * cells / a.height) * cells + (x * cells / a.width)]++;
                }

                var total = (long)a.width * a.height;

                var result = new JObject
                {
                    ["width"] = a.width,
                    ["height"] = a.height,
                    ["totalPixels"] = total,
                    ["changedPixels"] = changed,
                    ["changedRatio"] = total == 0 ? 0d : Math.Round((double)changed / total, 6),
                    ["identical"] = changed == 0,
                    ["meanDelta"] = changed == 0 ? 0d : Math.Round((double)sumDelta / changed, 2),
                    ["maxDelta"] = maxDelta,
                    ["threshold"] = threshold,
                };

                if (changed > 0)
                {
                    result["boundingBox"] = new JObject
                    {
                        ["x"] = minX,
                        ["y"] = minY,
                        ["width"] = maxX - minX + 1,
                        ["height"] = maxY - minY + 1,
                    };

                    // A grid of counts is a picture of where the change is, in a few dozen numbers
                    // rather than a few hundred kilobytes. Rows run top to bottom.
                    var rows = new JArray();
                    var cellPixels = Math.Max(1, (a.width / cells) * (a.height / cells));

                    for (var row = cells - 1; row >= 0; row--)
                    {
                        var cols = new JArray();

                        for (var col = 0; col < cells; col++)
                        {
                            cols.Add(Math.Round((double)cellCounts[row * cells + col] / cellPixels, 3));
                        }

                        rows.Add(cols);
                    }

                    result["gridChangedRatio"] = rows;
                }

                return result;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(a);
                UnityEngine.Object.DestroyImmediate(b);
            }
        }

        [McpTool(
            "render_pipeline_info",
            "Report what is actually drawing: the render pipeline asset in force, colour space, MSAA " +
            "sample count, graphics API, shadow and batching settings, and quality level. Read this " +
            "first when a shader behaves differently than expected — the quality level's pipeline " +
            "override is a common surprise. HDR is not a project-wide setting and is not reported " +
            "here; render_camera_info gives it per camera.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject PipelineInfo()
        {
            var quality = QualitySettings.renderPipeline;
            var graphics = GraphicsSettings.defaultRenderPipeline;
            var active = quality != null ? quality : graphics;

            return new JObject
            {
                ["activePipeline"] = active == null ? "Built-in" : active.GetType().Name,
                ["activePipelineAsset"] = active == null ? null : (JToken)AssetDatabase.GetAssetPath(active),
                // Two places can name a pipeline and the quality level wins. Reporting both is the
                // difference between "my URP settings do nothing" being a mystery and being obvious.
                ["defaultPipelineAsset"] = graphics == null ? null : (JToken)AssetDatabase.GetAssetPath(graphics),
                ["qualityPipelineAsset"] = quality == null ? null : (JToken)AssetDatabase.GetAssetPath(quality),
                ["qualityLevel"] = QualitySettings.names.ElementAtOrDefault(QualitySettings.GetQualityLevel()),
                ["colorSpace"] = QualitySettings.activeColorSpace.ToString(),
                ["graphicsApi"] = SystemInfo.graphicsDeviceType.ToString(),
                ["graphicsDevice"] = SystemInfo.graphicsDeviceName,
                ["shaderLevel"] = SystemInfo.graphicsShaderLevel,
                ["supportsComputeShaders"] = SystemInfo.supportsComputeShaders,
                ["antiAliasing"] = QualitySettings.antiAliasing,
                ["anisotropicFiltering"] = QualitySettings.anisotropicFiltering.ToString(),
                ["shadowResolution"] = QualitySettings.shadowResolution.ToString(),
                ["shadowDistance"] = QualitySettings.shadowDistance,
                ["activeBuildTarget"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
#if UNITY_2023_1_OR_NEWER
                ["batchingStatic"] = PlayerSettings.GetStaticBatchingForPlatform(EditorUserBuildSettings.activeBuildTarget),
#else
                ["batchingStatic"] = JValue.CreateNull(),
#endif
            };
        }

        [McpTool(
            "render_camera_info",
            "Report the cameras and their matrices. The view and projection matrices are here so a " +
            "value read off a screenshot can be checked against one computed on the CPU — screenshot " +
            "colours are post-tonemap and cannot settle an argument on their own. Every camera in " +
            "the open scenes is reported, disabled ones included; read each entry's enabled field " +
            "to tell which are drawing.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject CameraInfo(
            [McpArg("name", "Only report the camera with this name.")]
            string name = null,
            [McpArg("include_matrices", "Include the view and projection matrices, row-major.")]
            bool includeMatrices = true)
        {
            var cameras = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(c => string.IsNullOrWhiteSpace(name) || c.name == name)
                .OrderByDescending(c => c.isActiveAndEnabled)
                .ThenBy(c => c.depth)
                .ToArray();

            if (cameras.Length == 0)
            {
                throw new McpToolException(
                    "not_found",
                    string.IsNullOrWhiteSpace(name)
                        ? "No cameras in the open scenes."
                        : $"No camera named '{name}'.");
            }

            var list = new JArray(cameras.Select(c =>
            {
                var entry = new JObject
                {
                    ["name"] = c.name,
                    ["path"] = ObjectResolve.PathOf(c.gameObject),
                    ["enabled"] = c.isActiveAndEnabled,
                    ["depth"] = c.depth,
                    ["orthographic"] = c.orthographic,
                    ["fieldOfView"] = c.fieldOfView,
                    ["nearClipPlane"] = c.nearClipPlane,
                    ["farClipPlane"] = c.farClipPlane,
                    ["cullingMask"] = c.cullingMask,
                    ["clearFlags"] = c.clearFlags.ToString(),
                    ["allowHDR"] = c.allowHDR,
                    ["allowMSAA"] = c.allowMSAA,
                    ["targetTexture"] = c.targetTexture == null ? null : (JToken)c.targetTexture.name,
                    ["pixelWidth"] = c.pixelWidth,
                    ["pixelHeight"] = c.pixelHeight,
                };

                if (includeMatrices)
                {
                    entry["worldToCameraMatrix"] = Matrix(c.worldToCameraMatrix);
                    entry["projectionMatrix"] = Matrix(c.projectionMatrix);
                    // What the shader actually receives: the platform flips and depth range are
                    // applied here, and forgetting that is why a CPU replica disagrees.
                    entry["gpuProjectionMatrix"] = Matrix(
                        GL.GetGPUProjectionMatrix(c.projectionMatrix, c.targetTexture != null));
                }

                return (object)entry;
            }).ToArray());

            // Measured rather than asserted: whether FindObjectsByType surfaces the Scene View's
            // own cameras depends on their hide flags, which have moved between Unity versions.
            var sceneViewCameras = SceneView.GetAllSceneCameras();

            return new JObject
            {
                ["count"] = cameras.Length,
                ["cameras"] = list,
                ["sceneViewCameraIncluded"] = cameras.Any(c => Array.IndexOf(sceneViewCameras, c) >= 0),
            };
        }

        private static JArray Matrix(Matrix4x4 m)
        {
            var rows = new JArray();

            for (var r = 0; r < 4; r++)
            {
                rows.Add(new JArray(
                    (object)Math.Round(m[r, 0], 6),
                    (object)Math.Round(m[r, 1], 6),
                    (object)Math.Round(m[r, 2], 6),
                    (object)Math.Round(m[r, 3], 6)));
            }

            return rows;
        }

        private static Texture2D LoadPng(string path, string argumentName)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new McpToolException("invalid_params", $"'{argumentName}' is required.");
            }

            var full = Path.GetFullPath(path);

            if (!File.Exists(full))
            {
                throw new McpToolException(
                    "not_found",
                    $"No file at '{path}'. Capture one with capture_screenshot and its save_path argument.");
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);

            if (!texture.LoadImage(File.ReadAllBytes(full)))
            {
                UnityEngine.Object.DestroyImmediate(texture);

                throw new McpToolException("invalid_params", $"'{path}' is not an image Unity can read.");
            }

            return texture;
        }
    }
}
