using System.Net;
using System.Text.Json.Nodes;
using IsuzuUnityCli.Bridge;
using IsuzuUnityCli.Discovery;
using IsuzuUnityCli.Tests.Fakes;
using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public sealed class McpStdioHttpErrorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpAndSseFormattingNewlinesBecomeOneStdioMessage(bool sse)
    {
        var json = "{\n\"jsonrpc\":\"2.0\",\n\"id\":9007199254740993,\n\"result\":{}\n}";
        using var handler = new BodyHandler(sse ? "data: " + json.Replace("\n", "\ndata: ") + "\n\n" : json, sse);
        var output = new StringWriter();
        using var bridge = new McpStdioBridge(new StringReader("{\"jsonrpc\":\"2.0\",\"id\":9007199254740993,\"method\":\"tools/list\"}\n"), output, Descriptor, handler: handler);
        await bridge.RunAsync();
        var line = Assert.Single(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(9007199254740993L, JsonNode.Parse(line)!["id"]!.GetValue<long>());
    }

    private sealed class BodyHandler(string body, bool sse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(sse ? "text/event-stream" : "application/json");
            return Task.FromResult(response);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownCancelsAnUnresponsiveSessionDelete(bool cancel)
    {
        using var handler = new HangingDeleteHandler();
        using var cancellation = new CancellationTokenSource();
        var input = new GatedReader();
        var output = new RecordingWriter();
        using var bridge = new McpStdioBridge(input, output, Descriptor, handler: handler);
        var run = bridge.RunAsync(cancellation.Token);
        try
        {
            input.Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}");
            await RecordingWriter.WaitFor(() => output.Lines.Count == 1, "initialize reply");
            if (cancel)
                cancellation.Cancel();
            else
                input.CloseInput();
            await run.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.True(handler.DeleteWasCanceled);
        }
        finally
        {
            handler.Release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private sealed class HangingDeleteHandler : HttpMessageHandler
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DeleteWasCanceled { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete)
            {
                try { await Release.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { DeleteWasCanceled = true; throw; }
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}")
            };
            response.Headers.Add("Mcp-Session-Id", "session");
            return response;
        }
    }

    [Theory]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task HttpFailuresReturnMatchingRpcErrorsWithoutReplayingCalls(int status)
    {
        using var handler = new ScriptedHandler(status);
        var output = new StringWriter();
        using var bridge = new McpStdioBridge(
            new StringReader("{\"jsonrpc\":\"2.0\",\"id\":\"request-1\",\"method\":\"tools/call\"}\n"),
            output, Descriptor, handler: handler);

        await bridge.RunAsync();

        var reply = JsonNode.Parse(output.ToString())!;
        Assert.Equal("2.0", reply["jsonrpc"]!.GetValue<string>());
        Assert.Equal("request-1", reply["id"]!.GetValue<string>());
        Assert.Equal(-32000, reply["error"]!["code"]!.GetValue<int>());
        Assert.Contains(status.ToString(), reply["error"]!["message"]!.GetValue<string>());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task UnauthorizedNotificationProducesNoReply()
    {
        using var handler = new ScriptedHandler(401);
        var output = new StringWriter();
        using var bridge = new McpStdioBridge(
            new StringReader("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n"),
            output, Descriptor, handler: handler);
        await bridge.RunAsync();
        Assert.Equal("", output.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task UnauthorizedInvalidatesDescriptorAndSessionForNextRequest()
    {
        using var handler = new ScriptedHandler(200, 401, 200);
        var input = new GatedReader();
        var output = new RecordingWriter();
        var resolutions = 0;
        using var bridge = new McpStdioBridge(input, output, () =>
        {
            var descriptor = Descriptor();
            descriptor.Token = "token-" + ++resolutions;
            return descriptor;
        }, handler: handler);
        var run = bridge.RunAsync();
        for (var id = 1; id <= 3; id++)
        {
            input.Send("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/list\"}");
            await RecordingWriter.WaitFor(() => output.Lines.Count == id, "reply");
        }
        input.CloseInput();
        await run;
        Assert.Equal(2, resolutions);
        Assert.Equal(new[] { "token-1", "token-1", "token-2" }, handler.Tokens);
        Assert.Equal(new string?[] { null, "old-session", null }, handler.Sessions);
        Assert.Equal(3, handler.Calls);
    }

    private static InstanceDescriptor Descriptor() => new()
    {
        Endpoint = "http://127.0.0.1:12345",
        Token = "test",
        ProjectName = "test"
    };

    private sealed class ScriptedHandler(params int[] statuses) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string?> Tokens { get; } = [];
        public List<string?> Sessions { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Calls++;
            Tokens.Add(request.Headers.Authorization?.Parameter);
            Sessions.Add(request.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.Single() : null);
            var response = new HttpResponseMessage((HttpStatusCode)statuses[index])
            {
                Content = new StringContent(statuses[index] == 200
                    ? "{\"jsonrpc\":\"2.0\",\"id\":" + (index + 1) + ",\"result\":{}}"
                    : "{\"error\":\"Unauthorized\"}")
            };
            if (index == 0 && statuses[index] == 200)
                response.Headers.Add("Mcp-Session-Id", "old-session");
            return Task.FromResult(response);
        }
    }
}
