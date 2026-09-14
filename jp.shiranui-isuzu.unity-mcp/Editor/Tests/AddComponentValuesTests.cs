using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor.Core;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    internal sealed class AddComponentValuesTests
    {
        [Test]
        public void InvalidValueRevertsRequiredComponentsAndPreservesEarlierEdits()
        {
            var go = new GameObject("AddValues");
            try
            {
                Undo.IncrementCurrentGroup();
                Undo.RecordObject(go, "Earlier edit");
                go.name = "AddValuesEdited";
                Undo.FlushUndoRecordObjects();
                var error = Assert.Throws<McpToolException>(() => GameObjectTools.AddComponent(objectPath: "/AddValuesEdited", componentType: "HingeJoint",
                    values: new JObject { ["missingProperty"] = 1 }));
                Assert.That(error.Message, Does.Contain("missingProperty"));
                Assert.That(go.GetComponent<HingeJoint>(), Is.Null);
                Assert.That(go.GetComponent<Rigidbody>(), Is.Null);
                Assert.That(go.name, Is.EqualTo("AddValuesEdited"));
            }
            finally { Object.DestroyImmediate(go); }
        }
        [TestCase("m_IsTrigger")]
        [TestCase("isTrigger")]
        public void ValuesAreReturnedAndOneUndoRemovesTheAdd(string propertyPath)
        {
            var go = new GameObject("AddValues");
            try
            {
                var result = GameObjectTools.AddComponent(objectPath: "/AddValues", componentType: "BoxCollider",
                    values: new JObject { [propertyPath] = true });
                Assert.That(go.GetComponent<BoxCollider>().isTrigger, Is.True);
                Assert.That((bool)result["written"][propertyPath], Is.True);
                Undo.PerformUndo();
                Assert.That(go.GetComponent<BoxCollider>(), Is.Null);
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
