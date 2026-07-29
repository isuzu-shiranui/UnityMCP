using System;
using System.IO;
using System.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityEditor;

using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

using UnityMCP.Editor.Core;

using UnityObject = UnityEngine.Object;

namespace UnityMCP.Editor.Timeline.Tests
{
    /// <summary>
    /// Editing clips: what lands, what is refused, and what the caller is told when Timeline drops a
    /// write on the floor.
    /// </summary>
    /// <remarks>
    /// The cases that matter most are the ones where Timeline succeeds quietly at doing nothing. An
    /// Activation clip accepts a speed multiplier and keeps 1.0; a ripple that would push a clip
    /// before zero must move none of them rather than some. Both are invisible unless the test looks
    /// at the value afterwards, so these read back rather than trusting the call.
    /// </remarks>
    [TestFixture]
    internal sealed class TimelineEditToolsTests
    {
        private string folder;
        private GameObject director;

        [SetUp]
        public void SetUp()
        {
            this.folder = "Assets/__unity_mcp_edit_tests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(this.folder));
        }

        [TearDown]
        public void TearDown()
        {
            if (this.director != null)
            {
                UnityObject.DestroyImmediate(this.director);
            }

            if (AssetDatabase.IsValidFolder(this.folder))
            {
                AssetDatabase.DeleteAsset(this.folder);
            }
        }

        /// <summary>
        /// A director with one Activation track carrying clips at the given starts, each 1s long.
        /// The asset exists before any track does, because CreateTrack only persists a track into a
        /// timeline that is already an asset.
        /// </summary>
        private PlayableDirector Stage(string name, params double[] starts)
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.name = name;
            AssetDatabase.CreateAsset(timeline, this.folder + "/" + name + ".playable");

            var track = timeline.CreateTrack<ActivationTrack>(null, "Shots");

            for (var i = 0; i < starts.Length; i++)
            {
                var clip = track.CreateDefaultClip();
                clip.start = starts[i];
                clip.duration = 1;
                clip.displayName = "Shot" + i;
            }

            this.director = new GameObject(name + "Director");
            var playable = this.director.AddComponent<PlayableDirector>();
            playable.playableAsset = timeline;

            return playable;
        }

        private long Id => EntityIdCompat.IdOf(this.director);

        private static TrackAsset TrackOf(PlayableDirector director, string path = "Shots")
        {
            return TimelineResolve.Track((TimelineAsset)director.playableAsset, path);
        }

        [Test]
        public void RetimingAClipReportsWhereItEndedUp()
        {
            var playable = this.Stage("Retime", 0, 2);

            var result = TimelineEditTools.EditClip(
                instanceId: this.Id, track: "Shots", clip: "Shot0", start: 4, duration: 1.5);

            Assert.That((double)result["start"], Is.EqualTo(4).Within(1e-4));
            Assert.That((double)result["duration"], Is.EqualTo(1.5).Within(1e-4));
            Assert.That((double)result["end"], Is.EqualTo(5.5).Within(1e-4));

            var clip = TrackOf(playable).GetClips().Single(c => c.displayName == "Shot0");
            Assert.That(clip.start, Is.EqualTo(4).Within(1e-4), "the asset itself must carry the change");
        }

        [Test]
        public void MovingAClipPastAnotherReportsItsNewIndex()
        {
            var playable = this.Stage("Resort", 0, 2);

            // Timeline re-sorts a track whenever a start changes, so the index the caller used to
            // address this clip is stale the moment the call returns.
            var result = TimelineEditTools.EditClip(
                instanceId: this.Id, track: "Shots", clipIndex: 0, start: 9);

            Assert.That((int)result["clipIndex"], Is.EqualTo(1), "the clip is now the later of the two");

            var clips = TrackOf(playable).GetClips().ToList();
            Assert.That(clips[1].displayName, Is.EqualTo("Shot0"));
        }

