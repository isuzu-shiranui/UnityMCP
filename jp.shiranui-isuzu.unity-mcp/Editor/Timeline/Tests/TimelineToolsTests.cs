using System;
using System.IO;
using System.Linq;

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
    /// Reading a Timeline's structure, including the nesting a Control track creates, and moving a
    /// director without entering Play Mode.
    /// </summary>
    /// <remarks>
    /// The fixture chooses the numbers, so a wrong answer cannot agree with a wrong expectation.
    /// It also builds each timeline as an asset before adding tracks: a track created first is only
    /// held in memory — CreateTrack persists it through AddObjectToAsset, and that call is skipped
    /// while the timeline is not yet an asset — so it would vanish at the next domain reload and the
    /// test would be asserting against a structure that does not survive being saved.
    /// </remarks>
    [TestFixture]
    internal sealed class TimelineToolsTests
    {
        private string folder;
        private GameObject root;
        private GameObject child;

        [SetUp]
        public void SetUp()
        {
            this.folder = "Assets/__unity_mcp_timeline_tests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(this.folder));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in new[] { this.root, this.child })
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

        private TimelineAsset Timeline(string name, double frameRate = 30)
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.name = name;
            AssetDatabase.CreateAsset(timeline, this.folder + "/" + name + ".playable");
            timeline.editorSettings.frameRate = frameRate;

            return timeline;
        }

        private static PlayableDirector DirectorFor(GameObject go, TimelineAsset timeline)
        {
            var director = go.AddComponent<PlayableDirector>();
            director.playableAsset = timeline;

            return director;
        }

        /// <summary>
        /// A root timeline whose Control clip drives a child timeline, which is the shape a staged
        /// sequence actually has.
        /// </summary>
        private PlayableDirector BuildNested()
        {
            var childTimeline = this.Timeline("Child");
            var activation = childTimeline.CreateTrack<ActivationTrack>(null, "Visible");
            var activationClip = activation.CreateDefaultClip();
            activationClip.start = 1;
            activationClip.duration = 2;

            this.child = new GameObject("ChildDirector");
            var childDirector = DirectorFor(this.child, childTimeline);

            var rootTimeline = this.Timeline("Root");
            var control = rootTimeline.CreateTrack<ControlTrack>(null, "Drive");
            var controlClip = control.CreateDefaultClip();
            controlClip.start = 0;
            controlClip.duration = 4;
            controlClip.displayName = "Run Child";

            this.root = new GameObject("RootDirector");
            var rootDirector = DirectorFor(this.root, rootTimeline);

            var asset = (ControlPlayableAsset)controlClip.asset;
            asset.updateDirector = true;
            asset.sourceGameObject.exposedName = Guid.NewGuid().ToString("N");
            rootDirector.SetReferenceValue(asset.sourceGameObject.exposedName, this.child);

            return rootDirector;
        }

        [Test]
        public void TracksAndClipsComeBackWithTheirTimings()
        {
            var timeline = this.Timeline("Flat");
            var track = timeline.CreateTrack<ActivationTrack>(null, "Shown");
            var clip = track.CreateDefaultClip();
            clip.start = 2;
            clip.duration = 3;

            this.root = new GameObject("FlatDirector");
            var director = DirectorFor(this.root, timeline);

            var report = TimelineTools.Inspect(instanceId: EntityIdCompat.IdOf(this.root));

            Assert.That((string)report["timeline"], Is.EqualTo("Flat"));
            Assert.That((int)report["trackCount"], Is.EqualTo(1));

            var reported = report["tracks"][0];
            Assert.That((string)reported["name"], Is.EqualTo("Shown"));
            Assert.That((string)reported["type"], Is.EqualTo("ActivationTrack"));

            var reportedClip = reported["clips"][0];
            Assert.That((double)reportedClip["start"], Is.EqualTo(2).Within(1e-6));
            Assert.That((double)reportedClip["end"], Is.EqualTo(5).Within(1e-6));

            Assert.That(director.playableAsset, Is.SameAs(timeline));
        }

        [Test]
        public void AControlTrackIsFollowedIntoTheTimelineItDrives()
        {
            var rootDirector = this.BuildNested();

            var report = TimelineTools.Inspect(instanceId: EntityIdCompat.IdOf(this.root), nestDepth: 2);
            var controlClip = report["tracks"][0]["clips"][0];

            Assert.That((string)controlClip["controls"], Is.EqualTo("/ChildDirector"),
                        "the exposed reference should resolve to the driven object");
            Assert.That((bool)controlClip["updatesDirector"], Is.True);
            Assert.That((string)controlClip["childTimeline"], Is.EqualTo("Child"));

            var nested = controlClip["nested"];
            Assert.That(nested, Is.Not.Null, "the child timeline is where the work actually happens");
            Assert.That((string)nested["timeline"], Is.EqualTo("Child"));
            Assert.That((string)nested["tracks"][0]["name"], Is.EqualTo("Visible"));

            var nestedClip = nested["tracks"][0]["clips"][0];
            Assert.That((double)nestedClip["start"], Is.EqualTo(1).Within(1e-6));
            Assert.That((double)nestedClip["end"], Is.EqualTo(3).Within(1e-6));

            Assert.That(rootDirector, Is.Not.Null);
        }

        [Test]
        public void NestDepthZeroNamesTheChildWithoutExpandingIt()
        {
            this.BuildNested();

            var report = TimelineTools.Inspect(instanceId: EntityIdCompat.IdOf(this.root), nestDepth: 0);
            var controlClip = report["tracks"][0]["clips"][0];

            Assert.That((string)controlClip["childTimeline"], Is.EqualTo("Child"),
                        "the child should still be identified");

            // Not expanded — but the caller is told how to get it, rather than being left to guess
            // whether the child is empty or merely out of reach.
            Assert.That(controlClip["nested"], Is.Not.Null.And.Not.InstanceOf<Newtonsoft.Json.Linq.JObject>(),
                        "the child must not be expanded once the depth is exhausted");
            Assert.That((string)controlClip["nested"], Does.Contain("nest_depth"));
        }

        [Test]
        public void EvaluatingByFrameUsesTheTimelineFrameRate()
        {
            var timeline = this.Timeline("Scrub", frameRate: 30);
            var track = timeline.CreateTrack<ActivationTrack>(null, "Shown");
            var clip = track.CreateDefaultClip();
            clip.start = 0;
            clip.duration = 5;

            this.root = new GameObject("ScrubDirector");
            var director = DirectorFor(this.root, timeline);

            var report = TimelineTools.Evaluate(instanceId: EntityIdCompat.IdOf(this.root), frame: 60);

            Assert.That((double)report["time"], Is.EqualTo(2.0).Within(1e-3), "60 frames at 30fps is 2 seconds");
            Assert.That(director.time, Is.EqualTo(2.0).Within(1e-3));
        }

        [Test]
        public void EvaluatingLeavesTheDirectorAbleToAdvanceOnItsOwn()
        {
            var timeline = this.Timeline("Restore");
            timeline.CreateTrack<ActivationTrack>(null, "Shown").CreateDefaultClip().duration = 5;

            this.root = new GameObject("RestoreDirector");
            var director = DirectorFor(this.root, timeline);
            var before = director.timeUpdateMode;

            TimelineTools.Evaluate(instanceId: EntityIdCompat.IdOf(this.root), time: 1.5);

            // Scrubbing needs Manual; leaving it there would silently stop the director advancing
            // the next time Play was pressed.
            Assert.That(director.timeUpdateMode, Is.EqualTo(before));
        }

        [Test]
        public void ADirectorWithoutATimelineIsRefused()
        {
            this.root = new GameObject("Empty");
            this.root.AddComponent<PlayableDirector>();

            var error = Assert.Throws<McpToolException>(() =>
                TimelineTools.Evaluate(instanceId: EntityIdCompat.IdOf(this.root), time: 1));

            Assert.That(error.Code, Is.EqualTo("invalid_params"));
        }
    }
}
