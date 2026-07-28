using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

using Debug = UnityEngine.Debug;

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
        // ── Editor panel view → EditorWindow full type name map ──
        internal static readonly IReadOnlyDictionary<string, string> ViewToTypeName =
            new Dictionary<string, string>
            {
                ["inspector"]         = "UnityEditor.InspectorWindow",
                ["hierarchy"]         = "UnityEditor.SceneHierarchyWindow",
                ["project"]           = "UnityEditor.ProjectBrowser",
                ["console"]           = "UnityEditor.ConsoleWindow",
                ["game_view_window"]  = "UnityEditor.GameView",
                ["scene_view_window"] = "UnityEditor.SceneView",
            };

        /// <summary>
        /// Returns the capture as base64, or writes it to disk and returns the path instead.
        /// </summary>
        /// <remarks>
        /// A 1024px PNG is a few hundred kilobytes of base64, and comparing two of them means
        /// carrying both through the conversation. Writing to a file and passing paths to
        /// render_compare keeps the pixels out of the transcript entirely, which is the whole
        /// point of having a comparison tool rather than asking a model to eyeball two images.
        /// </remarks>
        private static JObject Deliver(byte[] pngBytes, string view, int width, int height, string savePath)
        {
            var result = new JObject
            {
                ["view"] = view,
                ["width"] = width,
                ["height"] = height,
                ["bytes"] = pngBytes.Length,
            };

            if (string.IsNullOrWhiteSpace(savePath))
            {
                result["image"] = Convert.ToBase64String(pngBytes);
                return result;
            }

            var full = System.IO.Path.GetFullPath(savePath);
            var directory = System.IO.Path.GetDirectoryName(full);

            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            System.IO.File.WriteAllBytes(full, pngBytes);

            result["path"] = full.Replace('\\', '/');
            result["note"] = "Written to disk rather than returned inline. Pass this path to render_compare.";

            return result;
        }

        private const string WindowPrefix = "window:";
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
                return CaptureEditorWindow(view, maxSize, requestedWidth, requestedHeight, savePath);
            }

            // Camera-based views: "game" / "scene" only.
            if (view == "game" || view == "scene")
            {
                return CaptureCameraView(view, maxSize, requestedWidth, requestedHeight, savePath);
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
            if (view.StartsWith(WindowPrefix, StringComparison.Ordinal)) return true;
            return ViewToTypeName.ContainsKey(view);
        }

        // ──────────────────────────────────────────────
        //  Camera-based capture (existing path, preserved)
        // ──────────────────────────────────────────────

        private static JObject CaptureCameraView(string view, int maxSize, int? requestedWidth, int? requestedHeight, string savePath)
        {
            try
            {
                Camera camera;
                int sourceWidth;
                int sourceHeight;

                if (view == "scene")
                {
                    var sceneView = SceneView.lastActiveSceneView;
                    if (sceneView == null || sceneView.camera == null)
                    {
                        return new JObject { ["error"] = "No active scene view found" };
                    }

                    camera = sceneView.camera;
                    sourceWidth = camera.pixelWidth;
                    sourceHeight = camera.pixelHeight;
                }
                else
                {
                    camera = Camera.main;
                    if (camera == null && Camera.allCameras.Length > 0)
                    {
                        camera = Camera.allCameras[0];
                    }

                    if (camera == null)
                    {
                        return new JObject { ["error"] = "No camera found in the scene" };
                    }

                    try
                    {
                        var gameViewSize = Handles.GetMainGameViewSize();
                        sourceWidth = (int)gameViewSize.x;
                        sourceHeight = (int)gameViewSize.y;
                    }
                    catch
                    {
                        sourceWidth = camera.pixelWidth;
                        sourceHeight = camera.pixelHeight;
                    }

                    if (sourceWidth <= 0 || sourceHeight <= 0)
                    {
                        sourceWidth = camera.pixelWidth;
                        sourceHeight = camera.pixelHeight;
                    }
                }

                var captureWidth = requestedWidth ?? sourceWidth;
                var captureHeight = requestedHeight ?? sourceHeight;

                if (captureWidth <= 0 || captureHeight <= 0)
                {
                    return new JObject { ["error"] = "Invalid capture dimensions" };
                }

                if (captureWidth > maxSize || captureHeight > maxSize)
                {
                    var scale = Math.Min((float)maxSize / captureWidth, (float)maxSize / captureHeight);
                    captureWidth = Mathf.Max(1, Mathf.RoundToInt(captureWidth * scale));
                    captureHeight = Mathf.Max(1, Mathf.RoundToInt(captureHeight * scale));
                }

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

                    return Deliver(tex2d.EncodeToPNG(), view, captureWidth, captureHeight, savePath);
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
            catch (Exception e)
            {
                return new JObject { ["error"] = $"Screenshot capture failed: {e.Message}" };
            }
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

                if (targetWidth > maxSize || targetHeight > maxSize)
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

                var delivered = Deliver(finalTex.EncodeToPNG(), view, finalTex.width, finalTex.height, savePath);
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
            var all = UnityEngine.Resources.FindObjectsOfTypeAll<EditorWindow>();

            List<EditorWindow> candidates;
            if (view.StartsWith(WindowPrefix, StringComparison.Ordinal))
            {
                var needle = view.Substring(WindowPrefix.Length);
                candidates = new List<EditorWindow>();
                foreach (var win in all)
                {
                    if (win == null) continue;
                    var title = win.titleContent?.text ?? string.Empty;
                    if (title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        candidates.Add(win);
                    }
                }
            }
            else if (ViewToTypeName.TryGetValue(view, out var typeName))
            {
                candidates = new List<EditorWindow>();
                foreach (var win in all)
                {
                    if (win == null) continue;
                    if (win.GetType().FullName == typeName)
                    {
                        candidates.Add(win);
                    }
                }
            }
            else
            {
                throw new McpScreenshotException(
                    "invalid_params",
                    $"Unknown view '{view}'.",
                    400);
            }

            if (candidates.Count == 0)
            {
                throw new McpScreenshotException(
                    "window_not_found",
                    $"No EditorWindow matches view '{view}'.",
                    400);
            }

            if (candidates.Count > 1)
            {
                Debug.LogWarning(
                    $"[ScreenshotCapture] multiple_matches: {candidates.Count} EditorWindows match view '{view}'. Using the first one ('{candidates[0].titleContent.text}').");
            }

            return candidates[0];
        }

#if UNITY_EDITOR_WIN
        // ── P/Invoke bindings (Windows GDI/User32) ──

        [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
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

        private static Texture2D ResizeTexture(Texture2D source, int newWidth, int newHeight)
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
    /// Exception carrying an MCP error code + HTTP status for screenshot capture failures.
    /// Caught by McpHttpServer.HandleCaptureScreenshot and translated to an error envelope.
    /// </summary>
    internal sealed class McpScreenshotException : Exception
    {
        public string Code { get; }
        public int HttpStatus { get; }

        public McpScreenshotException(string code, string message, int httpStatus)
            : base(message)
        {
            this.Code = code;
            this.HttpStatus = httpStatus;
        }
    }
}
