using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Handlers
{
    /// <summary>
    /// Captures screenshots of Unity Editor content. Supports:
    ///   - "game"  / "scene"  : Camera → RenderTexture (cross-platform)
    ///   - "inspector", "hierarchy", "project", "console",
    ///     "game_view_window", "scene_view_window",
    ///     "window:&lt;title&gt;"                 : EditorWindow desktop capture (Windows only)
    /// </summary>
    internal static class ScreenshotCapture
    {
        /// <summary>
        /// Largest edge an explicit width or height may ask for. A picture is priced by its area,
        /// so a 4096-square capture already costs a model about twenty thousand tokens; past this
        /// the call is refused rather than quietly shrunk.
        /// </summary>
        private const int MaxRequestedEdge = 4096;

        /// <summary>
        /// Editor panel view to window type name. Owned by <see cref="EditorWindowLocator"/> so
        /// capture and input replay cannot disagree about what a view name refers to.
        /// </summary>
        internal static IReadOnlyDictionary<string, string> ViewToTypeName => EditorWindowLocator.ViewToTypeName;

        /// <summary>
        /// Returns the capture as base64, or writes it to disk and returns the path instead.
        /// </summary>
        /// <remarks>
        /// A 1024px PNG is a few hundred kilobytes of base64, and comparing two of them means
        /// carrying both through the conversation. Writing to a file and passing paths to
        /// render_compare keeps the pixels out of the transcript entirely, which is the whole
        /// point of having a comparison tool rather than asking a model to eyeball two images.
        /// </remarks>
        private static JObject Deliver(
            byte[] pngBytes, string view, int width, int height, string savePath, string cameraName = null)
        {
            var result = new JObject
            {
                ["view"] = view,
                ["width"] = width,
                ["height"] = height,
                ["bytes"] = pngBytes.Length,
            };

            // Which camera drew this. Camera.main is not necessarily the one the caller just
            // made, and without the name a picture of the wrong view looks like a broken scene.
            if (cameraName != null)
            {
                result["camera"] = cameraName;
            }

            if (string.IsNullOrWhiteSpace(savePath))
            {
                result["image"] = Convert.ToBase64String(pngBytes);
                return result;
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

                System.IO.File.WriteAllBytes(full, pngBytes);
            }
            catch (Exception e) when (e is System.IO.IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException
                                          or ArgumentException)
            {
                throw new McpScreenshotException(
                    "save_path_unusable",
                    $"save_path '{savePath}' cannot be written: {e.Message}",
                    400);
            }

            result["path"] = full.Replace('\\', '/');
            result["note"] = "Written to disk rather than returned inline. Pass this path to render_compare.";

            return result;
        }

        private const uint SRCCOPY = 0x00CC0020;
        private const uint DIB_RGB_COLORS = 0;

        public static JObject Capture(JObject parameters)
        {
            var view = parameters["view"]?.ToString() ?? "game";
            var maxSize = parameters["maxSize"]?.Value<int>() ?? 1024;
            int? requestedWidth = parameters["width"]?.Value<int>();
            int? requestedHeight = parameters["height"]?.Value<int>();
            var savePath = parameters["savePath"]?.ToString();

            // Editor panel views (or explicit window:<title>) route to desktop capture.
            if (IsEditorPanelView(view))
            {
                // A panel is read off the desktop, with no camera to test the object against.
                // Answered silently, the reply looks like a capture that passed the check.
                if (!string.IsNullOrWhiteSpace(parameters["focus"]?.ToString()))
                {
                    throw new McpScreenshotException(
                        "invalid_params",
                        $"'focus' applies to the 'game' and 'scene' views, which render through a " +
                        $"camera. '{view}' is captured from the window itself, so there is nothing " +
                        "to test the object against.",
                        400);
                }

                return CaptureEditorWindow(view, maxSize, requestedWidth, requestedHeight, savePath);
            }

            // Camera-based views: "game" / "scene" only.
            if (view == "game" || view == "scene")
            {
                return CaptureCameraView(
                    view, maxSize, requestedWidth, requestedHeight, savePath,
                    parameters["camera"]?.ToString(),
                    parameters["focus"]?.ToString());
            }

            // Unknown view name — surface as invalid_params so clients get a proper error envelope.
            var supported = new List<string> { "game", "scene", "window:<title>" };
            supported.AddRange(ViewToTypeName.Keys);
            throw new McpScreenshotException(
                "invalid_params",
                $"Unknown view '{view}'. Supported: {string.Join(", ", supported)}",
                400);
        }

        private static bool IsEditorPanelView(string view)
        {
            if (string.IsNullOrEmpty(view)) return false;
            if (view.StartsWith(EditorWindowLocator.WindowPrefix, StringComparison.Ordinal)) return true;
            return ViewToTypeName.ContainsKey(view);
        }

        // ──────────────────────────────────────────────
        //  Camera-based capture (existing path, preserved)
        // ──────────────────────────────────────────────

        /// <summary>Where the object a capture is meant to show sits relative to the camera.</summary>
        /// <remarks>
        /// Nothing in a picture's reply says whether the subject was in it, so a camera aimed
        /// somewhere else answers with a perfectly valid image of the wrong place. One run spent
        /// three captures and a render_compare proving a light had no effect, when the light was
        /// at the origin and the camera was looking at a set built a thousand units away.
        /// </remarks>
        private static JObject FrameCheck(Camera camera, string focus, float aspect)
        {
            var go = Tools.ObjectResolve.Object(focus, null, "focus", null);

            // Only what would be drawn. A disabled renderer, or one under an inactive object, has
            // bounds that may still sit at the origin because nothing ever culled it, and taking
            // it in stretches the box from there to the real content - the same stretch the seed
            // below was removed to avoid.
            var renderers = go.GetComponentsInChildren<Renderer>(true)
                .Where(r => r.enabled && r.gameObject.activeInHierarchy)
                .ToArray();

            // A parent's own transform is not where its content is. Seeding the box with it and
            // then taking in the renderers stretched the box from the origin to a cube a thousand
            // units away, which passed the frustum test on the empty half and put the centre at a
            // screen x of 36031 in a 128-pixel-wide picture. Only an object with nothing to draw
            // falls back to its position.
            var bounds = renderers.Length > 0
                ? renderers[0].bounds
                : new Bounds(go.transform.position, Vector3.zero);

            for (var i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            // Built for the aspect the capture will actually render at, not the one the camera
            // happens to carry: a 1920x1080 Game View asked for a 256x1024 picture has a much
            // narrower horizontal field of view, and the camera's own planes passed an object the
            // render then left out of frame.
            var projection = ProjectionFor(camera, aspect);
            var clip = projection * camera.worldToCameraMatrix;
            var planes = GeometryUtility.CalculateFrustumPlanes(clip);
            var path = Tools.ObjectResolve.PathOf(go);

            if (!GeometryUtility.TestPlanesAABB(planes, bounds))
            {
                var centre = bounds.center;

                throw new McpScreenshotException(
                    "not_in_frame",
                    $"'{path}' is not in what {Tools.ObjectResolve.PathOf(camera.gameObject)} sees, " +
                    $"so the picture would not show it. The object is at " +
                    $"({centre.x:0.##}, {centre.y:0.##}, {centre.z:0.##}). Pick a camera that looks " +
                    "at it with render_camera_info, move one there, or drop 'focus' to capture " +
                    "this view anyway.",
                    400);
            }

            // Fractions of the frame rather than pixels: the capture is resized after this runs,
            // so a pixel coordinate would be quoted in a resolution the caller never receives.
            var viewport = Viewport(clip, bounds.center);

            return new JObject
            {
                ["path"] = path,
                ["inFrame"] = true,
                ["x"] = Math.Round(viewport.x, 3),
                ["y"] = Math.Round(viewport.y, 3),
                ["distance"] = Math.Round(viewport.z, 3),
            };
        }

        /// <summary>
        /// The camera a "game" or "scene" capture renders through, or null with
        /// <paramref name="error"/> set to why there is none.
        /// </summary>
        internal static Camera ResolveCamera(string view, string named, out string error)
        {
            error = null;

            if (view == "scene")
            {
                var sceneView = SceneView.lastActiveSceneView;

                if (sceneView == null || sceneView.camera == null)
                {
                    error = "No active scene view found";
                    return null;
                }

                return sceneView.camera;
            }

            if (!string.IsNullOrWhiteSpace(named))
            {
                var carrier = Tools.ObjectResolve.Object(named, null, "camera", null).GetComponent<Camera>();

                if (carrier == null)
                {
                    error = $"'{named}' carries no Camera.";
                }

                return carrier;
            }

            var camera = Camera.main;

            if (camera == null && Camera.allCameras.Length > 0)
            {
                camera = Camera.allCameras[0];
            }

            if (camera == null)
            {
                error = "No camera found in the scene";
            }

            return camera;
        }

        /// <summary>The size a capture takes when the caller asked for none.</summary>
        /// <remarks>
        /// A camera that has never drawn reports a zero <c>pixelWidth</c>, and the main Game view's
        /// size is the shape the caller is looking at, so that is asked for first.
        /// </remarks>
        internal static void SourceSize(string view, Camera camera, out int width, out int height)
        {
            if (view == "scene")
            {
                width = camera.pixelWidth;
                height = camera.pixelHeight;
                return;
            }

            try
            {
                var gameViewSize = Handles.GetMainGameViewSize();
                width = (int)gameViewSize.x;
                height = (int)gameViewSize.y;
            }
            catch
            {
                width = camera.pixelWidth;
                height = camera.pixelHeight;
            }

            if (width <= 0 || height <= 0)
            {
                width = camera.pixelWidth;
                height = camera.pixelHeight;
            }
        }

        private static JObject CaptureCameraView(
            string view, int maxSize, int? requestedWidth, int? requestedHeight,
            string savePath, string named = null, string focus = null)
        {
            try
            {
                var camera = ResolveCamera(view, named, out var refusal);

                if (camera == null)
                {
                    return new JObject { ["error"] = refusal };
                }

                SourceSize(view, camera, out var sourceWidth, out var sourceHeight);

                var captureWidth = requestedWidth ?? sourceWidth;
                var captureHeight = requestedHeight ?? sourceHeight;

                if (captureWidth <= 0 || captureHeight <= 0)
                {
                    return new JObject { ["error"] = "Invalid capture dimensions" };
                }

                if (requestedWidth > MaxRequestedEdge || requestedHeight > MaxRequestedEdge)
                {
                    throw new McpScreenshotException(
                        "invalid_params",
                        $"width and height are capped at {MaxRequestedEdge}. A picture costs a "
                        + "model by its area, and this one would be larger than any view of it needs.",
                        400);
                }

                // max_size shapes what the caller did not ask for. Applying it to an explicit
                // width or height contradicted their documented meaning and said nothing about it.
                var sized = requestedWidth.HasValue || requestedHeight.HasValue;

                if (!sized && (captureWidth > maxSize || captureHeight > maxSize))
                {
                    var scale = Math.Min((float)maxSize / captureWidth, (float)maxSize / captureHeight);
                    captureWidth = Mathf.Max(1, Mathf.RoundToInt(captureWidth * scale));
                    captureHeight = Mathf.Max(1, Mathf.RoundToInt(captureHeight * scale));
                }

                // Before the picture is paid for, not after, and against the shape the picture
                // will have: the frustum the render uses comes from the capture's own dimensions.
                var framing = string.IsNullOrWhiteSpace(focus)
                    ? null
                    : FrameCheck(camera, focus, (float)captureWidth / captureHeight);

                var rt = RenderTexture.GetTemporary(captureWidth, captureHeight, 24, RenderTextureFormat.ARGB32);
                var previousTargetTexture = camera.targetTexture;
                var previousActiveRT = RenderTexture.active;

                Texture2D tex2d = null;
                try
                {
                    camera.targetTexture = rt;
                    camera.Render();
                    camera.targetTexture = previousTargetTexture;

                    RenderTexture.active = rt;
                    tex2d = new Texture2D(captureWidth, captureHeight, TextureFormat.RGB24, false);
                    tex2d.ReadPixels(new Rect(0, 0, captureWidth, captureHeight), 0, 0);
                    tex2d.Apply();
                    RenderTexture.active = previousActiveRT;

                    var delivered = Deliver(
                        tex2d.EncodeToPNG(), view, captureWidth, captureHeight, savePath,
                        Tools.ObjectResolve.PathOf(camera.gameObject));

                    if (framing != null)
                    {
                        delivered["focus"] = framing;
                    }

                    return delivered;
                }
                finally
                {
                    camera.targetTexture = previousTargetTexture;
                    RenderTexture.active = previousActiveRT;
                    RenderTexture.ReleaseTemporary(rt);

                    if (tex2d != null)
                    {
                        UnityEngine.Object.DestroyImmediate(tex2d);
                    }
                }
            }
            // A refusal carries its own code and status, and wrapping it in the generic error
            // string turns "this camera cannot see it" into "capture failed". The object the focus
            // names is resolved here too, and its not_found is a refusal of the same kind.
            catch (Exception e) when (e is not McpToolException)
            {
                return new JObject { ["error"] = $"Screenshot capture failed: {e.Message}" };
            }
        }

        /// <summary>The projection this camera would use to fill a picture of the given aspect.</summary>
        private static Matrix4x4 ProjectionFor(Camera camera, float aspect)
        {
            if (camera.orthographic)
            {
                var half = camera.orthographicSize;

                return Matrix4x4.Ortho(
                    -half * aspect, half * aspect, -half, half,
                    camera.nearClipPlane, camera.farClipPlane);
            }

            return Matrix4x4.Perspective(
                camera.fieldOfView, aspect, camera.nearClipPlane, camera.farClipPlane);
        }

        /// <summary>Where a world point lands in the frame, as fractions from 0 to 1.</summary>
        private static Vector3 Viewport(Matrix4x4 worldToClip, Vector3 point)
        {
            var clip = worldToClip * new Vector4(point.x, point.y, point.z, 1f);

            if (Mathf.Approximately(clip.w, 0f))
            {
                return new Vector3(0.5f, 0.5f, 0f);
            }

            return new Vector3(
                (clip.x / clip.w + 1f) * 0.5f,
                (clip.y / clip.w + 1f) * 0.5f,
                clip.w);
        }

        // ──────────────────────────────────────────────
        //  EditorWindow capture (desktop DC, Windows only)
        // ──────────────────────────────────────────────

        private static JObject CaptureEditorWindow(string view, int maxSize, int? requestedWidth, int? requestedHeight, string savePath)
        {
#if UNITY_EDITOR_WIN
            var window = ResolveEditorWindow(view);
            var rect = window.position;

            if (rect.width <= 0 || rect.height <= 0)
            {
                throw new McpScreenshotException(
                    "window_minimized",
                    $"EditorWindow '{window.titleContent.text}' is minimized or off-screen (position={rect}).",
                    400);
            }

            // Activate docked-but-inactive tab before capture.
            try
            {
                window.Focus();
            }
            catch
            {
                // Focus may fail in some edge cases; capture proceeds with the registered rect.
            }

            RefuseIfAnotherApplicationIsInFront(window);

            Texture2D captured = null;
            Texture2D resized = null;
            try
            {
                captured = CaptureDesktopRegion(rect);

                // Apply maxSize / width / height resize.
                var targetWidth = requestedWidth ?? captured.width;
                var targetHeight = requestedHeight ?? captured.height;

                if (targetWidth <= 0 || targetHeight <= 0)
                {
                    throw new McpScreenshotException(
                        "invalid_params",
                        "Requested width/height must be positive.",
                        400);
                }

                if (targetWidth > MaxRequestedEdge || targetHeight > MaxRequestedEdge)
                {
                    throw new McpScreenshotException(
                        "invalid_params",
                        $"width and height are capped at {MaxRequestedEdge}. A picture costs a "
                        + "model by its area, and this one would be larger than any view of it needs.",
                        400);
                }

                var askedForSize = requestedWidth.HasValue || requestedHeight.HasValue;

                if (!askedForSize && (targetWidth > maxSize || targetHeight > maxSize))
                {
                    var scale = Math.Min((float)maxSize / targetWidth, (float)maxSize / targetHeight);
                    targetWidth = Mathf.Max(1, Mathf.RoundToInt(targetWidth * scale));
                    targetHeight = Mathf.Max(1, Mathf.RoundToInt(targetHeight * scale));
                }

                Texture2D finalTex;
                if (targetWidth == captured.width && targetHeight == captured.height)
                {
                    finalTex = captured;
                }
                else
                {
                    resized = ResizeTexture(captured, targetWidth, targetHeight);
                    finalTex = resized;
                }

                var delivered = Deliver(
                    finalTex.EncodeToPNG(), view, finalTex.width, finalTex.height, savePath);
                delivered["windowTitle"] = window.titleContent.text;
                return delivered;
            }
            finally
            {
                if (captured != null)
                {
                    UnityEngine.Object.DestroyImmediate(captured);
                }

                if (resized != null && resized != captured)
                {
                    UnityEngine.Object.DestroyImmediate(resized);
                }
            }
#else
            throw new McpScreenshotException(
                "unsupported_platform",
                "Editor window capture is Windows-only in v2.1. Use view=game or view=scene on other platforms.",
                501);
#endif
        }

        private static EditorWindow ResolveEditorWindow(string view)
        {
            try
            {
                return EditorWindowLocator.Resolve(view);
            }
            catch (McpToolException e)
            {
                // The capture route translates only its own exception type into an error
                // envelope; the code and status carry across unchanged.
                throw new McpScreenshotException(e.Code, e.Message, e.HttpStatus);
            }
        }

#if UNITY_EDITOR_WIN
        // ── P/Invoke bindings (Windows GDI/User32) ──

        [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObj);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hDest, int dx, int dy, int w, int h, IntPtr hSrc, int sx, int sy, uint op);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObj);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hDC, IntPtr hBmp, uint uStart, uint cLines, byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            // Followed by RGBQUAD[1] in the native layout; unused for 32-bpp BI_RGB capture.
            public uint bmiColors0;
        }

        /// <summary>
        /// Refuses the capture when the window in front belongs to another process.
        /// </summary>
        /// <remarks>
        /// The grab reads the desktop, not the window, so whatever is drawn over the Editor is
        /// what gets returned and then sent on to a model. Focus() above raises the panel within
        /// the Editor but cannot raise the Editor over another application, and the caller has no
        /// way to see that it happened.
        /// </remarks>
        private static void RefuseIfAnotherApplicationIsInFront(EditorWindow window)
        {
            var foreground = GetForegroundWindow();

            if (foreground == IntPtr.Zero)
            {
                return;
            }

            GetWindowThreadProcessId(foreground, out var owner);

            if (owner == 0 || owner == (uint)Process.GetCurrentProcess().Id)
            {
                return;
            }

            throw new McpScreenshotException(
                "window_occluded",
                $"Another application is in front of the Editor, so capturing '{window.titleContent.text}' " +
                "off the screen would return that application's window instead. Bring the Editor to the " +
                "front, or use 'game' or 'scene', which Unity renders and which no other window can reach.",
                409);
        }

        private static Texture2D CaptureDesktopRegion(Rect logicalRect)
        {
            var hwnd = Process.GetCurrentProcess().MainWindowHandle;
            var dpi = GetDpiForWindow(hwnd);
            if (dpi == 0) dpi = 96;
            var scale = dpi / 96f;

            var physicalX = Mathf.RoundToInt(logicalRect.x * scale);
            var physicalY = Mathf.RoundToInt(logicalRect.y * scale);
            var physicalW = Mathf.Max(1, Mathf.RoundToInt(logicalRect.width * scale));
            var physicalH = Mathf.Max(1, Mathf.RoundToInt(logicalRect.height * scale));

            var desktopHwnd = GetDesktopWindow();
            var desktopDC = GetWindowDC(desktopHwnd);
            if (desktopDC == IntPtr.Zero)
            {
                throw new McpScreenshotException("internal_error", "GetWindowDC(GetDesktopWindow()) returned NULL.", 500);
            }

            var destDC = IntPtr.Zero;
            var bmp = IntPtr.Zero;
            var previousObject = IntPtr.Zero;
            try
            {
                destDC = CreateCompatibleDC(desktopDC);
                if (destDC == IntPtr.Zero)
                {
                    throw new McpScreenshotException("internal_error", "CreateCompatibleDC failed.", 500);
                }

                bmp = CreateCompatibleBitmap(desktopDC, physicalW, physicalH);
                if (bmp == IntPtr.Zero)
                {
                    throw new McpScreenshotException("internal_error", "CreateCompatibleBitmap failed.", 500);
                }

                previousObject = SelectObject(destDC, bmp);

                if (!BitBlt(destDC, 0, 0, physicalW, physicalH, desktopDC, physicalX, physicalY, SRCCOPY))
                {
                    throw new McpScreenshotException("internal_error", "BitBlt failed.", 500);
                }

                var bmi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                        biWidth = physicalW,
                        // Positive height asks GDI for a bottom-up DIB, which is the row order
                        // LoadRawTextureData already expects: Unity's textures put v=0 at the
                        // bottom. Asking for top-down here, as this did, produces a texture that
                        // is upside down — captures came out mirrored top to bottom, readable
                        // only as a mirror image, for every Editor panel.
                        biHeight = physicalH,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0, // BI_RGB
                        biSizeImage = 0,
                        biXPelsPerMeter = 0,
                        biYPelsPerMeter = 0,
                        biClrUsed = 0,
                        biClrImportant = 0
                    },
                    bmiColors0 = 0
                };

                var bgra = new byte[physicalW * physicalH * 4];
                var lines = GetDIBits(destDC, bmp, 0, (uint)physicalH, bgra, ref bmi, DIB_RGB_COLORS);
                if (lines == 0)
                {
                    throw new McpScreenshotException("internal_error", "GetDIBits returned 0 lines.", 500);
                }

                // BGRA → RGBA in place.
                for (var i = 0; i < bgra.Length; i += 4)
                {
                    var b = bgra[i];
                    bgra[i] = bgra[i + 2]; // R
                    bgra[i + 2] = b;        // B
                    // alpha from BitBlt of desktop DC is typically 0; force opaque.
                    bgra[i + 3] = 255;
                }

                var tex = new Texture2D(physicalW, physicalH, TextureFormat.RGBA32, false);
                tex.LoadRawTextureData(bgra);
                tex.Apply(false, false);
                return tex;
            }
            finally
            {
                if (previousObject != IntPtr.Zero && destDC != IntPtr.Zero)
                {
                    SelectObject(destDC, previousObject);
                }

                if (bmp != IntPtr.Zero)
                {
                    DeleteObject(bmp);
                }

                if (destDC != IntPtr.Zero)
                {
                    DeleteDC(destDC);
                }

                if (desktopDC != IntPtr.Zero)
                {
                    ReleaseDC(desktopHwnd, desktopDC);
                }
            }
        }
#endif

        internal static Texture2D ResizeTexture(Texture2D source, int newWidth, int newHeight)
        {
            var rt = RenderTexture.GetTemporary(newWidth, newHeight, 0, RenderTextureFormat.ARGB32);
            var previousActive = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var result = new Texture2D(newWidth, newHeight, TextureFormat.RGBA32, false);
                result.ReadPixels(new Rect(0, 0, newWidth, newHeight), 0, 0);
                result.Apply();
                return result;
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }

    /// <summary>
    /// A capture failure the caller can act on: a minimised window, a bad size, another
    /// application in front of the Editor.
    /// </summary>
    /// <remarks>
    /// Derives from <see cref="McpToolException"/> so the code and status reach the envelope.
    /// As a standalone type it was turned into <c>tool_failed</c> with a 500, which made a
    /// refusal look like a fault and had the retry policy repeat it for fifteen seconds.
    /// </remarks>
    internal sealed class McpScreenshotException : McpToolException
    {
        public McpScreenshotException(string code, string message, int httpStatus)
            : base(code, message, httpStatus)
        {
        }
    }
}
