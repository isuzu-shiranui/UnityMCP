using System;
using System.IO;
using System.Linq;

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
    /// Building a timeline from nothing, and the nesting a Control clip creates.
    /// </summary>
    /// <remarks>
    /// The persistence cases are the reason these tools exist rather than being left to
    /// execute_code. Timeline writes a track into the .playable only when the timeline is already an
    /// asset, and offers nothing to fix it afterwards, so a timeline built in the wrong order looks
    /// entirely correct until the next domain reload discards it. These tests assert the file's
    /// contents rather than the in-memory object, because the in-memory object is exactly what looks
    /// right in the broken case.
    /// </remarks>
    [TestFixture]
    internal sealed class TimelineCreateToolsTests
    {
        private string folder;
        private GameObject director;
        private GameObject child;

        [SetUp]
        public void SetUp()
        {
            this.folder = "Assets/__unity_mcp_create_tests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", System.IO.Path.GetFileName(this.folder));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in new[] { this.director, this.child })
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

        private long Id => EntityIdCompat.IdOf(this.director);

        private string Path(string name) => this.folder + "/" + name + ".playable";

        /// <summary>A director created through the tool, which is the supported starting point.</summary>
        private PlayableDirector Stage(string name = "Stage")
        {
            this.director = new GameObject(name + "Director");

            TimelineCreateTools.Create(
                assetPath: this.Path(name), frameRate: 30,
                instanceId: EntityIdCompat.IdOf(this.director));

            return this.director.GetComponent<PlayableDirector>();
        }

        [Test]
        public void CreatingATimelineWritesItAndAttachesADirector()
        {
            var playable = this.Stage();

            Assert.That(playable, Is.Not.Null, "the GameObject should have been given a director");
            Assert.That(AssetDatabase.LoadAssetAtPath<TimelineAsset>(this.Path("Stage")), Is.Not.Null);
            Assert.That(((TimelineAsset)playable.playableAsset).editorSettings.frameRate,
                        Is.EqualTo(30).Within(1e-4));
        }

        [Test]
        public void TracksCreatedThroughTheToolSurviveInTheFile()
        {
            var playable = this.Stage();

            TimelineCreateTools.CreateTrack(instanceId: this.Id, type: "activation", name: "Shots");
            AssetDatabase.SaveAssets();

            // Loading the file again is the only check that distinguishes a persisted track from one
            // that exists only until the next reload.
            var stored = AssetDatabase.LoadAllAssetsAtPath(this.Path("Stage"))
                .OfType<ActivationTrack>()
                .SingleOrDefault();

            Assert.That(stored, Is.Not.Null, "the track was not written into the .playable");
            Assert.That(stored.name, Is.EqualTo("Shots"));
        }

        [Test]
        public void AddingATrackToAnUnsavedTimelineIsRefused()
        {
            // The failure this refusal prevents is silent: Timeline accepts the track, reports it,
            // and drops it at the next domain reload.
            var loose = ScriptableObject.CreateInstance<TimelineAsset>();
            loose.name = "Unsaved";

            this.director = new GameObject("LooseDirector");
            this.director.AddComponent<PlayableDirector>().playableAsset = loose;

            var error = Assert.Throws<McpToolException>(() =>
                TimelineCreateTools.CreateTrack(instanceId: this.Id, type: "activation", name: "Shots"));

            Assert.That(error.Code, Is.EqualTo("invalid_params"));
            Assert.That(error.Message, Does.Contain("timeline_create"));

            UnityObject.DestroyImmediate(loose);
        }

        [Test]
        public void ATrackCanBeNestedInsideAGroup()
        {
            this.Stage();

            TimelineCreateTools.CreateTrack(instanceId: this.Id, type: "group", name: "Cameras");
            var result = TimelineCreateTools.CreateTrack(
                instanceId: this.Id, type: "activation", name: "Front", parent: "Cameras");

            Assert.That((string)result["track"], Is.EqualTo("Cameras/Front"));
        }

        [Test]
        public void ANonGroupParentIsRefused()
        {
            this.Stage();
            TimelineCreateTools.CreateTrack(instanceId: this.Id, type: "activation", name: "Shots");

            var error = Assert.Throws<McpToolException>(() => TimelineCreateTools.CreateTrack(
                instanceId: this.Id, type: "activation", name: "Nested", parent: "Shots"));

            Assert.That(error.Code, Is.EqualTo("invalid_params"));
        }

        [Test]
        public void AnUnknownTrackTypeListsTheOnesThatWork()
        {
            this.Stage();

            var error = Assert.Throws<McpToolException>(() =>
                TimelineCreateTools.CreateTrack(instanceId: this.Id, type: "sparkle", name: "X"));

            Assert.That(error.Code, Is.EqualTo("invalid_params"));
            Assert.That(error.Message, Does.Contain("activation"));
        }

        [Test]
        public void ACreatedClipLandsWhereItWasAsked()
        {
            this.Stage();
            TimelineCreateTools.CreateTrack(instanceId: this.Id, type: "activation", name: "Shots");

            var result = TimelineCreateTools.CreateClip(
                instanceId: this.Id, track: "Shots", start: 2, duration: 3, displayName: "Wide");

            Assert.That((string)result["clip"], Is.EqualTo("Wide"));
            Assert.That((double)result["start"], Is.EqualTo(2).Within(1e-4));
            Assert.That((double)result["duration"], Is.EqualTo(3).Within(1e-4));
            Assert.That((double)result["end"], Is.EqualTo(5).Within(1e-4));
        }

        [Test]
        public void AClipOnAGroupTrackIsRefused()
        {
            this.Stage();
            TimelineCreateTools.CreateTrack(instanceId: this.Id, type: "group", name: "Cameras");

            var error = Assert.Throws<McpToolException>(() =>
                TimelineCreateTools.CreateClip(instanceId: this.Id, track: "Cameras"));

            Assert.That(error.Code, Is.EqualTo("invalid_params"));
        }

        [Test]
        public void AControlClipDrivesTheChildDirectorAndInspectSeesTheNesting()
        {
            // The child first: a timeline of its own, on its own object.
            this.child = new GameObject("ChildDirector");

            TimelineCreateTools.Create(
                assetPath: this.Path("Child"), frameRate: 30,
                instanceId: EntityIdCompat.IdOf(this.child));

            var childDirector = this.child.GetComponent<PlayableDirector>();
            var childTimeline = (TimelineAsset)childDirector.playableAsset;
            var childTrack = childTimeline.CreateTrack<ActivationTrack>(null, "Visible");
            var childClip = childTrack.CreateDefaultClip();
            childClip.start = 0;
            childClip.duration = 2;

            // Then the root, whose control clip points at the child.
            this.director = new GameObject("RootDirector");

            TimelineCreateTools.Create(
                assetPath: this.Path("Root"), frameRate: 30,
                instanceId: EntityIdCompat.IdOf(this.director));

            TimelineCreateTools.CreateTrack(instanceId: this.Id, type: "control", name: "Drive");

            var created = TimelineCreateTools.CreateClip(
                instanceId: this.Id, track: "Drive", start: 0, duration: 2,
                displayName: "Run Child", controlSource: ObjectResolve.PathOf(this.child));

            Assert.That((string)created["controls"], Is.EqualTo("/ChildDirector"));

            // Read back through the inspect tool, which resolves the ExposedReference against the
            // director exactly as playback does.
            var report = TimelineTools.Inspect(instanceId: this.Id, nestDepth: 2);
            var clip = report["tracks"][0]["clips"][0];

            Assert.That((string)clip["controls"], Is.EqualTo("/ChildDirector"));
            Assert.That((bool)clip["updatesDirector"], Is.True, "without this the child never plays");
            Assert.That((string)clip["childTimeline"], Is.EqualTo("Child"));
            Assert.That((string)clip["nested"]["tracks"][0]["name"], Is.EqualTo("Visible"));
        }

        [Test]
        public void AControlSourceOnAnOrdinaryClipIsRefused()
        {
            this.Stage();
            TimelineCreateTools.CreateTrack(instanceId: this.Id, type: "activation", name: "Shots");
            this.child = new GameObject("Other");

            var error = Assert.Throws<McpToolException>(() => TimelineCreateTools.CreateClip(
                instanceId: this.Id, track: "Shots", controlSource: ObjectResolve.PathOf(this.child)));

            Assert.That(error.Code, Is.EqualTo("invalid_params"));
        }

        [Test]
        public void WritingOverAnExistingTimelineIsRefused()
        {
            this.Stage();

            var error = Assert.Throws<McpToolException>(() =>
                TimelineCreateTools.Create(assetPath: this.Path("Stage")));

            Assert.That(error.Code, Is.EqualTo("conflict"));
        }

        [Test]
        public void APathOutsideAssetsIsRefused()
        {
            var error = Assert.Throws<McpToolException>(() =>
                TimelineCreateTools.Create(assetPath: "C:/Temp/Stage.playable"));

            Assert.That(error.Code, Is.EqualTo("invalid_params"));
        }

        [Test]
        public void AMissingFolderSaysHowToMakeIt()
        {
            var error = Assert.Throws<McpToolException>(() =>
                TimelineCreateTools.Create(assetPath: this.folder + "/Nope/Stage.playable"));

            Assert.That(error.Code, Is.EqualTo("not_found"));
            Assert.That(error.Message, Does.Contain("asset_create_folder"));
        }
    }
}
