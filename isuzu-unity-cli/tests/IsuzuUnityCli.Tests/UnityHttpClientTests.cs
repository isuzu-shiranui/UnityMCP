using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Commands;
using IsuzuUnityCli.Http;
using IsuzuUnityCli.Tests.Fakes;
using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public sealed class UnityHttpClientTests
{
    private static UnityHttpClient Client(int budgetMs = 5000) => new(new RetryOptions
    {
        InitialBackoffMs = 20,
        MaxBackoffMs = 100,
        BudgetMs = budgetMs,
        PerAttemptTimeoutMs = 5000,
    });

    [Fact]
    public async Task SafeGetRetriesAfterAGatewayError()
    {
        using var server = new FakeUnityServer()
            .Enqueue(503, """{"status":"error","error":{"code":"unavailable","message":"transient"}}""")
            .Enqueue(200, """{"status":"success","result":{"state":"running"}}""");

        var envelope = await Client().GetAsync(server.Descriptor(), "/health");

        Assert.Equal("running", envelope.Result!["state"]!.GetValue<string>());
        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, r => Assert.Equal("Bearer secret-token", r.Authorization));
        Assert.All(server.Requests, r => Assert.Equal("/health", r.Path));
    }

    [Fact]
    public async Task SafeGetDoesNotRetryAToolException()
    {
        using var server = new FakeUnityServer()
            .Enqueue(500, """{"status":"error","error":{"code":"tool_failed","message":"getter threw"}}""")
            .Enqueue(200, """{"status":"success","result":{"state":"running"}}""");

        var e = await Assert.ThrowsAsync<UnityError>(() => Client().GetAsync(server.Descriptor(), "/tools/reflect_read"));

        Assert.Equal("tool_failed", e.Code);
        Assert.Equal(500, e.HttpStatus);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task UnsafePostFailsOnceWithoutASecondRequest()
    {
        using var server = new FakeUnityServer()
            .Enqueue(500, """{"status":"error","error":{"code":"internal","message":"handler threw"}}""")
            .Enqueue(200, """{"status":"success","result":{}}""");

        var e = await Assert.ThrowsAsync<UnityError>(() =>
            Client().PostAsync(server.Descriptor(), "/tools/execute_code", new System.Text.Json.Nodes.JsonObject { ["code"] = "x" }));

        Assert.Equal("internal", e.Code);
        Assert.Equal("handler threw", e.Message);
        Assert.Equal(500, e.HttpStatus);

        var request = Assert.Single(server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("""{"code":"x"}""", request.Body);
    }

    [Fact]
    public async Task ConnectionRefusedRetriesUntilTheServerAppears()
    {
        var port = FakeUnityServer.FreePort();
        var descriptor = FakeUnityServer.DescriptorFor(port);

        var pending = Client(budgetMs: 10000).PostAsync(descriptor, "/tools/play_mode_status", new System.Text.Json.Nodes.JsonObject());

        await Task.Delay(300);
        using var server = new FakeUnityServer(port).Enqueue(200, """{"status":"success","result":{"isPlaying":false}}""");

        var envelope = await pending;

        Assert.False(envelope.Result!["isPlaying"]!.GetValue<bool>());
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task UnreachablePortReportsATransportCodeWithinBudget()
    {
        var descriptor = FakeUnityServer.DescriptorFor(FakeUnityServer.FreePort());
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        var e = await Assert.ThrowsAsync<UnityError>(() => Client(budgetMs: 200).GetAsync(descriptor, "/health"));

        // Windows may still be connecting when the 200ms budget expires; waiting for
        // the OS refusal would violate the deadline. Both outcomes identify transport failure.
        Assert.Contains(e.Code, new[] { "ECONNREFUSED", "ECONNTIMEOUT" });
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
        Assert.StartsWith("Retry budget exhausted after ", e.Message);
    }

    /// <summary>
    /// A write whose connection dies mid-request says what that is, not just the error code.
    /// </summary>
    /// <remarks>
    /// Entering or leaving play mode takes the Editor away in the middle of whatever is in
    /// flight, and the Editor cannot explain itself once it is gone: this message is the only
    /// text the caller gets. Reported four times from hands-on runs as calls that "failed with
    /// no recovery route", against a message that read "Fetch failed: ECONNRESET".
    /// </remarks>
    [Fact]
    public async Task AWriteDroppedMidRequestExplainsTheReload()
    {
        using var server = new FakeUnityServer().EnqueueDrop();

        var e = await Assert.ThrowsAsync<UnityError>(
            () => Client(budgetMs: 200).PostAsync(server.Descriptor(), "/tools/scene_save", new System.Text.Json.Nodes.JsonObject()));

        Assert.Contains("rebuilds its domain", e.Message);
        Assert.Contains("not repeated automatically", e.Message);
    }

    [Fact]
    public async Task ClientErrorSurfacesUnityMessageWithoutRetry()
    {
        using var server = new FakeUnityServer()
            .Enqueue(400, """{"status":"error","error":{"code":"invalid_arguments","message":"limit must be a number"}}""");

        var e = await Assert.ThrowsAsync<UnityError>(() => Client().GetAsync(server.Descriptor(), "/jobs/x"));

        Assert.Equal("invalid_arguments", e.Code);
        Assert.Equal("limit must be a number", e.Message);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task AcceptedResultIsPrintedByCall()
    {
        using var server = new FakeUnityServer()
            .Enqueue(202, """{"status":"success","result":{"jobId":"job-1","poll":"/jobs/job-1","message":"queued"}}""");

        var output = new StringWriter();
        var error = new StringWriter();
        var context = new CommandContext
        {
            Out = output,
            Err = error,
            Client = Client(),
            ReadDescriptors = () => [server.Descriptor()],
            WorkingDirectory = Path.GetTempPath(),
            // The assertion below is on the indented form. The default reads
            // Console.IsOutputRedirected, which is true under the test host.
            Indented = true,
        };

        var code = await CallCommand.Run(ArgParser.Parse(["call", "build_player", "--target", "StandaloneWindows64"]), context);

        Assert.Equal(0, code);
        Assert.Equal("", error.ToString());
        Assert.Equal("""
            {
              "jobId": "job-1",
              "poll": "/jobs/job-1",
              "message": "queued"
            }

            """.ReplaceLineEndings(), output.ToString().ReplaceLineEndings());
        Assert.Equal("""{"target":"StandaloneWindows64"}""", Assert.Single(server.Requests).Body);
    }

    [Fact]
    public async Task ErrorEnvelopeIsReportedOnStderr()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, """{"status":"error","error":{"code":"tool_not_found","message":"No tool named 'nope'."}}""");

        var output = new StringWriter();
        var error = new StringWriter();
        var context = new CommandContext
        {
            Out = output,
            Err = error,
            Client = Client(),
            ReadDescriptors = () => [server.Descriptor()],
            WorkingDirectory = Path.GetTempPath(),
        };

        var code = await CallCommand.Run(ArgParser.Parse(["call", "nope"]), context);

        Assert.Equal(1, code);
        Assert.Equal("", output.ToString());
        Assert.Equal("error [tool_not_found]: No tool named 'nope'." + Environment.NewLine, error.ToString());
    }
}
