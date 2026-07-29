using System;
using System.IO;
using System.Linq;

using NUnit.Framework;

using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
using UnityEditor.Recorder.Timeline;

using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

using UnityMCP.Editor.Core;

using UnityObject = UnityEngine.Object;

namespace UnityMCP.Editor.Recorder.Tests
{
    /// <summary>
    /// What a Recorder track is configured to do, and whether that configuration survives being
    /// saved and loaded again.
    /// </summary>
    /// <remarks>
    /// The destination cases carry the weight here. An earlier version reported the requested
    /// absolute path back correctly and then wrote the recording into the project folder, because
    /// the path only resolved while an internal field was still null and Unity deserializes a null
    /// string as "". Nothing about the in-memory object showed it; the failure appeared one domain
    /// reload later. So these tests round-trip the settings through Unity's serializer rather than
    /// asserting on the object that was just built.
    /// </remarks>
    [TestFixture]
    internal sealed class RecorderToolsTests
    {
        private string folder;
        private GameObject director;

        [SetUp]
        public void SetUp()
        {
            // Tracks and settings become sub-assets, so the timeline has to be a real asset first.
            this.folder = "Assets/__unity_mcp_recorder_tests_" + Guid.NewGuid().ToString("N");
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

        /// <summary>A director whose timeline is an asset, which is what the tool expects to find.</summary>
        private PlayableDirector Director(string name)
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.name = name;
            AssetDatabase.CreateAsset(timeline, this.folder + "/" + name + ".playable");

            this.director = new GameObject(name + "Director");
            var playable = this.director.AddComponent<PlayableDirector>();
            playable.playableAsset = timeline;

            return playable;
        }

        private static RecorderSettings SettingsOf(PlayableDirector director)
        {
            var timeline = (TimelineAsset)director.playableAsset;

            return timeline.GetOutputTracks()
                .OfType<RecorderTrack>()
                .SelectMany(t => t.GetClips())
                .Select(c => ((RecorderClip)c.asset).settings)
                .Single();
        }

        /// <summary>
        /// Puts the settings through Unity's serializer, which is what a domain reload does. This is
        /// the step that turns an unset string into "" and broke the absolute path.
        /// </summary>
        private static T RoundTrip<T>(T settings)
            where T : RecorderSettings
        {
            var json = EditorJsonUtility.ToJson(settings);
            var reloaded = (T)ScriptableObject.CreateInstance(settings.GetType());
            EditorJsonUtility.FromJsonOverwrite(json, reloaded);

            return reloaded;
        }

        [Test]
        public void AnAbsoluteDestinationSurvivesSerialization()
        {
            var playable = this.Director("Absolute");
            var target = Path.Combine(Path.GetTempPath(), "unity-mcp-out", "Shot").Replace('\\', '/');

            RecorderTools.AddTrack(objectPath: null, instanceId: EntityIdCompat.IdOf(playable.gameObject),
                                   outputPath: target, duration: 1);

            var settings = (MovieRecorderSettings)SettingsOf(playable);
            Assert.That(settings.OutputFile, Is.EqualTo(target), "the path should be kept as asked");

            // The real failure was only visible after a reload, so that is what is asserted.
            Assert.That(RoundTrip(settings).OutputFile, Is.EqualTo(target),
                        "the directory must not be lost when the settings are loaded again");
        }

        [Test]
        public void OmittingTheDestinationWritesBesideAssets()
        {
            var playable = this.Director("Stage");

            RecorderTools.AddTrack(objectPath: null, instanceId: EntityIdCompat.IdOf(playable.gameObject),
                                   duration: 1);

            var settings = (MovieRecorderSettings)SettingsOf(playable);
            var project = Path.GetDirectoryName(Application.dataPath).Replace('\\', '/');
            var expected = project + "/Recording/Stage";

            Assert.That(settings.OutputFile, Is.EqualTo(expected), "default is a Recording folder beside Assets");
            Assert.That(RoundTrip(settings).OutputFile, Is.EqualTo(expected));
        }

        [Test]
        public void TheRequestedFormatAndSizeReachTheSettings()
        {
            var playable = this.Director("Movie");

            RecorderTools.AddTrack(objectPath: null, instanceId: EntityIdCompat.IdOf(playable.gameObject),
                                   outputPath: this.folder + "/Out", format: "webm",
                                   width: 1280, height: 720, duration: 1);

            var settings = (MovieRecorderSettings)SettingsOf(playable);

            Assert.That(settings.OutputFormat, Is.EqualTo(MovieRecorderSettings.VideoRecorderOutputFormat.WebM));
            Assert.That(settings.ImageInputSettings.OutputWidth, Is.EqualTo(1280));
            Assert.That(settings.ImageInputSettings.OutputHeight, Is.EqualTo(720));
        }

