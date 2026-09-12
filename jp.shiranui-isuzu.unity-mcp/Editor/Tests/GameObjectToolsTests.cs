using System.IO;
using System.Linq;

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
    /// <summary>
    /// Creating objects, and what a primitive arrives carrying.
    /// </summary>
    [TestFixture]
    internal sealed class GameObjectToolsTests
    {
        /// <summary>
        /// A primitive can be created without the collider Unity attaches to it.
        /// </summary>
        /// <remarks>
        /// A mock-up built out of primitives is there to be looked at, and each one came with a
        /// collider that then took a call of its own to remove: ten such pairs in one session.
        /// The default keeps Unity's own behaviour, so nothing that relied on the collider
        /// changes.
        /// </remarks>
        [Test]
        public void APrimitiveCanBeCreatedWithoutItsCollider()
        {
            var kept = GameObjectTools.Create(name: "ColliderKept", primitive: "Sphere");
            var dropped = GameObjectTools.Create(name: "ColliderDropped", primitive: "Sphere", collider: false);

            try
            {
                var withCollider = GameObject.Find(kept["name"].Value<string>());
                var without = GameObject.Find(dropped["name"].Value<string>());

                Assert.That(withCollider.GetComponent<Collider>(), Is.Not.Null);
                Assert.That(without.GetComponent<Collider>(), Is.Null);
                Assert.That(without.GetComponent<MeshRenderer>(), Is.Not.Null,
                    "what is left has to still be visible");
            }
            finally
            {
                Object.DestroyImmediate(GameObject.Find("ColliderKept"));
                Object.DestroyImmediate(GameObject.Find("ColliderDropped"));
            }
        }

        [Test]
        public void AnEmptyObjectIsUnaffectedByTheColliderArgument()
        {
            var made = GameObjectTools.Create(name: "NoPrimitiveHere", collider: false);

            try
            {
                var go = GameObject.Find(made["name"].Value<string>());

                Assert.That(go, Is.Not.Null);
                Assert.That(go.GetComponent<Collider>(), Is.Null);
                Assert.That(go.GetComponent<MeshRenderer>(), Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(GameObject.Find("NoPrimitiveHere"));
            }
        }

        /// <summary>
        /// A call that is refused leaves nothing behind.
        /// </summary>
        /// <remarks>
        /// The parent and the vectors were checked after the object was made, so a call answered
        /// with an error still put a Cube in the scene.
        /// </remarks>
        [Test]
        public void ARefusedCreateLeavesNoObjectBehind()
        {
            Assert.Throws<McpToolException>(() => GameObjectTools.Create(
                name: "RefusedForItsParent", primitive: "Cube", parentPath: "/NoSuchParentAnywhere"));

            Assert.Throws<McpToolException>(() => GameObjectTools.Create(
                name: "RefusedForItsPosition", primitive: "Cube", position: new JObject { ["x"] = "a" }));

            Assert.That(GameObject.Find("RefusedForItsParent"), Is.Null);
            Assert.That(GameObject.Find("RefusedForItsPosition"), Is.Null);
        }

        /// <summary>
        /// A scale and an active state set on a prefab instance are still there after the scene
        /// is saved and opened again.
        /// </summary>
        /// <remarks>
        /// A scene stores a prefab instance as its differences from the asset. A change that is
        /// not recorded as one of those differences is saved as nothing, and the scene opens
        /// with the asset's value. The tools run through <see cref="ToolInvoker"/>, which is
        /// what opens and collapses their undo group.
        /// </remarks>
        [Test]
        public void ChangesToAPrefabInstanceSurviveSavingAndReopeningTheScene()
        {
            var folder = "Assets/__McpPrefabOverride_" + System.Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));

            try
            {
                var source = new GameObject("PrefabOverrideFixture");
                var prefab = PrefabUtility.SaveAsPrefabAsset(source, folder + "/Fixture.prefab");
                Object.DestroyImmediate(source);

                var scenePath = folder + "/Scene.unity";
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                Assert.That(EditorSceneManager.SaveScene(scene, scenePath), Is.True);

                var catalog = ToolCatalog.BuildFromTypes(new[] { typeof(GameObjectTools) });

                ToolInvoker.Invoke(
                    catalog.Tools.Single(t => t.Name == "gameobject_set_transform"),
                    new JObject
                    {
                        ["instance_id"] = EntityIdCompat.WireIdOf(instance),
                        ["scale"] = new JObject { ["x"] = 2, ["y"] = 3, ["z"] = 4 },
                    });

                ToolInvoker.Invoke(
                    catalog.Tools.Single(t => t.Name == "gameobject_set_active"),
                    new JObject
                    {
                        ["instance_id"] = EntityIdCompat.WireIdOf(instance),
                        ["active"] = false,
                    });

                Assert.That(EditorSceneManager.SaveScene(SceneManager.GetActiveScene()), Is.True);

                // Closed first, so the objects asserted on below are the ones read back from disk.
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Assert.That(instance == null, Is.True, "the saved scene was not closed");
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

                var reopened = SceneManager.GetActiveScene().GetRootGameObjects().Single();
                var modified = PrefabUtility.GetPropertyModifications(reopened)
                    .Select(m => m.propertyPath)
                    .ToArray();

                Assert.That(PrefabUtility.IsPartOfPrefabInstance(reopened), Is.True);
                Assert.That(reopened.transform.localScale, Is.EqualTo(new Vector3(2f, 3f, 4f)));
                Assert.That(reopened.activeSelf, Is.False);
                Assert.That(modified, Is.SupersetOf(new[] { "m_LocalScale.x", "m_LocalScale.y", "m_LocalScale.z", "m_IsActive" }));
            }
            finally
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
