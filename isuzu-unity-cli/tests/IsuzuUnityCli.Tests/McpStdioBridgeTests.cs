using System.Text.Json.Nodes;
using IsuzuUnityCli.Bridge;
using IsuzuUnityCli.Discovery;
using IsuzuUnityCli.Tests.Fakes;
using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public sealed class McpStdioBridgeTests
{
    private const string ToolsList = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
    private const string Initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    [Fact]
    public async Task ANotificationAnsweredWith202WritesNothing()
    {
        using var server = new FakeUnityServer();
        server.Enqueue(new ScriptedResponse(202, ""));

        var lines = await Drive(server, input => input.Send(Initialized));

        Assert.Empty(lines);
        Assert.Equal(Initialized, Assert.Single(server.Requests).Body);
    }

    [Fact]
    public async Task AJsonReplyIsForwardedByteForByte()
    {
        using var server = new FakeUnityServer();
        var reply = """{"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"play_mode_status"}]}}""";
        server.Enqueue(new ScriptedResponse(200, reply));

        var lines = await Drive(server, input => input.Send(ToolsList));

        Assert.Equal(reply, Assert.Single(lines));

        var request = Assert.Single(server.Requests);
        Assert.Equal("Bearer secret-token", request.Authorization);
        Assert.Equal(ToolsList, request.Body);
        Assert.Equal("/mcp", request.Path);
    }

    [Fact]
    public async Task ASessionIdIsCapturedAndSentBackOnTheNextMessage()
    {
        using var server = new FakeUnityServer();
        server.Enqueue(new ScriptedResponse(
            200,
            """{"jsonrpc":"2.0","id":1,"result":{}}""",
            Headers: new Dictionary<string, string> { ["Mcp-Session-Id"] = "abc123" }));
        server.Enqueue(new ScriptedResponse(200, """{"jsonrpc":"2.0","id":2,"result":{}}"""));

        var input = new GatedReader();
        var output = new RecordingWriter();
        using var bridge = new McpStdioBridge(input, output, () => server.Descriptor());
        var run = bridge.RunAsync();

        input.Send(ToolsList);
        await RecordingWriter.WaitFor(() => output.Lines.Count == 1, "the first reply");

        input.Send("""{"jsonrpc":"2.0","id":2,"method":"tools/call"}""");
        await RecordingWriter.WaitFor(() => output.Lines.Count == 2, "the second reply");

        input.CloseInput();
        await run;

        Assert.Null(server.Requests[0].SessionId);
        Assert.Equal("abc123", server.Requests[1].SessionId);

        // Stdin closing ends the session the way the protocol asks for.
        Assert.Equal("DELETE", server.Requests[2].Method);
        Assert.Equal("abc123", server.Requests[2].SessionId);
    }

    [Fact]
    public async Task EventStreamPayloadsAreUnwrappedOnePerLine()
    {
        using var server = new FakeUnityServer();
        server.Enqueue(new ScriptedResponse(
            200,
            string.Join('\n',
                "event: message",
                "data: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}",
                "",
                ": heartbeat",
                "id: 7",
                "data: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\"}",
                "",
                ""),
            ContentType: "text/event-stream"));

        var lines = await Drive(server, input => input.Send(ToolsList));

        Assert.Equal(2, lines.Count);
        Assert.Equal("""{"jsonrpc":"2.0","id":1,"result":{}}""", lines[0]);
        Assert.Equal("""{"jsonrpc":"2.0","method":"notifications/progress"}""", lines[1]);
    }

    [Fact]
    public async Task TheReaderJoinsAMultiLineDataFieldAndIgnoresEverythingElse()
    {
        var body = string.Join('\n',
            ": a comment",
            "event: message",
            "retry: 5000",
            "data: first",
            "data:second",
            "",
            "data: trailing event with no blank line after it",
            "");

        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        var payloads = new List<string>();

        await foreach (var payload in SseReader.ReadAsync(stream))
        {
            payloads.Add(payload);
        }

        Assert.Equal(new[] { "first\nsecond", "trailing event with no blank line after it" }, payloads);
    }

    [Fact]
    public async Task ARequestThatCannotReachTheEditorGetsAJsonRpcError()
    {
        var dead = FakeUnityServer.DescriptorFor(FakeUnityServer.FreePort(), "Game");

        var lines = await Drive(() => dead, input => input.Send(ToolsList));

        var error = JsonNode.Parse(Assert.Single(lines))!;

        Assert.Equal(1, error["id"]!.GetValue<int>());
        Assert.Equal(-32000, error["error"]!["code"]!.GetValue<int>());
        Assert.Equal("Unity Editor for Game is not running", error["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task ANotificationThatCannotBeDeliveredIsNotAnsweredAtAll()
    {
        var dead = FakeUnityServer.DescriptorFor(FakeUnityServer.FreePort(), "Game");

        var lines = await Drive(() => dead, input => input.Send(Initialized));

        Assert.Empty(lines);
    }

    [Fact]
    public async Task TheDescriptorIsReadAgainAfterAFailureSoARestartHeals()
    {
        using var server = new FakeUnityServer();
        server.Enqueue(new ScriptedResponse(200, """{"jsonrpc":"2.0","id":2,"result":{}}"""));

        // The Editor is down, then comes back on a different port; only the descriptor changes.
        InstanceDescriptor current = FakeUnityServer.DescriptorFor(FakeUnityServer.FreePort(), "Game");

        var input = new GatedReader();
        var output = new RecordingWriter();
        using var bridge = new McpStdioBridge(input, output, () => current);
        var run = bridge.RunAsync();

        input.Send(ToolsList);
        await RecordingWriter.WaitFor(() => output.Lines.Count == 1, "the transport error");
        Assert.Contains("-32000", output.Lines[0]);

        current = server.Descriptor("Game");
        input.Send("""{"jsonrpc":"2.0","id":2,"method":"tools/call"}""");
        await RecordingWriter.WaitFor(() => output.Lines.Count == 2, "the reply after the restart");

        input.CloseInput();
        await run;

        Assert.Equal("""{"jsonrpc":"2.0","id":2,"result":{}}""", output.Lines[1]);
    }

    [Fact]
    public void TheIdIsReadOutOfTheRequestWithoutRewritingIt()
    {
        Assert.Equal("1", McpStdioBridge.PeekId(ToolsList));
        Assert.Equal("\"abc\"", McpStdioBridge.PeekId("""{"jsonrpc":"2.0","id":"abc","method":"x"}"""));
        Assert.Equal("9007199254740993", McpStdioBridge.PeekId("""{"id":9007199254740993}"""));
        Assert.Equal("2", McpStdioBridge.PeekId("""{"params":{"id":99},"id":2}"""));
        Assert.Null(McpStdioBridge.PeekId(Initialized));
        Assert.Null(McpStdioBridge.PeekId("""{"id":null}"""));
        Assert.Null(McpStdioBridge.PeekId("not json at all"));
    }

    private static Task<IReadOnlyList<string>> Drive(FakeUnityServer server, Action<GatedReader> script)
    {
        return Drive(() => server.Descriptor(), script);
    }

    private static async Task<IReadOnlyList<string>> Drive(Func<InstanceDescriptor> resolve, Action<GatedReader> script)
    {
        var input = new GatedReader();
        var output = new RecordingWriter();

        using var bridge = new McpStdioBridge(input, output, resolve);
        var run = bridge.RunAsync();

        script(input);
        input.CloseInput();

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(15)));
        return output.Lines;
    }
}
