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
using UnityMCP.Editor.Tools;

using UnityObject = UnityEngine.Object;

namespace UnityMCP.Editor.Timeline.Tests
{
    /// <summary>
    /// Muting, locking, binding and deleting.
    /// </summary>
    /// <remarks>
    /// Two of these check something the Timeline window would never show. Deleting has to leave no
    /// orphaned sub-asset inside the .playable file — Timeline's own deletion skips the destroy step
    /// when undo is unavailable, and the leftover is invisible except by counting the objects in the
    /// file. And binding an Animation track to a GameObject rather than its Animator is accepted
    /// silently by Timeline and simply does nothing, so the resolved component is asserted.
    /// </remarks>
    [TestFixture]
    internal sealed class TimelineTrackToolsTests
    {
        private string folder;
        private GameObject director;
        private GameObject subject;

        [SetUp]
        public void SetUp()
        {
            this.folder = "Assets/__unity_mcp_track_tests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(this.folder));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in new[] { this.director, this.subject })
            {
                if (go != null)
                {
                    UnityObject.DestroyImmediate(go);
                }
            }

            if (AssetDatabase.IsValidFolder(this.folder))
            {
                AssetDatabase.DeleteAsset(this.folder);
            }
        }

        private string AssetPath => this.folder + "/Stage.playable";

        private PlayableDirector Stage()
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.name = "Stage";
            AssetDatabase.CreateAsset(timeline, this.AssetPath);

            var track = timeline.CreateTrack<ActivationTrack>(null, "Shots");
            var clip = track.CreateDefaultClip();
            clip.start = 0;
            clip.duration = 2;
            clip.displayName = "Shot";

            this.director = new GameObject("StageDirector");
            var playable = this.director.AddComponent<PlayableDirector>();
            playable.playableAsset = timeline;

            return playable;
        }

        private long Id => EntityIdCompat.IdOf(this.director);

        private static TimelineAsset TimelineOf(PlayableDirector d) => (TimelineAsset)d.playableAsset;

        [Test]
        public void MutingATrackShortensTheTimelineAndSaysSo()
        {
            var playable = this.Stage();
            Assert.That(TimelineOf(playable).duration, Is.GreaterThan(0));

            var result = TimelineTrackTools.SetTrack(instanceId: this.Id, track: "Shots", muted: true);

            Assert.That((bool)result["muted"], Is.True);
            // A muted track drops out of the length calculation, so the caller's idea of the
            // timeline's duration would silently go stale without this.
            Assert.That((double)result["duration"], Is.EqualTo(0).Within(1e-4));
        }

        [Test]
        public void ARenameIsReportedAsTheNewAddress()
        {
            var playable = this.Stage();

            var result = TimelineTrackTools.SetTrack(instanceId: this.Id, track: "Shots", name: "Cameras");

            Assert.That((string)result["track"], Is.EqualTo("Cameras"));
            Assert.That(TimelineResolve.Track(TimelineOf(playable), "Cameras"), Is.Not.Null);
        }

        [Test]
        public void BindingAnAnimationTrackResolvesTheAnimator()
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.name = "Anim";
            AssetDatabase.CreateAsset(timeline, this.folder + "/Anim.playable");
            timeline.CreateTrack<AnimationTrack>(null, "Motion");

            this.director = new GameObject("AnimDirector");
            var playable = this.director.AddComponent<PlayableDirector>();
            playable.playableAsset = timeline;

            this.subject = new GameObject("Cube");
            this.subject.AddComponent<Animator>();

            var result = TimelineTrackTools.SetTrack(
                instanceId: this.Id, track: "Motion", binding: ObjectResolve.PathOf(this.subject));

            // Timeline accepts a GameObject here without complaint and then does nothing, so the
            // component has to be resolved rather than passed through.
            Assert.That((string)result["binding"], Does.Contain("Animator"));

