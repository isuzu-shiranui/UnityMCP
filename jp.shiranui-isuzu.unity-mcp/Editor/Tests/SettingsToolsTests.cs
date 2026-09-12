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
    /// The settings a project carries outside any scene, which no scene path reaches.
    /// </summary>
    public sealed class SettingsToolsTests
    {
        [Test]
        public void SettingsBatchRejectsResizeAndElementEditBeforeApplyingOrSaving()
        {
            var target = new GameObject("SettingsArrayConflictFixture", typeof(MeshRenderer));
            try
            {
                var renderer = target.GetComponent<MeshRenderer>();
                renderer.sharedMaterials = new Material[] { null };
                using var serialized = new SerializedObject(renderer);
                var change = typeof(ProjectSettingsTools).GetMethod("ChangeMany",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                var thrown = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
                    change.Invoke(null, new object[] { serialized, "fixture", new JObject
                    {
                        ["m_Materials.Array.data[0]"] = JValue.CreateNull(),
                        ["m_Materials.Array.size"] = 0,
                    } }));
                Assert.That(thrown.InnerException, Is.TypeOf<McpToolException>());
                Assert.That(thrown.InnerException.Message, Does.Contain("Nothing was written"));
                Assert.That(renderer.sharedMaterials.Length, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void TheSectionsAreListedWhenNoneIsNamed()
        {
            var listed = ProjectSettingsTools.ProjectSettings();

            Assert.That(listed["sections"].Values<string>(), Does.Contain("player"));
            Assert.That(listed["sections"].Values<string>(), Does.Contain("tags"));
        }

        [Test]
        public void ASectionThatDoesNotExistIsRefusedWithTheOnesThatDo()
        {
            var thrown = Assert.Throws<McpToolException>(
                () => ProjectSettingsTools.ProjectSettings("nope"));

            Assert.That(thrown.Message, Does.Contain("physics"));
        }

        [Test]
        public void APropertyReadsBackWithItsTypeAndValue()
        {
            var read = ProjectSettingsTools.ProjectSettings("physics", "m_Gravity");

            Assert.That(read["property"]["type"].ToString(), Is.EqualTo("Vector3"));
            Assert.That(read["property"]["value"]["y"], Is.Not.Null);
        }

        /// <summary>
        /// A name that is not a path falls back to a search, because the Inspector's label and the
        /// serialized name differ often enough that a caller cannot be expected to know it.
        /// </summary>
        [Test]
        public void ANameThatIsNotAPathSearchesInstead()
        {
            var found = ProjectSettingsTools.ProjectSettings("physics", "gravity");

            Assert.That(found["properties"].Values<JObject>(), Is.Not.Empty);
            Assert.That(
                found["properties"].Values<JObject>(),
                Has.All.Matches<JObject>(p => p["path"].ToString().ToLowerInvariant().Contains("gravity")));
        }

        [Test]
        public void AValueWithoutASectionIsRefused()
        {
            Assert.Throws<McpToolException>(
                () => ProjectSettingsTools.ProjectSettings(null, "companyName", "x"));
        }

        /// <summary>
        /// Half the names worth finding sit inside an array or a struct, so a search has to
        /// descend where a listing stops at the top level.
        /// </summary>
        /// <summary>
        /// The change has to reach the file, not just the object in memory.
        /// </summary>
        /// <remarks>
        /// Every settings singleton carries the built-in GUID, so AssetDatabase.SaveAssetIfDirty
        /// cannot find one and leaves it dirty with the file untouched, under a reply saying it
        /// was written. A cleared dirty flag is what separates a real save from that.
        /// </remarks>
        [Test]
        public void AWriteIsSavedRatherThanLeftDirty()
        {
            var target = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TimeManager.asset")[0];
            var before = Time.timeScale;

            try
            {
                var written = ProjectSettingsTools.ProjectSettings("time", "m_TimeScale", 0.25f);

                Assert.That(written["written"].Value<bool>(), Is.True);
                Assert.That(written["property"]["value"].Value<float>(), Is.EqualTo(0.25f));
                Assert.That(EditorUtility.IsDirty(target), Is.False, "and the file was written");

                // The save that writes a settings file writes everything else unsaved with it,
                // and nothing in Unity narrows it, so the reply has to say so.
                Assert.That(written["note"].ToString(), Does.Contain("every other unsaved asset"));
            }
            finally
            {
                ProjectSettingsTools.ProjectSettings("time", "m_TimeScale", before);
            }
        }

        /// <summary>
        /// A settings asset is drawn by its own Inspector, so listing by visibility drops fields
        /// that are still readable and writable.
        /// </summary>
        /// <remarks>
        /// The physics layer collision matrix is the one that matters: it is where "why does this
        /// not collide" ends, and it was invisible while every other physics field was listed.
        /// Unity's per-Object header is dropped by name instead.
        /// </remarks>
        [Test]
        public void AListingIncludesAFieldTheSettingsInspectorDrawsItself()
        {
            var listed = ProjectSettingsTools.ProjectSettings("physics");

            var paths = listed["properties"].Select(p => p["path"].ToString()).ToArray();

            Assert.That(paths, Does.Contain("m_LayerCollisionMatrix"));
            Assert.That(paths, Does.Not.Contain("m_ObjectHideFlags"));
        }

        /// <summary>
        /// An unsigned field reports as Integer, and it has to survive being read and written back.
        /// </summary>
        /// <remarks>
        /// intValue reads a uint's bits as signed — a physics layer mask of 4292870143 comes back
        /// as -2097153 — and writing that number back is clamped to 0 with the reply still saying
        /// it was written. Read once, written back, "collide with everything" became "collide with
        /// nothing".
        /// </remarks>
        [Test]
        public void AnUnsignedFieldSurvivesBeingWrittenBack()
        {
            const string mask = "m_LayerCollisionMatrix.Array.data[0]";

            var before = ProjectSettingsTools.ProjectSettings("physics", mask)["property"]["value"]
                .Value<long>();

            Assert.That(before, Is.GreaterThanOrEqualTo(0), "a uint never reads back negative");

            try
            {
                ProjectSettingsTools.ProjectSettings("physics", mask, uint.MaxValue);

                var written = ProjectSettingsTools.ProjectSettings("physics", mask)["property"]["value"]
                    .Value<long>();

                Assert.That(written, Is.EqualTo(uint.MaxValue), "every bit set, not clamped to 0");

                var thrown = Assert.Throws<McpToolException>(
                    () => ProjectSettingsTools.ProjectSettings("physics", mask, -1));

                Assert.That(thrown.Message, Does.Contain("uint"));

                // The cast wraps rather than refusing, so this was stored as 0 under a success.
                var overflowed = Assert.Throws<McpToolException>(
                    () => ProjectSettingsTools.ProjectSettings("physics", mask, 4294967296L));

                Assert.That(overflowed.Message, Does.Contain("4294967295"));
                Assert.That(
                    ProjectSettingsTools.ProjectSettings("physics", mask)["property"]["value"].Value<long>(),
                    Is.EqualTo(uint.MaxValue),
                    "a refused write leaves the value alone");
            }
            finally
            {
                ProjectSettingsTools.ProjectSettings("physics", mask, before);
            }
        }

        [Test]
        public void ASearchFindsANameNestedInsideAnArray()
        {
            var found = ProjectSettingsTools.ProjectSettings("quality", "shadowDistance");

            Assert.That(found["properties"].Values<JObject>(), Is.Not.Empty);
        }

        /// <summary>
        /// Several settings in one call, and none of them written when one path is wrong.
        /// </summary>
        /// <remarks>
        /// Turning on a single layer collision pair took eight calls: two masks read, read again,
        /// written one at a time and read back. The pair is also why this is all-or-nothing —
        /// neither mask means anything without the other, so a half-applied write leaves the
        /// matrix disagreeing with itself.
        /// </remarks>
        [Test]
        public void SeveralSettingsAreWrittenTogetherOrNotAtAll()
        {
            var before = Time.timeScale;
            var maximumStep = Time.maximumDeltaTime;

            try
            {
                var written = ProjectSettingsTools.ProjectSettings(
                    "time",
                    values: new JObject
                    {
                        ["m_TimeScale"] = 0.25f,
                        ["Maximum Allowed Timestep"] = 0.2f,
                    });

                Assert.That(written["written"]["m_TimeScale"]["value"].Value<float>(), Is.EqualTo(0.25f));
                Assert.That(Time.timeScale, Is.EqualTo(0.25f).Within(0.0001f));

                var thrown = Assert.Throws<McpToolException>(
                    () => ProjectSettingsTools.ProjectSettings(
                        "time",
                        values: new JObject
                        {
                            ["m_TimeScale"] = 0.5f,
                            ["m_NoSuchSetting"] = 1f,
                        }));

                Assert.That(thrown.Message, Does.Contain("Nothing was written"));
                Assert.That(Time.timeScale, Is.EqualTo(0.25f).Within(0.0001f),
                    "the good half of a refused batch must not have been applied");
            }
            finally
            {
                ProjectSettingsTools.ProjectSettings(
                    "time",
                    values: new JObject
                    {
                        ["m_TimeScale"] = before,
                        ["Maximum Allowed Timestep"] = maximumStep,
                    });
            }
        }

        [Test]
        public void SeveralSettingsAreReadInOneCall()
        {
            var read = ProjectSettingsTools.ProjectSettings(
                "physics",
                properties: new[] { "m_Gravity", "m_LayerCollisionMatrix.Array.data[0]" });

            var reads = (JObject)read["reads"];

            Assert.That(reads.Count, Is.EqualTo(2));
            Assert.That(reads["m_Gravity"]["property"]["value"]["y"].Value<float>(), Is.LessThan(0f));
            Assert.That(reads["m_LayerCollisionMatrix.Array.data[0]"]["property"], Is.Not.Null);
        }
    }
}
