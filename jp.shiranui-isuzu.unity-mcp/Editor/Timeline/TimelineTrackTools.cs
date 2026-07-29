using System;
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

using UnityObject = UnityEngine.Object;

namespace UnityMCP.Editor.Timeline
{
    /// <summary>
    /// Track-level editing: muting, locking, renaming, what a track drives, and removing tracks and
    /// clips.
    /// </summary>
    /// <remarks>
    /// A track's binding is the usual reason "the animation does nothing" — the track is fine and it
    /// is pointed at nothing, or at the wrong object. It lives on the PlayableDirector in the scene
    /// rather than in the timeline asset, which is why setting one is a scene change and is reported
    /// as such, while muting the same track is an asset change that survives Play Mode.
    /// </remarks>
    internal static class TimelineTrackTools
    {
        [McpTool(
            "timeline_set_track",
            "Mute, lock, rename a Timeline track, or set what it drives. Binding a track is how you " +
            "fix 'the animation does nothing': pass the GameObject and the right component is " +
            "resolved for the track's type. Muting can shorten the timeline, so the new duration is " +
            "reported back. Track paths come from timeline_inspect.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Set Track")]
        public static JObject SetTrack(
            [McpArg("object_path", "Hierarchy path of the GameObject with the PlayableDirector.")]
            string objectPath = null,
            [McpArg("instance_id", "Address the director's GameObject by instance id instead.")]
            long? instanceId = null,
            [McpArg("track", "Track path, as timeline_inspect reports it, e.g. 'Cameras/CamFront'.")]
            string track = null,
            [McpArg("muted", "Mute or unmute the track. A muted track is left out of the timeline's length.")]
            bool? muted = null,
            [McpArg("locked", "Lock or unlock the track. Locking only stops edits made through these tools.")]
            bool? locked = null,
            [McpArg("name", "Rename the track.")]
            string name = null,
            [McpArg("binding", "Hierarchy path of the object this track drives.")]
            string binding = null,
            [McpArg("clear_binding", "Unbind the track instead of pointing it somewhere.")]
            bool clearBinding = false)
        {
            if (binding != null && clearBinding)
            {
                throw new McpToolException(
                    "invalid_params",
                    "'binding' and 'clear_binding' contradict each other; give one.");
            }

            var director = TimelineResolve.Director(objectPath, instanceId);
            var timeline = TimelineResolve.Timeline(director);
            var trackAsset = TimelineResolve.Track(timeline, track);

            // A locked track can still be unlocked, or there would be no way back out of a lock set
            // from this tool. Every other change here respects it.
            var onlyChangingLock = locked.HasValue && muted == null && name == null &&
                                   binding == null && !clearBinding;

            if (!onlyChangingLock)
            {
                TimelineResolve.RefuseIfLocked(trackAsset);
            }

            var changed = new JArray();
            var touchedScene = false;

            if (muted.HasValue || locked.HasValue || name != null)
            {
                UndoExtensions.RegisterTrack(trackAsset, "MCP Set Track");

                if (muted.HasValue && trackAsset.muted != muted.Value)
                {
                    trackAsset.muted = muted.Value;
                    changed.Add($"muted = {muted.Value.ToString().ToLowerInvariant()}");
                }

                if (locked.HasValue && trackAsset.locked != locked.Value)
                {
                    trackAsset.locked = locked.Value;
                    changed.Add($"locked = {locked.Value.ToString().ToLowerInvariant()}");
                }

                if (name != null && trackAsset.name != name)
                {
                    changed.Add($"name = {name}");
                    trackAsset.name = name;
                }
            }

            if (clearBinding)
            {
                Undo.RegisterCompleteObjectUndo(director, "MCP Set Track");
                director.ClearGenericBinding(trackAsset);
                EditorUtility.SetDirty(director);
                changed.Add("binding cleared");
                touchedScene = true;
            }
            else if (binding != null)
            {
                var bound = Bind(director, trackAsset, binding);
                changed.Add($"binding = {bound}");
                touchedScene = true;
            }

            Commit(timeline, director, structural: muted.HasValue || name != null);

            var current = director.GetGenericBinding(trackAsset);

            var result = new JObject
            {
                ["track"] = TimelineResolve.PathOf(trackAsset),
                ["type"] = trackAsset.GetType().Name,
                ["muted"] = trackAsset.muted,
                ["locked"] = trackAsset.lockedInHierarchy,
                ["binding"] = current == null ? null : (JToken)Describe(current),
                // Muting removes a track from the length calculation, so the number the caller had
                // before this call may no longer be right.
                ["duration"] = Math.Round(timeline.duration, 4),
                ["changed"] = changed,
            };

            return touchedScene ? EditorNotes.SceneChange(result) : result;
        }