            var track = TimelineResolve.Track(timeline, "Motion");
            Assert.That(playable.GetGenericBinding(track), Is.InstanceOf<Animator>());
        }

        [Test]
        public void BindingAnObjectThatCannotProvideTheComponentIsRefused()
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.name = "Anim";
            AssetDatabase.CreateAsset(timeline, this.folder + "/Anim.playable");
            timeline.CreateTrack<AnimationTrack>(null, "Motion");

            this.director = new GameObject("AnimDirector");
            this.director.AddComponent<PlayableDirector>().playableAsset = timeline;

            this.subject = new GameObject("NoAnimator");

            var error = Assert.Throws<McpToolException>(() => TimelineTrackTools.SetTrack(
                instanceId: this.Id, track: "Motion", binding: ObjectResolve.PathOf(this.subject)));

            Assert.That(error.Code, Is.EqualTo("not_found"));
            Assert.That(error.Message, Does.Contain("Animator"));
        }

        [Test]
        public void ABindingCanBeCleared()
        {
            var playable = this.Stage();
            this.subject = new GameObject("Target");

            TimelineTrackTools.SetTrack(
                instanceId: this.Id, track: "Shots", binding: ObjectResolve.PathOf(this.subject));

            var result = TimelineTrackTools.SetTrack(instanceId: this.Id, track: "Shots", clearBinding: true);

            Assert.That(result["binding"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(playable.GetGenericBinding(TimelineResolve.Track(TimelineOf(playable), "Shots")), Is.Null);
        }

        [Test]
        public void BindingAndClearingTogetherIsRefused()
        {
            this.Stage();
            this.subject = new GameObject("Target");

            var error = Assert.Throws<McpToolException>(() => TimelineTrackTools.SetTrack(
                instanceId: this.Id, track: "Shots",
                binding: ObjectResolve.PathOf(this.subject), clearBinding: true));

            Assert.That(error.Code, Is.EqualTo("invalid_params"));
        }

        [Test]
        public void ALockedTrackCanStillBeUnlocked()
        {
            var playable = this.Stage();
            TimelineResolve.Track(TimelineOf(playable), "Shots").locked = true;

            // Everything else on a locked track is refused; unlocking has to remain possible or the
            // lock would be a one-way door for these tools.
            Assert.That(Assert.Throws<McpToolException>(() =>
                TimelineTrackTools.SetTrack(instanceId: this.Id, track: "Shots", muted: true)).Code,
                Is.EqualTo("conflict"));

            var result = TimelineTrackTools.SetTrack(instanceId: this.Id, track: "Shots", locked: false);
            Assert.That((bool)result["locked"], Is.False);
        }

        [Test]
        public void DeletingAClipLeavesTheTrack()
        {
            var playable = this.Stage();

            var result = TimelineTrackTools.Delete(instanceId: this.Id, track: "Shots", clip: "Shot");

            Assert.That((string)result["kind"], Is.EqualTo("clip"));
            Assert.That((int)result["clipCount"], Is.EqualTo(0));
            Assert.That(TimelineResolve.Track(TimelineOf(playable), "Shots"), Is.Not.Null);
        }

        [Test]
        public void DeletingATrackLeavesNoOrphanInsideTheAsset()
        {
            var playable = this.Stage();
            AssetDatabase.SaveAssets();

            var before = AssetDatabase.LoadAllAssetsAtPath(this.AssetPath).Length;
            Assert.That(before, Is.GreaterThan(1), "the track and clip asset should be sub-assets");

            TimelineTrackTools.Delete(instanceId: this.Id, track: "Shots");
            AssetDatabase.SaveAssets();

            var after = AssetDatabase.LoadAllAssetsAtPath(this.AssetPath);

            // Timeline skips the destroy when undo is unavailable, which unparents the track but
            // leaves it inside the file. Counting the objects is the only way to see that.
            Assert.That(after.Length, Is.EqualTo(1), "only the timeline itself should remain");
            Assert.That(after.Single(), Is.InstanceOf<TimelineAsset>());
            Assert.That(TimelineOf(playable).GetRootTracks().Count(), Is.EqualTo(0));
        }

        [Test]
        public void DeletingAGroupTakesTheTracksInsideIt()
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.name = "Grouped";
            AssetDatabase.CreateAsset(timeline, this.folder + "/Grouped.playable");

            var group = timeline.CreateTrack<GroupTrack>(null, "Cameras");
            timeline.CreateTrack<ActivationTrack>(group, "Front");
            timeline.CreateTrack<ActivationTrack>(group, "Side");

            this.director = new GameObject("GroupedDirector");
            this.director.AddComponent<PlayableDirector>().playableAsset = timeline;

            var result = TimelineTrackTools.Delete(instanceId: this.Id, track: "Cameras");

            Assert.That((int)result["tracksRemoved"], Is.EqualTo(3), "the group and both children");
            Assert.That(timeline.GetRootTracks().Count(), Is.EqualTo(0));
        }

        [Test]
        public void DeletingIsUndoableOneCallAtATime()
        {
            var playable = this.Stage();

            var catalog = ToolCatalog.BuildFromTypes(new[] { typeof(TimelineTrackTools) });
            var descriptor = catalog.Tools.Single(t => t.Name == "timeline_delete");
            var id = this.Id;

            Undo.IncrementCurrentGroup();

            ToolInvoker.Invoke(descriptor, new JObject
            {
                ["instance_id"] = id, ["track"] = "Shots", ["clip"] = "Shot",
            });

            Assert.That(TimelineResolve.Track(TimelineOf(playable), "Shots").GetClips().Count(), Is.EqualTo(0));

            Undo.PerformUndo();

            Assert.That(TimelineResolve.Track(TimelineOf(playable), "Shots").GetClips().Count(), Is.EqualTo(1),
                        "the clip did not come back");
        }

        [Test]
        public void DeletingAMissingTrackNamesTheOnesThatExist()
        {
            this.Stage();

            var error = Assert.Throws<McpToolException>(() =>
                TimelineTrackTools.Delete(instanceId: this.Id, track: "Nope"));

            Assert.That(error.Code, Is.EqualTo("not_found"));
            Assert.That(error.Message, Does.Contain("Shots"));
        }
    }
}
