using System.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityEditor;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Handlers;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// inspect_read, inspect_write and inspect_list share one handler. The components view belongs
    /// to a listing alone: reached from a write it changes nothing and answers with a component
    /// list, which reads as a result rather than as a failure.
    /// </summary>
    public sealed class InspectorAccessTests
    {
        private GameObject target;

        [SetUp]
        public void SetUp()
        {
            target = new GameObject("InspectorAccessTests");
        }

        [TearDown]
        public void TearDown()
        {
            if (target != null)
            {
                Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void AWriteWithNoComponentNamedChangesTheGameObject()
        {
            var reply = InspectorAccess.Access(ToolArgs.Of(
                ("mode", "write"),
                ("objectPath", "/InspectorAccessTests"),
                ("propertyPath", "m_Name"),
                ("value", "Renamed")));

            Assert.That(reply["error"], Is.Null, "the write must not report a failure");
            Assert.That(reply["written"], Is.Not.Null, "a listing came back instead of a write");
            Assert.That(target.name, Is.EqualTo("Renamed"), "the object itself has to change");
        }

        /// <summary>
        /// Several properties on one component, in one call and one undo step.
        /// </summary>
        /// <remarks>
        /// Setting up a single ConfigurableJoint cost twenty-one calls one property at a time,
        /// which is twenty-one round trips for one object's state.
        /// </remarks>
        [Test]
        public void SeveralPropertiesOnOneComponentGoInOneCall()
        {
            var body = target.AddComponent<Rigidbody>();

            var reply = InspectorAccess.Access(ToolArgs.Of(
                ("mode", "write"),
                ("objectPath", "/InspectorAccessTests"),
                ("componentType", "Rigidbody"),
                ("values", new JObject { ["m_Mass"] = 7.5f, ["m_Drag"] = 2.5f })));

            Assert.That(reply["error"], Is.Null, (string)reply["error"]);
            Assert.That(reply["count"].Value<int>(), Is.EqualTo(2));
            Assert.That(body.mass, Is.EqualTo(7.5f).Within(0.001f));
            Assert.That(body.linearDamping, Is.EqualTo(2.5f).Within(0.001f));
        }

        /// <summary>
        /// One path that does not resolve leaves the component exactly as it was.
        /// </summary>
        /// <remarks>
        /// Nothing reaches the object until ApplyModifiedProperties, so refusing costs a round
        /// trip rather than leaving a component half configured for the caller to work out.
        /// </remarks>
        [Test]
        public void APathThatDoesNotResolveWritesNoneOfThem()
        {
            var body = target.AddComponent<Rigidbody>();
            body.mass = 1f;

            var reply = InspectorAccess.Access(ToolArgs.Of(
                ("mode", "write"),
                ("objectPath", "/InspectorAccessTests"),
                ("componentType", "Rigidbody"),
                ("values", new JObject { ["m_Mass"] = 99f, ["m_NoSuchThing"] = 1 })));

            Assert.That(reply["error"], Is.Not.Null);
            Assert.That((string)reply["error"], Does.Contain("Nothing was written"));
            Assert.That(body.mass, Is.EqualTo(1f).Within(0.001f),
                "the property that would have worked must not have been applied either");
        }

        /// <summary>
        /// The same edit across several objects, in one call and one undo step.
        /// </summary>
        /// <remarks>
        /// Swapping a material across three hundred objects cost two hundred and ninety-nine
        /// calls that differed only in which object they named. One SerializedObject over many
        /// targets is how the Inspector edits a multi-selection.
        /// </remarks>
        [Test]
        public void TheSameEditReachesEveryObjectNamed()
        {
            var second = new GameObject("InspectorAccessTests_Second");

            try
            {
                var a = target.AddComponent<Rigidbody>();
                var b = second.AddComponent<Rigidbody>();

                var reply = InspectorAccess.Access(ToolArgs.Of(
                    ("mode", "write"),
                    ("objectPaths", new JArray("/InspectorAccessTests", "/InspectorAccessTests_Second")),
                    ("componentType", "Rigidbody"),
                    ("propertyPath", "m_Mass"),
                    ("value", 4f)));

                Assert.That(reply["error"], Is.Null, (string)reply["error"]);
                Assert.That(reply["objects"].Value<int>(), Is.EqualTo(2));
                Assert.That(a.mass, Is.EqualTo(4f).Within(0.001f));
                Assert.That(b.mass, Is.EqualTo(4f).Within(0.001f));
            }
            finally
            {
                Object.DestroyImmediate(second);
            }
        }

        /// <summary>
        /// One object that cannot be resolved leaves every other one untouched.
        /// </summary>
        [Test]
        public void AnObjectThatDoesNotResolveWritesToNoneOfThem()
        {
            var body = target.AddComponent<Rigidbody>();
            body.mass = 1f;

            Assert.Throws<McpToolException>(() => InspectorAccess.Access(ToolArgs.Of(
                ("mode", "write"),
                ("objectPaths", new JArray("/InspectorAccessTests", "/NoSuchObjectAnywhere")),
                ("componentType", "Rigidbody"),
                ("propertyPath", "m_Mass"),
                ("value", 88f))));

            Assert.That(body.mass, Is.EqualTo(1f).Within(0.001f),
                "the object that would have worked must not have been written either");
        }

        [Test]
        public void AReadWithNoComponentNamedReadsTheGameObject()
        {
            var reply = InspectorAccess.Access(ToolArgs.Of(
                ("mode", "read"),
                ("objectPath", "/InspectorAccessTests"),
                ("propertyPath", "m_Name")));

            Assert.That(reply["property"]?["value"]?.ToString(), Is.EqualTo("InspectorAccessTests"));
        }

        /// <summary>Only a listing asks for the components view, and only when none is named.</summary>
        [Test]
        public void AListWithNoComponentNamedStillListsComponents()
        {
            var reply = InspectorAccess.Access(ToolArgs.Of(
                ("mode", "list"),
                ("objectPath", "/InspectorAccessTests")));

            Assert.That(reply["components"], Is.Not.Null);
        }

        /// <summary>
        /// The schema offers offset and limit whichever way the tool is called, so naming a
        /// component cannot silently return every property.
        /// </summary>
        [Test]
        public void NamingAComponentStillHonoursTheLimit()
        {
            var all = InspectorAccess.Access(ToolArgs.Of(
                ("mode", "list"),
                ("objectPath", "/InspectorAccessTests"),
                ("componentType", "Transform")));

            var paged = InspectorAccess.Access(ToolArgs.Of(
                ("mode", "list"),
                ("objectPath", "/InspectorAccessTests"),
                ("componentType", "Transform"),
                ("limit", 2)));

            Assert.That(paged["properties"].Children().Count(), Is.EqualTo(2));
            Assert.That(all["properties"].Children().Count(), Is.GreaterThan(2),
                "the fixture needs more properties than the page for this to prove anything");
            Assert.That(paged["truncated"].Value<bool>(), Is.True);
            Assert.That(paged["count"].Value<int>(), Is.EqualTo(all["count"].Value<int>()),
                "a page still reports how many there were");
        }

        [Test]
        public void AnOffsetSkipsProperties()
        {
            var first = InspectorAccess.Access(ToolArgs.Of(
                ("mode", "list"),
                ("objectPath", "/InspectorAccessTests"),
                ("componentType", "Transform"),
                ("limit", 1)));

            var second = InspectorAccess.Access(ToolArgs.Of(
                ("mode", "list"),
                ("objectPath", "/InspectorAccessTests"),
                ("componentType", "Transform"),
                ("offset", 1),
                ("limit", 1)));

            Assert.That(second["properties"][0]["path"].ToString(),
                Is.Not.EqualTo(first["properties"][0]["path"].ToString()));
        }

        /// <summary>
        /// A reference is known to a caller by where the thing sits, not by the identity the
        /// Editor holds for it. A path names a GameObject, and a field usually wants one of its
        /// components: assigning the GameObject itself is refused by Unity without a word, leaving
        /// the property unchanged while the call looks like it worked.
        /// </summary>
        [Test]
        public void AScenePathIsNarrowedToTheComponentTheFieldHolds()
        {
            var other = new GameObject("InspectorAccessBody");
            other.AddComponent<Rigidbody>();
            target.AddComponent<Rigidbody>();
            target.AddComponent<HingeJoint>();

            try
            {
                var reply = InspectorAccess.Access(ToolArgs.Of(
                    ("mode", "write"),
                    ("objectPath", "/InspectorAccessTests"),
                    ("componentType", "HingeJoint"),
                    ("propertyPath", "m_ConnectedBody"),
                    ("value", "/InspectorAccessBody")));

                Assert.That(reply["error"], Is.Null, reply["error"]?.ToString());
                Assert.That(reply["property"]["value"]["type"].ToString(), Is.EqualTo("Rigidbody"),
                    "the path named a GameObject and the field holds a Rigidbody");
                Assert.That(reply["property"]["value"]["name"].ToString(),
                    Is.EqualTo("InspectorAccessBody"));
            }
            finally
            {
                Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void AnEmptyStringClearsAReference()
        {
            target.AddComponent<Rigidbody>();
            target.AddComponent<HingeJoint>();

            InspectorAccess.Access(ToolArgs.Of(
                ("mode", "write"), ("objectPath", "/InspectorAccessTests"),
                ("componentType", "HingeJoint"), ("propertyPath", "m_ConnectedBody"),
                ("value", "")));

            var read = InspectorAccess.Access(ToolArgs.Of(
                ("mode", "read"), ("objectPath", "/InspectorAccessTests"),
                ("componentType", "HingeJoint"), ("propertyPath", "m_ConnectedBody")));

            Assert.That(read["property"]["value"]["instanceId"].Value<int>(), Is.Zero);
        }

        [Test]
        public void AReferenceToSomethingThatIsNotThereSaysSo()
        {
            target.AddComponent<BoxCollider>();

            var reply = InspectorAccess.Access(ToolArgs.Of(
                ("mode", "write"),
                ("objectPath", "/InspectorAccessTests"),
                ("componentType", "BoxCollider"),
                ("propertyPath", "m_Material"),
                ("value", "Assets/NoSuchThing.physicMaterial")));

            Assert.That(reply["error"]?.ToString(), Does.Contain("No asset at"));
        }


        /// <summary>
        /// An array on a fresh component has no elements, and an element cannot be written into a
        /// length that is still zero. Writing the length is what the Inspector's plus button does.
        /// </summary>
        [Test]
        public void AnArrayCanBeGrownAndThenFilled()
        {
            var other = new GameObject("InspectorAccessMesh");
            other.AddComponent<MeshRenderer>();
            var renderer = target.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = new Material[0];

            try
            {
                var grow = InspectorAccess.Access(ToolArgs.Of(
                    ("mode", "write"), ("objectPath", "/InspectorAccessTests"),
                    ("componentType", "MeshRenderer"),
                    ("propertyPath", "m_Materials.Array.size"), ("value", 1)));

                Assert.That(grow["error"], Is.Null, grow["error"]?.ToString());
                Assert.That(grow["property"]["value"].Value<int>(), Is.EqualTo(1));
                Assert.That(renderer.sharedMaterials.Length, Is.EqualTo(1),
                    "the component itself has to have grown, not just the serialized copy");
            }
            finally
            {
                Object.DestroyImmediate(other);
            }
        }

        /// <summary>
        /// Reporting only the type name left a caller unable to tell an empty list from one that
        /// could not be read, and with no idea how to address an element.
        /// </summary>
        [Test]
        public void ReadingAnArraySaysHowLongItIsAndHowToReachAnElement()
        {
            var renderer = target.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = new Material[2];

            var read = InspectorAccess.Access(ToolArgs.Of(
                ("mode", "read"), ("objectPath", "/InspectorAccessTests"),
                ("componentType", "MeshRenderer"), ("propertyPath", "m_Materials")));

            var value = read["property"]["value"];

            Assert.That(value["isArray"].Value<bool>(), Is.True);
            Assert.That(value["length"].Value<int>(), Is.EqualTo(2));
            Assert.That(value["elementPath"].ToString(), Is.EqualTo("m_Materials.Array.data[0]"));
            Assert.That(value["lengthPath"].ToString(), Is.EqualTo("m_Materials.Array.size"));
        }

        /// <summary>
        /// Both listings answer the same question, so they have to answer it the same way.
        /// </summary>
        /// <remarks>
        /// Only the narrowed one was moved off visibility at first, so a HingeJoint asked with a
        /// component_type reported m_ConnectedBody and the same joint asked through the components
        /// view did not. Whichever a caller happened to use decided whether they found it.
        /// </remarks>
        [Test]
        public void BothListingsReportTheSameProperties()
        {
            target.AddComponent<Rigidbody>();
            target.AddComponent<HingeJoint>();

            var narrowed = InspectorAccess.Access(ToolArgs.Of(
                ("mode", "list"),
                ("objectPath", "/InspectorAccessTests"),
                ("componentType", "HingeJoint")));

            var everything = InspectorAccess.Access(ToolArgs.Of(
                ("mode", "list"),
                ("objectPath", "/InspectorAccessTests"),
                ("detail", "full")));

            var joint = ((JArray)everything["components"])
                .First(c => c["type"].ToString() == "HingeJoint");

            var fromNarrowed = ((JArray)narrowed["properties"]).Select(p => p["path"].ToString());
            var fromView = ((JArray)joint["properties"]).Select(p => p["path"].ToString());

            Assert.That(fromView, Is.EquivalentTo(fromNarrowed));
            Assert.That(fromView, Does.Contain("m_ConnectedBody"));
        }

        /// <summary>
        /// A layer mask has to read as layers and be writable as layers.
        /// </summary>
        /// <remarks>
        /// "Why is this not drawn" and "why is this not hit" both end at a culling or collision
        /// mask, and it read back as nothing but its type name while writing it was refused
        /// outright — so the one field the question was about needed execute_code. The bitmask
        /// alone is unreadable: everything is -1, and one layer switched off is 2147483645.
        /// </remarks>
        [Test]
        public void ALayerMaskReadsAndWritesAsTheLayersItHolds()
        {
            var camera = target.AddComponent<Camera>();

            camera.cullingMask = -1;

            var everything = InspectorAccess.Access(ToolArgs.Of(
                ("mode", "read"), ("objectPath", "/InspectorAccessTests"),
                ("componentType", "Camera"), ("propertyPath", "m_CullingMask")));

            Assert.That(everything["property"]["value"]["mask"].Value<int>(), Is.EqualTo(-1));
            Assert.That(
                ((JArray)everything["property"]["value"]["layers"]).Select(l => l.ToString()),
                Is.EqualTo(new[] { "Everything" }),
                "spelling out all thirty-two is noise, and twenty-six have no name to give");

            var written = InspectorAccess.Access(ToolArgs.Of(
                ("mode", "write"), ("objectPath", "/InspectorAccessTests"),
                ("componentType", "Camera"), ("propertyPath", "m_CullingMask"),
                ("value", new JArray("Default", "UI"))));

            Assert.That(written["error"], Is.Null, written["error"]?.ToString());
            Assert.That(camera.cullingMask, Is.EqualTo((1 << 0) | (1 << 5)));

            // Whatever a read hands back has to be writable, or the round trip does not close.
            InspectorAccess.Access(ToolArgs.Of(
                ("mode", "write"), ("objectPath", "/InspectorAccessTests"),
                ("componentType", "Camera"), ("propertyPath", "m_CullingMask"),
                ("value", new JArray("Everything"))));

            Assert.That(camera.cullingMask, Is.EqualTo(-1));
        }

        [Test]
        public void ALayerThatDoesNotExistIsNamedBackRatherThanIgnored()
        {
            target.AddComponent<Camera>();

            var reply = InspectorAccess.Access(ToolArgs.Of(
                ("mode", "write"), ("objectPath", "/InspectorAccessTests"),
                ("componentType", "Camera"), ("propertyPath", "m_CullingMask"),
                ("value", new JArray("NoSuchLayer"))));

            Assert.That(reply["error"]?.ToString(), Does.Contain("NoSuchLayer"));
            Assert.That(reply["error"]?.ToString(), Does.Contain("project_settings"));
        }

        /// <summary>
        /// A listing has to include what a component's own Inspector draws by hand.
        /// </summary>
        /// <remarks>
        /// Unity marks such a field invisible, and listing by visibility left a HingeJoint's
        /// m_ConnectedBody out — the one property someone debugging a joint has come to find,
        /// and one that inspect_read and inspect_write both take. Unity's per-Object header is
        /// dropped by name instead, which is what visibility was standing in for.
        /// </remarks>
        [Test]
        public void AListingIncludesAPropertyTheInspectorDrawsItself()
        {
            target.AddComponent<Rigidbody>();
            target.AddComponent<HingeJoint>();

            var listed = InspectorAccess.Access(ToolArgs.Of(
                ("mode", "list"),
                ("objectPath", "/InspectorAccessTests"),
                ("componentType", "HingeJoint")));

            var paths = ((JArray)listed["properties"])
                .Select(p => p["path"].ToString())
                .ToArray();

            Assert.That(paths, Does.Contain("m_ConnectedBody"));

            foreach (var header in new[]
                     {
                         "m_ObjectHideFlags", "m_GameObject", "m_PrefabInstance",
                         "m_PrefabAsset", "m_CorrespondingSourceObject",
                     })
            {
                Assert.That(paths, Does.Not.Contain(header), "the header is noise on every component");
            }
        }

        /// <summary>
        /// A struct has to say where its values are. Answered with nothing but its type name, a
        /// caller reading a joint's spring or a light's shadow settings has nowhere to go next,
        /// and reaches for execute_code instead.
        /// </summary>
        [Test]
        public void AStructNamesThePathsOfItsOwnFields()
        {
            target.AddComponent<Rigidbody>();
            target.AddComponent<HingeJoint>();

            var read = InspectorAccess.Access(ToolArgs.Of(
                ("mode", "read"),
                ("objectPath", "/InspectorAccessTests"),
                ("componentType", "HingeJoint"),
                ("propertyPath", "m_Spring")));

            var fields = (JArray)read["property"]["value"]["fields"];

            Assert.That(fields, Is.Not.Empty, "a spring has a spring, a damper and a target");

            // The paths it hands back have to be the ones it takes back.
            var leaf = InspectorAccess.Access(ToolArgs.Of(
                ("mode", "read"),
                ("objectPath", "/InspectorAccessTests"),
                ("componentType", "HingeJoint"),
                ("propertyPath", fields[0].ToString())));

            Assert.That(leaf["error"], Is.Null, leaf["error"]?.ToString());
            Assert.That(leaf["property"]["value"], Is.Not.Null);
        }

        /// <summary>
        /// A path has to reach an object that is switched off, and one whose name repeats among
        /// its siblings.
        /// </summary>
        /// <remarks>
        /// These three tools resolved their own paths with GameObject.Find, which sees neither.
        /// A caller reading back what they had just deactivated was told it did not exist.
        /// </remarks>
        [Test]
        public void APathReachesAnObjectThatIsSwitchedOff()
        {
            var hidden = new GameObject("InspectorAccessHidden");
            hidden.transform.SetParent(target.transform);
            hidden.AddComponent<Light>();
            hidden.SetActive(false);

            var read = InspectorAccess.Access(ToolArgs.Of(
                ("mode", "read"),
                ("objectPath", "/InspectorAccessTests/InspectorAccessHidden"),
                ("componentType", "Light"),
                ("propertyPath", "m_Intensity")));

            Assert.That(read["error"], Is.Null, read["error"]?.ToString());
            Assert.That(read["property"]["value"], Is.Not.Null);
        }

        /// <summary>
        /// The refusal has to name what is actually at that level. "GameObject not found" left a
        /// caller with a correct-looking path and nothing to try next.
        /// </summary>
        [Test]
        public void APathThatMissesNamesTheSiblingsItFoundInstead()
        {
            var present = new GameObject("InspectorAccessPresent");
            present.transform.SetParent(target.transform);

            var thrown = Assert.Throws<McpToolException>(() => InspectorAccess.Access(ToolArgs.Of(
                ("mode", "read"),
                ("objectPath", "/InspectorAccessTests/NoSuchChild"),
                ("componentType", "Transform"),
                ("propertyPath", "m_LocalPosition"))));

            Assert.That(thrown.Message, Does.Contain("InspectorAccessPresent"));
        }

        /// <summary>
        /// Before Unity 6.5 an identifier is an int, and a wider one wraps rather than fails: an
        /// id 2^32 above a live object's names that object. The reference would then point at
        /// something the caller never asked for, under a reply that says it worked.
        /// </summary>
        [Test]
        public void AnIdentifierTooWideToBeRealIsRefusedRatherThanWrapped()
        {
            var other = new GameObject("InspectorAccessBody");
            var body = other.AddComponent<Rigidbody>();

            target.AddComponent<Rigidbody>();
            target.AddComponent<HingeJoint>();

            try
            {
                var wrapped = EntityIdCompat.IdOf(body) + 4294967296L;

                var reply = InspectorAccess.Access(ToolArgs.Of(
                    ("mode", "write"),
                    ("objectPath", "/InspectorAccessTests"),
                    ("componentType", "HingeJoint"),
                    ("propertyPath", "m_ConnectedBody"),
                    ("value", wrapped)));

                Assert.That(reply["error"], Is.Not.Null, "an id that cannot exist is not an object");

                var read = InspectorAccess.Access(ToolArgs.Of(
                    ("mode", "read"), ("objectPath", "/InspectorAccessTests"),
                    ("componentType", "HingeJoint"), ("propertyPath", "m_ConnectedBody")));

                Assert.That(read["property"]["value"]["instanceId"].Value<long>(), Is.Zero,
                    "and nothing was written");
            }
            finally
            {
                Object.DestroyImmediate(other);
            }
        }

        /// <summary>
        /// A texture's Sprite is a sub-asset, so loading the path alone yields the Texture2D —
        /// the one object in the file a Sprite field cannot hold.
        /// </summary>
        [Test]
        public void APathWhoseSpriteIsASubAssetStillFillsASpriteField()
        {
            const string folder = "Assets/_McpSubAssetTests";
            const string path = folder + "/Fixture.asset";

            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder("Assets", "_McpSubAssetTests");
            }

            AssetDatabase.DeleteAsset(path);

            var texture = new Texture2D(4, 4);
            AssetDatabase.CreateAsset(texture, path);

            var sprite = Sprite.Create(texture, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
            sprite.name = "FixtureSprite";
            AssetDatabase.AddObjectToAsset(sprite, path);
            AssetDatabase.SaveAssetIfDirty(texture);

            try
            {
                target.AddComponent<SpriteRenderer>();

                var reply = InspectorAccess.Access(ToolArgs.Of(
                    ("mode", "write"),
                    ("objectPath", "/InspectorAccessTests"),
                    ("componentType", "SpriteRenderer"),
                    ("propertyPath", "m_Sprite"),
                    ("value", path)));

                Assert.That(reply["error"], Is.Null, reply["error"]?.ToString());
                Assert.That(reply["property"]["value"]["type"].ToString(), Is.EqualTo("Sprite"));
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