        [Test]
        public void AWriteTheClipTypeIgnoresIsReportedRatherThanAssumed()
        {
            this.Stage("Caps", 0);

            // ActivationPlayableAsset declares ClipCaps.None: the setter runs and keeps 1.0.
            var result = TimelineEditTools.EditClip(
                instanceId: this.Id, track: "Shots", clip: "Shot0", timeScale: 2.0);

            Assert.That((double)result["timeScale"], Is.EqualTo(1.0).Within(1e-4),
                        "the effective value, not the requested one");

            var ignored = result["ignored"].Single();
            Assert.That((string)ignored["arg"], Is.EqualTo("time_scale"));
            Assert.That((double)ignored["requested"], Is.EqualTo(2.0).Within(1e-4));
            Assert.That((double)ignored["effective"], Is.EqualTo(1.0).Within(1e-4));
            Assert.That((string)ignored["reason"], Does.Contain("ActivationPlayableAsset"));
        }

        [Test]
        public void DurationAndEndTogetherAreRefused()
        {
            this.Stage("Both", 0);

            var error = Assert.Throws<McpToolException>(() => TimelineEditTools.EditClip(
                instanceId: this.Id, track: "Shots", clip: "Shot0", duration: 2, end: 5));

            Assert.That(error.Code, Is.EqualTo("invalid_params"));
        }

        [Test]
        public void AnEndBeforeTheStartIsRefused()
        {
            this.Stage("Backwards", 3);

            var error = Assert.Throws<McpToolException>(() => TimelineEditTools.EditClip(
                instanceId: this.Id, track: "Shots", clip: "Shot0", end: 1));

            Assert.That(error.Code, Is.EqualTo("invalid_params"));
        }

        [Test]
        public void ANonFiniteTimeIsRefusedBeforeItReachesTimeline()
        {
            this.Stage("NaN", 0);

            // Timeline's own guard logs an error and silently keeps the old value, which would look
            // like success to the caller.
            var error = Assert.Throws<McpToolException>(() => TimelineEditTools.EditClip(
                instanceId: this.Id, track: "Shots", clip: "Shot0", start: double.NaN));

            Assert.That(error.Code, Is.EqualTo("invalid_params"));
        }

        [Test]
        public void EditingALockedTrackIsRefused()
        {
            var playable = this.Stage("Locked", 0);
            TrackOf(playable).locked = true;

            var error = Assert.Throws<McpToolException>(() => TimelineEditTools.EditClip(
                instanceId: this.Id, track: "Shots", clip: "Shot0", start: 1));

            Assert.That(error.Code, Is.EqualTo("conflict"));
            Assert.That(error.Message, Does.Contain("timeline_set_track"));
        }

        [Test]
        public void TwoEditsTakeTwoUndoSteps()
        {
            var playable = this.Stage("Undo", 0);

            // Through the invoker, because that is what opens and collapses the undo group; calling
            // the method directly would leave the edits sharing whatever group the fixture's own
            // track creation is in, and the first undo would take the track with it.
            var catalog = ToolCatalog.BuildFromTypes(new[] { typeof(TimelineEditTools) });
            var descriptor = catalog.Tools.Single(t => t.Name == "timeline_edit_clip");
            var id = this.Id;

            void Move(double to) => ToolInvoker.Invoke(descriptor, new JObject
            {
                ["instance_id"] = id,
                ["track"] = "Shots",
                ["clip"] = "Shot0",
                ["start"] = to,
            });

            Undo.IncrementCurrentGroup();

            Move(2);
            Move(5);

            Assert.That(TrackOf(playable).GetClips().Single().start, Is.EqualTo(5).Within(1e-4));

            // One step per call. A single-call test cannot tell a correct group from one that
            // swallowed the whole session, which is a bug this repository has already shipped once.
            Undo.PerformUndo();
            Assert.That(TrackOf(playable).GetClips().Single().start, Is.EqualTo(2).Within(1e-4),
                        "one undo reversed more than the last call");

            Undo.PerformUndo();
            Assert.That(TrackOf(playable).GetClips().Single().start, Is.EqualTo(0).Within(1e-4),
                        "the earlier edit did not come back");
        }

