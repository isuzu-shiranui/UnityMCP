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
        [TestCase("material", false)]
        [TestCase("material", true)]
        [TestCase("controller", false)]
        [TestCase("controller", true)]
        [TestCase("clip", false)]
        [TestCase("clip", true)]
        public void CreateNeverReplacesAnUnreadableExistingFile(string kind, bool overwrite)
        {
            var folderName = "_McpCreateGuard_" + System.Guid.NewGuid().ToString("N");
            var folder = "Assets/" + folderName;
            AssetDatabase.CreateFolder("Assets", folderName);
            var extension = kind == "material" ? ".mat" : kind == "controller" ? ".controller" : ".anim";
            var path = folder + "/Existing" + extension;
            const string sentinel = "Unimported file owned by the caller";
            try
            {
                System.IO.File.WriteAllText(path, sentinel);
                var error = Assert.Throws<McpToolException>(() =>
                {
                    if (kind == "material") ShaderTools.MaterialCreate(path, overwrite: overwrite);
                    else if (kind == "controller") AnimatorEditTools.AnimatorCreate(path, overwrite: overwrite);
                    else AnimatorEditTools.AnimationClipCreate(path, overwrite: overwrite);
                });
                Assert.That(error.Message, Does.Contain("not a compatible asset"));
                Assert.That(System.IO.File.ReadAllText(path), Is.EqualTo(sentinel));
            }
            finally
            {
                System.IO.File.Delete(path);
                AssetDatabase.DeleteAsset(folder);
            }
        }

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

                // Replaced, not created: the asset was already there and keeps its GUID, so a
                // caller keying off created has to be able to tell the two apart.
                var again = ShaderTools.MaterialCreate(path, overwrite: true);

                Assert.That(again["created"].Value<bool>(), Is.False);
                Assert.That(again["replaced"].Value<bool>(), Is.True);
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

        /// <summary>
        /// Objects drawn the same way come back as one description, not one each.
        /// </summary>
        /// <remarks>
        /// "Are these three hundred the same material" is answered by three hundred identical
        /// descriptions otherwise, which came to 738 KB in a hands-on run against 3 KB grouped.
        /// Grouping is by content: two materials set up the same way are interchangeable, which
        /// is what the question is about, and reflect_read over sharedMaterial counts instances.
        /// </remarks>
        [Test]
        public void ObjectsDrawnTheSameWayAreOneGroup()
        {
            var second = new GameObject("MaterialSlotsSecond", typeof(MeshFilter), typeof(MeshRenderer));
            second.GetComponent<MeshRenderer>().sharedMaterial = this.good;

            var other = new GameObject("MaterialSlotsOther", typeof(MeshFilter), typeof(MeshRenderer));
            other.GetComponent<MeshRenderer>().sharedMaterial = this.broken;

            try
            {
                var reply = ShaderTools.MaterialRead(
                    slot: 0,
                    objectPaths: new[]
                    {
                        "/MaterialSlotsRoot", "/MaterialSlotsSecond", "/MaterialSlotsOther", "/NoSuchObject",
                    },
                    group: true);

                Assert.That(reply["distinct"].Value<int>(), Is.EqualTo(2),
                    "the two drawn with the same material are one group, the third is its own");

                var groups = (JArray)reply["groups"];
                var shared = groups.First(g => ((JArray)g["objects"]).Count == 2);

                Assert.That(shared["objects"].Values<string>(),
                    Is.EquivalentTo(new[] { "/MaterialSlotsRoot", "/MaterialSlotsSecond" }));
                Assert.That(shared["slots"], Is.Not.Null, "a group still describes the material");

                Assert.That(reply["failed"]["/NoSuchObject"], Is.Not.Null,
                    "a path that could not be read is reported rather than grouped");
            }
            finally
            {
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(other);
            }
        }

        /// <summary>
        /// Materials that differ only in a property value are different groups, with no slot named.
        /// </summary>
        /// <remarks>
        /// Reading every slot describes a material by its property count, and grouping compared
        /// those descriptions: a red and a blue Unlit/Color came back as one material, which is the
        /// answer that gets two materials merged and an object recoloured.
        /// </remarks>
        [Test]
        public void MaterialsThatDifferOnlyInAValueAreNotOneGroup()
        {
            var red = new Material(this.good.shader) { name = "RedMaterial", color = Color.red };
            var blue = new Material(this.good.shader) { name = "BlueMaterial", color = Color.blue };

            var first = new GameObject("MaterialSlotsRed", typeof(MeshFilter), typeof(MeshRenderer));
            first.GetComponent<MeshRenderer>().sharedMaterial = red;

            var second = new GameObject("MaterialSlotsBlue", typeof(MeshFilter), typeof(MeshRenderer));
            second.GetComponent<MeshRenderer>().sharedMaterial = blue;

            try
            {
                var reply = ShaderTools.MaterialRead(
                    objectPaths: new[] { "/MaterialSlotsRed", "/MaterialSlotsBlue" },
                    group: true);

                Assert.That(reply["distinct"].Value<int>(), Is.EqualTo(2));
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(red);
                Object.DestroyImmediate(blue);
            }
        }

        /// <summary>
        /// Two materials set up the same way group together however they are named.
        /// </summary>
        /// <remarks>
        /// Three hundred bricks set up identically carry three hundred materials called
        /// BrickMaterial_0 upward, and a key taken over the whole description put each in a group
        /// of its own - the grouping saved nothing in the one case it was built for. Whether they
        /// can share one material is a question about their settings, so the names are reported
        /// beside the group rather than deciding it.
        /// </remarks>
        [Test]
        public void MaterialsWithTheSameSettingsGroupUnderDifferentNames()
        {
            var twin = new Material(this.good) { name = "TwinOfGoodMaterial" };
            var second = new GameObject("MaterialSlotsTwin", typeof(MeshFilter), typeof(MeshRenderer));
            second.GetComponent<MeshRenderer>().sharedMaterial = twin;

            try
            {
                var reply = ShaderTools.MaterialRead(
                    slot: 0,
                    objectPaths: new[] { "/MaterialSlotsRoot", "/MaterialSlotsTwin" },
                    group: true);

                Assert.That(reply["distinct"].Value<int>(), Is.EqualTo(1),
                    "the same settings under two names is one material as far as sharing goes");

                var only = ((JArray)reply["groups"])[0];

                Assert.That(only["names"].Values<string>(),
                    Is.EquivalentTo(new[] { "GoodMaterial", "TwinOfGoodMaterial" }),
                    "and both names are still reported");
            }
            finally
            {
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(twin);
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
