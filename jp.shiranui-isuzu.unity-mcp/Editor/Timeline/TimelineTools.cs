using System;
using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;

using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

using UnityObject = UnityEngine.Object;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Timeline
{
    /// <summary>
    /// Reading a Timeline's structure — including the timelines it nests through Control tracks —
    /// and moving a director to a moment so its frame can be seen.
    /// </summary>
    /// <remarks>
    /// The tool this is built around is <c>timeline_evaluate</c>: set a director to a time and
    /// evaluate it in the Editor, without entering Play Mode, so the exact frame under question can
    /// be captured and diffed. Video work spends its time on "the expression at 3.5s is wrong",
    /// and answering that means driving the timeline to 3.5s and looking — which is what this does,
    /// so capture_screenshot and render_compare do the rest.
    /// <para>
    /// <c>timeline_inspect</c> follows Control tracks into the child timelines they drive. A live
    /// stage is routinely a root timeline whose Control clips each start a character or effect
    /// timeline, several layers deep, and a tool that stops at the first layer cannot see where
    /// anything actually happens. Resolving that nesting is the point.
    /// </para>
    /// <para>
    /// In its own assembly, constrained to <c>UNITY_TIMELINE</c>: a project without
    /// com.unity.timeline loses these tools rather than failing to compile the package.
    /// </para>
    /// </remarks>
    internal static class TimelineTools
    {
        [McpTool(
            "timeline_inspect",
            "Report a Timeline's tracks, clips and bindings, and follow Control tracks into the child " +
            "timelines they drive. Read this before evaluating or editing: it gives the track and clip " +
            "names the other timeline tools take, and it shows the nested structure — a live stage is " +
            "usually a root timeline whose Control clips start character and effect timelines several " +
            "layers down — in numbers rather than a screenshot of the Timeline window.",
            Idempotency = McpIdempotency.Safe,
            // A nested stage expanded a couple of layers deep is a large report, and it is the one
            // the editing tools take their addresses from.
            MaxResultSizeChars = 200000)]
        public static JObject Inspect(
            [McpArg("object_path", "Hierarchy path of a GameObject with a PlayableDirector. " +
                                   "Omit to list every director in the open scenes.")]
            string objectPath = null,
            [McpArg("instance_id", "Address the director's GameObject by instance id instead.")]
            long? instanceId = null,
            [McpArg("include_clips", "Include each track's clips, with their start and duration.")]
            bool includeClips = true,
            [McpArg("track", "Only report the top-level track whose name contains this text.")]
            string track = null,
            [McpArg("nest_depth", "How many Control-track layers to follow into child timelines. " +
                                  "0 stops at this timeline; the default follows one layer.")]
            int nestDepth = 1)
        {
            if (string.IsNullOrWhiteSpace(objectPath) && !instanceId.HasValue)
            {
                var directors = UnityObject.FindObjectsByType<PlayableDirector>(FindObjectsInactive.Include, FindObjectsSortMode.None);

                return new JObject
                {
                    ["directors"] = new JArray(directors.Select(d => (object)new JObject
                    {
                        ["path"] = ObjectResolve.PathOf(d.gameObject),
                        ["timeline"] = d.playableAsset == null ? null : (JToken)d.playableAsset.name,
                        ["time"] = d.time,
                        ["duration"] = d.duration,
                        ["state"] = d.state.ToString(),
                    }).ToArray()),
                    ["count"] = directors.Length,
                };
            }

            var director = ResolveDirector(objectPath, instanceId);

            // Guards against a Control track that loops back to a timeline already being reported,
            // which would otherwise recurse until the stack gave out.
            var visited = new HashSet<int>();

            return DescribeDirector(director, includeClips, track, Math.Max(nestDepth, 0), visited);
        }

        [McpTool(
            "timeline_evaluate",
            "Move a Timeline director to a time (or frame) and evaluate it in the Editor, without " +
            "entering Play Mode. This is how you look at one moment: evaluate, then capture_screenshot " +
            "the result and render_compare it against another. Give either 'time' in seconds or " +
            "'frame'; frame uses the timeline's frame rate.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Evaluate(
            [McpArg("object_path", "Hierarchy path of the GameObject with the PlayableDirector.")]
            string objectPath = null,
            [McpArg("instance_id", "Address the director's GameObject by instance id instead.")]
            long? instanceId = null,
            [McpArg("time", "Time in seconds to move to.")]
            double? time = null,
            [McpArg("frame", "Frame to move to, using the timeline's frame rate. Overrides time.")]
            int? frame = null)
        {
            var director = ResolveDirector(objectPath, instanceId);
            var timeline = director.playableAsset as TimelineAsset;

            if (timeline == null)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{ObjectResolve.PathOf(director.gameObject)}' has no TimelineAsset to evaluate.");
            }

            var frameRate = FrameRateOf(timeline);
            double target;

            if (frame.HasValue)
            {
                target = frame.Value / frameRate;
            }
            else if (time.HasValue)
            {
                target = time.Value;
            }
            else
            {
                throw new McpToolException("invalid_params", "Pass either 'time' (seconds) or 'frame'.");
            }

            if (target < 0 || target > director.duration + 1e-6)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"{Math.Round(target, 4)}s is outside the timeline, which runs 0 to " +
                    $"{Math.Round(director.duration, 4)}s.");
            }

            // Remember and restore the update mode. Scrubbing needs Manual, but leaving a director
            // in Manual would mean it no longer advanced on its own when the user next pressed
            // Play — a change they did not ask for and would not connect to a tool call.
            var previousMode = director.timeUpdateMode;
            director.timeUpdateMode = DirectorUpdateMode.Manual;

            if (!director.playableGraph.IsValid())
            {
                director.RebuildGraph();
            }

            director.time = target;
            director.Evaluate();
            director.timeUpdateMode = previousMode;
            SceneView.RepaintAll();

            return new JObject
            {
                ["path"] = ObjectResolve.PathOf(director.gameObject),
                ["timeline"] = timeline.name,
                ["time"] = Math.Round(director.time, 4),
                ["frame"] = (int)Math.Round(director.time * frameRate),
                ["frameRate"] = frameRate,
                ["duration"] = Math.Round(director.duration, 4),
                ["note"] = "Evaluated in the Editor. capture_screenshot now shows this moment.",
            };
        }

        private static JObject DescribeDirector(
            PlayableDirector director, bool includeClips, string trackFilter, int nestDepth, HashSet<int> visited)
        {
            var timeline = director.playableAsset as TimelineAsset;

            var result = new JObject
            {
                ["path"] = ObjectResolve.PathOf(director.gameObject),
                ["timeline"] = timeline == null ? null : (JToken)timeline.name,
                ["timelinePath"] = timeline == null ? null : (JToken)AssetDatabase.GetAssetPath(timeline),
                ["time"] = director.time,
                ["duration"] = director.duration,
                ["state"] = director.state.ToString(),
                ["frameRate"] = timeline == null ? 0d : FrameRateOf(timeline),
                ["extrapolation"] = director.extrapolationMode.ToString(),
            };

            if (timeline == null)
            {
                result["note"] = "The director has no TimelineAsset assigned.";
                return result;
            }

            if (!visited.Add(timeline.GetInstanceID()))
            {
                result["note"] = "Already reported above; a Control track loops back to it.";
                return result;
            }

            var tracks = new JArray();

            foreach (var t in timeline.GetOutputTracks())
            {
                if (!string.IsNullOrWhiteSpace(trackFilter) &&
                    t.name.IndexOf(trackFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var binding = director.GetGenericBinding(t);

                var entry = new JObject
                {
                    ["name"] = t.name,
                    // The address the editing tools take. Track names repeat freely and a track can
                    // sit inside a group, so the name alone is not enough to act on.
                    ["path"] = TimelineResolve.PathOf(t),
                    ["type"] = t.GetType().Name,
                    ["muted"] = t.muted,
                    ["locked"] = t.lockedInHierarchy,
                    ["clipCount"] = t.GetClips().Count(),
                    // The binding is what a track is pointing at — the wrong one is a common
                    // reason "the animation does nothing", and it does not show in the window.
                    ["binding"] = binding == null ? null : (JToken)DescribeBinding(binding),
                };

                if (includeClips || IsControlTrack(t))
                {
                    entry["clips"] = new JArray(t.GetClips()
                        .Select(c => (object)DescribeClip(c, director, nestDepth, visited))
                        .ToArray());
                }

                tracks.Add(entry);
            }

            result["trackCount"] = tracks.Count;
            result["tracks"] = tracks;

            return result;
        }

        private static JObject DescribeClip(
            TimelineClip clip, PlayableDirector director, int nestDepth, HashSet<int> visited)
        {
            var entry = new JObject
            {
                ["name"] = clip.displayName,
                ["start"] = Math.Round(clip.start, 4),
                ["end"] = Math.Round(clip.end, 4),
                ["duration"] = Math.Round(clip.duration, 4),
                ["asset"] = clip.asset == null ? null : (JToken)clip.asset.GetType().Name,
            };

            if (clip.asset is ControlPlayableAsset control)
            {
                // The child this Control clip drives. sourceGameObject is an ExposedReference, so
                // it resolves against the director whose table holds the value — the reference is
                // empty in the asset on its own.
                var childGo = control.sourceGameObject.Resolve(director);

                entry["controls"] = childGo == null ? null : (JToken)ObjectResolve.PathOf(childGo);
                entry["updatesDirector"] = control.updateDirector;

                if (childGo != null && control.updateDirector)
                {
                    var childDirector = childGo.GetComponent<PlayableDirector>();

                    if (childDirector != null && childDirector.playableAsset != null)
                    {
                        entry["childTimeline"] = childDirector.playableAsset.name;

                        if (nestDepth > 0)
                        {
                            entry["nested"] = DescribeDirector(
                                childDirector, true, null, nestDepth - 1, visited);
                        }
                        else
                        {
                            entry["nested"] = "depth reached; raise nest_depth to expand";
                        }
                    }
                }
            }

            return entry;
        }

        private static bool IsControlTrack(TrackAsset track)
        {
            return track.GetType().Name == "ControlTrack";
        }

        // Kept as a thin forward so this file reads the same as before; the implementation moved to
        // TimelineResolve when the editing tools needed it too, rather than being copied a third time.
        private static PlayableDirector ResolveDirector(string objectPath, long? instanceId)
        {
            return TimelineResolve.Director(objectPath, instanceId);
        }

        private static double FrameRateOf(TimelineAsset timeline)
        {
            try
            {
                var rate = timeline.editorSettings.frameRate;
                return rate > 0 ? rate : 60d;
            }
            catch
            {
                // editorSettings.frameRate is a double on recent Timeline and was fps before it.
                // Rather than branch on the package version for a fallback nobody hits, assume the
                // common default.
                return 60d;
            }
        }

        private static string DescribeBinding(UnityObject binding)
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
    }
}
