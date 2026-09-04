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
    /// Editing a Timeline's clips: retiming one, and shifting a run of them together.
    /// </summary>
    /// <remarks>
    /// Every value written here is read back and reported as it actually landed, because Timeline's
    /// setters discard writes silently. A clip's <c>clipCaps</c> come from its PlayableAsset, and an
    /// Activation clip advertises <c>ClipCaps.None</c> — so setting its speed or blend does nothing at
    /// all, with no error. An agent that trusted its own request would carry on believing a change it
    /// never made, which is the worst failure available here. So the result carries the effective
    /// value, and names anything that did not take in <c>ignored</c>.
    /// </remarks>
    internal static class TimelineEditTools
    {
        /// <summary>Times equal within this are the same time; well below one frame at 1000fps.</summary>
        private const double Epsilon = 1e-6;

        [McpTool(
            "timeline_edit_clip",
            "Retime or rename one clip on a Timeline. Give 'duration' or 'end', not both. The result " +
            "reports the values as they actually landed and lists anything that did not take: a clip " +
            "only accepts speed, blend and ease if its type supports them, and Activation clips " +
            "support none of them. Addresses come from timeline_inspect.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Edit Clip",
            // Shows the three ways a clip is addressed and that duration and end are alternatives,
            // which the parameter list can state but not demonstrate.
            Examples = new[]
            {
                @"{""object_path"":""/StageDirector"",""track"":""Cameras/CamFront"",""clip"":""CamFront shot"",""start"":2.5,""duration"":2.0}",
                @"{""object_path"":""/StageDirector"",""track"":""Shots"",""at_time"":3.2,""end"":6.0}",
            })]
        public static JObject EditClip(
            [McpArg("object_path", "Hierarchy path of the GameObject with the PlayableDirector.")]
            string objectPath = null,
            [McpArg("instance_id", "Address the director's GameObject by instance id instead.")]
            long? instanceId = null,
            [McpArg("track", "Track path, as timeline_inspect reports it, e.g. 'Cameras/CamFront'.")]
            string track = null,
            [McpArg("clip", "Display name of the clip to edit.")]
            string clip = null,
            [McpArg("clip_index", "Index of the clip on the track instead of its name.")]
            int? clipIndex = null,
            [McpArg("at_time", "Address the clip that covers this time, in seconds.")]
            double? atTime = null,
            [McpArg("start", "New start on the timeline, in seconds. Moves the clip; its length is kept.")]
            double? start = null,
            [McpArg("duration", "New length in seconds.")]
            double? duration = null,
            [McpArg("end", "New end in seconds, as an alternative to 'duration'.")]
            double? end = null,
            [McpArg("display_name", "New name for the clip.")]
            string displayName = null,
            [McpArg("ease_in", "Ease-in length in seconds. Needs a clip type that supports blending.")]
            double? easeIn = null,
            [McpArg("ease_out", "Ease-out length in seconds.")]
            double? easeOut = null,
            [McpArg("blend_in", "Blend-in length in seconds.")]
            double? blendIn = null,
            [McpArg("blend_out", "Blend-out length in seconds.")]
            double? blendOut = null,
            [McpArg("time_scale", "Playback speed multiplier. Needs a clip type that supports it.")]
            double? timeScale = null,
            [McpArg("clip_in", "Offset into the source asset, in seconds.")]
            double? clipIn = null,
            [McpArg("control_source", "For a Control clip, the GameObject whose director it drives.")]
            string controlSource = null)
        {
            if (duration.HasValue && end.HasValue)
            {
                throw new McpToolException(
                    "invalid_params",
                    "'duration' and 'end' both set the clip's length; give one.");
            }

            var director = TimelineResolve.Director(objectPath, instanceId);
            var timeline = TimelineResolve.Timeline(director);
            var trackAsset = TimelineResolve.Track(timeline, track);

            TimelineResolve.RefuseIfLocked(trackAsset);

            var target = TimelineResolve.Clip(trackAsset, clip, clipIndex, atTime);

            foreach (var pair in new[]
                     {
                         ("start", start), ("duration", duration), ("end", end),
                         ("ease_in", easeIn), ("ease_out", easeOut),
                         ("blend_in", blendIn), ("blend_out", blendOut),
                         ("time_scale", timeScale), ("clip_in", clipIn),
                     })
            {
                RejectNonFinite(pair.Item1, pair.Item2);
            }

            // Everything that can be refused is refused here, before the first write. Validating as
            // we go would leave the clip already moved when a later argument turns out to be bad,
            // and the caller would be told the call failed.
            if (duration.HasValue && duration.Value <= 0)
            {
                throw new McpToolException("invalid_params", "'duration' must be greater than zero.");
            }

            var startAfter = start ?? target.start;

            if (end.HasValue && end.Value - startAfter <= 0)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'end' {end.Value:0.###} is at or before the clip's start {startAfter:0.###}, " +
                    "which would leave it no length.");
            }

            var control = string.IsNullOrWhiteSpace(controlSource)
                ? null
                : ResolveControlSource(target, controlSource);

            // Recorded before the first write, and the whole method runs inside one undo group, so a
            // multi-field edit collapses to a single step for the human at the keyboard.
            UndoExtensions.RegisterClip(target, "MCP Edit Clip");

            var ignored = new JArray();

            // Start first: 'end' is relative to it, and Timeline offers no end setter.
            Assign("start", start, () => target.start, v => target.start = v, ignored, target, Clamped);

            if (end.HasValue)
            {
                Assign("end", end.Value - target.start, () => target.duration,
                       v => target.duration = v, ignored, target, Clamped);
            }

            Assign("duration", duration, () => target.duration, v => target.duration = v, ignored, target, Clamped);

            Assign("ease_in", easeIn, () => target.easeInDuration, v => target.easeInDuration = v, ignored, target, Unsupported);
            Assign("ease_out", easeOut, () => target.easeOutDuration, v => target.easeOutDuration = v, ignored, target, Unsupported);
            Assign("blend_in", blendIn, () => target.blendInDuration, v => target.blendInDuration = v, ignored, target, Unsupported);
            Assign("blend_out", blendOut, () => target.blendOutDuration, v => target.blendOutDuration = v, ignored, target, Unsupported);
            Assign("time_scale", timeScale, () => target.timeScale, v => target.timeScale = v, ignored, target, Unsupported);
            Assign("clip_in", clipIn, () => target.clipIn, v => target.clipIn = v, ignored, target, Unsupported);

            if (displayName != null)
            {
                target.displayName = displayName;
            }

            var rebound = control == null ? null : ApplyControlSource(target, director, control);

            Commit(timeline, director, structural: false);

            var result = Describe(target, trackAsset, director);
            result["ignored"] = ignored;

            if (rebound != null)
            {
                result["controls"] = rebound;
            }

            return result;
        }

        [McpTool(
            "timeline_shift_clips",
            "Shift every clip at or after a time by the same amount, so a change of length earlier in " +
            "the timeline does not have to be repaired clip by clip. Applies to one track or, by " +
            "default, all of them. Nothing is moved if the shift would push a clip before zero.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Shift Clips",
            // by and to_time are mutually exclusive, and which one a caller wants is easier to see
            // from a worked pair than from prose.
            Examples = new[]
            {
                @"{""object_path"":""/StageDirector"",""from_time"":3.0,""by"":0.5}",
                @"{""object_path"":""/StageDirector"",""track"":""Shots"",""from_time"":2.0,""to_time"":4.0}",
            })]
        public static JObject ShiftClips(
            [McpArg("object_path", "Hierarchy path of the GameObject with the PlayableDirector.")]
            string objectPath = null,
            [McpArg("instance_id", "Address the director's GameObject by instance id instead.")]
            long? instanceId = null,
            [McpArg("track", "Limit the shift to this track. Omit to shift every track.")]
            string track = null,
            [McpArg("from_time", "Shift clips starting at or after this time, in seconds.")]
            double fromTime = 0,
            [McpArg("by", "Seconds to move by; negative moves earlier.")]
            double? by = null,
            [McpArg("to_time", "Move the earliest affected clip to this time, taking the rest with it.")]
            double? toTime = null,
            [McpArg("include_overlapping", "Also move clips that start before 'from_time' but run past it.")]
            bool includeOverlapping = false)
        {
            if (by.HasValue == toTime.HasValue)
            {
                throw new McpToolException(
                    "invalid_params",
                    "Give either 'by' (a distance) or 'to_time' (a destination), not both and not neither.");
            }

            RejectNonFinite("from_time", fromTime);
            RejectNonFinite("by", by);
            RejectNonFinite("to_time", toTime);

            var director = TimelineResolve.Director(objectPath, instanceId);
            var timeline = TimelineResolve.Timeline(director);

            var tracks = string.IsNullOrWhiteSpace(track)
                ? TimelineResolve.AllTracks(timeline).Where(t => !(t is GroupTrack)).ToList()
                : new List<TrackAsset> { TimelineResolve.Track(timeline, track) };

            foreach (var t in tracks.Where(t => t.lockedInHierarchy))
            {
                // A blanket shift silently skipping locked tracks would tear a sequence apart, so it
                // refuses instead of half-applying.
                throw new McpToolException(
                    "conflict",
                    $"Track '{TimelineResolve.PathOf(t)}' is locked, so shifting would move only part " +
                    "of the timeline. Unlock it with timeline_set_track, or pass 'track' to shift one.",
                    409);
            }

            var affected = tracks
                .SelectMany(t => t.GetClips().Select(c => (Track: t, Clip: c)))
                .Where(x => includeOverlapping
                    ? x.Clip.end > fromTime + Epsilon
                    : x.Clip.start >= fromTime - Epsilon)
                .ToList();

            if (affected.Count == 0)
            {
                return new JObject
                {
                    ["timeline"] = timeline.name,
                    ["moved"] = 0,
                    ["note"] = $"No clip starts at or after {fromTime:0.###}s, so nothing moved.",
                };
            }

            var delta = by ?? (toTime.Value - affected.Min(x => x.Clip.start));

            // Checked across every clip before anything moves: a partly-applied ripple is worse than
            // a refusal, because the caller cannot tell how far it got.
            var wouldGoNegative = affected
                .Where(x => x.Clip.start + delta < -Epsilon)
                .Select(x => $"{TimelineResolve.PathOf(x.Track)}/{x.Clip.displayName}")
                .ToList();

            if (wouldGoNegative.Count > 0)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"Shifting by {delta:0.###}s would move {wouldGoNegative.Count} clip(s) before zero " +
                    $"({string.Join(", ", wouldGoNegative.Take(5))}). Nothing was moved.");
            }

            UndoExtensions.RegisterTracks(affected.Select(x => x.Track).Distinct(), "MCP Shift Clips");

            // Ordered so clips never pass through each other mid-shift; the list is re-sorted by
            // Timeline on every start change regardless, but this keeps the intermediate states sane.
            var landedWrong = new JArray();

            foreach (var item in delta >= 0
                         ? affected.OrderByDescending(x => x.Clip.start)
                         : affected.OrderBy(x => x.Clip.start))
            {
                var wanted = item.Clip.start + delta;
                item.Clip.start = wanted;

                // Read back for the same reason a single edit does: the setter clamps, and a ripple
                // that quietly moved one clip somewhere else has broken the spacing the caller was
                // preserving — reporting the count alone would hide that.
                if (Math.Abs(item.Clip.start - wanted) > Epsilon)
                {
                    landedWrong.Add(new JObject
                    {
                        ["clip"] = $"{TimelineResolve.PathOf(item.Track)}/{item.Clip.displayName}",
                        ["requested"] = Math.Round(wanted, 4),
                        ["effective"] = Math.Round(item.Clip.start, 4),
                    });
                }
            }

            Commit(timeline, director, structural: false);

            return new JObject
            {
                ["timeline"] = timeline.name,
                ["moved"] = affected.Count,
                ["by"] = Math.Round(delta, 4),
                ["duration"] = Math.Round(timeline.duration, 4),
                ["clampedByTimeline"] = landedWrong,
                ["clips"] = new JArray(affected
                    .OrderBy(x => x.Clip.start)
                    .Select(x => (object)Describe(x.Clip, x.Track, director))
                    .ToArray()),
            };
        }

        // ---- shared ------------------------------------------------------------------------

        private static string Clamped(TimelineClip clip) => "Timeline clamped the value";

        private static string Unsupported(TimelineClip clip) =>
            $"{clip.asset?.GetType().Name ?? "this clip type"} does not support it";

        /// <summary>
        /// Writes a value, reads it back, and records the argument as ignored when the two disagree.
        /// Reading back is the only reliable check: the setters are gated on capabilities the asset
        /// declares and simply drop what they do not accept.
        /// </summary>
        private static void Assign(
            string argument, double? requested, Func<double> read, Action<double> write,
            JArray ignored, TimelineClip clip, Func<TimelineClip, string> reason)
        {
            if (!requested.HasValue)
            {
                return;
            }

            write(requested.Value);
            var effective = read();

            if (Math.Abs(effective - requested.Value) > Epsilon)
            {
                ignored.Add(new JObject
                {
                    ["arg"] = argument,
                    ["requested"] = Math.Round(requested.Value, 4),
                    ["effective"] = Math.Round(effective, 4),
                    ["reason"] = reason(clip),
                });
            }
        }

        private static void RejectNonFinite(string argument, double? value)
        {
            if (value.HasValue && (double.IsNaN(value.Value) || double.IsInfinity(value.Value)))
            {
                // Timeline's own guard logs an error and keeps the old value, which would leave the
                // caller with a success and no change.
                throw new McpToolException("invalid_params", $"'{argument}' must be a finite number.");
            }
        }

        /// <summary>
        /// Checks that this clip can take a control source and that the source exists, without
        /// changing anything. Split from applying it so the whole call can be refused before the
        /// first write.
        /// </summary>
        private static GameObject ResolveControlSource(TimelineClip clip, string sourcePath)
        {
            if (!(clip.asset is ControlPlayableAsset))
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'control_source' only applies to a Control clip; this one is a " +
                    $"{clip.asset?.GetType().Name ?? "clip with no asset"}.");
            }

            return ObjectResolve.Object(sourcePath, null, "control_source", null);
        }

        /// <summary>Points a Control clip at the object whose director it drives.</summary>
        private static string ApplyControlSource(TimelineClip clip, PlayableDirector director, GameObject source)
        {
            var control = (ControlPlayableAsset)clip.asset;

            // The name lives in the asset and the value in the director's table, so both are dirtied.
            if (string.IsNullOrEmpty(control.sourceGameObject.exposedName.ToString()))
            {
                control.sourceGameObject.exposedName = GUID.Generate().ToString();
            }

            Undo.RegisterCompleteObjectUndo(new UnityEngine.Object[] { control, director }, "MCP Edit Clip");
            director.SetReferenceValue(control.sourceGameObject.exposedName, source);

            EditorUtility.SetDirty(control);
            EditorUtility.SetDirty(director);

            return ObjectResolve.PathOf(source);
        }

        /// <summary>
        /// Persists the asset and tells the runtime graph and the window. Refresh is a safe no-op when
        /// the Timeline window is closed, which is the normal case when an agent is driving.
        /// </summary>
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

        /// <summary>
        /// A clip's address and its effective timing. The index is included because it is how the
        /// caller addresses the clip next, and editing a start re-sorts the track.
        /// </summary>
        private static JObject Describe(TimelineClip clip, TrackAsset track, PlayableDirector director)
        {
            var entry = new JObject
            {
                ["track"] = TimelineResolve.PathOf(track),
                ["clip"] = clip.displayName,
                ["clipIndex"] = TimelineResolve.IndexOf(clip, track),
                ["start"] = Math.Round(clip.start, 4),
                ["end"] = Math.Round(clip.end, 4),
                ["duration"] = Math.Round(clip.duration, 4),
                ["asset"] = clip.asset == null ? null : (JToken)clip.asset.GetType().Name,
            };

            // -1 is Timeline's "never set", and reporting it as a length would be a lie.
            if (clip.blendInDuration >= 0) entry["blendIn"] = Math.Round(clip.blendInDuration, 4);
            if (clip.blendOutDuration >= 0) entry["blendOut"] = Math.Round(clip.blendOutDuration, 4);

            entry["easeIn"] = Math.Round(clip.easeInDuration, 4);
            entry["easeOut"] = Math.Round(clip.easeOutDuration, 4);
            entry["timeScale"] = Math.Round(clip.timeScale, 4);
            entry["clipIn"] = Math.Round(clip.clipIn, 4);

            return entry;
        }
    }
}
