using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// A reference whose target went away keeps its identifier and loses the object; a field
    /// nobody filled in has neither.
    /// </summary>
    /// <remarks>
    /// Telling those two apart is the whole tool. Testing the object alone reports every empty
    /// slot in the scene as damage, which is the same as reporting nothing.
    /// </remarks>
    [TestFixture]
    internal sealed class BrokenReferenceToolsTests
    {
        private const string RootName = "BrokenReferenceProbe";

        private GameObject root;

        [SetUp]
        public void Plant()
        {
            this.root = new GameObject(RootName);

            var damaged = new GameObject("Damaged");
            damaged.transform.SetParent(this.root.transform);
            damaged.AddComponent<BoxCollider>();
            var joint = damaged.AddComponent<HingeJoint>();

            var anchor = new GameObject("Anchor");
            anchor.transform.SetParent(this.root.transform);
            var body = anchor.AddComponent<Rigidbody>();
            body.isKinematic = true;
            joint.connectedBody = body;

            // The reference is real until this line, which is what leaves the identifier behind.
            Object.DestroyImmediate(anchor);

            var untouched = new GameObject("NeverSet");
            untouched.transform.SetParent(this.root.transform);
            untouched.AddComponent<BoxCollider>();
            untouched.AddComponent<HingeJoint>();
        }

        [TearDown]
        public void Clear()
        {
            if (this.root != null)
            {
                Object.DestroyImmediate(this.root);
            }
        }

        [Test]
        public void AReferenceWhoseTargetWentAwayIsReported()
        {
            var broken = Findings();

            Assert.That(Where(broken, RootName + "/Damaged"), Is.Not.Null,
                "the joint lost its connected body");
            Assert.That(Where(broken, RootName + "/Damaged")["property"].ToString(),
                Is.EqualTo("m_ConnectedBody"));
        }

        [Test]
        public void AFieldNobodyFilledInIsNotReported()
        {
            Assert.That(Where(Findings(), RootName + "/NeverSet"), Is.Null,
                "an empty slot is not damage, and reporting it would bury the one that is");
        }

        [Test]
        public void TheReportSaysWhenItStoppedShort()
        {
            var result = BrokenReferenceTools.BrokenReferences("scene", "Assets", limit: 1);

            Assert.That(result["stoppedBecause"], Is.Not.Null,
                "a short list from a walk that stopped and one from a clean project read alike");
        }

        [Test]
        public void AnUnknownScopeIsRefusedRatherThanTreatedAsTheDefault()
        {
            Assert.That(
                Assert.Throws<McpToolException>(() => BrokenReferenceTools.BrokenReferences("everything")).Code,
                Is.EqualTo("invalid_params"));
        }

        private static JArray Findings()
        {
            return (JArray)BrokenReferenceTools.BrokenReferences("scene", "Assets", limit: 200)["broken"];
        }

        private static JObject Where(JArray broken, string suffix)
        {
            foreach (var entry in broken)
            {
                if (entry["object"]?.ToString().EndsWith(suffix) == true)
                {
                    return (JObject)entry;
                }
            }

            return null;
        }
    }
}
