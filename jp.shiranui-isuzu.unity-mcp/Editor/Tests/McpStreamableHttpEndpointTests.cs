using System;
using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// The MCP protocol as clients see it, without a socket. The endpoint takes method, headers
    /// and body, so every rule about framing, negotiation and error mapping is checked here.
    /// </summary>
    [TestFixture]
    internal sealed class McpStreamableHttpEndpointTests
    {
        private static class Tools
        {
            [McpTool("ep_echo", "Echoes text.", Idempotency = McpIdempotency.Safe, MainThread = false)]
            public static string Echo([McpArg("text", "Text")] string text) => text;

            [McpTool("ep_delete", "Deletes something.", Destructive = true, MainThread = false)]
            public static void Delete([McpArg("path", "Path")] string path) => _ = path;
        }

        private ToolCatalog catalog;
        private McpMainThreadDispatcher dispatcher;
        private McpJobRegistry jobs;
        private McpStreamableHttpEndpoint endpoint;

        [SetUp]
        public void SetUp()
        {
            this.catalog = ToolCatalog.BuildFromTypes(new[] { typeof(Tools) });
            this.dispatcher = new McpMainThreadDispatcher();
            this.jobs = new McpJobRegistry();
            var runner = new ToolCallRunner(this.dispatcher, this.jobs, () => 250);
            this.endpoint = new McpStreamableHttpEndpoint(() => this.catalog, runner.Run, () => "4.0.0-test");
        }

        private static IReadOnlyDictionary<string, string> Headers(params (string, string)[] pairs)
        {
            var headers = new Dictionary<string, string>();
            foreach (var (key, value) in pairs)
            {
                headers[key] = value;
            }

            return headers;
        }

        private static string Request(JToken id, string method, JObject parameters = null)
        {
            var message = new JObject { ["jsonrpc"] = "2.0", ["method"] = method };
            if (id != null)
            {
                message["id"] = id;
            }

            if (parameters != null)
            {
                message["params"] = parameters;
            }

            return message.ToString();
        }

        private EndpointResponse Post(string body, params (string, string)[] headers) =>
            this.endpoint.Handle("POST", Headers(headers), body);

        // The endpoint splices cached text in as JRaw, so the array only exists once serialised.
        private static JArray ToolsOf(EndpointResponse response) =>
            (JArray)JObject.Parse(response.Text)["result"]["tools"];

        [Test]
        public void GetAndDeleteAreNotAllowed()
        {
            foreach (var method in new[] { "GET", "DELETE", "PUT" })
            {
                var response = this.endpoint.Handle(method, Headers(), null);
                Assert.That(response.Status, Is.EqualTo(405), method);
                Assert.That(response.Allow, Is.EqualTo("POST"), method);
                Assert.That(response.Body, Is.Null, method);
            }
        }

        [Test]
        public void ForeignOriginIsForbiddenAndLoopbackIsNot()
        {
            var body = Request(1, "ping");

            Assert.That(this.Post(body, ("Origin", "http://evil.example")).Status, Is.EqualTo(403));
            Assert.That(this.Post(body, ("Origin", "null")).Status, Is.EqualTo(403));
            Assert.That(this.Post(body, ("Origin", "http://localhost:5173")).Status, Is.EqualTo(200));
            Assert.That(this.Post(body, ("Origin", "http://127.0.0.1")).Status, Is.EqualTo(200));
            Assert.That(this.Post(body).Status, Is.EqualTo(200), "No Origin header is the normal non-browser case.");
        }

        [Test]
        public void ProtocolVersionHeaderIsValidated()
        {
            var body = Request(1, "ping");

            foreach (var version in McpStreamableHttpEndpoint.SupportedProtocolVersions)
            {
                Assert.That(this.Post(body, ("MCP-Protocol-Version", version)).Status, Is.EqualTo(200), version);
            }

            Assert.That(this.Post(body, ("mcp-protocol-version", "2025-06-18")).Status, Is.EqualTo(200),
                "Header names are case-insensitive.");

            var unsupported = this.Post(body, ("MCP-Protocol-Version", "1999-01-01"));
            Assert.That(unsupported.Status, Is.EqualTo(400));
            Assert.That(unsupported.Body["error"].Value<string>(), Does.Contain("1999-01-01"));
        }

        [Test]
        public void InitializeNegotiatesTheClientsVersionWhenSupported()
        {
            var response = this.Post(Request(1, "initialize", new JObject { ["protocolVersion"] = "2025-06-18" }));

            Assert.That(response.Status, Is.EqualTo(200));
            var result = response.Body["result"];
            Assert.That(result["protocolVersion"].Value<string>(), Is.EqualTo("2025-06-18"));
            Assert.That(result["capabilities"]["tools"]["listChanged"].Value<bool>(), Is.False);
            Assert.That(result["serverInfo"]["name"].Value<string>(), Is.EqualTo("unity-mcp"));
            Assert.That(result["serverInfo"]["version"].Value<string>(), Is.EqualTo("4.0.0-test"));
            Assert.That(result["instructions"].Value<string>(), Does.Contain("job_status"));
            Assert.That(result["instructions"].Value<string>(), Does.Contain(" input_ ").And.Contain(" definitions_"),
                "A client that searches tools by the advertised prefixes must learn the input and definitions groups exist.");
        }

        [Test]
        public void InitializeFallsBackToTheNewestVersionForAnUnknownClient()
        {
            var response = this.Post(Request(1, "initialize", new JObject { ["protocolVersion"] = "2099-01-01" }));

            Assert.That(response.Body["result"]["protocolVersion"].Value<string>(),
                Is.EqualTo(McpStreamableHttpEndpoint.SupportedProtocolVersions[0]));
        }

        [Test]
        public void NotificationsAreAcceptedWithoutABody()
        {
            var response = this.Post(Request(null, "notifications/initialized"));

            Assert.That(response.Status, Is.EqualTo(202));
            Assert.That(response.Body, Is.Null);
        }

        [Test]
        public void PingEchoesTheIdWhetherNumericOrString()
        {
            var numeric = this.Post(Request(7, "ping"));
            Assert.That(numeric.Body["id"].Value<int>(), Is.EqualTo(7));
            Assert.That(numeric.Body["result"], Is.InstanceOf<JObject>());

            var text = this.Post(Request("abc", "ping"));
            Assert.That(text.Body["id"].Value<string>(), Is.EqualTo("abc"));
        }

        [Test]
        public void ToolsListCarriesEveryToolWithAnnotations()
        {
            var response = this.Post(Request(1, "tools/list"));

            var tools = ToolsOf(response);
            Assert.That(tools.Select(t => t["name"].Value<string>()), Is.EquivalentTo(new[] { "ep_echo", "ep_delete" }));

            var echo = tools.Single(t => t["name"].Value<string>() == "ep_echo");
            Assert.That(echo["annotations"]["readOnlyHint"].Value<bool>(), Is.True);
            Assert.That(echo["inputSchema"]["properties"]["text"], Is.Not.Null);

            var delete = tools.Single(t => t["name"].Value<string>() == "ep_delete");
            Assert.That(delete["annotations"]["destructiveHint"].Value<bool>(), Is.True);
        }

        [Test]
        public void ToolsListIsServedFromCachedBytesAndCarriesTheRequestId()
        {
            var first = this.Post(Request(41, "tools/list"));
            var second = this.Post(Request("x-2", "tools/list"));

            Assert.That(first.Segments, Is.Not.Null, "tools/list is written from pre-rendered UTF-8.");
            Assert.That(JObject.Parse(first.Text)["id"].Value<int>(), Is.EqualTo(41));
            Assert.That(JObject.Parse(second.Text)["id"].Value<string>(), Is.EqualTo("x-2"));

            // The catalogue array is the same byte[] instance both times: nothing was re-rendered.
            Assert.That(ReferenceEquals(first.Segments[3], second.Segments[3]), Is.True);
        }

        [Test]
        public void ToolsCallReturnsTextAndStructuredContent()
        {
            var response = this.Post(Request(1, "tools/call", new JObject
            {
                ["name"] = "ep_echo",
                ["arguments"] = new JObject { ["text"] = "hi" },
            }));

            var result = response.Body["result"];
            Assert.That(result["isError"], Is.Null);
            Assert.That(result["content"][0]["type"].Value<string>(), Is.EqualTo("text"));
            Assert.That(result["content"][0]["text"].Value<string>(), Does.Contain("\"hi\""));
            Assert.That(result["structuredContent"]["result"].Value<string>(), Is.EqualTo("hi"));
        }

        [Test]
        public void ToolFailureIsAToolErrorNotAProtocolError()
        {
            // A destructive tool without confirm is refused by the real ToolInvoker with
            // confirmation_required. The model must see that text, so it is a tool result.
            var response = this.Post(Request(1, "tools/call", new JObject
            {
                ["name"] = "ep_delete",
                ["arguments"] = new JObject { ["path"] = "Assets/x" },
            }));

            Assert.That(response.Status, Is.EqualTo(200));
            Assert.That(response.Body["error"], Is.Null);
            var result = response.Body["result"];
            Assert.That(result["isError"].Value<bool>(), Is.True);
            Assert.That(result["content"][0]["text"].Value<string>(), Does.StartWith("Error [confirmation_required]"));
        }

        [Test]
        public void UnknownToolIsAToolErrorThatSuggestsReconnecting()
        {
            var response = this.Post(Request(1, "tools/call", new JObject { ["name"] = "nope" }));

            var result = response.Body["result"];
            Assert.That(result["isError"].Value<bool>(), Is.True);
            Assert.That(result["content"][0]["text"].Value<string>(), Does.Contain("nope").And.Contain("reconnect"));
        }

        [Test]
        public void MissingToolNameIsInvalidParams()
        {
            var response = this.Post(Request(1, "tools/call", new JObject()));

            Assert.That(response.Body["error"]["code"].Value<int>(), Is.EqualTo(-32602));
            Assert.That(response.Body["id"].Value<int>(), Is.EqualTo(1));
        }

        [Test]
        public void ProtocolErrorsUseTheStandardCodes()
        {
            Assert.That(this.Post("{not json").Body["error"]["code"].Value<int>(), Is.EqualTo(-32700));
            Assert.That(this.Post("[]").Body["error"]["code"].Value<int>(), Is.EqualTo(-32600));
            Assert.That(this.Post("\"text\"").Body["error"]["code"].Value<int>(), Is.EqualTo(-32600));
            Assert.That(this.Post("{\"id\":1,\"method\":\"ping\"}").Body["error"]["code"].Value<int>(), Is.EqualTo(-32600),
                "jsonrpc must be 2.0");
            Assert.That(this.Post(Request(1, "resources/list")).Body["error"]["code"].Value<int>(), Is.EqualTo(-32601));
        }

        [Test]
        public void RunningCallReportsAJobIdTheClientCanPoll()
        {
            var mainThread = ToolCatalog.BuildFromTypes(new[] { typeof(MainThreadTools) });
            var runner = new ToolCallRunner(this.dispatcher, this.jobs, () => 50);
            var slowEndpoint = new McpStreamableHttpEndpoint(() => mainThread, runner.Run, () => "x");

            var response = slowEndpoint.Handle("POST", Headers(), Request(1, "tools/call", new JObject { ["name"] = "ep_main" }));

            var result = response.Body["result"];
            var jobId = result["structuredContent"]["jobId"].Value<string>();
            Assert.That(result["structuredContent"]["state"].Value<string>(), Is.EqualTo("running"));
            Assert.That(result["content"][0]["text"].Value<string>(), Does.Contain(jobId).And.Contain("job_status"));
            Assert.That(this.jobs.TryGet(jobId, out _), Is.True);
        }

        [Test]
        public void RunningCallCarriesTheNoticeAboutWhatBlocksTheMainThread()
        {
            var mainThread = ToolCatalog.BuildFromTypes(new[] { typeof(MainThreadTools) });
            var runner = new ToolCallRunner(this.dispatcher, this.jobs, () => 50);
            var slowEndpoint = new McpStreamableHttpEndpoint(
                () => mainThread, runner.Run, () => "x", () => "The Editor is showing a dialog \"Probe\".");

            var response = slowEndpoint.Handle("POST", Headers(), Request(1, "tools/call", new JObject { ["name"] = "ep_main" }));

            var result = response.Body["result"];
            Assert.That(result["content"][0]["text"].Value<string>(), Does.EndWith(" The Editor is showing a dialog \"Probe\"."));
            Assert.That(result["structuredContent"]["message"].Value<string>(), Is.EqualTo("The Editor is showing a dialog \"Probe\"."));
        }

        private static class MainThreadTools
        {
            [McpTool("ep_main", "Needs the main thread.", Idempotency = McpIdempotency.Safe)]
            public static int Main() => 1;
        }
    }
}
