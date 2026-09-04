using System;
using System.Linq;
using System.Reflection;

using Newtonsoft.Json.Linq;

using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
using UnityEditor.Recorder.Timeline;

using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Recorder
{
    /// <summary>
    /// Adding a Recorder track to a Timeline, the way a live render is set up.
    /// </summary>
    /// <remarks>
    /// Recorder is driven through a track on the timeline rather than the Recorder API directly:
    /// the RecorderClip picks up its frame rate from the timeline, and the whole render is one
    /// PlayableDirector play. It also sidesteps the Recorder API's version drift — the settings
    /// classes reached here (RecorderClip.settings, the input settings, OutputFormat, OutputFile)
    /// are the public surface that has stayed put across 2.x to 5.x.
    /// <para>
    /// Own assembly, constrained to <c>UNITY_RECORDER</c> and <c>UNITY_TIMELINE</c>: a project
    /// without both packages loses these tools rather than failing to compile.
    /// </para>
    /// </remarks>
    internal static class RecorderTools
    {
        [McpTool(
            "recorder_add_track",
            "Add a Recorder track and clip to a Timeline, so playing the director records it. Set " +
            "the output type and format (movie mp4/webm/mov, or image png/jpeg/exr), the input " +
            "source (game view, a camera, or a render texture), the resolution and the output path. " +
            "The frame rate is not set here — the Recorder takes it from the timeline. Adding the " +
            "track saves every unsaved asset in the project, not just this timeline, so any pending " +
            "edit elsewhere is committed to disk by this call.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Add Recorder Track",
            // Several arguments constrain each other — camera_tag only means anything with
            // source=tagged_camera, format only with type=movie — so the combinations are shown
            // rather than left to be inferred from a list of independent-looking parameters.
            Examples = new[]
            {
                @"{""object_path"":""/StageDirector"",""type"":""movie"",""format"":""mp4"",""source"":""game_view"",""width"":1920,""height"":1080}",
                @"{""object_path"":""/StageDirector"",""type"":""png"",""source"":""tagged_camera"",""camera_tag"":""MainCamera"",""output_path"":""Assets/Shots/frame""}",
            })]
        public static JObject AddTrack(
            [McpArg("object_path", "Hierarchy path of the GameObject with the PlayableDirector.")]
            string objectPath = null,
            [McpArg("instance_id", "Address the director's GameObject by instance id instead.")]
            long? instanceId = null,
            [McpArg("output_path", "Where to write the recording, without extension. " +
                                   "Recorder adds one for the format. Omit to write to a " +
                                   "'Recording' folder beside Assets, named after the timeline.")]
            string outputPath = null,
            [McpArg("type", "What to record: movie, png, jpeg or exr. Defaults to movie.")]
            string type = "movie",
            [McpArg("format", "Movie container/codec: mp4, webm or mov. Ignored for image types.")]
            string format = "mp4",
            [McpArg("source", "What to capture: game_view, active_camera, main_camera, tagged_camera " +
                              "or render_texture.")]
            string source = "game_view",
            [McpArg("camera_tag", "Tag of the camera to record, when source is tagged_camera.")]
            string cameraTag = null,
            [McpArg("render_texture_path", "Asset path of the RenderTexture, when source is render_texture.")]
            string renderTexturePath = null,
            [McpArg("width", "Output width in pixels. Omit to use the source's own size.")]
            int? width = null,
            [McpArg("height", "Output height in pixels.")]
            int? height = null,
            [McpArg("capture_alpha", "Record the alpha channel, for formats that support it.")]
            bool captureAlpha = false,
            [McpArg("start", "Clip start on the timeline, in seconds.")]
            double start = 0,
            [McpArg("duration", "Clip length in seconds. Omit to cover the timeline's duration.")]
            double? duration = null,
            [McpArg("track_name", "Name for the new track.")]
            string trackName = "Recorder")
        {
            var director = ResolveDirector(objectPath, instanceId);
            var timeline = director.playableAsset as TimelineAsset;

            if (timeline == null)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{ObjectResolve.PathOf(director.gameObject)}' has no TimelineAsset to add a track to.");
            }

            var settings = BuildSettings(type, format, outputPath, captureAlpha, timeline.name);
            ApplySource(settings, source, cameraTag, renderTexturePath, width, height);
            settings.name = trackName + " Settings";

            // The settings live as a sub-asset of the timeline, so they are saved and loaded with
            // it rather than dangling. CreateTrack and CreateDefaultClip below register their own
            // undo, but this object is created here and would otherwise be left behind inside the
            // asset when the track it belongs to is undone.
            AssetDatabase.AddObjectToAsset(settings, timeline);
            Undo.RegisterCreatedObjectUndo(settings, "MCP Add Recorder Track");

            var track = timeline.CreateTrack<RecorderTrack>(null, trackName);
            var clip = track.CreateDefaultClip();
            clip.start = start;
            clip.duration = duration ?? Math.Max(director.duration, 0.001);
            clip.displayName = trackName;

            var recorderClip = (RecorderClip)clip.asset;
            recorderClip.settings = settings;

            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssets();

            return new JObject
            {
                ["director"] = ObjectResolve.PathOf(director.gameObject),
                ["timeline"] = timeline.name,
                ["track"] = trackName,
                ["type"] = settings.GetType().Name,
                ["outputFile"] = settings.OutputFile,
                ["source"] = source,
                ["start"] = clip.start,
                ["duration"] = clip.duration,
                ["note"] = "Play the director to record. The frame rate comes from the timeline.",
            };
        }

        [McpTool(
            "recorder_list",
            "List the Recorder tracks on a Timeline and what each is set to record. Read this to " +
            "confirm a render is configured before playing it.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject List(
            [McpArg("object_path", "Hierarchy path of the GameObject with the PlayableDirector.")]
            string objectPath = null,
            [McpArg("instance_id", "Address the director's GameObject by instance id instead.")]
            long? instanceId = null)
        {
            var director = ResolveDirector(objectPath, instanceId);
            var timeline = director.playableAsset as TimelineAsset;

            if (timeline == null)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{ObjectResolve.PathOf(director.gameObject)}' has no TimelineAsset.");
            }

            var tracks = new JArray();

            foreach (var track in timeline.GetOutputTracks().OfType<RecorderTrack>())
            {
                foreach (var clip in track.GetClips())
                {
                    var recorderClip = clip.asset as RecorderClip;
                    var settings = recorderClip?.settings;

                    tracks.Add(new JObject
                    {
                        ["track"] = track.name,
                        ["clip"] = clip.displayName,
                        ["start"] = Math.Round(clip.start, 4),
                        ["duration"] = Math.Round(clip.duration, 4),
                        ["type"] = settings == null ? null : (JToken)settings.GetType().Name,
                        ["outputFile"] = settings?.OutputFile,
                        ["enabled"] = settings?.Enabled ?? false,
                    });
                }
            }

            return new JObject
            {
                ["timeline"] = timeline.name,
                ["recorderClips"] = tracks,
                ["count"] = tracks.Count,
            };
        }

        private static RecorderSettings BuildSettings(
            string type, string format, string outputPath, bool captureAlpha, string defaultName)
        {
            switch ((type ?? "movie").Trim().ToLowerInvariant())
            {
                case "movie":
                case "mp4":
                case "video":
                {
                    var movie = ScriptableObject.CreateInstance<MovieRecorderSettings>();
                    movie.OutputFormat = ParseMovieFormat(format);
                    movie.CaptureAlpha = captureAlpha;
                    ApplyOutputPath(movie, outputPath, defaultName);
                    return movie;
                }

                case "png":
                case "jpeg":
                case "jpg":
                case "exr":
                case "image":
                {
                    var image = ScriptableObject.CreateInstance<ImageRecorderSettings>();
                    image.OutputFormat = ParseImageFormat(type);
                    image.CaptureAlpha = captureAlpha;
                    ApplyOutputPath(image, outputPath, defaultName);
                    return image;
                }

                default:
                    throw new McpToolException(
                        "invalid_params",
                        $"'{type}' is not a recording type. Use movie, png, jpeg or exr.");
            }
        }

        /// <summary>
        /// Sets the destination, keeping an absolute path intact across the domain reload.
        /// </summary>
        /// <remarks>
        /// Assigning an absolute <c>OutputFile</c> makes Recorder store Root=Absolute with the
        /// directory in Leaf, but its internal absolutePath stays null, and Recorder only falls back
        /// to Leaf while that field <em>is</em> null. Unity deserializes a null string as "", so the
        /// domain reload on entering Play mode makes the root resolve to empty and the recording
        /// silently lands in the project folder instead of where it was asked to go. Pinning the
        /// internal field keeps the destination across that reload.
        /// </remarks>
        private static void ApplyOutputPath(RecorderSettings settings, string outputPath, string defaultName)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                // Default destination: a "Recording" folder beside Assets. Root.Project already means
                // "the folder containing Assets" and, unlike Root.Absolute, resolves without the
                // internal absolutePath field, so this survives the domain reload on its own.
                var fallback = settings.FileNameGenerator;
                fallback.Root = OutputPath.Root.Project;
                fallback.Leaf = "Recording";
                fallback.FileName = string.IsNullOrWhiteSpace(defaultName) ? "Recording" : defaultName;
                return;
            }

            settings.OutputFile = outputPath;

            var generator = settings.FileNameGenerator;
            if (generator.Root != OutputPath.Root.Absolute || string.IsNullOrEmpty(generator.Leaf))
                return;

            var absolutePath = typeof(FileNameGenerator)
                .GetProperty("AbsolutePath", BindingFlags.Instance | BindingFlags.NonPublic);

            if (absolutePath == null || !absolutePath.CanWrite)
                throw new McpToolException(
                    "unsupported",
                    $"This Recorder version does not expose FileNameGenerator.AbsolutePath, so the " +
                    $"absolute 'output_path' \"{outputPath}\" would silently be written to the project " +
                    $"folder. Pass a path inside the project instead.");

            absolutePath.SetValue(generator, generator.Leaf);
        }

        private static MovieRecorderSettings.VideoRecorderOutputFormat ParseMovieFormat(string format)
        {
            switch ((format ?? "mp4").Trim().ToLowerInvariant())
            {
                case "mp4":
                case "h264":
                    return MovieRecorderSettings.VideoRecorderOutputFormat.MP4;

                case "webm":
                    return MovieRecorderSettings.VideoRecorderOutputFormat.WebM;

                case "mov":
                    return MovieRecorderSettings.VideoRecorderOutputFormat.MOV;

                default:
                    throw new McpToolException(
                        "invalid_params",
                        $"'{format}' is not a movie format. Use mp4, webm or mov.");
            }
        }

        private static ImageRecorderSettings.ImageRecorderOutputFormat ParseImageFormat(string type)
        {
            switch (type.Trim().ToLowerInvariant())
            {
                case "png":
                    return ImageRecorderSettings.ImageRecorderOutputFormat.PNG;

                case "jpeg":
                case "jpg":
                    return ImageRecorderSettings.ImageRecorderOutputFormat.JPEG;

                case "exr":
                    return ImageRecorderSettings.ImageRecorderOutputFormat.EXR;

                default:
                    return ImageRecorderSettings.ImageRecorderOutputFormat.PNG;
            }
        }

        private static void ApplySource(
            RecorderSettings settings, string source, string cameraTag, string renderTexturePath,
            int? width, int? height)
        {
            ImageInputSettings input;

            switch ((source ?? "game_view").Trim().ToLowerInvariant())
            {
                case "game_view":
                case "gameview":
                    input = new GameViewInputSettings();
                    break;

                case "active_camera":
                    input = new CameraInputSettings { Source = ImageSource.ActiveCamera };
                    break;

                case "main_camera":
                    input = new CameraInputSettings { Source = ImageSource.MainCamera };
                    break;

                case "tagged_camera":
                    if (string.IsNullOrWhiteSpace(cameraTag))
                    {
                        throw new McpToolException("invalid_params", "'camera_tag' is required for source tagged_camera.");
                    }

                    input = new CameraInputSettings { Source = ImageSource.TaggedCamera, CameraTag = cameraTag };
                    break;

                case "render_texture":
                {
                    if (string.IsNullOrWhiteSpace(renderTexturePath))
                    {
                        throw new McpToolException("invalid_params", "'render_texture_path' is required for source render_texture.");
                    }

                    var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(renderTexturePath.Replace('\\', '/'));

                    if (rt == null)
                    {
                        throw new McpToolException("not_found", $"No RenderTexture at '{renderTexturePath}'.");
                    }

                    input = new RenderTextureInputSettings { RenderTexture = rt };
                    break;
                }

                default:
                    throw new McpToolException(
                        "invalid_params",
                        $"'{source}' is not a source. Use game_view, active_camera, main_camera, " +
                        "tagged_camera or render_texture.");
            }

            if (width.HasValue) input.OutputWidth = width.Value;
            if (height.HasValue) input.OutputHeight = height.Value;

            // Recorder spells this property differently on the two settings types — capital
            // ImageInputSettings on Movie, lower-case imageInputSettings on Image — so it cannot
            // be set through the shared base and is switched on the concrete type here.
            switch (settings)
            {
                case MovieRecorderSettings movie:
                    movie.ImageInputSettings = input;
                    break;

                case ImageRecorderSettings image:
                    image.imageInputSettings = input;
                    break;

                default:
                    throw new McpToolException(
                        "tool_failed",
                        $"{settings.GetType().Name} has no input-source setting this tool knows how to set.");
            }
        }

        private static PlayableDirector ResolveDirector(string objectPath, long? instanceId)
        {
            var go = ObjectResolve.Object(objectPath, instanceId);
            var director = go.GetComponent<PlayableDirector>();

            if (director == null)
            {
                throw new McpToolException(
                    "not_found",
                    $"'{ObjectResolve.PathOf(go)}' has no PlayableDirector.");
            }

            return director;
        }
    }
}
