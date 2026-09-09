using System.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

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
    }
}