        [Test]
        public void AnImageSequenceUsesTheImageRecorder()
        {
            var playable = this.Director("Frames");

            RecorderTools.AddTrack(objectPath: null, instanceId: EntityIdCompat.IdOf(playable.gameObject),
                                   outputPath: this.folder + "/Frame", type: "png",
                                   width: 640, height: 480, duration: 1);

            var settings = SettingsOf(playable);
            Assert.That(settings, Is.InstanceOf<ImageRecorderSettings>());

            // Movie and image settings spell their input property differently; both must be set.
            var image = (ImageRecorderSettings)settings;
            Assert.That(image.OutputFormat, Is.EqualTo(ImageRecorderSettings.ImageRecorderOutputFormat.PNG));
            Assert.That(image.imageInputSettings.OutputWidth, Is.EqualTo(640));
        }

        [Test]
        public void ACameraSourceRecordsThatCameraRatherThanTheGameView()
        {
            var playable = this.Director("Camera");

            RecorderTools.AddTrack(objectPath: null, instanceId: EntityIdCompat.IdOf(playable.gameObject),
                                   outputPath: this.folder + "/Cam", source: "tagged_camera",
                                   cameraTag: "MainCamera", duration: 1);

            var settings = (MovieRecorderSettings)SettingsOf(playable);
            var input = settings.ImageInputSettings as CameraInputSettings;

            Assert.That(input, Is.Not.Null, "a camera source should not leave the game-view input in place");
            Assert.That(input.CameraTag, Is.EqualTo("MainCamera"));
        }

        [Test]
        public void TheClipCoversTheTimelineWhenNoDurationIsGiven()
        {
            var playable = this.Director("Length");
            var timeline = (TimelineAsset)playable.playableAsset;

            // A track with a clip is what gives the timeline a duration to cover.
            var group = timeline.CreateTrack<GroupTrack>(null, "Group");
            Assert.That(group, Is.Not.Null);

            RecorderTools.AddTrack(objectPath: null, instanceId: EntityIdCompat.IdOf(playable.gameObject),
                                   outputPath: this.folder + "/Cover");

            var clip = timeline.GetOutputTracks().OfType<RecorderTrack>().Single().GetClips().Single();
            Assert.That(clip.duration, Is.GreaterThan(0), "a zero-length recording would capture nothing");
        }

        [Test]
        public void ListReportsWhatWillBeRecordedAndWhere()
        {
            var playable = this.Director("Listed");
            var target = this.folder + "/Listed";

            RecorderTools.AddTrack(objectPath: null, instanceId: EntityIdCompat.IdOf(playable.gameObject),
                                   outputPath: target, duration: 2);

            var listed = RecorderTools.List(objectPath: null, instanceId: EntityIdCompat.IdOf(playable.gameObject));

            Assert.That((int)listed["count"], Is.EqualTo(1));

            var clip = listed["recorderClips"][0];
            Assert.That((string)clip["outputFile"], Is.EqualTo(target));
            Assert.That((bool)clip["enabled"], Is.True);
            Assert.That((double)clip["duration"], Is.EqualTo(2).Within(1e-6));
        }

        [Test]
        public void AnUnknownTypeIsRefusedRatherThanRecordedWrongly()
        {
            var playable = this.Director("Bad");

            var error = Assert.Throws<McpToolException>(() =>
                RecorderTools.AddTrack(objectPath: null, instanceId: EntityIdCompat.IdOf(playable.gameObject),
                                       outputPath: this.folder + "/Bad", type: "avi"));

            Assert.That(error.Code, Is.EqualTo("invalid_params"));
            Assert.That(error.Message, Does.Contain("avi"));
        }

        [Test]
        public void ADirectorWithoutATimelineIsRefused()
        {
            this.director = new GameObject("Empty");
            var playable = this.director.AddComponent<PlayableDirector>();

            var error = Assert.Throws<McpToolException>(() =>
                RecorderTools.AddTrack(objectPath: null, instanceId: EntityIdCompat.IdOf(playable.gameObject),
                                       outputPath: this.folder + "/None"));

            Assert.That(error.Code, Is.EqualTo("invalid_params"));
        }
    }
}
