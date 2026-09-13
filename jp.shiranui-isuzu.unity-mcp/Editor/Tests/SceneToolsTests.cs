using System;
using System.IO;
using System.Linq;

using NUnit.Framework;

using UnityEditor;
using UnityEditor.SceneManagement;

using UnityEngine;
using UnityEngine.SceneManagement;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// A refused scene_create leaves the open scenes and the files on disk as they were.
    /// </summary>
    [TestFixture]
    internal sealed class SceneToolsTests
    {
        private string folder;

        private string openPath;

        [SetUp]
        public void SetUp()
        {
            this.folder = "Assets/__McpSceneTools_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(this.folder));

            // scene_create refuses first while an open scene has unsaved changes. Each test starts
            // from a saved scene of its own, so the path is the only reason left to refuse.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("SceneToolsMarker");
            this.openPath = this.folder + "/Open.unity";
            Assert.That(EditorSceneManager.SaveScene(scene, this.openPath), Is.True);
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(this.folder);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AnExistingSceneIsNotWrittenOver(bool additive)
        {
            var target = this.folder + "/Existing.unity";
            Assert.That(AssetDatabase.CopyAsset(this.openPath, target), Is.True);
            var contents = File.ReadAllBytes(target);

            var refusal = Assert.Throws<McpToolException>(() => SceneTools.Create(path: target, additive: additive));

            Assert.That(refusal.Code, Is.EqualTo("conflict"));
            Assert.That(refusal.HttpStatus, Is.EqualTo(409));
            Assert.That(refusal.Message, Does.Contain("scene_open"));
            Assert.That(File.ReadAllBytes(target), Is.EqualTo(contents), "the scene at the path was written over");
            this.AssertTheOpenSceneIsUntouched();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AMissingFolderIsRefusedBeforeAnySceneIsCreated(bool additive)
        {
            var refusal = Assert.Throws<McpToolException>(
                () => SceneTools.Create(path: this.folder + "/Missing/New.unity", additive: additive));

            Assert.That(refusal.Code, Is.EqualTo("not_found"));
            this.AssertTheOpenSceneIsUntouched();
        }

        private void AssertTheOpenSceneIsUntouched()
        {
            var active = SceneManager.GetActiveScene();

            Assert.That(SceneManager.sceneCount, Is.EqualTo(1), "a scene was added before the refusal");
            Assert.That(active.path, Is.EqualTo(this.openPath), "the open scene was replaced before the refusal");
            Assert.That(active.isDirty, Is.False);
            Assert.That(active.GetRootGameObjects().Select(g => g.name), Is.EqualTo(new[] { "SceneToolsMarker" }));
        }
    }
}
