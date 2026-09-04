using System.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Reading and writing the materials a renderer draws with, addressed by scene path.
    /// </summary>
    /// <remarks>
    /// Two properties carry the weight. A material whose shader could not be loaded has to be
    /// named as such, because that is the state behind every magenta object and the reason
    /// anyone asks. And writing through a renderer has to reach the shared material: a write
    /// that went through <c>Renderer.material</c> would silently create a copy belonging to no
    /// asset, which survives into the scene file and cannot be found again by asset path.
    /// </remarks>
    [TestFixture]
    internal sealed class MaterialSlotsTests
    {
        private GameObject root;
        private Material good;
        private Material broken;

        [SetUp]
        public void SetUp()
        {
            var unlit = Shader.Find("Unlit/Color");
            var errorShader = Shader.Find("Hidden/InternalErrorShader");

            Assert.That(unlit, Is.Not.Null, "Unlit/Color is a built-in shader and should always resolve");
            Assert.That(errorShader, Is.Not.Null, "Hidden/InternalErrorShader is what Unity substitutes for a shader it cannot load");

            this.good = new Material(unlit) { name = "GoodMaterial" };
            this.broken = new Material(errorShader) { name = "BrokenMaterial" };

            this.root = new GameObject("MaterialSlotsRoot", typeof(MeshFilter), typeof(MeshRenderer));
            this.root.GetComponent<MeshRenderer>().sharedMaterials = new[] { this.good, this.broken };
        }

        [TearDown]
        public void TearDown()
        {
            if (this.root != null)
            {
                Object.DestroyImmediate(this.root);
            }

            if (this.good != null)
            {
                Object.DestroyImmediate(this.good);
            }

            if (this.broken != null)
            {
                Object.DestroyImmediate(this.broken);
            }
        }

        private static JArray Slots(JObject result)
        {
            return (JArray)result["slots"];
        }

        [Test]
        public void ReportsEverySlotWithItsIndexNameAndShader()
        {
            var result = ShaderTools.MaterialRead(objectPath: "/MaterialSlotsRoot");
            var slots = Slots(result);

            Assert.That(result["renderer"].Value<string>(), Is.EqualTo("MeshRenderer"));
            Assert.That(result["slotCount"].Value<int>(), Is.EqualTo(2));
            Assert.That(slots.Count, Is.EqualTo(2));

            Assert.That(slots[0]["slot"].Value<int>(), Is.EqualTo(0));
            Assert.That(slots[0]["name"].Value<string>(), Is.EqualTo("GoodMaterial"));
            Assert.That(slots[0]["shader"].Value<string>(), Is.EqualTo("Unlit/Color"));

            Assert.That(slots[1]["slot"].Value<int>(), Is.EqualTo(1));
            Assert.That(slots[1]["name"].Value<string>(), Is.EqualTo("BrokenMaterial"));
            Assert.That(slots[1]["shader"].Value<string>(), Is.EqualTo("Hidden/InternalErrorShader"));
        }

        [Test]
        public void NamesTheSlotWhoseShaderCouldNotBeLoaded()
        {
            var result = ShaderTools.MaterialRead(objectPath: "/MaterialSlotsRoot");

            Assert.That(result["brokenSlots"].Values<int>().ToArray(), Is.EqualTo(new[] { 1 }));
            Assert.That(result["shaderProblem"].Value<string>(), Does.Contain("magenta"));

            var problem = Slots(result)[1]["shaderProblem"].Value<string>();

            Assert.That(problem, Does.Contain("Hidden/InternalErrorShader"));
            Assert.That(problem, Does.Contain("magenta"));
        }

        [Test]
        public void ASlotThatCanDrawHasNoProblem()
        {
            var slot = Slots(ShaderTools.MaterialRead(objectPath: "/MaterialSlotsRoot"))[0];

            Assert.That(slot["shaderProblem"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(slot["shaderIsSupported"].Value<bool>(), Is.True);
        }

        [Test]
        public void ReportsAMaterialThatIsNotAnAssetRatherThanSkippingIt()
        {
            var slot = Slots(ShaderTools.MaterialRead(objectPath: "/MaterialSlotsRoot"))[0];

            Assert.That(slot["isAsset"].Value<bool>(), Is.False);
            Assert.That(slot["path"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(slot["note"].Value<string>(), Does.Contain("not an asset"));
        }

        [Test]
        public void SlotNarrowsToOneEntryAndKeepsItsIndex()
        {
            var result = ShaderTools.MaterialRead(objectPath: "/MaterialSlotsRoot", slot: 1);
            var slots = Slots(result);

            Assert.That(result["slotCount"].Value<int>(), Is.EqualTo(2), "the renderer still has two");
            Assert.That(slots.Count, Is.EqualTo(1));
            Assert.That(slots[0]["slot"].Value<int>(), Is.EqualTo(1));
        }

        [Test]
        public void ReadingEverySlotCountsThePropertiesInsteadOfListingThem()
        {
            var slot = Slots(ShaderTools.MaterialRead(objectPath: "/MaterialSlotsRoot"))[0];

            Assert.That(slot["properties"], Is.Null, "a renderer full of lilToon materials would bury the answer");
            Assert.That(slot["propertyCount"].Value<int>(), Is.GreaterThan(0), "Unlit/Color declares _Color");
        }

        [Test]
        public void NamingASlotReturnsThatMaterialsPropertyValues()
        {
            var slot = Slots(ShaderTools.MaterialRead(objectPath: "/MaterialSlotsRoot", slot: 0))[0];

            Assert.That(slot["propertyCount"], Is.Null);
            Assert.That(slot["properties"].Any(), Is.True);
            Assert.That(
                slot["properties"].Any(p => p["name"].Value<string>() == "_Color"), Is.True,
                "Unlit/Color declares _Color");
        }

        [Test]
        public void RefusesAPathAndAnObjectPathTogether()
        {
            Assert.That(
                () => ShaderTools.MaterialRead("Assets/Nothing.mat", "/MaterialSlotsRoot"),
                Throws.TypeOf<McpToolException>());
        }

        [Test]
        public void RefusesASlotThatDoesNotExist()
        {
            Assert.That(
                () => ShaderTools.MaterialRead(objectPath: "/MaterialSlotsRoot", slot: 5),
                Throws.TypeOf<McpToolException>());
        }

        [Test]
        public void RefusesAnObjectWithNoRenderer()
        {
            var bare = new GameObject("MaterialSlotsBare");

            try
            {
                Assert.That(
                    () => ShaderTools.MaterialRead(objectPath: "/MaterialSlotsBare"),
                    Throws.TypeOf<McpToolException>());
            }
            finally
            {
                Object.DestroyImmediate(bare);
            }
        }

        [Test]
        public void WritingRefusesAnOmittedSlotWhenThereIsMoreThanOne()
        {
            Assert.That(
                () => ShaderTools.MaterialSet(
                    objectPath: "/MaterialSlotsRoot", property: "_Color", value: new JArray(1, 0, 0, 1)),
                Throws.TypeOf<McpToolException>(),
                "writing to two materials because the slot was left out is the surprise this avoids");
        }

        [Test]
        public void WritingThroughARendererChangesTheSharedMaterialAndInstantiatesNothing()
        {
            var result = ShaderTools.MaterialSet(
                objectPath: "/MaterialSlotsRoot",
                slot: 0,
                property: "_Color",
                value: new JArray(1f, 0f, 0f, 1f));

            var after = this.root.GetComponent<MeshRenderer>().sharedMaterials;

            Assert.That(after.Length, Is.EqualTo(2));
            Assert.That(after[0], Is.SameAs(this.good), "a per-renderer copy would break the link to the asset");
            Assert.That(this.good.GetColor("_Color"), Is.EqualTo(new Color(1f, 0f, 0f, 1f)));

            Assert.That(result["slot"].Value<int>(), Is.EqualTo(0));
            Assert.That(result["savedToDisk"].Value<bool>(), Is.False, "this material has no .mat file to write");
            Assert.That(result["notes"].Values<string>().Any(n => n.Contains("shared material")), Is.True);
        }
    }
}
