using System;
using System.Collections.Generic;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using UnityMCP.Editor.Handlers;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// The Model Context Protocol endpoint, spoken over Streamable HTTP at <c>/mcp</c>.
    /// </summary>
    /// <remarks>
    /// Socket-free by construction: it takes the method, headers and body of a request and
    /// returns a status and JSON body, so the whole protocol is testable without an
    /// <see cref="System.Net.HttpListener"/>. <see cref="McpHttpServer"/> only copies bytes in
    /// and out.
    /// <para>
    /// Stateless on purpose. No <c>Mcp-Session-Id</c> is issued, so a client whose Editor was
    /// restarted keeps working without renegotiating, and a request that arrives before
    /// <c>initialize</c> is served rather than refused. Nothing is ever pushed to the client:
    /// GET (the SSE stream) answers 405, and <c>listChanged</c> is advertised as false. The
    /// official SDK clients treat both as normal.
    /// </para>
    /// <para>
    /// Bearer authentication runs in the server before this class sees a request. The Origin
    /// check here is defence in depth against a browser page that has somehow obtained the
    /// token; the protocol-version check is what the specification requires of servers.
    /// </para>
    /// </remarks>
    internal sealed class McpStreamableHttpEndpoint
    {
        /// <summary>Protocol revisions this endpoint speaks, newest first.</summary>
        public static readonly string[] SupportedProtocolVersions = { "2025-11-25", "2025-06-18", "2025-03-26" };

        /// <summary>Assumed when a client sends no <c>MCP-Protocol-Version</c> header.</summary>
        private const string DefaultProtocolVersion = "2025-03-26";

        public const string Instructions =
            "Controls a running Unity Editor. Search this server's tools for any Unity work:\n" +
            "inspecting or editing a scene, assets and prefabs, Timeline and Recorder, shaders and\n" +
            "rendering, play mode, the console, tests, and builds.\n" +
            "\n" +
            "Tool name prefixes: scene_ gameobject_ inspect_ asset_ prefab_ console_ compile_\n" +
            "play_mode_ timeline_ recorder_ render_ shader_ material_ reflect_ gpu_ test_ build_\n" +
            "project_ editor_ menu_ capture_ execute_ job_ input_ definitions_.\n" +
            "\n" +
            "Reach for these when asked to look at, change or debug anything in a Unity project,\n" +
            "including questions like why a material renders wrong, what a Timeline does at a given\n" +
            "moment, why a script did not compile, or what the console reported. Read the console\n" +
            "(console_read_logs) before building any instrumentation of your own; Unity has usually\n" +
            "already written down the cause. Prefer a specific tool over execute_code, which cannot\n" +
            "be undone and is the last resort when nothing else reaches.\n" +
            "\n" +
            "This endpoint belongs to one Unity project: the Editor that opened it. Which tools exist\n" +
            "depends on that project's packages, so Timeline and Recorder tools appear only where those\n" +
            "packages are present. If a tool you used earlier is missing, the project's packages\n" +
            "changed; reconnect to refresh the tool list.\n" +
            "\n" +
            "A call that takes longer than a few seconds returns a job id instead of a result. Fetch\n" +
            "it with job_status; do not repeat the call, the work is still running.";

        private readonly Func<ToolCatalog> catalog;
        private readonly Func<McpToolDescriptor, JObject, ToolCallOutcome> run;
        private readonly Func<string> serverVersion;
        private readonly Func<string> runningNotice;

        /// <param name="runningNotice">
        /// What is holding the main thread, appended to every "still running" answer; null or a
        /// function returning null adds nothing.
        /// </param>
        public McpStreamableHttpEndpoint(
            Func<ToolCatalog> catalog,
            Func<McpToolDescriptor, JObject, ToolCallOutcome> run,
            Func<string> serverVersion,
            Func<string> runningNotice = null)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            this.run = run ?? throw new ArgumentNullException(nameof(run));
            this.serverVersion = serverVersion ?? throw new ArgumentNullException(nameof(serverVersion));
            this.runningNotice = runningNotice;
        }

        /// <summary>Answers one HTTP request to the endpoint.</summary>
        /// <param name="httpMethod">The request method.</param>
        /// <param name="headers">Request headers; lookup is case-insensitive.</param>
        /// <param name="body">The request body, or null.</param>
        /// <param name="groupQuery">
        /// The <c>group</c> query parameter, a comma-separated list that limits <c>tools/list</c> to
        /// those groups. Null or empty lists every tool. <c>tools/call</c> is not limited.
        /// </param>
        public EndpointResponse Handle(string httpMethod, IReadOnlyDictionary<string, string> headers, string body, string groupQuery = null)
        {
            headers ??= new Dictionary<string, string>();

            var groups = McpToolGroups.Parse(groupQuery, out var unknownGroups);
            if (unknownGroups.Count > 0)
            {
                return EndpointResponse.Plain(
                    400,
                    $"Unknown tool group '{unknownGroups[0]}'. Known: {string.Join(", ", McpToolGroups.Known)}.");
            }

            if (!string.Equals(httpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                return new EndpointResponse(405, null) { Allow = "POST" };
            }

            if (TryGet(headers, "Origin", out var origin) && !IsLoopbackOrigin(origin))
            {
                return EndpointResponse.Plain(403, "Origin not allowed.");
            }

            if (TryGet(headers, "MCP-Protocol-Version", out var version) &&
                Array.IndexOf(SupportedProtocolVersions, version) < 0)
            {
                return EndpointResponse.Plain(
                    400,
                    $"Unsupported MCP-Protocol-Version '{version}'. Supported: {string.Join(", ", SupportedProtocolVersions)}.");
            }

            JToken parsed;
            try
            {
                parsed = string.IsNullOrWhiteSpace(body) ? null : JToken.Parse(body);
            }
            catch (JsonException e)
            {
                return EndpointResponse.Json(200, RpcError(null, -32700, $"Parse error: {e.Message}"));
            }

            if (parsed is JArray)
            {
                return EndpointResponse.Json(200, RpcError(null, -32600, "JSON-RPC batching is not supported."));
            }

            if (parsed is not JObject message)
            {
                return EndpointResponse.Json(200, RpcError(null, -32600, "Expected a JSON-RPC request object."));
            }

            var id = message["id"];
            var method = message["method"]?.Type == JTokenType.String ? message["method"].Value<string>() : null;

            if (message["jsonrpc"]?.Value<string>() != "2.0" || string.IsNullOrEmpty(method))
            {
                return EndpointResponse.Json(200, RpcError(id, -32600, "Not a JSON-RPC 2.0 request."));
            }

            // A notification or a response from the client has no id and expects nothing back.
            if (id == null || id.Type == JTokenType.Null)
            {
                return new EndpointResponse(202, null);
            }

            var parameters = message["params"] as JObject ?? new JObject();

            switch (method)
            {
                case "initialize":
                    return EndpointResponse.Json(200, RpcResult(id, this.Initialize(parameters)));

                case "ping":
                    return EndpointResponse.Json(200, RpcResult(id, new JObject()));

                case "tools/list":
                    return this.ListTools(id, groups);

                case "tools/call":
                    return this.CallTool(id, parameters);

                default:
                    return EndpointResponse.Json(200, RpcError(id, -32601, $"Method not found: {method}"));
            }
        }

        private JObject Initialize(JObject parameters)
        {
            var requested = parameters["protocolVersion"]?.Value<string>();
            var negotiated = requested != null && Array.IndexOf(SupportedProtocolVersions, requested) >= 0
                ? requested
                : SupportedProtocolVersions[0];

            return new JObject
            {
                ["protocolVersion"] = negotiated,
                ["capabilities"] = new JObject
                {
                    ["tools"] = new JObject { ["listChanged"] = false },
                },
                ["serverInfo"] = new JObject
                {
                    ["name"] = "unity-mcp",
                    ["version"] = this.serverVersion(),
                },
                ["instructions"] = Instructions,
            };
        }

        private static readonly byte[] ListToolsPrefix = System.Text.Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":");
        private static readonly byte[] ListToolsMiddle = System.Text.Encoding.UTF8.GetBytes(",\"result\":{\"tools\":");
        private static readonly byte[] ListToolsSuffix = System.Text.Encoding.UTF8.GetBytes("}}");

        /// <summary>
        /// The list is the one response a client sends on every connect, so it is written from the
        /// catalog's cached UTF-8 around the request id. Only the id is rendered per request.
        /// </summary>
        private EndpointResponse ListTools(JToken id, IReadOnlyList<string> groups)
        {
            return EndpointResponse.Raw(
                200,
                ListToolsPrefix,
                System.Text.Encoding.UTF8.GetBytes(id.ToString(Formatting.None)),
                ListToolsMiddle,
                this.catalog().ToolsArrayUtf8(groups, mcpShape: true),
                ListToolsSuffix);
        }

        private EndpointResponse CallTool(JToken id, JObject parameters)
        {
            var name = parameters["name"]?.Type == JTokenType.String ? parameters["name"].Value<string>() : null;
            if (string.IsNullOrEmpty(name))
            {
                return EndpointResponse.Json(200, RpcError(id, -32602, "tools/call requires params.name."));
            }

            // Reported as a tool error rather than a protocol error so the model sees the text
            // and can correct the call instead of the whole request failing.
            if (!this.catalog().TryGet(name, out var descriptor))
            {
                return EndpointResponse.Json(200, RpcResult(id, ToolError(
                    $"No tool named '{name}'. The tool set may have changed since tools/list; reconnect to refresh it.")));
            }

            var arguments = parameters["arguments"] as JObject ?? new JObject();
            var outcome = this.run(descriptor, arguments);

            switch (outcome.State)
            {
                case ToolCallOutcome.Kind.Completed:
                    var reported = HandlerErrorResult.Message(outcome.Result);

                    if (reported != null)
                    {
                        return EndpointResponse.Json(200, RpcResult(id, ToolError($"Error [invalid_params]: {reported}")));
                    }

                    return EndpointResponse.Json(200, RpcResult(id, new JObject
                    {
                        ["content"] = ResultContent(outcome.Result),
                        ["structuredContent"] = outcome.Result,
                    }));

                case ToolCallOutcome.Kind.Failed:
                    var code = outcome.Error switch
                    {
                        McpToolException tool => tool.Code,
                        _ => "internal_error",
                    };
                    return EndpointResponse.Json(200, RpcResult(id, ToolError($"Error [{code}]: {outcome.Error.Message}")));

                default:
                    var text =
                        $"Still running on the Editor main thread as job {outcome.JobId}. " +
                        $"Call job_status with job_id \"{outcome.JobId}\" to fetch the result. " +
                        "Do not retry this call; the work is in progress and retrying would run it twice.";

                    var structured = new JObject
                    {
                        ["state"] = "running",
                        ["jobId"] = outcome.JobId,
                    };

                    var notice = this.runningNotice?.Invoke();
                    if (notice != null)
                    {
                        text += " " + notice;
                        structured["message"] = notice;
                    }

                    return EndpointResponse.Json(200, RpcResult(id, new JObject
                    {
                        ["content"] = TextContent(text),
                        ["structuredContent"] = structured,
                    }));
            }
        }

        private static JObject ToolError(string text)
        {
            return new JObject
            {
                ["isError"] = true,
                ["content"] = TextContent(text),
            };
        }

        /// <summary>
        /// The MCP content for a result, with an image carried as an image rather than as text.
        /// </summary>
        /// <remarks>
        /// A capture returns its PNG base64-encoded. Left inside the JSON it is a wall of text a
        /// model cannot look at, and a small screenshot is large enough to crowd out everything
        /// else in the reply. As image content the client renders it and the model sees the
        /// picture, which is the whole point of asking for one. The base64 is taken out of the
        /// structured copy for the same reason.
        /// </remarks>
        private static JArray ResultContent(JObject result)
        {
            // A capture answered inline carries the PNG at the top; fetched through job_status it
            // sits under "result", because that reply is the job's detail rather than the tool's
            // own. Looking only at the top meant the same screenshot came back as an image when
            // the Editor was idle and as a wall of base64 when it was busy.
            var carrier = result?["result"] as JObject ?? result;
            var image = carrier?["image"];

            if (image == null || image.Type != JTokenType.String)
            {
                return TextContent(result.ToString(Formatting.None));
            }

            var describing = (JObject)result.DeepClone();

            if (describing["result"] is JObject nested)
            {
                nested.Remove("image");
            }
            else
            {
                describing.Remove("image");
            }

            return new JArray
            {
                new JObject { ["type"] = "text", ["text"] = describing.ToString(Formatting.None) },
                new JObject
                {
                    ["type"] = "image",
                    ["data"] = image.ToString(),
                    ["mimeType"] = "image/png",
                },
            };
        }

        private static JArray TextContent(string text)
        {
            return new JArray
            {
                new JObject { ["type"] = "text", ["text"] = text },
            };
        }

        private static JObject RpcResult(JToken id, JObject result)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result,
            };
        }

        private static JObject RpcError(JToken id, int code, string message)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["error"] = new JObject
                {
                    ["code"] = code,
                    ["message"] = message,
                },
            };
        }

        private static bool IsLoopbackOrigin(string origin)
        {
            if (string.IsNullOrEmpty(origin) || origin == "null")
            {
                return false;
            }

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return uri.IsLoopback;
        }

        private static bool TryGet(IReadOnlyDictionary<string, string> headers, string name, out string value)
        {
            foreach (var pair in headers)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(pair.Value))
                {
                    value = pair.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }
    }

    /// <summary>
    /// What the endpoint wants written back: a status, an optional Allow header, and either a
    /// JSON body to serialise or pre-rendered UTF-8 segments to copy out.
    /// </summary>
    internal sealed class EndpointResponse
    {
        public int Status { get; }

        /// <summary>The body to serialise, or null for an empty or pre-rendered response.</summary>
        public JObject Body { get; }

        /// <summary>Pre-rendered UTF-8 written in order, or null when <see cref="Body"/> applies.</summary>
        public byte[][] Segments { get; }

        /// <summary>Value of the <c>Allow</c> header, set on 405.</summary>
        public string Allow { get; set; }

        /// <summary>True when there is something to write.</summary>
        public bool HasContent => this.Body != null || this.Segments != null;

        /// <summary>The response text, whichever form it is in. For tests and logs.</summary>
        public string Text
        {
            get
            {
                if (this.Segments != null)
                {
                    var total = 0;
                    foreach (var segment in this.Segments)
                    {
                        total += segment.Length;
                    }

                    var joined = new byte[total];
                    var offset = 0;
                    foreach (var segment in this.Segments)
                    {
                        System.Buffer.BlockCopy(segment, 0, joined, offset, segment.Length);
                        offset += segment.Length;
                    }

                    return System.Text.Encoding.UTF8.GetString(joined);
                }

                return this.Body?.ToString(Formatting.None);
            }
        }

        public EndpointResponse(int status, JObject body)
        {
            this.Status = status;
            this.Body = body;
        }

        private EndpointResponse(int status, byte[][] segments)
        {
            this.Status = status;
            this.Segments = segments;
        }

        public static EndpointResponse Json(int status, JObject body) => new(status, body);

        public static EndpointResponse Raw(int status, params byte[][] segments) => new(status, segments);

        /// <summary>An HTTP-level refusal that never reached JSON-RPC, as a small JSON object.</summary>
        public static EndpointResponse Plain(int status, string message) =>
            new(status, new JObject { ["error"] = message });
    }
}