        [McpTool(
            "timeline_delete",
            "Delete a track, or one clip from a track, on a Timeline. Deleting a track takes its " +
            "clips and any tracks grouped under it. This is undoable, so it does not ask for " +
            "confirmation. Addresses come from timeline_inspect.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Delete Timeline Item")]
        public static JObject Delete(
            [McpArg("object_path", "Hierarchy path of the GameObject with the PlayableDirector.")]
            string objectPath = null,
            [McpArg("instance_id", "Address the director's GameObject by instance id instead.")]
            long? instanceId = null,
            [McpArg("track", "Track path. Without a clip argument, the track itself is deleted.")]
            string track = null,
            [McpArg("clip", "Display name of a clip to delete, leaving the track in place.")]
            string clip = null,
            [McpArg("clip_index", "Index of the clip to delete instead of its name.")]
            int? clipIndex = null,
            [McpArg("at_time", "Delete the clip covering this time, in seconds.")]
            double? atTime = null)
        {
            var director = TimelineResolve.Director(objectPath, instanceId);
            var timeline = TimelineResolve.Timeline(director);
            var trackAsset = TimelineResolve.Track(timeline, track);

            TimelineResolve.RefuseIfLocked(trackAsset);

            var deletingClip = !string.IsNullOrWhiteSpace(clip) || clipIndex.HasValue || atTime.HasValue;

            if (deletingClip)
            {
                var target = TimelineResolve.Clip(trackAsset, clip, clipIndex, atTime);
                var name = target.displayName;

                // The two deletion entry points fail differently for the same mistake: the track
                // throws, the timeline logs and returns false. Both are turned into one answer.
                bool removed;

                try
                {
                    removed = timeline.DeleteClip(target);
                }
                catch (InvalidOperationException ex)
                {
                    throw new McpToolException("tool_failed", $"Timeline refused to delete '{name}': {ex.Message}");
                }

                if (!removed)
                {
                    throw new McpToolException(
                        "tool_failed",
                        $"Timeline refused to delete clip '{name}' from '{TimelineResolve.PathOf(trackAsset)}'.");
                }

                Commit(timeline, director, structural: true);

                return new JObject
                {
                    ["track"] = TimelineResolve.PathOf(trackAsset),
                    ["deleted"] = name,
                    ["kind"] = "clip",
                    ["clipCount"] = trackAsset.GetClips().Count(),
                    ["duration"] = Math.Round(timeline.duration, 4),
                };
            }

            var trackPath = TimelineResolve.PathOf(trackAsset);
            var childCount = TimelineResolve.AllTracks(timeline).Count(t => t != trackAsset && IsUnder(t, trackAsset));
            var clipCount = trackAsset.GetClips().Count();

            // Deleting clears the binding too; leaving it behind would keep the scene pointing at a
            // track that no longer exists.
            Undo.RegisterCompleteObjectUndo(director, "MCP Delete Timeline Item");
            director.ClearGenericBinding(trackAsset);
            EditorUtility.SetDirty(director);

            if (!timeline.DeleteTrack(trackAsset))
            {
                throw new McpToolException("tool_failed", $"Timeline refused to delete track '{trackPath}'.");
            }

            Commit(timeline, director, structural: true);

            return EditorNotes.SceneChange(new JObject
            {
                ["deleted"] = trackPath,
                ["kind"] = "track",
                ["clipsRemoved"] = clipCount,
                ["tracksRemoved"] = childCount + 1,
                ["duration"] = Math.Round(timeline.duration, 4),
            });
        }

        // ---- shared ------------------------------------------------------------------------

        private static bool IsUnder(TrackAsset candidate, TrackAsset ancestor)
        {
            for (var parent = candidate.parent as TrackAsset; parent != null; parent = parent.parent as TrackAsset)
            {
                if (parent == ancestor)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Points a track at an object, resolving the component the track's type actually wants.
        /// </summary>
        /// <remarks>
        /// An Animation track binds an Animator and an Activation track a GameObject. Handing the
        /// GameObject to both is the natural thing for a caller to do, and Timeline accepts it without
        /// complaint — the binding is simply wrong and the track does nothing when the graph is built.
        /// So the component is resolved here, and a missing one is an error rather than a silent
        /// half-binding.
        /// </remarks>
        private static string Bind(PlayableDirector director, TrackAsset track, string path)
        {
            var go = ObjectResolve.Object(path, null, "binding");
            var wanted = track.outputs.FirstOrDefault().outputTargetType;

            UnityObject value = go;

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
                        $"'{TimelineResolve.PathOf(track)}' needs. Add one with " +
                        "gameobject_add_component, or bind a different object.");
                }

                value = component;
            }

            Undo.RegisterCompleteObjectUndo(director, "MCP Set Track");
            director.SetGenericBinding(track, value);
            EditorUtility.SetDirty(director);

            return Describe(value);
        }

        private static string Describe(UnityObject binding)
        {
            if (binding is GameObject go)
            {
                return ObjectResolve.PathOf(go);
            }

            if (binding is Component component)
            {
                return $"{ObjectResolve.PathOf(component.gameObject)} ({component.GetType().Name})";
            }

            return $"{binding.name} ({binding.GetType().Name})";
        }

        private static void Commit(TimelineAsset timeline, PlayableDirector director, bool structural)
        {
            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssetIfDirty(timeline);

            if (director != null)
            {
                director.RebuildGraph();
            }

            TimelineEditor.Refresh(structural
                ? RefreshReason.ContentsAddedOrRemoved
                : RefreshReason.ContentsModified);
        }
    }
}
