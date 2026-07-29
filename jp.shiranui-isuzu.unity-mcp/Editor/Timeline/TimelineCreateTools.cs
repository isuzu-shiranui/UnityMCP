using System;
using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;
using UnityEditor.Timeline;

using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Timeline
{
    /// <summary>
    /// Building a Timeline: the asset, its tracks, its clips, and the Control clips that nest one
    /// timeline inside another.
    /// </summary>
    /// <remarks>
    /// The ordering these tools enforce is the whole point of having them. Timeline persists a track
    /// into the .playable file only if the timeline is <em>already</em> an asset when the track is
    /// created, and a clip's asset is added to its track under the same rule. Build it the other way
    /// round and everything looks correct — the tracks are there, the window draws them, an inspect
    /// reports them — right up until the next domain reload, when they are gone, because they only
    /// ever existed in memory. There is no public API to persist them afterwards. So creating the
    /// asset first is not a convenience here, it is the only order that works, and these tools refuse
    /// to proceed when it is not met.
    /// </remarks>
    internal static class TimelineCreateTools
    {
        /// <summary>Friendly names for the track types worth naming; anything else is matched by type name.</summary>
        private static readonly Dictionary<string, Type> KnownTracks = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            ["activation"] = typeof(ActivationTrack),
            ["animation"] = typeof(AnimationTrack),
            ["audio"] = typeof(AudioTrack),
            ["control"] = typeof(ControlTrack),
            ["group"] = typeof(GroupTrack),
            ["playable"] = typeof(PlayableTrack),
            ["signal"] = typeof(SignalTrack),
        };

        [McpTool(
            "timeline_create",
            "Create a Timeline asset, optionally adding a PlayableDirector to a GameObject to play " +
            "it. Use this before timeline_create_track: a track is only written into the file if the " +
            "timeline is already an asset, so tracks added to an unsaved timeline are lost at the " +
            "next reload.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Create Timeline")]
        public static JObject Create(
            [McpArg("asset_path", "Where to write the .playable, e.g. 'Assets/Stage/Stage.playable'.")]
            string assetPath = null,
            [McpArg("frame_rate", "Timeline frame rate. Recorder takes its capture rate from this.")]
            double frameRate = 60,
            [McpArg("object_path", "GameObject to give a PlayableDirector that plays this timeline.")]
            string objectPath = null,
            [McpArg("instance_id", "Address that GameObject by instance id instead.")]
            long? instanceId = null)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                throw new McpToolException("invalid_params", "'asset_path' is required.");
            }

            var path = assetPath.Replace('\\', '/').Trim();

            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
            {
                throw new McpToolException(
                    "invalid_params", $"'asset_path' must be inside Assets; got '{path}'.");
            }

            if (!path.EndsWith(".playable", StringComparison.OrdinalIgnoreCase))
            {
                path += ".playable";
            }

            var folder = path.Substring(0, path.LastIndexOf('/'));

            if (!AssetDatabase.IsValidFolder(folder))
            {
                throw new McpToolException(
                    "not_found",
                    $"No folder '{folder}'. Create it with asset_create_folder first.");
            }

            if (AssetDatabase.LoadAssetAtPath<TimelineAsset>(path) != null)
            {
                throw new McpToolException(
                    "conflict",
                    $"'{path}' already exists. Delete it, or pick another path.",
                    409);
            }

            if (frameRate <= 0 || double.IsNaN(frameRate) || double.IsInfinity(frameRate))
            {
                throw new McpToolException("invalid_params", "'frame_rate' must be a positive number.");
            }

            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.name = System.IO.Path.GetFileNameWithoutExtension(path);

            // Before anything is added to it, so tracks created later are written into the file.
            AssetDatabase.CreateAsset(timeline, path);
            timeline.editorSettings.frameRate = frameRate;

            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssets();

            var result = new JObject
            {
                ["timeline"] = timeline.name,
                ["timelinePath"] = path,
                ["frameRate"] = frameRate,
                ["created"] = true,
            };

            if (!string.IsNullOrWhiteSpace(objectPath) || instanceId.HasValue)
            {
                var go = ObjectResolve.Object(objectPath, instanceId);
                var director = go.GetComponent<PlayableDirector>();

                if (director == null)
                {
                    director = Undo.AddComponent<PlayableDirector>(go);
                }
                else
                {
                    Undo.RegisterCompleteObjectUndo(director, "MCP Create Timeline");
                }

                director.playableAsset = timeline;
                EditorUtility.SetDirty(director);

                result["director"] = ObjectResolve.PathOf(go);

                return EditorNotes.SceneChange(result);
            }

            return result;
        }

        [McpTool(
            "timeline_create_track",
            "Add a track to a Timeline. Types: activation, animation, audio, control, group, " +
            "playable, signal. Pass 'parent' to put it inside a group track, and 'binding' to point " +
            "it at the object it drives in the same call.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Create Track")]
        public static JObject CreateTrack(
            [McpArg("object_path", "Hierarchy path of the GameObject with the PlayableDirector.")]
            string objectPath = null,
            [McpArg("instance_id", "Address the director's GameObject by instance id instead.")]
            long? instanceId = null,
            [McpArg("type", "Track type: activation, animation, audio, control, group, playable or signal.")]
            string type = null,
            [McpArg("name", "Name for the track. Timeline makes it unique among its siblings.")]
            string name = null,
            [McpArg("parent", "Path of a group track to nest this one under.")]
            string parent = null,
            [McpArg("binding", "Hierarchy path of the object this track drives.")]
            string binding = null)
        {
            var director = TimelineResolve.Director(objectPath, instanceId);
            var timeline = TimelineResolve.Timeline(director, "add a track to");

            RequirePersisted(timeline);

            var trackType = ResolveTrackType(type);
            TrackAsset parentTrack = null;

            if (!string.IsNullOrWhiteSpace(parent))
            {
                parentTrack = TimelineResolve.Track(timeline, parent);

                if (!(parentTrack is GroupTrack))
                {
                    throw new McpToolException(
                        "invalid_params",
                        $"'{TimelineResolve.PathOf(parentTrack)}' is a {parentTrack.GetType().Name}, " +
                        "and only a group track can hold other tracks.");
                }
            }

            TrackAsset track;

            try
            {
                // CreateTrack registers its own undo and persists the track itself.
                track = timeline.CreateTrack(trackType, parentTrack, name);
            }
            catch (InvalidOperationException ex)
            {
                throw new McpToolException("invalid_params", $"Timeline refused the track: {ex.Message}");
            }

            if (track == null)
            {
                throw new McpToolException("tool_failed", $"Timeline returned no track for type '{type}'.");
            }

            string bound = null;

            if (!string.IsNullOrWhiteSpace(binding))
            {
                bound = BindTo(director, track, binding);
            }

            Commit(timeline, director);

            var result = new JObject
            {
                ["track"] = TimelineResolve.PathOf(track),
                ["type"] = track.GetType().Name,
                ["timeline"] = timeline.name,
                ["created"] = true,
            };

            if (bound != null)
            {
                result["binding"] = bound;

                return EditorNotes.SceneChange(result);
            }

            return result;
        }

        [McpTool(
            "timeline_create_clip",
            "Add a clip to a Timeline track. On a control track, 'control_source' points the clip at " +
            "the GameObject whose director it drives, which is how one timeline is nested inside " +
            "another. On an animation track, 'animation_clip' is the AnimationClip asset to play.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Create Clip")]
        public static JObject CreateClip(
            [McpArg("object_path", "Hierarchy path of the GameObject with the PlayableDirector.")]
            string objectPath = null,
            [McpArg("instance_id", "Address the director's GameObject by instance id instead.")]
            long? instanceId = null,
            [McpArg("track", "Track path to add the clip to, as timeline_inspect reports it.")]
            string track = null,
            [McpArg("start", "Clip start on the timeline, in seconds.")]
            double start = 0,
            [McpArg("duration", "Clip length in seconds. Omit to keep the type's default.")]
            double? duration = null,
            [McpArg("display_name", "Name for the clip.")]
            string displayName = null,
            [McpArg("control_source", "For a control clip, the GameObject whose director it drives.")]
            string controlSource = null,
            [McpArg("animation_clip", "For an animation clip, the AnimationClip asset path to play.")]
            string animationClip = null)
        {
            if (double.IsNaN(start) || double.IsInfinity(start))
            {
                throw new McpToolException("invalid_params", "'start' must be a finite number.");
            }

            if (duration.HasValue && (duration.Value <= 0 || double.IsNaN(duration.Value) || double.IsInfinity(duration.Value)))
            {
                throw new McpToolException("invalid_params", "'duration' must be a positive, finite number.");
            }

            var director = TimelineResolve.Director(objectPath, instanceId);
            var timeline = TimelineResolve.Timeline(director, "add a clip to");

            RequirePersisted(timeline);

            var trackAsset = TimelineResolve.Track(timeline, track);
            TimelineResolve.RefuseIfLocked(trackAsset);

            if (trackAsset is GroupTrack)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{TimelineResolve.PathOf(trackAsset)}' is a group track, which holds tracks rather than clips.");
            }

            var before = trackAsset.GetClips().ToList();
            var clip = trackAsset.CreateDefaultClip();

            if (clip == null)
            {
                // Timeline logs and returns null when the clip's own creation hook throws, having
                // already attached the clip. Left alone that is an invisible half-clip on the track.
                var orphan = trackAsset.GetClips().FirstOrDefault(c => !before.Contains(c));

                if (orphan != null)
                {
                    timeline.DeleteClip(orphan);
                }

                throw new McpToolException(
                    "tool_failed",
                    $"Timeline could not create a clip on '{TimelineResolve.PathOf(trackAsset)}' " +
                    $"({trackAsset.GetType().Name}). Any partial clip has been removed.");
            }

            clip.start = start;

            if (duration.HasValue)
            {
                clip.duration = duration.Value;
            }

            if (!string.IsNullOrWhiteSpace(displayName))
            {
                clip.displayName = displayName;
            }

            string controls = null;

            if (!string.IsNullOrWhiteSpace(controlSource))
            {
                controls = SetControlSource(clip, director, controlSource);
            }

            if (!string.IsNullOrWhiteSpace(animationClip))
            {
                SetAnimationClip(clip, animationClip);
            }

            Commit(timeline, director);

            var result = new JObject
            {
                ["track"] = TimelineResolve.PathOf(trackAsset),
                ["clip"] = clip.displayName,
                ["clipIndex"] = TimelineResolve.IndexOf(clip, trackAsset),
                ["start"] = Math.Round(clip.start, 4),
                ["end"] = Math.Round(clip.end, 4),
                ["duration"] = Math.Round(clip.duration, 4),
                ["asset"] = clip.asset == null ? null : (JToken)clip.asset.GetType().Name,
                ["created"] = true,
            };

            if (controls != null)
            {
                result["controls"] = controls;

                return EditorNotes.SceneChange(result);
            }

            return result;
        }

        // ---- shared ------------------------------------------------------------------------

        /// <summary>
        /// Refuses a timeline that is not yet on disk, because tracks and clips added to one are
        /// silently discarded at the next domain reload.
        /// </summary>
        private static void RequirePersisted(TimelineAsset timeline)
        {
            if (!AssetDatabase.Contains(timeline))
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{timeline.name}' is not saved as an asset, and Timeline only writes tracks into " +
                    "a timeline that already is one — anything added now would disappear at the next " +
                    "reload. Create it with timeline_create, or save the timeline first.");
            }
        }

        private static Type ResolveTrackType(string type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'type' is required. Use one of: {string.Join(", ", KnownTracks.Keys)}.");
            }

            var wanted = type.Trim();

            if (KnownTracks.TryGetValue(wanted, out var known))
            {
                return known;
            }

            var byName = KnownTracks.Values.FirstOrDefault(
                t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase));

            if (byName != null)
            {
                return byName;
            }

            throw new McpToolException(
                "invalid_params",
                $"'{type}' is not a track type. Use one of: {string.Join(", ", KnownTracks.Keys)}.");
        }

        /// <summary>Wires a Control clip to the object whose director it drives.</summary>
        private static string SetControlSource(TimelineClip clip, PlayableDirector director, string sourcePath)
        {
            if (!(clip.asset is ControlPlayableAsset control))
            {
                throw new McpToolException(
                    "invalid_params",
                    "'control_source' only applies to a clip on a control track; this one is a " +
                    $"{clip.asset?.GetType().Name ?? "clip with no asset"}.");
            }

            var source = ObjectResolve.Object(sourcePath, null, "control_source");

            if (string.IsNullOrEmpty(control.sourceGameObject.exposedName.ToString()))
            {
                control.sourceGameObject.exposedName = GUID.Generate().ToString();
            }

            // Driving the child's director is the reason to nest at all; without it the clip only
            // activates the object.
            control.updateDirector = true;

            Undo.RegisterCompleteObjectUndo(new UnityEngine.Object[] { control, director }, "MCP Create Clip");
            director.SetReferenceValue(control.sourceGameObject.exposedName, source);

            // The name is stored in the asset and the value in the director, so both are dirtied.
            EditorUtility.SetDirty(control);
            EditorUtility.SetDirty(director);

            return ObjectResolve.PathOf(source);
        }

        private static void SetAnimationClip(TimelineClip clip, string assetPath)
        {
            if (!(clip.asset is AnimationPlayableAsset animation))
            {
                throw new McpToolException(
                    "invalid_params",
                    "'animation_clip' only applies to a clip on an animation track; this one is a " +
                    $"{clip.asset?.GetType().Name ?? "clip with no asset"}.");
            }

            var loaded = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath.Replace('\\', '/'));

            if (loaded == null)
            {
                throw new McpToolException(
                    "not_found",
                    $"No AnimationClip at '{assetPath}'. asset_find with type 'AnimationClip' will list them.");
            }

            Undo.RegisterCompleteObjectUndo(animation, "MCP Create Clip");
            animation.clip = loaded;
            clip.duration = loaded.length > 0 ? loaded.length : clip.duration;

            EditorUtility.SetDirty(animation);
        }

        private static string BindTo(PlayableDirector director, TrackAsset track, string path)
        {
            var go = ObjectResolve.Object(path, null, "binding");
            var wanted = track.outputs.FirstOrDefault().outputTargetType;

            UnityEngine.Object value = go;

            if (wanted != null && !wanted.IsInstanceOfType(go))
            {
                if (!typeof(Component).IsAssignableFrom(wanted))
                {
                    throw new McpToolException(
                        "invalid_params",
                        $"'{TimelineResolve.PathOf(track)}' binds a {wanted.Name}, which a GameObject cannot provide.");
                }

                var component = go.GetComponent(wanted);

                if (component == null)
                {
                    throw new McpToolException(
                        "not_found",
                        $"'{ObjectResolve.PathOf(go)}' has no {wanted.Name}, which " +
                        $"'{TimelineResolve.PathOf(track)}' needs. Add one with gameobject_add_component.");
                }

                value = component;
            }

            Undo.RegisterCompleteObjectUndo(director, "MCP Create Track");
            director.SetGenericBinding(track, value);
            EditorUtility.SetDirty(director);

            return value is Component c
                ? $"{ObjectResolve.PathOf(c.gameObject)} ({c.GetType().Name})"
                : ObjectResolve.PathOf(go);
        }

        private static void Commit(TimelineAsset timeline, PlayableDirector director)
        {
            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssetIfDirty(timeline);

            if (director != null)
            {
                director.RebuildGraph();
            }

            TimelineEditor.Refresh(RefreshReason.ContentsAddedOrRemoved);
        }
    }
}
