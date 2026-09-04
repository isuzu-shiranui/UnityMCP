using System.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Every tool lands in exactly one group, the attribute can override it, and a client can
    /// ask for a subset without seeing the rest.
    /// </summary>
    [TestFixture]
    internal sealed class McpToolGroupsTests
    {
        private static class Tools
        {
            [McpTool("console_read_logs_x", "Reads.", Idempotency = McpIdempotency.Safe, MainThread = false)]
            public static int Read() => 1;

            [McpTool("gameobject_create_x", "Creates.")]
            public static int Create() => 2;

            [McpTool("render_compare_x", "Compares.", Idempotency = McpIdempotency.Safe)]
            public static int Compare() => 3;

            [McpTool("odd_name", "Overridden into timeline.", Group = McpToolGroups.Timeline)]
            public static int Odd() => 4;
        }

        private static class BadGroup
        {
            [McpTool("bad_group", "Names a group that does not exist.", Group = "misc")]
            public static int Bad() => 0;
        }

        private static class UndoOffThread
        {
            [McpTool("undo_off_thread", "Undo cannot run off the main thread.", MainThread = false, UndoGroup = "x")]
            public static int Bad() => 0;
        }

        [Test]
        public void GroupsDeriveFromThePrefixAndTheAttributeOverrides()
        {
            var catalog = ToolCatalog.BuildFromTypes(new[] { typeof(Tools) });

            Assert.That(catalog.Errors, Is.Empty);
            Assert.That(Group(catalog, "console_read_logs_x"), Is.EqualTo(McpToolGroups.Diagnostics));
            Assert.That(Group(catalog, "gameobject_create_x"), Is.EqualTo(McpToolGroups.Authoring));
            Assert.That(Group(catalog, "render_compare_x"), Is.EqualTo(McpToolGroups.Rendering));
            Assert.That(Group(catalog, "odd_name"), Is.EqualTo(McpToolGroups.Timeline));
        }

        [Test]
        public void ReadOnlyToolsWithAnAuthoringPrefixAreDiagnostics()
        {
            Assert.That(McpToolGroups.Derive("scene_browse_hierarchy"), Is.EqualTo(McpToolGroups.Diagnostics));
            Assert.That(McpToolGroups.Derive("inspect_write"), Is.EqualTo(McpToolGroups.Authoring));
            Assert.That(McpToolGroups.Derive("play_mode_status"), Is.EqualTo(McpToolGroups.Diagnostics));
            Assert.That(McpToolGroups.Derive("play_mode_play"), Is.EqualTo(McpToolGroups.Authoring));
        }

        [Test]
        public void InputToolsDeriveTheInputGroup()
        {
            Assert.That(McpToolGroups.Derive("input_pointer"), Is.EqualTo(McpToolGroups.Input));
            Assert.That(McpToolGroups.Known, Contains.Item(McpToolGroups.Input));
            Assert.That(McpToolGroups.IsKnown(McpToolGroups.Input), Is.True);
        }

        [Test]
        public void DefinitionsToolsDeriveTheDiagnosticsGroup()
        {
            Assert.That(McpToolGroups.Derive("definitions_list"), Is.EqualTo(McpToolGroups.Diagnostics));
            Assert.That(McpToolGroups.Derive("definitions_reload"), Is.EqualTo(McpToolGroups.Diagnostics),
                "Any future sibling must land where definitions_list is, not in code.");
        }

        [Test]
        public void AnimatorReadToolsAreDiagnosticsAndTheEditingOnesAuthoring()
        {
            Assert.That(McpToolGroups.Derive("animator_inspect"), Is.EqualTo(McpToolGroups.Diagnostics));
            Assert.That(McpToolGroups.Derive("animator_audit"), Is.EqualTo(McpToolGroups.Diagnostics));
            Assert.That(McpToolGroups.Derive("animator_add_layer"), Is.EqualTo(McpToolGroups.Authoring));
            Assert.That(McpToolGroups.Derive("animator_set_write_defaults"), Is.EqualTo(McpToolGroups.Authoring));
        }

        [Test]
        public void EveryLiveToolHasAKnownGroup()
        {
            foreach (var tool in ToolCatalog.Build().Tools)
            {
                Assert.That(McpToolGroups.IsKnown(tool.Group), Is.True, $"'{tool.Name}' is in group '{tool.Group}'");
            }
        }

        [Test]
        public void UnknownGroupAndOffThreadUndoAreDiscoveryErrors()
        {
            var bad = ToolCatalog.BuildFromTypes(new[] { typeof(BadGroup) });
            Assert.That(bad.Errors, Has.Some.Contains("unknown group 'misc'"));
            Assert.That(bad.TryGet("bad_group", out _), Is.False);

            var undo = ToolCatalog.BuildFromTypes(new[] { typeof(UndoOffThread) });
            Assert.That(undo.Errors, Has.Some.Contains("UndoGroup with MainThread = false"));
            Assert.That(undo.TryGet("undo_off_thread", out _), Is.False);
        }

        [Test]
        public void CatalogAndEndpointFilterByGroup()
        {
            var catalog = ToolCatalog.BuildFromTypes(new[] { typeof(Tools) });

            var json = catalog.ToJson(new[] { McpToolGroups.Rendering, McpToolGroups.Timeline });
            Assert.That(json["tools"].Select(t => t["name"].Value<string>()), Is.EquivalentTo(new[] { "render_compare_x", "odd_name" }));
            Assert.That(json["tools"][0]["group"], Is.Not.Null);

            var envelope = JObject.Parse(System.Text.Encoding.UTF8.GetString(catalog.CatalogEnvelopeUtf8(new[] { McpToolGroups.Rendering })));
            Assert.That(envelope["status"].Value<string>(), Is.EqualTo("success"));
            Assert.That(envelope["result"]["tools"].Count(), Is.EqualTo(1));
            Assert.That(ReferenceEquals(
                catalog.ToolsArrayUtf8(new[] { "rendering", "timeline" }, true),
                catalog.ToolsArrayUtf8(new[] { "timeline", "rendering" }, true)), Is.True, "Group order does not split the cache.");

            var text = JArray.Parse(catalog.ToolsArrayJson(new[] { McpToolGroups.Rendering }, mcpShape: true));
            Assert.That(text.Select(t => t["name"].Value<string>()), Is.EqualTo(new[] { "render_compare_x" }));
            Assert.That(text[0]["annotations"]["readOnlyHint"].Value<bool>(), Is.True);

            var runner = new ToolCallRunner(new McpMainThreadDispatcher(), new McpJobRegistry(), () => 250);
            var endpoint = new McpStreamableHttpEndpoint(() => catalog, runner.Run, () => "x");
            var body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}";

            var filtered = endpoint.Handle("POST", null, body, "diagnostics");
            Assert.That(ToolsOf(filtered).Select(t => t["name"].Value<string>()), Is.EqualTo(new[] { "console_read_logs_x" }));

            var all = endpoint.Handle("POST", null, body, null);
            Assert.That(ToolsOf(all).Count, Is.EqualTo(4));

            var unknown = endpoint.Handle("POST", null, body, "misc");
            Assert.That(unknown.Status, Is.EqualTo(400));

            // Calls are not limited by the list filter: a client that narrowed its list can still
            // call a tool it learned about elsewhere.
            var call = endpoint.Handle("POST", null,
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"render_compare_x\"}}", "diagnostics");
            Assert.That(call.Body["result"]["isError"], Is.Null);
        }

        // The endpoint splices cached text in as JRaw, so the array only exists once serialised.
        private static JArray ToolsOf(EndpointResponse response) =>
            (JArray)JObject.Parse(response.Text)["result"]["tools"];

        private static string Group(ToolCatalog catalog, string name)
        {
            Assert.That(catalog.TryGet(name, out var descriptor), Is.True, name);
            return descriptor.Group;
        }
    }
}
