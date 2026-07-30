using System;
using System.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Covers discovery and JSON Schema generation for <see cref="McpToolAttribute"/> methods.
    /// <para>
    /// These fixtures are built through <see cref="ToolCatalog.BuildFromTypes"/> rather than
    /// <see cref="ToolCatalog.Build"/> so they never enter the live catalog.
    /// </para>
    /// </summary>
    [TestFixture]
    internal sealed class ToolCatalogTests
    {
        private enum SampleMode
        {
            Fast,
            Thorough,
        }

        private static class ValidTools
        {
            [McpTool("sample_query", "Reads something harmless.", Idempotency = McpIdempotency.Safe, MainThread = false)]
            public static string Query(
                [McpArg("path", "Asset path to read")] string path,
                [McpArg("depth", "How deep to look")] int depth = 3,
                [McpArg("mode", "Traversal mode")] SampleMode mode = SampleMode.Fast)
            {
                return $"{path}/{depth}/{mode}";
            }

            [McpTool("sample_delete", "Deletes something.", Destructive = true)]
            public static void Delete([McpArg("path", "Asset path")] string path)
            {
                _ = path;
            }
        }

        private static class ExampleTools
        {
            [McpTool("sample_examples", "Publishes worked examples.",
                     Idempotency = McpIdempotency.Safe,
                     Examples = new[] { @"{""path"":""Assets/A.mat"",""depth"":2}" })]
            public static void Good([McpArg("path", "Asset path")] string path)
            {
                _ = path;
            }
        }

        private static class BrokenExampleTool
        {
            [McpTool("sample_bad_example", "Its example is not JSON.",
                     Examples = new[] { "not json at all" })]
            public static void Run()
            {
            }
        }

        private static class BadNameTool
        {
            [McpTool("sample.dotted", "Dots are not legal in MCP tool names.")]
            public static void Run()
            {
            }
        }

        private static class ReservedParameterTool
        {
            [McpTool("sample_reserved", "Declares a reserved parameter.", Destructive = true)]
            public static void Run([McpArg("confirm", "Collides with the injected flag")] bool confirm)
            {
                _ = confirm;
            }
        }

        private sealed class InstanceTool
        {
            [McpTool("sample_instance", "Attribute on a non-static method.")]
            public void Run()
            {
            }
        }

        private static class DuplicateA
        {
            [McpTool("sample_duplicate", "First definition.")]
            public static void Run()
            {
            }
        }

        private static class DuplicateB
        {
            [McpTool("sample_duplicate", "Second definition.")]
            public static void Run()
            {
            }
        }

        private static ToolCatalog Build(params Type[] types)
        {
            return ToolCatalog.BuildFromTypes(types);
        }

        [Test]
        public void Discovers_StaticAttributedMethods()
        {
            var catalog = Build(typeof(ValidTools));

            Assert.That(catalog.Errors, Is.Empty);
            Assert.That(catalog.Count, Is.EqualTo(2));
            Assert.That(catalog.TryGet("sample_query", out _), Is.True);
            Assert.That(catalog.TryGet("sample_delete", out _), Is.True);
        }

        [Test]
        public void CarriesDeclaredFlags()
        {
            var catalog = Build(typeof(ValidTools));

            Assert.That(catalog.TryGet("sample_query", out var query), Is.True);
            Assert.That(query.Idempotency, Is.EqualTo(McpIdempotency.Safe));
            Assert.That(query.MainThread, Is.False, "MainThread = false must survive discovery.");
            Assert.That(query.Destructive, Is.False);

            Assert.That(catalog.TryGet("sample_delete", out var delete), Is.True);
            Assert.That(delete.Destructive, Is.True);
            Assert.That(delete.Idempotency, Is.EqualTo(McpIdempotency.Unsafe), "Unsafe is the default.");
            Assert.That(delete.MainThread, Is.True, "MainThread defaults to true.");
        }

        [Test]
        public void RequiredInferredFromDefaultValue()
        {
            var catalog = Build(typeof(ValidTools));
            catalog.TryGet("sample_query", out var query);

            var required = query.InputSchema["required"].Select(t => t.Value<string>()).ToArray();

            Assert.That(required, Is.EquivalentTo(new[] { "path" }),
                "Parameters with a compile-time default must not be required.");
        }

        [Test]
        public void GeneratesSchemaTypesFromSignature()
        {
            var catalog = Build(typeof(ValidTools));
            catalog.TryGet("sample_query", out var query);

            var properties = query.InputSchema["properties"];

            Assert.That(properties["path"]["type"].Value<string>(), Is.EqualTo("string"));
            Assert.That(properties["depth"]["type"].Value<string>(), Is.EqualTo("integer"));
            Assert.That(properties["depth"]["default"].Value<int>(), Is.EqualTo(3));
            Assert.That(properties["path"]["description"].Value<string>(), Is.EqualTo("Asset path to read"));
        }

        [Test]
        public void EnumBecomesStringWithEnumValues()
        {
            var catalog = Build(typeof(ValidTools));
            catalog.TryGet("sample_query", out var query);

            var mode = query.InputSchema["properties"]["mode"];

            Assert.That(mode["type"].Value<string>(), Is.EqualTo("string"));
            Assert.That(mode["enum"].Select(t => t.Value<string>()), Is.EquivalentTo(new[] { "Fast", "Thorough" }));
        }

        [Test]
        public void DestructiveToolGetsConfirmAndDryRun()
        {
            var catalog = Build(typeof(ValidTools));
            catalog.TryGet("sample_delete", out var delete);

            var properties = delete.InputSchema["properties"];

            Assert.That(properties["confirm"], Is.Not.Null, "Destructive tools must expose confirm.");
            Assert.That(properties["dry_run"], Is.Not.Null, "Destructive tools must expose dry_run.");

            var required = delete.InputSchema["required"].Select(t => t.Value<string>()).ToArray();
            Assert.That(required, Does.Not.Contain("confirm"), "Injected flags must stay optional in the schema.");
        }

        [Test]
        public void NonDestructiveToolHasNoConfirmFlag()
        {
            var catalog = Build(typeof(ValidTools));
            catalog.TryGet("sample_query", out var query);

            Assert.That(query.InputSchema["properties"]["confirm"], Is.Null);
        }

        [Test]
        public void PublishesExamplesOnTheInputSchema()
        {
            var catalog = Build(typeof(ExampleTools));

            Assert.That(catalog.TryGet("sample_examples", out var tool), Is.True);

            var examples = tool.InputSchema["examples"];
            Assert.That(examples, Is.Not.Null, "examples belong on the schema the client receives");
            Assert.That(examples.Count(), Is.EqualTo(1));
            Assert.That((string)examples[0]["path"], Is.EqualTo("Assets/A.mat"));
            Assert.That((int)examples[0]["depth"], Is.EqualTo(2));
        }

        [Test]
        public void ATooWithoutExamplesHasNoExamplesKey()
        {
            var catalog = Build(typeof(ValidTools));

            Assert.That(catalog.TryGet("sample_query", out var tool), Is.True);

            // An empty array would be a claim that the tool was considered and found not to need
            // any, which is a different thing from never having been annotated.
            Assert.That(tool.InputSchema["examples"], Is.Null);
        }

        [Test]
        public void RejectsAToolWhoseExampleIsNotJson()
        {
            var catalog = Build(typeof(BrokenExampleTool));

            // Refused rather than published with the example dropped: a tool that still works
            // hides the authoring mistake, and the schema it serves would be the one thing nobody
            // rechecks.
            Assert.That(catalog.Count, Is.Zero);
            Assert.That(catalog.Errors.Count, Is.EqualTo(1));
            Assert.That(catalog.Errors[0], Does.Contain("sample_bad_example"));
        }

        [Test]
        public void RejectsDottedName()
        {
            var catalog = Build(typeof(BadNameTool));

            Assert.That(catalog.Count, Is.Zero);
            Assert.That(catalog.Errors.Count, Is.EqualTo(1));
            Assert.That(catalog.Errors[0], Does.Contain("sample.dotted"));
        }

        [Test]
        public void RejectsReservedParameterName()
        {
            var catalog = Build(typeof(ReservedParameterTool));

            Assert.That(catalog.Count, Is.Zero);
            Assert.That(catalog.Errors.Count, Is.EqualTo(1));
            Assert.That(catalog.Errors[0], Does.Contain("reserved parameter"));
        }

        [Test]
        public void RejectsInstanceMethod()
        {
            var catalog = Build(typeof(InstanceTool));

            Assert.That(catalog.Count, Is.Zero);
            Assert.That(catalog.Errors.Count, Is.EqualTo(1));
            Assert.That(catalog.Errors[0], Does.Contain("must be static"));
        }

        [Test]
        public void RejectsDuplicateName()
        {
            var catalog = Build(typeof(DuplicateA), typeof(DuplicateB));

            Assert.That(catalog.Count, Is.EqualTo(1), "The first definition wins.");
            Assert.That(catalog.Errors.Count, Is.EqualTo(1));
            Assert.That(catalog.Errors[0], Does.Contain("Duplicate tool name"));
        }

        [Test]
        public void CatalogJsonContainsEveryTool()
        {
            var catalog = Build(typeof(ValidTools));

            var json = catalog.ToJson();
            var names = json["tools"].Select(t => t["name"].Value<string>()).ToArray();

            Assert.That(names, Is.EquivalentTo(new[] { "sample_delete", "sample_query" }));
            Assert.That(json["tools"][0]["inputSchema"], Is.Not.Null,
                "The catalog must carry the schema the TS server forwards to tools/list.");
        }

        [Test]
        public void LiveCatalogBuildsWithoutErrors()
        {
            // Guards the real package: any [McpTool] added later that breaks the naming or
            // parameter rules fails here rather than silently disappearing from /tools.
            var catalog = ToolCatalog.Build();

            Assert.That(catalog.Errors, Is.Empty,
                "ToolCatalog.Build() reported: " + string.Join(" | ", catalog.Errors));
            Assert.That(catalog.TryGet("sample_query", out _), Is.False,
                "Test fixture tools must not leak into the live catalog.");
        }

        [Test]
        public void EveryLiveToolIsWellFormed()
        {
            // The description is the model's only cue for when to reach for a tool, and the
            // schema is the only thing telling it how to call one. Both are easy to leave
            // half-written, and nothing else in the pipeline complains if you do.
            foreach (var tool in ToolCatalog.Build().Tools)
            {
                Assert.That(tool.Description, Is.Not.Null.And.Length.GreaterThan(20),
                    $"'{tool.Name}' needs a description saying when to use it, not just what it is.");

                Assert.That(tool.InputSchema["type"].Value<string>(), Is.EqualTo("object"),
                    $"'{tool.Name}' must expose an object schema.");

                foreach (var property in (JObject)tool.InputSchema["properties"])
                {
                    Assert.That(property.Value["type"], Is.Not.Null,
                        $"'{tool.Name}.{property.Key}' has no schema type.");
                }
            }
        }
    }
}