        [Test]
        public void ShiftingMovesEveryClipAtOrAfterTheTime()
        {
            var playable = this.Stage("Ripple", 0, 2, 4);

            var result = TimelineEditTools.ShiftClips(instanceId: this.Id, fromTime: 2, by: 0.5);

            Assert.That((int)result["moved"], Is.EqualTo(2));

            var byName = TrackOf(playable).GetClips().ToDictionary(c => c.displayName, c => c.start);
            Assert.That(byName["Shot0"], Is.EqualTo(0).Within(1e-4), "the clip before the cut stays put");
            Assert.That(byName["Shot1"], Is.EqualTo(2.5).Within(1e-4));
            Assert.That(byName["Shot2"], Is.EqualTo(4.5).Within(1e-4));
        }

        [Test]
        public void AShiftThatWouldCrossZeroMovesNothingAtAll()
        {
            var playable = this.Stage("Guard", 0, 2, 4);

            var error = Assert.Throws<McpToolException>(() =>
                TimelineEditTools.ShiftClips(instanceId: this.Id, fromTime: 0, by: -1));

            Assert.That(error.Code, Is.EqualTo("invalid_params"));

            // The point of the guard: a partly applied ripple leaves the caller unable to tell how
            // far it got, so none of the movable clips may have moved either.
            var starts = TrackOf(playable).GetClips().Select(c => c.start).OrderBy(s => s).ToList();
            CollectionAssert.AreEqual(new[] { 0d, 2d, 4d }, starts);
        }

        [Test]
        public void ShiftingToATimeMovesTheEarliestAffectedClipThere()
        {
            var playable = this.Stage("Destination", 0, 2, 4);

            TimelineEditTools.ShiftClips(instanceId: this.Id, fromTime: 2, toTime: 3);

            var byName = TrackOf(playable).GetClips().ToDictionary(c => c.displayName, c => c.start);
            Assert.That(byName["Shot1"], Is.EqualTo(3).Within(1e-4));
            Assert.That(byName["Shot2"], Is.EqualTo(5).Within(1e-4), "the rest keep their spacing");
        }

        [Test]
        public void ShiftingNeedsExactlyOneOfByOrToTime()
        {
            this.Stage("Ambiguous", 0);

            Assert.That(Assert.Throws<McpToolException>(() =>
                TimelineEditTools.ShiftClips(instanceId: this.Id, fromTime: 0)).Code,
                Is.EqualTo("invalid_params"));

            Assert.That(Assert.Throws<McpToolException>(() =>
                TimelineEditTools.ShiftClips(instanceId: this.Id, fromTime: 0, by: 1, toTime: 2)).Code,
                Is.EqualTo("invalid_params"));
        }

        [Test]
        public void AnUnknownTrackNamesTheOnesThatExist()
        {
            this.Stage("Missing", 0);

            var error = Assert.Throws<McpToolException>(() => TimelineEditTools.EditClip(
                instanceId: this.Id, track: "Nope", clip: "Shot0", start: 1));

            Assert.That(error.Code, Is.EqualTo("not_found"));
            Assert.That(error.Message, Does.Contain("Shots"), "the message should list what is there");
        }

        [Test]
        public void ATrackInsideAGroupIsAddressedByItsPath()
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.name = "Grouped";
            AssetDatabase.CreateAsset(timeline, this.folder + "/Grouped.playable");

            var group = timeline.CreateTrack<GroupTrack>(null, "Cameras");
            var track = timeline.CreateTrack<ActivationTrack>(group, "Front");
            var clip = track.CreateDefaultClip();
            clip.displayName = "Take";
            clip.start = 0;
            clip.duration = 1;

            this.director = new GameObject("GroupedDirector");
            var playable = this.director.AddComponent<PlayableDirector>();
            playable.playableAsset = timeline;

            Assert.That(TimelineResolve.PathOf(track), Is.EqualTo("Cameras/Front"));

            var result = TimelineEditTools.EditClip(
                instanceId: this.Id, track: "Cameras/Front", clip: "Take", start: 2);

            Assert.That((string)result["track"], Is.EqualTo("Cameras/Front"));
            Assert.That((double)result["start"], Is.EqualTo(2).Within(1e-4));
        }
    }
}
