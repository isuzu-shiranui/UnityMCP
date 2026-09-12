using System;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEngine;
using UnityEngine.Rendering;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Handlers
{
    /// <summary>
    /// Saves one of the render pipeline's intermediate targets — the depth, normals, motion or
    /// opaque texture — as a picture.
    /// </summary>
    /// <remarks>
    /// Three things make this hard to do by hand, and each of them fails quietly.
    /// <para>
    /// The global textures are pooled. Reading one after <c>Camera.Render</c> has returned hands
    /// back a target that has already been released and reused, which comes out as every pixel
    /// zero — and on a reversed-Z depth buffer zero is the far plane, so the result looks like a
    /// correctly captured empty scene. The grab has to happen inside
    /// <c>RenderPipelineManager.endCameraRendering</c>, which fires for a manual
    /// <c>Camera.Render</c> as well as for the Editor's own.
    /// </para>
    /// <para>
    /// Depth has to be read through a single-channel float target. An 8-bit readback quantises a
    /// reversed-Z buffer, whose useful values sit within a few hundredths of zero, to nothing.
    /// </para>
    /// <para>
    /// A buffer the pipeline is not producing reads as null rather than as an error, so every
    /// kind carries the name of the setting that turns it on.
    /// </para>
    /// </remarks>
    internal static class CameraBufferCapture
    {
        /// <summary>Pixels at the far plane are painted this, leaving 1-255 for real distances.</summary>
        private const byte Background = 0;

        /// <summary>
        /// Below this on either edge, what is bound is one of Unity's placeholder textures rather
        /// than a target the camera drew into.
        /// </summary>
        /// <remarks>
        /// A camera that is not producing one of these leaves the global holding a 4x4 stand-in,
        /// not null, so a null check alone lets a picture of the stand-in through as an answer.
        /// </remarks>
        private const int PlaceholderEdge = 16;

        internal sealed class Kind
        {
            public string Name { get; set; }

            /// <summary>The shader global the pipeline leaves the target in.</summary>
            public string Global { get; set; }

            /// <summary>What a caller turns on when the pipeline is not producing it.</summary>
            public string Setting { get; set; }

            public bool IsDepth { get; set; }
        }

        internal static readonly Kind[] Kinds =
        {
            new Kind
            {
                Name = "depth",
                Global = "_CameraDepthTexture",
                Setting = "Depth Texture on the URP asset, or Output > Depth Texture on the camera",
                IsDepth = true,
            },
            new Kind
            {
                Name = "normals",
                Global = "_CameraNormalsTexture",
                Setting = "a DepthNormals prepass, which URP runs when SSAO or a renderer feature asks for one",
            },
            new Kind
            {
                Name = "motion",
                Global = "_MotionVectorTexture",
                Setting = "motion vectors, which URP renders when a renderer feature or temporal anti-aliasing asks for them",
            },
            new Kind
            {
                Name = "opaque",
                Global = "_CameraOpaqueTexture",
                Setting = "Opaque Texture on the URP asset, or Output > Opaque Texture on the camera",
            },
        };

        internal static Kind Named(string buffer)
        {
            var match = Kinds.FirstOrDefault(k => string.Equals(k.Name, buffer, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                return match;
            }

            throw new McpToolException(
                "invalid_params",
                $"'{buffer}' is not a buffer this can capture. It takes "
                + string.Join(", ", Kinds.Select(k => k.Name))
                + ". capture_screenshot is what takes the finished picture.");
        }

        internal static void RequireSupport()
        {
            if (GraphicsSettings.currentRenderPipeline == null)
            {
                throw new McpToolException(
                    "not_supported",
                    "These are targets a scriptable render pipeline leaves behind, and this project "
                    + "is on the built-in one, which has no point at which they can be read. "
                    + "render_pipeline_info reports which pipeline is active.",
                    501);
            }
        }

        /// <summary>
        /// Eye-space distance in metres for one depth sample.
        /// </summary>
        /// <remarks>
        /// The perspective form is Unity's <c>LinearEyeDepth</c> written out. An orthographic
        /// camera writes depth linearly and the same arithmetic returns the near plane for every
        /// pixel of it.
        /// </remarks>
        internal static float Distance(float sample, float near, float far, bool reversed, bool orthographic)
        {
            if (orthographic)
            {
                return reversed ? far - (sample * (far - near)) : near + (sample * (far - near));
            }

            return reversed
                ? 1f / ((sample * ((1f / near) - (1f / far))) + (1f / far))
                : 1f / ((sample * (1f - (far / near)) / far) + (1f / near));
        }

        /// <summary>
        /// Renders <paramref name="camera"/> at the given size and returns the pipeline target
        /// named by <paramref name="kind"/>, or null when the pipeline did not produce one.
        /// </summary>
        /// <remarks>
        /// The subscription is removed in a <c>finally</c>: left in place it would blit into a
        /// released target on every subsequent frame the Editor draws.
        /// </remarks>
        private static Texture2D Grab(Camera camera, Kind kind, int width, int height)
        {
            RenderTexture copy = null;

            void OnCameraRendered(ScriptableRenderContext context, Camera rendered)
            {
                // Single-pass stereo calls this once per eye, and taking the second would leak
                // the first target and answer with the right eye's view of the scene.
                if (rendered != camera || copy != null)
                {
                    return;
                }

                var source = Shader.GetGlobalTexture(kind.Global);

                if (source == null || source.width < PlaceholderEdge || source.height < PlaceholderEdge)
                {
                    return;
                }

                copy = RenderTexture.GetTemporary(
                    source.width,
                    source.height,
                    0,
                    kind.IsDepth ? RenderTextureFormat.RFloat : RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Linear);

                Graphics.Blit(source, copy);
            }

            var target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;

            try
            {
                RenderPipelineManager.endCameraRendering += OnCameraRendered;

                try
                {
                    camera.targetTexture = target;
                    camera.Render();
                }
                finally
                {
                    camera.targetTexture = previousTarget;
                    RenderPipelineManager.endCameraRendering -= OnCameraRendered;
                }

                if (copy == null)
                {
                    return null;
                }

                RenderTexture.active = copy;

                var pixels = new Texture2D(
                    copy.width,
                    copy.height,
                    kind.IsDepth ? TextureFormat.RFloat : TextureFormat.RGBA32,
                    false);

                pixels.ReadPixels(new Rect(0, 0, copy.width, copy.height), 0, 0);
                pixels.Apply();

                return pixels;
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(target);

                if (copy != null)
                {
                    RenderTexture.ReleaseTemporary(copy);
                }
            }
        }

        /// <summary>
        /// Turns a float depth readback into a grey picture and the numbers needed to read it
        /// back: white is the nearest surface, black the farthest, and pure black also means
        /// nothing was drawn there.
        /// </summary>
        internal static JObject Shade(Texture2D depth, Camera camera, out Texture2D grey)
        {
            var samples = depth.GetPixels();
            var reversed = SystemInfo.usesReversedZBuffer;
            var far = camera.farClipPlane;
            var near = camera.nearClipPlane;

            var distances = new float[samples.Length];
            var nearest = float.MaxValue;
            var farthest = float.MinValue;
            var background = 0;

            for (var i = 0; i < samples.Length; i++)
            {
                var metres = Distance(samples[i].r, near, far, reversed, camera.orthographic);
                distances[i] = metres;

                // The far plane is where the pipeline cleared to, not a surface it measured.
                if (metres >= far * 0.9999f)
                {
                    background++;
                    continue;
                }

                nearest = Mathf.Min(nearest, metres);
                farthest = Mathf.Max(farthest, metres);
            }

            var hit = samples.Length - background;
            var span = hit == 0 ? 0f : farthest - nearest;
            var shaded = new Color32[samples.Length];

            for (var i = 0; i < samples.Length; i++)
            {
                if (distances[i] >= far * 0.9999f)
                {
                    shaded[i] = new Color32(Background, Background, Background, 255);
                    continue;
                }

                var t = span <= 0f ? 0f : (distances[i] - nearest) / span;
                var value = (byte)Mathf.Clamp(Mathf.RoundToInt(255f - (t * 254f)), 1, 255);
                shaded[i] = new Color32(value, value, value, 255);
            }

            grey = new Texture2D(depth.width, depth.height, TextureFormat.RGBA32, false);
            grey.SetPixels32(shaded);
            grey.Apply();

            return new JObject
            {
                ["nearestMetres"] = hit == 0 ? (JToken)JValue.CreateNull() : Math.Round(nearest, 4),
                ["farthestMetres"] = hit == 0 ? (JToken)JValue.CreateNull() : Math.Round(farthest, 4),
                ["backgroundPixels"] = background,
                ["measuredPixels"] = hit,
                ["note"] = "Grey 255 is nearestMetres and grey 1 is farthestMetres, spread linearly "
                           + "over distance. Grey 0 is the far plane, where nothing was drawn.",
            };
        }

        internal static JObject Capture(
            string buffer, string view, string named, int maxSize, int? width, int? height, string savePath)
        {
            var kind = Named(buffer);
            RequireSupport();

            var camera = ScreenshotCapture.ResolveCamera(view, named, out var refusal);

            if (camera == null)
            {
                throw new McpToolException("not_found", refusal, 404);
            }

            // The pipeline sizes its own targets and ignores the size of the camera's colour
            // target, so shrinking the render would not shrink what is captured - it would only
            // risk reading a target larger than the viewport just drawn into. The requested size
            // is applied to the finished picture instead.
            ScreenshotCapture.SourceSize(view, camera, out var renderWidth, out var renderHeight);

            if (renderWidth <= 0 || renderHeight <= 0)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{camera.name}' has no size to render at ({renderWidth}x{renderHeight}). "
                    + "It has never drawn, and a camera with no viewport has no buffers either.");
            }

            var pixels = Grab(camera, kind, renderWidth, renderHeight);

            if (pixels == null)
            {
                throw new McpToolException(
                    "not_found",
                    $"This pipeline left no {kind.Global} behind for '{camera.name}', so there is "
                    + $"nothing to capture. Turn on {kind.Setting}, then ask again. What is bound "
                    + "instead is Unity's placeholder, and a picture of that would read as a "
                    + "captured buffer - for depth, as a scene with nothing in it.",
                    404);
            }

            Texture2D picture = null;

            try
            {
                JObject result;

                if (kind.IsDepth)
                {
                    result = Shade(pixels, camera, out picture);
                }
                else
                {
                    result = new JObject();
                    picture = pixels;
                }

                var capturedWidth = picture.width;
                var capturedHeight = picture.height;
                var shown = Fit(picture, maxSize, width, height);

                try
                {
                    var png = shown.EncodeToPNG();

                    result["buffer"] = kind.Name;
                    result["camera"] = camera.name;
                    result["width"] = shown.width;
                    result["height"] = shown.height;
                    result["bytes"] = png.Length;

                    if (shown.width != capturedWidth || shown.height != capturedHeight)
                    {
                        result["capturedAt"] = $"{capturedWidth}x{capturedHeight}";
                    }

                    Deliver(result, png, savePath);
                    return result;
                }
                finally
                {
                    if (!ReferenceEquals(shown, picture))
                    {
                        UnityEngine.Object.DestroyImmediate(shown);
                    }
                }
            }
            finally
            {
                if (picture != null && !ReferenceEquals(picture, pixels))
                {
                    UnityEngine.Object.DestroyImmediate(picture);
                }

                UnityEngine.Object.DestroyImmediate(pixels);
            }
        }

        /// <summary>
        /// The picture at the size the caller asked for, or the picture itself when it already
        /// fits.
        /// </summary>
        private static Texture2D Fit(Texture2D picture, int maxSize, int? width, int? height)
        {
            if (width.HasValue || height.HasValue)
            {
                var wanted = Mathf.Max(1, width ?? picture.width);
                var tall = Mathf.Max(1, height ?? picture.height);

                return wanted == picture.width && tall == picture.height
                    ? picture
                    : ScreenshotCapture.ResizeTexture(picture, wanted, tall);
            }

            if (picture.width <= maxSize && picture.height <= maxSize)
            {
                return picture;
            }

            var scale = Mathf.Min((float)maxSize / picture.width, (float)maxSize / picture.height);

            return ScreenshotCapture.ResizeTexture(
                picture,
                Mathf.Max(1, Mathf.RoundToInt(picture.width * scale)),
                Mathf.Max(1, Mathf.RoundToInt(picture.height * scale)));
        }

        private static void Deliver(JObject result, byte[] png, string savePath)
        {
            if (string.IsNullOrWhiteSpace(savePath))
            {
                result["image"] = Convert.ToBase64String(png);
                return;
            }

            string full;

            try
            {
                full = System.IO.Path.GetFullPath(savePath);
                var directory = System.IO.Path.GetDirectoryName(full);

                if (!string.IsNullOrEmpty(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }

                System.IO.File.WriteAllBytes(full, png);
            }
            catch (Exception e)
            {
                throw new McpToolException(
                    "save_path_unusable",
                    $"'{savePath}' could not be written: {e.Message}",
                    400);
            }

            result["path"] = full.Replace('\\', '/');
        }
    }
}
