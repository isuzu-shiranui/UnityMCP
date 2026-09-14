using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tests
{
    [TestFixture]
    internal sealed class ObjectArgumentTests
    {
        private static class Fixture
        {
            [McpTool("t_object_argument_fixture", "Argument binding fixture.")]
            public static JObject Echo([McpArg("offset", "Offset as {x, y, z}.")] JObject offset = null,
                [McpArg("values", "Property values.")] JObject values = null) => offset ?? values;
        }
        private static JObject Invoke(JObject arguments)
        {
            var catalog = ToolCatalog.BuildFromTypes(new[] { typeof(Fixture) });
            return ToolInvoker.Invoke(catalog.Tools.Single(), arguments);
        }
        [TestCase("1,2,3")]
        [TestCase("1.5,-2e1")]
        [TestCase("1,2,3,4")]
        public void VectorStringsReachObjectParameters(string vector)
        {
            var reply = Invoke(new JObject { ["offset"] = vector });
            Assert.That((double)reply["x"], Is.GreaterThanOrEqualTo(1));
            Assert.That(reply.Count, Is.EqualTo(vector.Split(',').Length));
        }
        [Test]
        public void NumericArraysAndStrictObjectStringsAreAccepted()
        {
            Assert.That((int)Invoke(new JObject { ["offset"] = new JArray(1, 2, 3) })["z"], Is.EqualTo(3));
            var reply = Invoke(new JObject { ["values"] = "{\"m_Text\":\"true\",\"nested\":{\"at\":\"2026-01-01T00:00:00Z\"}}" });
            Assert.That(reply["m_Text"].Type, Is.EqualTo(JTokenType.String));
            Assert.That(reply["nested"]["at"].Type, Is.EqualTo(JTokenType.String));
        }
        [TestCase("{x:1}")]
        [TestCase("{'x':1}")]
        [TestCase("{\"x\":1,}")]
        [TestCase("{\"x\":NaN}")]
        [TestCase("{\"x\":/*comment*/1}")]
        [TestCase("{\"x\":1} {}")]
        [TestCase("1,2,3")]
        public void ObjectValuesDoNotGuessMalformedJsonOrVectors(string value)
        {
            Assert.That(Assert.Throws<McpToolException>(() => Invoke(new JObject { ["values"] = value })).Code, Is.EqualTo("invalid_params"));
        }
        [Test]
        public void VectorArraysRejectStringsAndExcessAxes()
        {
            Assert.Throws<McpToolException>(() => Invoke(new JObject { ["offset"] = new JArray(1, "2") }));
            Assert.Throws<McpToolException>(() => Invoke(new JObject { ["offset"] = new JArray(1, 2, 3, 4, 5) }));
        }
    }
}
