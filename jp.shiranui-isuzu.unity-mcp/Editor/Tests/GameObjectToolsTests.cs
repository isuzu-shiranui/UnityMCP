using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityEngine;

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
    }
}
