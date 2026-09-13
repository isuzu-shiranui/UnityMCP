using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityMCP.Editor.Core;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    [TestFixture]
    internal sealed class PrefabValidationTests
    {
        /// <summary>
        /// A file Unity has not imported yet loads as nothing, so the check for an existing prefab
        /// passes it by. 'overwrite' speaks for a prefab at the path, not for whatever else is there.
        /// </summary>
        [Test]
        public void AFileUnityHasNotImportedIsNotWrittenOverEvenWithOverwrite()
        {
            var folder = "Assets/__McpPrefabValidation_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            var preview = EditorSceneManager.NewPreviewScene();
            var source = new GameObject("PrefabValidationSource");
            SceneManager.MoveGameObjectToScene(source, preview);

            try
            {
                var path = folder + "/Existing.prefab";
                File.WriteAllText(path, "not a prefab");

                Assert.Throws<McpToolException>(() => PrefabTools.Create(
                    instanceId: EntityIdCompat.IdOf(source), path: path, connect: false, overwrite: true));

                Assert.That(File.ReadAllText(path), Is.EqualTo("not a prefab"));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InvalidVectorDoesNotLeaveAPrefabInstance(bool invalidRotation)
        {
            var folder = "Assets/__McpPrefabValidation_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            var preview = EditorSceneManager.NewPreviewScene();
            var source = new GameObject("PrefabValidationSource");
            SceneManager.MoveGameObjectToScene(source, preview);
            try
            {
                var path = folder + "/Fixture.prefab";
                PrefabUtility.SaveAsPrefabAsset(source, path);
                var count = UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>().Length;
                var selection = Selection.activeObject;
                var bad = new JObject { ["x"] = "bad" };
                Assert.Throws<McpToolException>(() => PrefabTools.Instantiate(path,
                    position: invalidRotation ? new JObject { ["x"] = 5 } : bad,
                    rotation: invalidRotation ? bad : null));
                Assert.That(UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>().Length, Is.EqualTo(count));
                Assert.That(Selection.activeObject, Is.EqualTo(selection));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
