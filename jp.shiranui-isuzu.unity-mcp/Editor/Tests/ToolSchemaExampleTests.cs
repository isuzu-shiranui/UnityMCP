using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// A published example that its own schema rejects is worse than no example: a client that
    /// validates before sending refuses the call, and the tool looks broken from outside.
    /// </summary>
    [TestFixture]
    internal sealed class ToolSchemaExampleTests
    {
        [Test]
        public void EveryPublishedExampleMatchesItsOwnSchema()
        {
            var live = ToolCatalog.Build().Tools.ToList();
            Assert.That(live, Is.Not.Empty, "The live catalog is what gives this test its coverage.");

            var examined = 0;
            var wrong = new List<string>();

            foreach (var tool in live)
            {
                if (!(tool.InputSchema?["properties"] is JObject properties) ||
                    !(tool.InputSchema["examples"] is JArray examples))
                {
                    continue;
                }

                foreach (var example in examples.OfType<JObject>())
                {
                    foreach (var argument in example.Properties())
                    {
                        if (!(properties[argument.Name] is JObject schema))
                        {
                            wrong.Add($"{tool.Name}: example sets '{argument.Name}', which the schema does not declare");
                            continue;
                        }

                        examined++;

                        if (!Accepts(schema["type"], argument.Value))
                        {
                            wrong.Add(
                                $"{tool.Name}.{argument.Name}: schema says {schema["type"]}, " +
                                $"example passes {argument.Value.Type}");
                        }
                    }
                }
            }

            Assert.That(examined, Is.GreaterThan(0), "no example was checked, so this test proves nothing");
            Assert.That(wrong, Is.Empty);
        }

        /// <summary>
        /// Whether a JSON Schema <c>type</c>, which is either a name or a list of names, admits
        /// the value. An absent <c>type</c> constrains nothing.
        /// </summary>
        private static bool Accepts(JToken declared, JToken value)
        {
            if (declared == null)
            {
                return true;
            }

            var names = declared.Type == JTokenType.Array
                ? declared.Select(t => (string)t)
                : new[] { (string)declared };

            return names.Any(name => Matches(name, value));
        }

        private static bool Matches(string name, JToken value)
        {
            switch (name)
            {
                case "string":
                    return value.Type == JTokenType.String;
                case "number":
                    return value.Type == JTokenType.Float || value.Type == JTokenType.Integer;
                case "integer":
                    return value.Type == JTokenType.Integer;
                case "boolean":
                    return value.Type == JTokenType.Boolean;
                case "object":
                    return value.Type == JTokenType.Object;
                case "array":
                    return value.Type == JTokenType.Array;
                case "null":
                    return value.Type == JTokenType.Null;
                default:
                    return false;
            }
        }
    }
}
