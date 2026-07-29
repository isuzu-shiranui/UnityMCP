using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using UnityEngine.Playables;
using UnityEngine.Timeline;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Timeline
{
    /// <summary>
    /// Addressing a director, a track and a clip by name, the way <see cref="ObjectResolve"/>
    /// addresses a GameObject.
    /// </summary>
    /// <remarks>
    /// Tracks are addressed by a path so a track inside a group can be named unambiguously —
    /// <c>Cameras/CamFront</c> — with a <c>[n]</c> index only where a name repeats, because Timeline
    /// places no uniqueness requirement on track names at all.
    /// <para>
    /// Clips cannot be addressed the same way. A <see cref="TimelineClip"/> is a plain serializable
    /// class owned by its track, not a UnityEngine.Object: it has no instance id and cannot survive a
    /// domain reload as a reference. Worse, changing a clip's start re-sorts its track's clip list, so
    /// an index is only valid until the next edit. Callers therefore address a clip by display name,
    /// by index, or by a time it covers, and every editing tool reports the clip's address again in
    /// its result so the caller can re-address it after the edit moved it.
    /// </para>
    /// </remarks>
    internal static class TimelineResolve
    {
        /// <summary>How many names a "not found" message lists before giving up.</summary>
        private const int MaxSuggestions = 12;

        private static readonly Regex IndexedSegment = new Regex(@"^(?<name>.*?)\[(?<index>\d+)\]$");

        /// <summary>
        /// The director on the addressed GameObject. Shared so the Timeline and Recorder tools agree
        /// on what a missing director looks like.
        /// </summary>
        internal static PlayableDirector Director(string objectPath, long? instanceId)
        {
            var go = ObjectResolve.Object(objectPath, instanceId);
            var director = go.GetComponent<PlayableDirector>();

            if (director == null)
            {
                throw new McpToolException(
                    "not_found",
                    $"'{ObjectResolve.PathOf(go)}' has no PlayableDirector. timeline_inspect with no " +
                    "arguments lists the objects that do.");
            }

            return director;
        }

        /// <summary>The director's timeline, refusing rather than returning null.</summary>
        internal static TimelineAsset Timeline(PlayableDirector director, string verb = "edit")
        {
            if (!(director.playableAsset is TimelineAsset timeline))
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{ObjectResolve.PathOf(director.gameObject)}' has no TimelineAsset to {verb}.");
            }

            return timeline;
        }

        /// <summary>
        /// Every track, groups included, depth first. <c>GetOutputTracks</c> is flat and drops the
        /// group tracks themselves, so it cannot describe where a track sits.
        /// </summary>
        internal static IEnumerable<TrackAsset> AllTracks(TimelineAsset timeline)
        {
            foreach (var root in timeline.GetRootTracks())
            {
                foreach (var track in Descend(root))
                {
                    yield return track;
                }
            }
        }

        private static IEnumerable<TrackAsset> Descend(TrackAsset track)
        {
            yield return track;

            foreach (var child in track.GetChildTracks())
            {
                foreach (var nested in Descend(child))
                {
                    yield return nested;
                }
            }
        }

        /// <summary>
        /// The address of a track: its name, prefixed by its groups, with a <c>[n]</c> index only
        /// where the name repeats among its siblings.
        /// </summary>
        internal static string PathOf(TrackAsset track)
        {
            var segments = new List<string>();

            for (var current = track; current != null; current = current.parent as TrackAsset)
            {
                segments.Add(Segment(current));
            }

            segments.Reverse();

            return string.Join("/", segments);
        }

        private static string Segment(TrackAsset track)
        {
            var siblings = (track.parent as TrackAsset)?.GetChildTracks()
                           ?? (track.timelineAsset != null
                               ? track.timelineAsset.GetRootTracks()
                               : Enumerable.Empty<TrackAsset>());

            var sameName = siblings.Where(s => s.name == track.name).ToList();

            if (sameName.Count <= 1)
            {
                return track.name;
            }

            return $"{track.name}[{sameName.IndexOf(track)}]";
        }

        /// <summary>
        /// Finds a track by the path <see cref="PathOf"/> produces. A bare name also matches a track
        /// at any depth, so the common case does not need the group prefix.
        /// </summary>
        internal static TrackAsset Track(TimelineAsset timeline, string trackPath)
        {
            if (string.IsNullOrWhiteSpace(trackPath))
            {
                throw new McpToolException(
                    "invalid_params",
                    "'track' is required. timeline_inspect reports the path of every track.");
            }

            var wanted = trackPath.Trim().Trim('/');

            // The full path first, so an explicit address always wins over a coincidental bare name.
            var byPath = AllTracks(timeline).FirstOrDefault(
                t => string.Equals(PathOf(t), wanted, StringComparison.Ordinal));

            if (byPath != null)
            {
                return byPath;
            }

            var match = IndexedSegment.Match(wanted);
            var bareName = match.Success ? match.Groups["name"].Value : wanted;
            var index = match.Success ? int.Parse(match.Groups["index"].Value) : (int?)null;

            var candidates = AllTracks(timeline)
                .Where(t => string.Equals(t.name, bareName, StringComparison.Ordinal))
                .ToList();

            if (candidates.Count == 0)
            {
                throw new McpToolException("not_found", NotFound(timeline, wanted));
            }

            if (index.HasValue)
            {
                if (index.Value >= candidates.Count)
                {
                    throw new McpToolException(
                        "not_found",
                        $"'{wanted}' is out of range; {candidates.Count} track(s) are named '{bareName}'.");
                }

                return candidates[index.Value];
            }

            if (candidates.Count > 1)
            {
                var paths = string.Join(", ", candidates.Select(PathOf));

                throw new McpToolException(
                    "invalid_params",
                    $"'{wanted}' matches {candidates.Count} tracks. Use one of: {paths}.");
            }

            return candidates[0];
        }

        private static string NotFound(TimelineAsset timeline, string wanted)
        {
            var names = AllTracks(timeline).Select(PathOf).ToList();

            if (names.Count == 0)
            {
                return $"'{timeline.name}' has no tracks, so '{wanted}' cannot be found.";
            }

            var listed = string.Join(", ", names.Take(MaxSuggestions));
            var suffix = names.Count > MaxSuggestions ? $", and {names.Count - MaxSuggestions} more" : string.Empty;

            return $"No track '{wanted}' on '{timeline.name}'. It has: {listed}{suffix}.";
        }

        /// <summary>
        /// A locked track is a deliberate "do not touch" from whoever locked it, and Timeline itself
        /// enforces that only in its window — every API still writes straight through.
        /// </summary>
        internal static void RefuseIfLocked(TrackAsset track)
        {
            if (track.lockedInHierarchy)
            {
                throw new McpToolException(
                    "conflict",
                    $"Track '{PathOf(track)}' is locked. Unlock it with timeline_set_track " +
                    "(locked=false) before editing it.",
                    409);
            }
        }

        /// <summary>
        /// Finds a clip on a track by display name, by index, or by a time it covers. Exactly one of
        /// the three must be given.
        /// </summary>
        internal static TimelineClip Clip(TrackAsset track, string clipName, int? index, double? atTime)
        {
            var given = (string.IsNullOrWhiteSpace(clipName) ? 0 : 1) + (index.HasValue ? 1 : 0) + (atTime.HasValue ? 1 : 0);

            if (given == 0)
            {
                throw new McpToolException(
                    "invalid_params",
                    "Give one of 'clip', 'clip_index' or 'at_time' to say which clip on " +
                    $"'{PathOf(track)}' to act on.");
            }

            if (given > 1)
            {
                throw new McpToolException(
                    "invalid_params",
                    "'clip', 'clip_index' and 'at_time' address the same clip in different ways; give one.");
            }

            var clips = track.GetClips().ToList();

            if (clips.Count == 0)
            {
                throw new McpToolException("not_found", $"Track '{PathOf(track)}' has no clips.");
            }

            if (index.HasValue)
            {
                if (index.Value < 0 || index.Value >= clips.Count)
                {
                    throw new McpToolException(
                        "not_found",
                        $"'clip_index' {index.Value} is out of range; '{PathOf(track)}' has {clips.Count} clip(s). " +
                        "Note that editing a clip's start re-sorts the track, so indices move.");
                }

                return clips[index.Value];
            }

            if (atTime.HasValue)
            {
                var covering = clips.FirstOrDefault(c => atTime.Value >= c.start && atTime.Value < c.end);

                if (covering == null)
                {
                    var spans = string.Join(", ", clips.Take(MaxSuggestions).Select(c => $"{c.displayName} [{c.start:0.###}-{c.end:0.###}]"));

                    throw new McpToolException(
                        "not_found",
                        $"No clip covers {atTime.Value:0.###}s on '{PathOf(track)}'. It has: {spans}.");
                }

                return covering;
            }

            var named = clips.Where(c => string.Equals(c.displayName, clipName, StringComparison.Ordinal)).ToList();

            if (named.Count == 0)
            {
                var names = string.Join(", ", clips.Take(MaxSuggestions).Select(c => c.displayName));

                throw new McpToolException(
                    "not_found",
                    $"No clip named '{clipName}' on '{PathOf(track)}'. It has: {names}.");
            }

            if (named.Count > 1)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{clipName}' matches {named.Count} clips on '{PathOf(track)}'. " +
                    "Use 'clip_index' or 'at_time' instead.");
            }

            return named[0];
        }

        /// <summary>The index a clip currently sits at, which is how the caller re-addresses it.</summary>
        internal static int IndexOf(TimelineClip clip, TrackAsset track)
        {
            return track.GetClips().ToList().IndexOf(clip);
        }
    }
}
