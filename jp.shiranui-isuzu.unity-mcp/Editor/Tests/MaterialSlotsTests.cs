using System.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityEditor;

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

        /// <summary>
        /// A toon shader declares several hundred properties, so reading them all to check one
        /// costs about a hundred times what the answer needs. The filter matches a substring
        /// because a caller after "_Color" cannot know whether the shader spelled it
        /// "_ShadowColor" or "_Color2nd".
        /// </summary>
        /// <summary>
        /// Making a material had no tool, and the Assets/Create menu leaves one unnamed in
        /// whichever folder the Project window is showing, finishing only once the rename field
        /// is dismissed.
        /// </summary>
        [Test]
        public void AMaterialIsCreatedWhereItWasAskedFor()
        {
            const string folder = "Assets/_McpMaterialCreateTests";
            const string path = folder + "/Nested/Made.mat";

            try
            {
                var made = ShaderTools.MaterialCreate(path);

                Assert.That(made["created"].Value<bool>(), Is.True);
                Assert.That(made["path"].ToString(), Is.EqualTo(path), "the folders were created");
                Assert.That(AssetDatabase.LoadAssetAtPath<Material>(path), Is.Not.Null);

                // A second one at the same path is a mistake worth naming, not a silent overwrite.
                var thrown = Assert.Throws<McpToolException>(() => ShaderTools.MaterialCreate(path));

                Assert.That(thrown.Message, Does.Contain("overwrite"));
                Assert.That(ShaderTools.MaterialCreate(path, overwrite: true)["created"].Value<bool>(),
                    Is.True);
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
            }
        }

        /// <summary>The extension is what the AssetDatabase needs, not what a caller remembers.</summary>
        [Test]
        public void APathWithoutAnExtensionStillMakesAMaterial()
        {
            const string folder = "Assets/_McpMaterialCreateTests";

            try
            {
                var made = ShaderTools.MaterialCreate(folder + "/NoExtension");

                Assert.That(made["path"].ToString(), Does.EndWith(".mat"));
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void AShaderThatDoesNotExistIsRefusedRatherThanSubstituted()
        {
            var thrown = Assert.Throws<McpToolException>(
                () => ShaderTools.MaterialCreate("Assets/_McpMaterialCreateTests/Bad.mat", "NoSuchShader"));

            Assert.That(thrown.Message, Does.Contain("shader_info"));
            Assert.That(AssetDatabase.IsValidFolder("Assets/_McpMaterialCreateTests"), Is.False,
                "and nothing was left behind");
        }

        [Test]
        public void ReadingOnePropertyDoesNotReturnThemAll()
        {
            // Standard rather than the fixture's Unlit/Color: a shader with one property has
            // nothing for a filter to leave out.
            var many = new GameObject("MaterialFilterTarget");
            many.AddComponent<MeshRenderer>().sharedMaterial =
                new Material(Shader.Find("Standard")) { name = "ManyProperties" };

            try
            {
                var all = Slots(ShaderTools.MaterialRead(objectPath: "/MaterialFilterTarget", slot: 0))[0];
                var narrowed = Slots(ShaderTools.MaterialRead(
                    objectPath: "/MaterialFilterTarget", slot: 0, property: "_Color"))[0];

                var everything = ((JArray)all["properties"]).Count;
                var matched = ((JArray)narrowed["properties"]).Count;

                Assert.That(everything, Is.GreaterThan(1), "Standard declares many properties");
                Assert.That(matched, Is.LessThan(everything), "the filter has to leave something out");
                Assert.That(matched, Is.GreaterThan(0), "Standard declares a _Color");
                Assert.That(narrowed["propertyCount"].Value<int>(), Is.EqualTo(everything),
                    "a narrowed reply still says what it narrowed from");

                foreach (JObject property in (JArray)narrowed["properties"])
                {
                    Assert.That(property["name"].ToString().ToLowerInvariant(), Does.Contain("_color"));
                }
            }
            finally
            {
                Object.DestroyImmediate(many);
            }
        }

        [Test]
        public void AFilterMatchingNothingReturnsNoPropertiesRatherThanAll()
        {
            var narrowed = Slots(ShaderTools.MaterialRead(
                objectPath: "/MaterialSlotsRoot", slot: 0, property: "nosuchproperty"))[0];

            Assert.That(((JArray)narrowed["properties"]).Count, Is.Zero);
            Assert.That(narrowed["propertyCount"].Value<int>(), Is.GreaterThan(0),
                "the count is what tells the caller the material was not empty");
        }

        /// <summary>
        /// Several objects in one call, with the one that cannot be read costing only itself.
        /// </summary>
        /// <remarks>
        /// Comparing a scene's materials is a read per object otherwise: three hundred bricks went
        /// out as three hundred calls and 744 KB in a hands-on run, after a description that said
        /// as much had already been added and changed nothing.
        /// </remarks>
        [Test]
        public void SeveralObjectsAreReadInOneCallAndOneBadPathCostsOnlyItself()
        {
            var second = new GameObject("MaterialSlotsSecond", typeof(MeshFilter), typeof(MeshRenderer));
            second.GetComponent<MeshRenderer>().sharedMaterial = this.good;

            try
            {
                var reply = ShaderTools.MaterialRead(
                    slot: 0,
                    objectPaths: new[] { "/MaterialSlotsRoot", "/MaterialSlotsSecond", "/NoSuchObject" });

                var reads = (JObject)reply["reads"];

                Assert.That(reads.Count, Is.EqualTo(3), "every path asked for is answered");
                Assert.That(reads["/MaterialSlotsRoot"]["slots"], Is.Not.Null);
                Assert.That(reads["/MaterialSlotsSecond"]["slots"], Is.Not.Null);
                Assert.That(reads["/NoSuchObject"]["error"], Is.Not.Null,
                    "the path that failed carries its own error");
                Assert.That(reads["/MaterialSlotsSecond"]["error"], Is.Null,
                    "and does not take the others with it");
            }
            finally
            {
                Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void ReadingMoreObjectsThanTheCapIsRefused()
        {
            var tooMany = Enumerable.Range(0, 51).Select(i => "/MaterialSlotsRoot").ToArray();

            var thrown = Assert.Throws<McpToolException>(
                () => ShaderTools.MaterialRead(objectPaths: tooMany));

            Assert.That(thrown.Message, Does.Contain("50"));
        }
    }
}
