using System;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor.Core;
using UnityMCP.Editor.Tools;
using Object = UnityEngine.Object;
#if UNITY_6000_0_OR_NEWER
using SurfaceMaterial = UnityEngine.PhysicsMaterial;
#else
using SurfaceMaterial = UnityEngine.PhysicMaterial;
#endif

namespace UnityMCP.Editor.Tests
{
    internal sealed class InspectorAssetTests
    {
        private string folder;
        private GameObject root;
        private GameObject instance;
        [SetUp] public void SetUp()
        {
            folder = "Assets/InspectorAsset_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            root = new GameObject("AssetRoot");
        }
        [TearDown] public void TearDown()
        {
            if (instance != null) Object.DestroyImmediate(instance);
            if (root != null) Object.DestroyImmediate(root);
            AssetDatabase.DeleteAsset(folder);
        }
        [TestCase(false, "m_IsTrigger", "m_Center.x")]
        [TestCase(true, "m_IsTrigger", "m_Center.x")]
        [TestCase(false, "isTrigger", "center.x")]
        [TestCase(true, "isTrigger", "center.x")]
        public void PrefabChildWriteSavesAndReportsSceneOverrides(bool variant, string triggerPath, string centerPath)
        {
            var child = new GameObject("Body");
            child.transform.SetParent(root.transform);
            child.AddComponent<BoxCollider>();
            var path = folder + "/Root.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            if (variant)
            {
                var variantRoot = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                path = folder + "/Variant.prefab";
                prefab = PrefabUtility.SaveAsPrefabAsset(variantRoot, path);
                Object.DestroyImmediate(variantRoot);
            }
            instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var collider = instance.GetComponentInChildren<BoxCollider>();
            collider.isTrigger = true;
            PrefabUtility.RecordPrefabInstancePropertyModifications(collider);
            var reply = InspectorTools.Write(assetPath: path, objectPath: "Body", componentType: "UnityEngine.BoxCollider",
                values: new JObject { [triggerPath] = false, [centerPath] = 3 });
            Assert.That(reply["error"], Is.Null, reply.ToString());
            Assert.That((bool)reply["saved"], Is.True);
            Assert.That((JArray)reply["overriddenBy"][triggerPath], Has.Count.EqualTo(1));
            Assert.That(collider.isTrigger, Is.True);
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentInChildren<BoxCollider>().center.x, Is.EqualTo(3));
            var read = InspectorTools.Read(centerPath, assetPath: path, objectPath: "Body");
            Assert.That((string)read["component"], Is.EqualTo("UnityEngine.BoxCollider"));
            Assert.That((float)read["property"]["value"], Is.EqualTo(3));
        }
        [Test]
        public void BasePrefabWriteReportsOverridesInheritedThroughAVariant()
        {
            root.AddComponent<BoxCollider>();
            var basePath = folder + "/Base.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, basePath);
            var variantRoot = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            variantRoot.GetComponent<BoxCollider>().isTrigger = true;
            PrefabUtility.RecordPrefabInstancePropertyModifications(variantRoot.GetComponent<BoxCollider>());
            var variant = PrefabUtility.SaveAsPrefabAsset(variantRoot, folder + "/Variant.prefab");
            Object.DestroyImmediate(variantRoot);
            instance = (GameObject)PrefabUtility.InstantiatePrefab(variant);
            var reply = InspectorTools.Write(assetPath: basePath, propertyPath: "m_IsTrigger", value: new JValue(false));
            Assert.That(reply["error"], Is.Null, reply.ToString());
            Assert.That((JArray)reply["overriddenBy"]["m_IsTrigger"], Has.Count.EqualTo(1));
            Assert.That(instance.GetComponent<BoxCollider>().isTrigger, Is.True);
        }
        [Test]
        public void MainAssetWriteIsSavedAndInvalidBatchDoesNotPartiallyApply()
        {
            var material = new SurfaceMaterial();
            var path = folder + "/Surface.physicMaterial";
            AssetDatabase.CreateAsset(material, path);
            var reply = InspectorTools.Write(assetPath: path, values: new JObject { ["m_Name"] = "EditedSurface" });
            Assert.That(reply["error"], Is.Null, reply.ToString());
            Assert.That((bool)reply["saved"], Is.True);
            reply = InspectorTools.Write(assetPath: path, values: new JObject { ["m_Name"] = "BadRename", ["missingProperty"] = 1 });
            Assert.That(reply["error"], Is.Not.Null);
            Assert.That(material.name, Is.EqualTo("EditedSurface"));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            Assert.That(AssetDatabase.LoadMainAssetAtPath(path).name, Is.EqualTo("EditedSurface"));
            Assert.That(InspectorTools.List(assetPath: path)["properties"], Is.Not.Null);
        }
        [Test]
        public void InferenceRefusesAmbiguousOrSplitComponentProperties()
        {
            root.AddComponent<BoxCollider>();
            root.AddComponent<SphereCollider>();
            Assert.Throws<McpToolException>(() => InspectorTools.Read("m_IsTrigger", objectPath: "/AssetRoot"));
            Assert.Throws<McpToolException>(() => InspectorTools.Write(objectPath: "/AssetRoot",
                values: new JObject { ["m_Size.x"] = 3, ["m_Radius"] = 4 }));
            Assert.That(root.GetComponent<BoxCollider>().size.x, Is.EqualTo(1));
        }
    }
}
