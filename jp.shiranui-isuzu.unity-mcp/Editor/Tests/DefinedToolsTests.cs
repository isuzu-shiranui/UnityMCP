using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityEngine;
using UnityEngine.TestTools;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;
using UnityMCP.Editor.Handlers;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Covers tools defined by JSON files: loading and refusal, the schema and entry shape they
    /// share with attribute tools, and the three runners.
    /// <para>
    /// Definitions are written under the test's own temporary directory, never under the real
    /// state root, and loaded through <see cref="ToolCatalog.BuildFromTypes(IEnumerable{Type}, List{string}, Func{ToolCatalog, List{string}, IReadOnlyList{McpToolDescriptor}})"/>
    /// so nothing here reaches the live catalog.
    /// </para>
    /// </summary>
    [TestFixture]
    internal sealed class DefinedToolsTests
    {
        internal static class ProbeSample
        {
            public static int Counter = 1;

            public static string Label = "a";
        }

        private static class StepTools
        {
            [McpTool("seq_echo", "Returns the text it was given.", Idempotency = McpIdempotency.Safe, MainThread = false)]
            public static JObject Echo([McpArg("text", "Text to echo")] string text)
            {
                return new JObject { ["text"] = text, ["length"] = text.Length };
            }

            [McpTool("seq_fail", "Always fails.", MainThread = false)]
            public static void Fail()
            {
                throw new McpToolException("boom", "deliberate");
            }

            [McpTool("seq_wipe", "Pretends to wipe something.", Destructive = true, MainThread = false)]
            public static JObject Wipe([McpArg("what", "What to wipe")] string what)
            {
                return new JObject { ["wiped"] = what };
            }

            [McpTool("seq_main", "Needs the main thread.", MainThread = true)]
            public static void Main()
            {
            }

            /// <summary>The item <see cref="DeferWorker"/> hands back; a test settles it from another thread.</summary>
            public static McpMainThreadDispatcher.WorkItem Pending;

            [McpTool("seq_defer_worker", "Answers later, off the main thread.", Idempotency = McpIdempotency.Safe, MainThread = false)]
            public static JObject DeferWorker() => new DeferredToolResult(Pending);

            [McpTool("seq_defer_frames", "Answers after a few Editor frames.", Idempotency = McpIdempotency.Safe)]
            public static JObject DeferFrames([McpArg("frames", "Frames to wait")] int frames = 3)
            {
                return new DeferredToolResult(FrameSequencer.Run(CountFrames(frames), "seq_defer_frames"));
            }

            private static IEnumerator<FrameStep> CountFrames(int frames)
            {
                for (var i = 0; i < frames; i++)
                {
                    yield return FrameStep.Wait();
                }

                yield return FrameStep.Done(new JObject { ["frames"] = frames });
            }
        }

        private static readonly string SampleType = typeof(ProbeSample).FullName;

        private string root;
        private string project;
        private string shared;

        [SetUp]
        public void SetUp()
        {
            this.root = Path.Combine(Path.GetTempPath(), "UnityMCP.DefinedToolsTests", Guid.NewGuid().ToString("N"));
            this.project = Path.Combine(this.root, "project");
            this.shared = Path.Combine(this.root, "shared");
            Directory.CreateDirectory(this.project);
            Directory.CreateDirectory(this.shared);
            ProbeSample.Counter = 1;
            ProbeSample.Label = "a";
            ProbeRunner.ResetBaselines();
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                Directory.Delete(this.root, recursive: true);
            }
            catch (IOException)
            {
                // A watcher or the OS may still hold a handle; the temp directory is disposable.
            }
        }

        // ── helpers ──

        private string Write(string folder, string fileName, string json)
        {
            var path = Path.Combine(folder, fileName);
            File.WriteAllText(path, json);
            return path;
        }

        private string WriteProbe(string name, string path, string mode = null, string extra = "")
        {
            var modeField = mode == null ? string.Empty : $@", ""mode"": ""{mode}""";

            return this.Write(this.project, name + ".json", $@"{{
  ""name"": ""{name}"",
  ""description"": ""Reads {name}."",
  ""kind"": ""probe"",
  ""reads"": [ {{ ""id"": ""value"", ""path"": ""{path}"" }} ]{modeField}{extra}
}}");
        }

        private (DefinedToolSet Set, ToolCatalog Catalog, List<string> Errors) Load(params Type[] fixtureTypes)
        {
            DefinedToolSet set = null;
            var errors = new List<string>();

            var catalog = ToolCatalog.BuildFromTypes(fixtureTypes, errors, (attributeTools, sink) =>
            {
                set = DefinedTools.Load(new[] { this.project, this.shared }, sink, attributeTools);
                return set.Descriptors;
            });

            return (set, catalog, errors);
        }

        private static McpToolDescriptor Tool(ToolCatalog catalog, string name)
        {
            Assert.That(catalog.TryGet(name, out var descriptor), Is.True, $"'{name}' was not registered.");
            return descriptor;
        }

        private static JObject Call(McpToolDescriptor descriptor, JObject arguments = null)
        {
            return ToolInvoker.Invoke(descriptor, arguments ?? new JObject());
        }

        // ── loading ──

        [Test]
        public void LoadsAllThreeKinds()
        {
            this.WriteProbe("probe_one", $"@type:{SampleType}/Counter");
            var script = Path.Combine(this.project, "light_bump.cs");
            File.WriteAllText(script, "return 1;");
            this.Write(this.project, "light_bump.json", @"{
  ""name"": ""light_bump"", ""description"": ""Runs a file."", ""kind"": ""script"", ""file"": ""light_bump.cs""
}");
            this.Write(this.project, "chain.json", @"{
  ""name"": ""chain"", ""description"": ""Echoes twice."", ""kind"": ""sequence"",
  ""steps"": [ { ""id"": ""a"", ""tool"": ""seq_echo"", ""arguments"": { ""text"": ""x"" } } ]
}");

            var (set, catalog, errors) = this.Load(typeof(StepTools));

            Assert.That(errors, Is.Empty, string.Join("\n", errors));
            Assert.That(set.Entries.Select(e => e.Kind), Is.EquivalentTo(new[] { "probe", "script", "sequence" }));
            Assert.That(set.Entries.Select(e => e.File), Has.All.StartsWith(this.project));

            Assert.That(Tool(catalog, "probe_one").Idempotency, Is.EqualTo(McpIdempotency.Safe));
            Assert.That(Tool(catalog, "light_bump").Idempotency, Is.EqualTo(McpIdempotency.Unsafe));
            Assert.That(Tool(catalog, "chain").Idempotency, Is.EqualTo(McpIdempotency.Unsafe));
            Assert.That(Tool(catalog, "probe_one").Direct, Is.Not.Null);
            Assert.That(Tool(catalog, "probe_one").Method, Is.Null);
            Assert.That(Tool(catalog, "probe_one").Origin, Is.EqualTo(Path.Combine(this.project, "probe_one.json")));
        }

        [Test]
        public void BrokenJsonNamesTheFile()
        {
            var file = this.Write(this.project, "broken.json", "{ not json");

            var (set, _, errors) = this.Load();

            Assert.That(set.Descriptors, Is.Empty);
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0], Does.StartWith(file));
            Assert.That(errors[0], Does.Contain("not valid JSON"));
        }

        [Test]
        public void UnknownTopLevelKeyIsRefused()
        {
            var file = this.WriteProbe("probe_typo", $"@type:{SampleType}/Counter", extra: @", ""reeds"": []");

            var (set, catalog, errors) = this.Load();

            Assert.That(set.Descriptors, Is.Empty);
            Assert.That(catalog.TryGet("probe_typo", out _), Is.False);
            Assert.That(errors.Single(), Does.StartWith(file).And.Contain("'reeds'"));
        }

        [Test]
        public void MissingOrWrongKindIsRefused()
        {
            var file = this.Write(this.project, "nokind.json", @"{ ""name"": ""nokind"", ""description"": ""d"" }");
            var other = this.Write(this.project, "badkind.json", @"{ ""name"": ""badkind"", ""description"": ""d"", ""kind"": ""widget"" }");

            var (_, _, errors) = this.Load();

            Assert.That(errors.Count(e => e.StartsWith(file) && e.Contains("'kind' is required")), Is.EqualTo(1));
            Assert.That(errors.Count(e => e.StartsWith(other) && e.Contains("expected probe, script or sequence")), Is.EqualTo(1));
        }

        [Test]
        public void ReservedInputNameIsRefused()
        {
            var file = this.WriteProbe(
                "probe_reserved", $"@type:{SampleType}/Counter",
                extra: @", ""inputs"": { ""confirm"": { ""type"": ""boolean"" } }");

            var (set, _, errors) = this.Load();

            Assert.That(set.Descriptors, Is.Empty);
            Assert.That(errors.Single(), Does.StartWith(file).And.Contain("reserved"));
        }

        [Test]
        public void UnknownGroupIsRefused()
        {
            var file = this.WriteProbe("probe_grouped", $"@type:{SampleType}/Counter", extra: @", ""group"": ""misc""");

            var (_, catalog, errors) = this.Load();

            Assert.That(catalog.TryGet("probe_grouped", out _), Is.False);
            Assert.That(errors.Single(), Does.StartWith(file).And.Contain("'group' is 'misc'"));
        }

        [Test]
        public void InvalidNameIsRefusedWithTheFile()
        {
            var file = this.WriteProbe("Probe.Dotted", $"@type:{SampleType}/Counter");

            var (_, _, errors) = this.Load();

            Assert.That(errors.Single(), Does.StartWith(file).And.Contain("Names must match"));
        }

        [Test]
        public void AttributeToolWinsANameCollision()
        {
            var file = this.WriteProbe("seq_echo", $"@type:{SampleType}/Counter");

            var (set, catalog, errors) = this.Load(typeof(StepTools));

            Assert.That(set.Descriptors, Is.Empty);
            Assert.That(errors.Single(), Does.StartWith(file).And.Contain("collides").And.Contain("StepTools.Echo"));
            Assert.That(Tool(catalog, "seq_echo").Method, Is.Not.Null, "The attribute tool must stay registered.");
        }

        [Test]
        public void ProjectDefinitionShadowsSharedOneByToolNameOnly()
        {
            var projectFile = this.WriteProbe("probe_shadow", $"@type:{SampleType}/Counter");
            this.Write(this.shared, "other_name.json", $@"{{
  ""name"": ""probe_shadow"", ""description"": ""Shared copy."", ""kind"": ""probe"",
  ""reads"": [ {{ ""id"": ""value"", ""path"": ""@type:{SampleType}/Label"" }} ]
}}");
            var sharedFile = this.Write(this.shared, "probe_shadow.json", $@"{{
  ""name"": ""probe_shared_sibling"", ""description"": ""Same file name, its own tool."", ""kind"": ""probe"",
  ""reads"": [ {{ ""id"": ""value"", ""path"": ""@type:{SampleType}/Label"" }} ]
}}");

            var (set, catalog, errors) = this.Load();

            Assert.That(errors, Is.Empty, string.Join("\n", errors));
            Assert.That(set.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "probe_shadow", "probe_shared_sibling" }));
            Assert.That(Tool(catalog, "probe_shadow").Origin, Is.EqualTo(projectFile));
            Assert.That(Tool(catalog, "probe_shared_sibling").Origin, Is.EqualTo(sharedFile),
                "A shared file is read whatever its name; only the tool name can be shadowed.");
        }

        [Test]
        public void SharedDefinitionsLoadWhenTheProjectHasNone()
        {
            this.Write(this.shared, "probe_shared.json", $@"{{
  ""name"": ""probe_shared"", ""description"": ""Shared."", ""kind"": ""probe"",
  ""reads"": [ {{ ""id"": ""value"", ""path"": ""@type:{SampleType}/Label"" }} ]
}}");

            var (_, catalog, errors) = this.Load();

            Assert.That(errors, Is.Empty);
            Assert.That(Tool(catalog, "probe_shared").Origin, Does.StartWith(this.shared));
        }

        [Test]
        public void DuplicateNameInOneDirectoryIsAnError()
        {
            this.WriteProbe("probe_dup", $"@type:{SampleType}/Counter");
            var second = this.Write(this.project, "zz_dup.json", $@"{{
  ""name"": ""probe_dup"", ""description"": ""Again."", ""kind"": ""probe"",
  ""reads"": [ {{ ""id"": ""value"", ""path"": ""@type:{SampleType}/Label"" }} ]
}}");

            var (set, _, errors) = this.Load();

            Assert.That(set.Entries, Has.Count.EqualTo(1));
            Assert.That(errors.Single(), Does.StartWith(second).And.Contain("already defined"));
        }

        [Test]
        public void MissingDirectoriesAreTolerated()
        {
            var errors = new List<string>();

            var set = DefinedTools.Load(
                new[] { Path.Combine(this.root, "absent"), Path.Combine(this.root, "gone") }, errors, null);

            Assert.That(set.Descriptors, Is.Empty);
            Assert.That(errors, Is.Empty);
        }

        [Test]
        public void ReloadPicksUpAChangedDefinition()
        {
            var file = this.WriteProbe("probe_reload", $"@type:{SampleType}/Counter");
            var (_, first, _) = this.Load();
            Assert.That(Tool(first, "probe_reload").Description, Is.EqualTo("Reads probe_reload."));

            File.WriteAllText(file, File.ReadAllText(file).Replace("Reads probe_reload.", "Reads it again."));
            var (_, second, errors) = this.Load();

            Assert.That(errors, Is.Empty);
            Assert.That(Tool(second, "probe_reload").Description, Is.EqualTo("Reads it again."));
        }

        // ── shape ──

        [Test]
        public void EntryShapeMatchesAttributeTools()
        {
            this.WriteProbe("probe_shape", $"@type:{SampleType}/Counter");
            var (_, catalog, _) = this.Load(typeof(StepTools));

            var defined = Tool(catalog, "probe_shape").ToMcpToolEntry();
            var attribute = Tool(catalog, "seq_echo").ToMcpToolEntry();

            Assert.That(defined.Properties().Select(p => p.Name), Is.EquivalentTo(attribute.Properties().Select(p => p.Name)));
            Assert.That(((JObject)defined["annotations"]).Properties().Select(p => p.Name),
                Is.EquivalentTo(((JObject)attribute["annotations"]).Properties().Select(p => p.Name)));
            Assert.That(defined["annotations"]["readOnlyHint"].Value<bool>(), Is.True);
            Assert.That(defined["annotations"]["destructiveHint"].Value<bool>(), Is.False);
            Assert.That(defined["group"].Value<string>(), Is.EqualTo(McpToolGroups.Code));

            var catalogEntry = Tool(catalog, "probe_shape").ToCatalogEntry();
            Assert.That(catalogEntry.Properties().Select(p => p.Name),
                Is.EquivalentTo(Tool(catalog, "seq_echo").ToCatalogEntry().Properties().Select(p => p.Name)));
            Assert.That(catalogEntry["idempotency"].Value<string>(), Is.EqualTo("safe"));
        }

        [Test]
        public void InputSchemaMatchesTheAttributeDerivedShape()
        {
            this.WriteProbe(
                "probe_inputs", $"@type:{SampleType}/{{member}}",
                extra: @", ""inputs"": {
    ""member"": { ""type"": ""string"", ""description"": ""Which member."", ""required"": true, ""enum"": [""Counter"", ""Label""] },
    ""depth"": { ""type"": ""integer"", ""description"": ""How deep."", ""default"": 3 }
  },
  ""examples"": [ ""{\""member\"": \""Label\""}"" ]");

            var (_, catalog, errors) = this.Load(typeof(StepTools));
            Assert.That(errors, Is.Empty, string.Join("\n", errors));

            var schema = Tool(catalog, "probe_inputs").InputSchema;
            var reference = Tool(catalog, "seq_echo").InputSchema;

            Assert.That(schema["type"].Value<string>(), Is.EqualTo(reference["type"].Value<string>()));
            Assert.That(schema["properties"]["member"]["type"].Value<string>(), Is.EqualTo("string"));
            Assert.That(schema["properties"]["member"]["description"].Value<string>(), Is.EqualTo("Which member."));
            Assert.That(schema["properties"]["member"]["enum"].ToObject<string[]>(), Is.EqualTo(new[] { "Counter", "Label" }));
            Assert.That(schema["properties"]["depth"]["default"].Value<int>(), Is.EqualTo(3));
            Assert.That(schema["required"].ToObject<string[]>(), Is.EqualTo(new[] { "member" }));
            Assert.That(schema["examples"][0]["member"].Value<string>(), Is.EqualTo("Label"));
            Assert.That(reference["required"].ToObject<string[]>(), Is.EqualTo(new[] { "text" }));
        }

        [Test]
        public void DestructiveDefinitionGetsConfirmAndDryRun()
        {
            this.WriteProbe("probe_destructive", $"@type:{SampleType}/Counter", extra: @", ""destructive"": true");

            var (_, catalog, errors) = this.Load();
            Assert.That(errors, Is.Empty, string.Join("\n", errors));

            var properties = (JObject)Tool(catalog, "probe_destructive").InputSchema["properties"];
            Assert.That(properties["confirm"]["type"].Value<string>(), Is.EqualTo("boolean"));
            Assert.That(properties["dry_run"]["type"].Value<string>(), Is.EqualTo("boolean"));
        }

        [Test]
        public void MalformedExampleIsRefused()
        {
            var file = this.WriteProbe("probe_example", $"@type:{SampleType}/Counter", extra: @", ""examples"": [ ""not json"" ]");

            var (_, _, errors) = this.Load();

            Assert.That(errors.Single(), Does.StartWith(file).And.Contain("example"));
        }

        // ── probe ──

        [Test]
        public void TypeRootReadsStatics()
        {
            this.Write(this.project, "probe_static.json", $@"{{
  ""name"": ""probe_static"", ""description"": ""Reads statics."", ""kind"": ""probe"",
  ""reads"": [
    {{ ""id"": ""counter"", ""path"": ""@type:{SampleType}/Counter"" }},
    {{ ""id"": ""label"", ""path"": ""{SampleType}/Label"" }}
  ]
}}");
            var (_, catalog, errors) = this.Load();
            Assert.That(errors, Is.Empty, string.Join("\n", errors));

            var result = Call(Tool(catalog, "probe_static"));

            Assert.That(result["mode"].Value<string>(), Is.EqualTo("full"));
            Assert.That(result["baseline"].Value<bool>(), Is.True);
            Assert.That(result["reads"]["counter"]["value"].Value<int>(), Is.EqualTo(1));
            Assert.That(result["reads"]["counter"]["type"].Value<string>(), Is.EqualTo("System.Int32"));
            Assert.That(result["reads"]["label"]["value"].Value<string>(), Is.EqualTo("a"));
            Assert.That(result["changed"].ToObject<string[]>(), Is.EqualTo(new[] { "counter", "label" }));
        }

        [Test]
        public void SceneRootReadsAnObjectAndItsComponent()
        {
            var go = new GameObject("DefinedToolsProbeTarget");

            try
            {
                go.transform.position = new Vector3(1f, 2f, 3f);
                this.Write(this.project, "probe_scene.json", @"{
  ""name"": ""probe_scene"", ""description"": ""Reads a scene object."", ""kind"": ""probe"",
  ""reads"": [
    { ""id"": ""viaProperty"", ""path"": ""@scene:/DefinedToolsProbeTarget/transform/position"" },
    { ""id"": ""viaComponent"", ""path"": ""@scene:/DefinedToolsProbeTarget/Transform/position"" },
    { ""id"": ""segments"", ""path"": ""@scene:/DefinedToolsProbeTarget/transform/position/y"" }
  ]
}");
                var (_, catalog, errors) = this.Load();
                Assert.That(errors, Is.Empty, string.Join("\n", errors));

                var result = Call(Tool(catalog, "probe_scene"));
                var reads = result["reads"];

                Assert.That(reads["viaProperty"]["value"]["x"].Value<float>(), Is.EqualTo(1f));
                Assert.That(reads["viaComponent"]["value"]["z"].Value<float>(), Is.EqualTo(3f));
                Assert.That(reads["segments"]["value"].Value<float>(), Is.EqualTo(2f));

                var direct = UnityMCP.Editor.Tools.ReflectTools.Read("@scene:/DefinedToolsProbeTarget/Transform/position/x");
                Assert.That(direct["value"].Value<float>(), Is.EqualTo(1f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void UnknownRootIsReportedPerRead()
        {
            this.WriteProbe("probe_root", "@nowhere:x/y");
            var (_, catalog, _) = this.Load();

            var result = Call(Tool(catalog, "probe_root"));

            Assert.That(result["reads"]["value"]["error"].Value<string>(), Does.Contain("not a root"));
            Assert.That(result["reads"]["value"]["value"], Is.Null);
        }

        [Test]
        public void InputsAreSubstitutedIntoPaths()
        {
            this.WriteProbe(
                "probe_member", $"@type:{SampleType}/{{member}}",
                extra: @", ""inputs"": { ""member"": { ""type"": ""string"" } }");
            var (_, catalog, errors) = this.Load();
            Assert.That(errors, Is.Empty, string.Join("\n", errors));

            var result = Call(Tool(catalog, "probe_member"), new JObject { ["member"] = "Label" });
            Assert.That(result["reads"]["value"]["value"].Value<string>(), Is.EqualTo("a"));
            Assert.That(result["reads"]["value"]["path"].Value<string>(), Does.EndWith("/Label"));

            var missing = Assert.Throws<McpToolException>(() => Call(Tool(catalog, "probe_member")));
            Assert.That(missing.Code, Is.EqualTo("invalid_params"));
            Assert.That(missing.Message, Does.Contain("'member'"));
        }

        [Test]
        public void DefaultsFillAnOmittedInput()
        {
            this.WriteProbe(
                "probe_default", $"@type:{SampleType}/{{member}}",
                extra: @", ""inputs"": { ""member"": { ""type"": ""string"", ""default"": ""Counter"" } }");
            var (_, catalog, _) = this.Load();

            var result = Call(Tool(catalog, "probe_default"));

            Assert.That(result["reads"]["value"]["value"].Value<int>(), Is.EqualTo(1));
        }

        [Test]
        public void UndeclaredPlaceholderIsRefusedAtLoad()
        {
            var file = this.WriteProbe("probe_undeclared", $"@type:{SampleType}/{{member}}");

            var (_, _, errors) = this.Load();

            Assert.That(errors.Single(), Does.StartWith(file).And.Contain("'member'"));
        }

        [Test]
        public void ChangesModeReportsOnlyWhatMoved()
        {
            this.Write(this.project, "probe_changes.json", $@"{{
  ""name"": ""probe_changes"", ""description"": ""Diffs."", ""kind"": ""probe"", ""mode"": ""changes"",
  ""reads"": [
    {{ ""id"": ""counter"", ""path"": ""@type:{SampleType}/Counter"" }},
    {{ ""id"": ""label"", ""path"": ""@type:{SampleType}/Label"" }}
  ]
}}");
            var (_, catalog, errors) = this.Load();
            Assert.That(errors, Is.Empty, string.Join("\n", errors));
            var tool = Tool(catalog, "probe_changes");

            var first = Call(tool);
            Assert.That(first["mode"].Value<string>(), Is.EqualTo("changes"));
            Assert.That(first["baseline"].Value<bool>(), Is.True);
            Assert.That(((JObject)first["reads"]).Count, Is.EqualTo(2));
            Assert.That(first["changed"].ToObject<string[]>(), Is.EqualTo(new[] { "counter", "label" }));

            var second = Call(tool);
            Assert.That(second["baseline"], Is.Null);
            Assert.That(((JObject)second["reads"]).Count, Is.EqualTo(0));
            Assert.That(second["changed"], Is.Empty);

            ProbeSample.Counter = 7;
            var third = Call(tool);
            Assert.That(third["changed"].ToObject<string[]>(), Is.EqualTo(new[] { "counter" }));
            Assert.That(((JObject)third["reads"]).Properties().Select(p => p.Name), Is.EqualTo(new[] { "counter" }));
            Assert.That(third["reads"]["counter"]["value"].Value<int>(), Is.EqualTo(7));
        }

        [Test]
        public void FullModeReturnsEverythingAndStillNamesChanges()
        {
            this.WriteProbe("probe_full", $"@type:{SampleType}/Counter", mode: "full");
            var (_, catalog, _) = this.Load();
            var tool = Tool(catalog, "probe_full");

            Call(tool);
            var unchanged = Call(tool);
            Assert.That(((JObject)unchanged["reads"]).Count, Is.EqualTo(1));
            Assert.That(unchanged["changed"], Is.Empty);
            Assert.That(unchanged["baseline"], Is.Null);

            ProbeSample.Counter = 2;
            var moved = Call(tool);
            Assert.That(moved["changed"].ToObject<string[]>(), Is.EqualTo(new[] { "value" }));
        }

        [Test]
        public void ChangingTheDefinitionResetsTheBaseline()
        {
            var file = this.WriteProbe("probe_reset", $"@type:{SampleType}/Counter", mode: "changes");
            var (_, first, _) = this.Load();
            Call(Tool(first, "probe_reset"));
            Assert.That(Call(Tool(first, "probe_reset"))["baseline"], Is.Null);

            var (_, reloadedSame, _) = this.Load();
            Assert.That(Call(Tool(reloadedSame, "probe_reset"))["baseline"], Is.Null,
                "Reloading an unchanged definition keeps the baseline.");

            File.WriteAllText(file, File.ReadAllText(file).Replace("Reads probe_reset.", "Reads differently."));
            var (_, changed, _) = this.Load();

            Assert.That(Call(Tool(changed, "probe_reset"))["baseline"].Value<bool>(), Is.True);
        }

        [Test]
        public void FieldsNarrowTheValue()
        {
            this.Write(this.project, "probe_fields.json", $@"{{
  ""name"": ""probe_fields"", ""description"": ""Two fields."", ""kind"": ""probe"",
  ""reads"": [ {{ ""id"": ""sample"", ""path"": ""@type:{SampleType}"", ""fields"": [""Counter"", ""Label""] }} ]
}}");
            var (_, catalog, errors) = this.Load();
            Assert.That(errors, Is.Empty, string.Join("\n", errors));

            var result = Call(Tool(catalog, "probe_fields"));

            Assert.That(result["reads"]["sample"]["value"]["Counter"].Value<int>(), Is.EqualTo(1));
            Assert.That(result["reads"]["sample"]["value"]["Label"].Value<string>(), Is.EqualTo("a"));
        }

        [Test]
        public void UndoGroupIsOnlyForSequences()
        {
            var file = this.WriteProbe("probe_undo", $"@type:{SampleType}/Counter", extra: @", ""undoGroup"": ""Probe""");

            var (_, _, errors) = this.Load();

            Assert.That(errors.Single(), Does.StartWith(file).And.Contain("'undoGroup'"));
        }

        [Test]
        public void DirectToolDryRunEchoesArgumentsWithoutTheFlags()
        {
            this.WriteProbe(
                "probe_dry", $"@type:{SampleType}/{{member}}",
                extra: @", ""destructive"": true, ""inputs"": { ""member"": { ""type"": ""string"" } }");
            var (_, catalog, errors) = this.Load();
            Assert.That(errors, Is.Empty, string.Join("\n", errors));
            var tool = Tool(catalog, "probe_dry");

            var preview = Call(tool, new JObject { ["member"] = "Label", ["dry_run"] = true, ["confirm"] = true });

            Assert.That(preview["dry_run"].Value<bool>(), Is.True);
            Assert.That(preview["tool"].Value<string>(), Is.EqualTo("probe_dry"));
            Assert.That(preview["would_execute"].Value<bool>(), Is.True);
            Assert.That(preview["arguments"]["member"].Value<string>(), Is.EqualTo("Label"));
            Assert.That(preview["arguments"]["confirm"], Is.Null);
            Assert.That(preview["arguments"]["dry_run"], Is.Null);

            var refused = Assert.Throws<McpToolException>(() => Call(tool, new JObject { ["member"] = "Label" }));
            Assert.That(refused.Code, Is.EqualTo("confirmation_required"));

            var executed = Call(tool, new JObject { ["member"] = "Label", ["confirm"] = true });
            Assert.That(executed["reads"]["value"]["value"].Value<string>(), Is.EqualTo("a"));
        }

        // ── script ──

        [Test]
        public void WrapWithoutArgsIsUnchanged()
        {
            const string code = "return 1 + 1;";

            Assert.That(CodeExecutor.Wrap(code), Is.EqualTo(CodeExecutor.Wrap(code, withArgs: false)));
            Assert.That(CodeExecutor.Wrap(code, withArgs: false), Does.Contain("public static object Execute()"));
            Assert.That(CodeExecutor.Wrap(code, withArgs: false), Does.Not.Contain("Newtonsoft"));

            var withArgs = CodeExecutor.Wrap(code, withArgs: true);
            Assert.That(withArgs, Does.Contain("public static object Execute(Newtonsoft.Json.Linq.JObject args)"));
            Assert.That(withArgs, Does.Contain("using Newtonsoft.Json.Linq;"));
        }

        [Test]
        public void MissingScriptFileIsRefusedAtLoad()
        {
            var file = this.Write(this.project, "ghost.json", @"{
  ""name"": ""ghost"", ""description"": ""Names a file that is not there."", ""kind"": ""script"", ""file"": ""ghost.cs""
}");

            var (_, _, errors) = this.Load();

            Assert.That(errors.Single(), Does.StartWith(file).And.Contain("ghost.cs").And.Contain("does not exist"));
        }

        [Test]
        public void ScriptReceivesItsArguments()
        {
            File.WriteAllText(Path.Combine(this.project, "double.cs"), "return args[\"factor\"].Value<double>() * 2;");
            this.Write(this.project, "double.json", @"{
  ""name"": ""double_it"", ""description"": ""Doubles factor."", ""kind"": ""script"", ""file"": ""double.cs"",
  ""inputs"": { ""factor"": { ""type"": ""number"", ""default"": 1.5 } }
}");
            var (_, catalog, errors) = this.Load();
            Assert.That(errors, Is.Empty, string.Join("\n", errors));

            var result = Call(Tool(catalog, "double_it"), new JObject { ["factor"] = 4 });
            Assert.That(result["returnValue"].Value<double>(), Is.EqualTo(8d));

            File.WriteAllText(Path.Combine(this.project, "double.cs"), "return args[\"factor\"].Value<double>() * 3;");
            var edited = Call(Tool(catalog, "double_it"), new JObject { ["factor"] = 4 });
            Assert.That(edited["returnValue"].Value<double>(), Is.EqualTo(12d), "The file is read on every call.");

            File.WriteAllText(Path.Combine(this.project, "double.cs"), "throw new System.InvalidOperationException(\"deliberate\");");
            var failed = Assert.Throws<McpToolException>(() => Call(Tool(catalog, "double_it")));
            Assert.That(failed.Code, Is.EqualTo("tool_failed"));
            Assert.That(failed.Message, Does.Contain("deliberate"));
        }

        // ── sequence ──

        private void WriteSequence(string name, string steps, string extra = "")
        {
            this.Write(this.project, name + ".json", $@"{{
  ""name"": ""{name}"", ""description"": ""Runs steps."", ""kind"": ""sequence"",
  ""steps"": [ {steps} ]{extra}
}}");
        }

        [Test]
        public void StepsSeeEarlierResults()
        {
            this.WriteSequence("seq_chain", @"
    { ""id"": ""first"", ""tool"": ""seq_echo"", ""arguments"": { ""text"": ""hi {suffix}"" } },
    { ""id"": ""second"", ""tool"": ""seq_echo"", ""arguments"": { ""text"": ""{{first.text}}"" } },
    { ""id"": ""third"", ""tool"": ""seq_echo"", ""arguments"": { ""text"": ""{{ first.length }}"" } }",
                extra: @", ""inputs"": { ""suffix"": { ""type"": ""string"", ""default"": ""there"" } }");
            var (_, catalog, errors) = this.Load(typeof(StepTools));
            Assert.That(errors, Is.Empty, string.Join("\n", errors));

            var result = Call(Tool(catalog, "seq_chain"));
            var steps = (JArray)result["steps"];

            Assert.That(steps.Count, Is.EqualTo(3));
            Assert.That(steps.All(s => s["ok"].Value<bool>()), Is.True);
            Assert.That(steps[0]["result"]["text"].Value<string>(), Is.EqualTo("hi there"));
            Assert.That(steps[1]["result"]["text"].Value<string>(), Is.EqualTo("hi there"));
            Assert.That(steps[2]["result"]["text"].Value<string>(), Is.EqualTo("8"), "A number token is coerced by the step's own binder.");
            Assert.That(steps[2]["id"].Value<string>(), Is.EqualTo("third"));
            Assert.That(steps[2]["tool"].Value<string>(), Is.EqualTo("seq_echo"));
        }

        [Test]
        public void AFailedStepStopsTheSequence()
        {
            this.WriteSequence("seq_stops", @"
    { ""id"": ""a"", ""tool"": ""seq_echo"", ""arguments"": { ""text"": ""x"" } },
    { ""id"": ""b"", ""tool"": ""seq_fail"" },
    { ""id"": ""c"", ""tool"": ""seq_echo"", ""arguments"": { ""text"": ""never"" } }");
            var (_, catalog, errors) = this.Load(typeof(StepTools));
            Assert.That(errors, Is.Empty, string.Join("\n", errors));

            var steps = (JArray)Call(Tool(catalog, "seq_stops"))["steps"];

            Assert.That(steps.Count, Is.EqualTo(2));
            Assert.That(steps[1]["ok"].Value<bool>(), Is.False);
            Assert.That(steps[1]["error"]["code"].Value<string>(), Is.EqualTo("boom"));
            Assert.That(steps[1]["error"]["message"].Value<string>(), Is.EqualTo("deliberate"));
            Assert.That(steps[1]["result"], Is.Null);
        }

        [Test]
        public void ContinueOnErrorKeepsGoing()
        {
            this.WriteSequence("seq_continues", @"
    { ""id"": ""a"", ""tool"": ""seq_fail"", ""continue_on_error"": true },
    { ""id"": ""b"", ""tool"": ""seq_echo"", ""arguments"": { ""text"": ""after"" } }");
            var (_, catalog, _) = this.Load(typeof(StepTools));

            var steps = (JArray)Call(Tool(catalog, "seq_continues"))["steps"];

            Assert.That(steps.Count, Is.EqualTo(2));
            Assert.That(steps[0]["ok"].Value<bool>(), Is.False);
            Assert.That(steps[1]["result"]["text"].Value<string>(), Is.EqualTo("after"));
        }

        [Test]
        public void ADestructiveStepMakesTheSequenceDestructiveAndConfirmIsForwarded()
        {
            this.WriteSequence("seq_wipes", @"
    { ""id"": ""wipe"", ""tool"": ""seq_wipe"", ""arguments"": { ""what"": ""cache"" } }");
            this.WriteSequence("seq_wipes_nested", @"{ ""id"": ""inner"", ""tool"": ""seq_wipes"" }");
            var (_, catalog, errors) = this.Load(typeof(StepTools));
            Assert.That(errors, Is.Empty, string.Join("\n", errors));
            var tool = Tool(catalog, "seq_wipes");

            Assert.That(tool.Destructive, Is.True, "A sequence inherits destructiveness from its steps.");
            Assert.That(((JObject)tool.InputSchema["properties"])["confirm"]["type"].Value<string>(), Is.EqualTo("boolean"));
            Assert.That(Tool(catalog, "seq_wipes_nested").Destructive, Is.True, "Through a sibling sequence too.");

            var refused = Assert.Throws<McpToolException>(() => Call(tool));
            Assert.That(refused.Code, Is.EqualTo("confirmation_required"), "The sequence asks once, before any step runs.");

            var confirmed = (JArray)Call(tool, new JObject { ["confirm"] = true })["steps"];
            Assert.That(confirmed[0]["ok"].Value<bool>(), Is.True);
            Assert.That(confirmed[0]["result"]["wiped"].Value<string>(), Is.EqualTo("cache"));

            var nested = (JArray)Call(Tool(catalog, "seq_wipes_nested"), new JObject { ["confirm"] = true })["steps"];
            Assert.That(nested[0]["ok"].Value<bool>(), Is.True, nested.ToString());
            Assert.That(nested[0]["result"]["steps"][0]["result"]["wiped"].Value<string>(), Is.EqualTo("cache"));
        }

        [Test]
        public void DestructiveFalseWithADestructiveStepIsRefusedAtLoad()
        {
            this.WriteSequence("seq_denies", @"
    { ""id"": ""wipe"", ""tool"": ""seq_wipe"", ""arguments"": { ""what"": ""cache"" } }",
                extra: @", ""destructive"": false");

            var (_, catalog, errors) = this.Load(typeof(StepTools));

            Assert.That(catalog.TryGet("seq_denies", out _), Is.False);
            Assert.That(errors.Single(), Does.Contain("seq_denies.json").And.Contain("'seq_wipe'").And.Contain("confirm cannot be forwarded"));
        }

        [Test]
        public void ReferencingALaterStepIsRefusedAtLoad()
        {
            this.WriteSequence("seq_forward", @"
    { ""id"": ""a"", ""tool"": ""seq_echo"", ""arguments"": { ""text"": ""{{b.text}}"" } },
    { ""id"": ""b"", ""tool"": ""seq_echo"", ""arguments"": { ""text"": ""x"" } }");

            var (_, catalog, errors) = this.Load(typeof(StepTools));

            Assert.That(catalog.TryGet("seq_forward", out _), Is.False);
            Assert.That(errors.Single(), Does.Contain("not an earlier step"));
        }

        [Test]
        public void UnknownStepToolIsRefusedAtLoad()
        {
            this.WriteSequence("seq_unknown", @"{ ""id"": ""a"", ""tool"": ""no_such_tool"" }");

            var (_, _, errors) = this.Load(typeof(StepTools));

            Assert.That(errors.Single(), Does.Contain("'no_such_tool'"));
        }

        [Test]
        public void SequenceMainThreadIsTheOrOfItsSteps()
        {
            this.WriteSequence("seq_worker", @"{ ""id"": ""a"", ""tool"": ""seq_echo"", ""arguments"": { ""text"": ""x"" } }");
            this.WriteSequence("seq_needs_main", @"
    { ""id"": ""a"", ""tool"": ""seq_echo"", ""arguments"": { ""text"": ""x"" } },
    { ""id"": ""b"", ""tool"": ""seq_main"" }");
            this.WriteSequence("seq_contradiction", @"{ ""id"": ""a"", ""tool"": ""seq_main"" }", extra: @", ""mainThread"": false");
            this.WriteSequence("seq_nested", @"{ ""id"": ""a"", ""tool"": ""seq_needs_main"" }");

            var (_, catalog, errors) = this.Load(typeof(StepTools));

            Assert.That(Tool(catalog, "seq_worker").MainThread, Is.False);
            Assert.That(Tool(catalog, "seq_needs_main").MainThread, Is.True);
            Assert.That(Tool(catalog, "seq_nested").MainThread, Is.True, "A sibling sequence counts like any other step.");
            Assert.That(catalog.TryGet("seq_contradiction", out _), Is.False);
            Assert.That(errors.Single(), Does.Contain("seq_contradiction.json").And.Contain("'mainThread' is false"));
        }

        [Test]
        public void UndoGroupOnASequenceNeedsTheMainThread()
        {
            this.WriteSequence("seq_undo_worker", @"{ ""id"": ""a"", ""tool"": ""seq_echo"", ""arguments"": { ""text"": ""x"" } }",
                extra: @", ""undoGroup"": ""Chain""");
            this.WriteSequence("seq_undo_main", @"{ ""id"": ""a"", ""tool"": ""seq_echo"", ""arguments"": { ""text"": ""x"" } }",
                extra: @", ""undoGroup"": ""Chain"", ""mainThread"": true");

            var (_, catalog, errors) = this.Load(typeof(StepTools));

            Assert.That(catalog.TryGet("seq_undo_worker", out _), Is.False);
            Assert.That(errors.Single(), Does.Contain("seq_undo_worker.json").And.Contain("'undoGroup'"));
            Assert.That(Tool(catalog, "seq_undo_main").UndoGroup, Is.EqualTo("Chain"));
            Assert.That(Tool(catalog, "seq_undo_main").MainThread, Is.True);
        }

        [Test]
        public void SequenceCanCallASiblingDefinedTool()
        {
            this.WriteProbe("probe_sibling", $"@type:{SampleType}/Label");
            this.WriteSequence("seq_sibling", @"
    { ""id"": ""read"", ""tool"": ""probe_sibling"" },
    { ""id"": ""echo"", ""tool"": ""seq_echo"", ""arguments"": { ""text"": ""{{read.reads.value.value}}"" } }");
            var (_, catalog, errors) = this.Load(typeof(StepTools));
            Assert.That(errors, Is.Empty, string.Join("\n", errors));

            var steps = (JArray)Call(Tool(catalog, "seq_sibling"))["steps"];

            Assert.That(steps[1]["result"]["text"].Value<string>(), Is.EqualTo("a"));
        }

        [Test]
        public void MutualRecursionBetweenSequencesIsRefusedAtLoad()
        {
            this.WriteSequence("seq_ping", @"{ ""id"": ""a"", ""tool"": ""seq_pong"" }");
            this.WriteSequence("seq_pong", @"{ ""id"": ""a"", ""tool"": ""seq_echo"", ""arguments"": { ""text"": ""x"" } }, { ""id"": ""b"", ""tool"": ""seq_ping"" }");
            this.WriteSequence("seq_caller", @"{ ""id"": ""a"", ""tool"": ""seq_ping"" }");
            this.WriteSequence("seq_fine", @"{ ""id"": ""a"", ""tool"": ""seq_echo"", ""arguments"": { ""text"": ""x"" } }");

            var (_, catalog, errors) = this.Load(typeof(StepTools));

            Assert.That(catalog.TryGet("seq_ping", out _), Is.False);
            Assert.That(catalog.TryGet("seq_pong", out _), Is.False);
            Assert.That(catalog.TryGet("seq_fine", out _), Is.True, "Sequences outside the cycle still load.");

            var cycle = errors.Where(e => e.Contains("cycle")).ToList();
            Assert.That(cycle, Has.Count.EqualTo(2), string.Join("\n", errors));
            Assert.That(cycle, Has.All.Contains("seq_ping").And.All.Contains("seq_pong").And.All.Contains(" -> "));
            Assert.That(cycle.Any(e => e.Contains("seq_ping -> seq_pong -> seq_ping") || e.Contains("seq_pong -> seq_ping -> seq_pong")), Is.True, string.Join("\n", cycle));
            Assert.That(errors.Single(e => e.Contains("seq_caller.json")), Does.Contain("'seq_ping'"),
                "A sequence that only calls into the cycle fails as naming an unloaded tool.");
        }

        [Test]
        public void AnOutOfRangeIntegerIsAnErrorEntryNotAThrow()
        {
            var bad = this.WriteProbe("probe_huge", $"@type:{SampleType}/Counter", extra: @", ""maxResultSizeChars"": 99999999999");
            this.WriteProbe("probe_fine", $"@type:{SampleType}/Counter");

            var (set, catalog, errors) = this.Load();

            Assert.That(set.Entries.Select(e => e.Name), Is.EqualTo(new[] { "probe_fine" }));
            Assert.That(errors.Single(), Does.StartWith(bad).And.Contain("'maxResultSizeChars'").And.Contain("integer"));
            Assert.That(catalog.TryGet("probe_fine", out _), Is.True);
        }

        [Test]
        public void ProbeOffTheMainThreadIsRefusedAtLoad()
        {
            var file = this.WriteProbe("probe_worker", $"@type:{SampleType}/Counter", extra: @", ""mainThread"": false");

            var (_, catalog, errors) = this.Load();

            Assert.That(catalog.TryGet("probe_worker", out _), Is.False);
            Assert.That(errors.Single(), Does.StartWith(file).And.Contain("'mainThread'").And.Contain("main-thread only"));
        }

        [Test]
        public void ScriptThatDoesNotCompileIsTheCallersError()
        {
            File.WriteAllText(Path.Combine(this.project, "broken.cs"), "return 1 +;");
            this.Write(this.project, "broken.json", @"{
  ""name"": ""broken_script"", ""description"": ""Does not compile."", ""kind"": ""script"", ""file"": ""broken.cs"", ""idempotency"": ""safe""
}");
            var (_, catalog, errors) = this.Load();
            Assert.That(errors, Is.Empty, string.Join("\n", errors));

            var failed = Assert.Throws<McpToolException>(() => Call(Tool(catalog, "broken_script")));

            Assert.That(failed.Code, Is.EqualTo("script_compile_error"));
            Assert.That(failed.HttpStatus, Is.EqualTo(400), "A 5xx would be retried by clients that retry safe calls.");
            Assert.That(failed.Message, Does.Contain("broken.cs"));
        }

        [Test]
        public void InputsAreCheckedAgainstTheirDeclaredTypeAtCallTime()
        {
            this.WriteProbe(
                "probe_typed", $"@type:{SampleType}/Counter",
                extra: @", ""inputs"": {
    ""n"": { ""type"": ""integer"", ""default"": 1 },
    ""ratio"": { ""type"": ""number"" },
    ""flag"": { ""type"": ""boolean"" },
    ""mode"": { ""type"": ""string"", ""enum"": [ ""full"", ""changes"" ] },
    ""must"": { ""type"": ""string"", ""required"": true },
    ""bag"": { ""type"": ""object"" },
    ""items"": { ""type"": ""array"" }
  }");
            var (_, catalog, errors) = this.Load();
            Assert.That(errors, Is.Empty, string.Join("\n", errors));
            var tool = Tool(catalog, "probe_typed");

            Assert.That(Call(tool, new JObject { ["must"] = "x", ["n"] = 3, ["ratio"] = 2, ["flag"] = true, ["mode"] = "full", ["bag"] = new JObject(), ["items"] = new JArray() })["reads"], Is.Not.Null);
            Assert.That(Call(tool, new JObject { ["must"] = "x", ["ratio"] = 2.5 })["reads"], Is.Not.Null, "An integer or a float is a number.");

            void Refused(JObject arguments, string input)
            {
                var error = Assert.Throws<McpToolException>(() => Call(tool, arguments), arguments.ToString());
                Assert.That(error.Code, Is.EqualTo("invalid_params"), arguments.ToString());
                Assert.That(error.Message, Does.Contain($"'{input}'"), arguments.ToString());
            }

            Refused(new JObject { ["must"] = "x", ["n"] = "abc" }, "n");
            Refused(new JObject { ["must"] = "x", ["n"] = 1.5 }, "n");
            Refused(new JObject { ["must"] = "x", ["ratio"] = "2" }, "ratio");
            Refused(new JObject { ["must"] = "x", ["flag"] = "true" }, "flag");
            Refused(new JObject { ["must"] = "x", ["mode"] = "partial" }, "mode");
            Refused(new JObject { ["must"] = "x", ["bag"] = new JArray() }, "bag");
            Refused(new JObject { ["must"] = "x", ["items"] = new JObject() }, "items");
            Refused(new JObject { ["must"] = 5 }, "must");
            Refused(new JObject(), "must");
        }

        [Test]
        public void ASequenceWaitsForADeferredStepBeforeTheNextOneReadsIt()
        {
            this.WriteSequence("seq_waits", @"
    { ""id"": ""slow"", ""tool"": ""seq_defer_worker"" },
    { ""id"": ""echo"", ""tool"": ""seq_echo"", ""arguments"": { ""text"": ""{{slow.answer}}"" } }");
            var (_, catalog, errors) = this.Load(typeof(StepTools));
            Assert.That(errors, Is.Empty, string.Join("\n", errors));

            var tool = Tool(catalog, "seq_waits");
            Assert.That(tool.MainThread, Is.False);

            var pending = McpMainThreadDispatcher.CreateDeferred();
            StepTools.Pending = pending;
            var settle = Task.Run(() =>
            {
                Thread.Sleep(50);
                pending.Complete(new JObject { ["answer"] = "later" });
            });

            var result = Call(tool);

            Assert.That(settle.Wait(5000), Is.True);
            Assert.That(result, Is.Not.InstanceOf<DeferredToolResult>(), "Off the main thread the sequence answers inline.");
            var steps = (JArray)result["steps"];
            Assert.That(steps[0]["result"]["answer"].Value<string>(), Is.EqualTo("later"), "The marker must never be recorded as the step's result.");
            Assert.That(steps[1]["result"]["text"].Value<string>(), Is.EqualTo("later"));
        }

        [Test]
        public void AFailedDeferredStepIsReportedLikeAnyOtherFailure()
        {
            this.WriteSequence("seq_waits_fail", @"
    { ""id"": ""slow"", ""tool"": ""seq_defer_worker"" },
    { ""id"": ""echo"", ""tool"": ""seq_echo"", ""arguments"": { ""text"": ""never"" } }");
            var (_, catalog, _) = this.Load(typeof(StepTools));

            var pending = McpMainThreadDispatcher.CreateDeferred();
            StepTools.Pending = pending;
            Task.Run(() =>
            {
                Thread.Sleep(50);
                pending.Fail(new McpToolException("cancelled", "Server stopped.", 409));
            });

            var steps = (JArray)Call(Tool(catalog, "seq_waits_fail"))["steps"];

            Assert.That(steps.Count, Is.EqualTo(1));
            Assert.That(steps[0]["ok"].Value<bool>(), Is.False);
            Assert.That(steps[0]["error"]["code"].Value<string>(), Is.EqualTo("cancelled"));
        }

        [UnityTest]
        public IEnumerator AMainThreadSequenceComposesMultiFrameSteps()
        {
            this.WriteSequence("seq_frames", @"
    { ""id"": ""first"", ""tool"": ""seq_defer_frames"", ""arguments"": { ""frames"": 3 } },
    { ""id"": ""echo"", ""tool"": ""seq_echo"", ""arguments"": { ""text"": ""{{first.frames}}"" } },
    { ""id"": ""second"", ""tool"": ""seq_defer_frames"", ""arguments"": { ""frames"": 2 } }");
            this.WriteSequence("seq_frames_outer", @"
    { ""id"": ""inner"", ""tool"": ""seq_frames"" },
    { ""id"": ""echo"", ""tool"": ""seq_echo"", ""arguments"": { ""text"": ""{{inner.steps[2].result.frames}}"" } }");
            var (_, catalog, errors) = this.Load(typeof(StepTools));
            Assert.That(errors, Is.Empty, string.Join("\n", errors));
            Assert.That(Tool(catalog, "seq_frames").MainThread, Is.True);

            try
            {
                var outer = Call(Tool(catalog, "seq_frames_outer"));
                var deferred = outer as DeferredToolResult;
                Assert.That(deferred, Is.Not.Null, "A sequence with a multi-frame step answers through a deferred item.");

                for (var frame = 0; frame < 60 && !deferred.Item.IsCompleted; frame++)
                {
                    yield return null;
                }

                Assert.That(deferred.Item.IsCompleted, Is.True, "The sequence never finished.");
                Assert.That(deferred.Item.Error, Is.Null, deferred.Item.Error?.ToString());
                Assert.That(FrameSequencer.ActiveCount, Is.Zero);

                var steps = (JArray)deferred.Item.Result["steps"];
                Assert.That(steps[1]["result"]["text"].Value<string>(), Is.EqualTo("2"));

                var inner = (JArray)steps[0]["result"]["steps"];
                Assert.That(inner.Count, Is.EqualTo(3));
                Assert.That(inner[0]["result"]["frames"].Value<int>(), Is.EqualTo(3));
                Assert.That(inner[1]["result"]["text"].Value<string>(), Is.EqualTo("3"), "The second step read the first step's real result.");
                Assert.That(inner[2]["result"]["frames"].Value<int>(), Is.EqualTo(2));
            }
            finally
            {
                FrameSequencer.CancelAll("Test teardown.");
            }
        }
    }
}
