using System.Text.Json.Nodes;
using IsuzuUnityCli.Commands;
using IsuzuUnityCli.Tests.Fakes;
using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public sealed class VerifyCompletionTests
{
    /// <summary>
    /// A failure the test_run tool reported keeps its own reason, 4xx or 5xx.
    /// </summary>
    /// <remarks>
    /// A 500 went down the lost-reply path and came out as "may already have run, read
    /// test_results", which is the wrong advice for a run that threw before it started.
    /// </remarks>
    [Theory]
    [InlineData(400)]
    [InlineData(500)]
    public async Task KnownTestRunFailureKeepsItsDiagnostic(int status)
    {
        using var server = new FakeUnityServer()
            .Enqueue(status, "{\"status\":\"error\",\"error\":{\"code\":\"tool_failed\",\"message\":\"invalid test mode\"}}");
        var error = new StringWriter();
        var context = new CommandContext { Out = new StringWriter(), Err = error, ReadDescriptors = () => [server.Descriptor()] };
        Assert.Equal(1, await Program.Run(["verify", "--no-compile", "--test"], context));
        Assert.Single(server.Requests);
        Assert.Contains("invalid test mode", error.ToString());
        Assert.DoesNotContain("test_run_unconfirmed", error.ToString());
    }

    /// <summary>
    /// A run that was not started because another is in progress fails verify.
    /// </summary>
    /// <remarks>
    /// test_run answers started:false and leaves the running one alone; reading test_results
    /// after that took the other run's outcome as this one's and passed.
    /// </remarks>
    [Fact]
    public async Task ARunThatWasNotStartedIsNotPassedOnAnotherRunsResults()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, "{\"status\":\"success\",\"result\":{\"started\":false,\"message\":\"already running\"}}")
            .Enqueue(200, "{\"status\":\"success\",\"result\":{\"status\":\"completed\",\"passed\":3,\"failed\":0,\"skipped\":0}}");
        var error = new StringWriter();
        var context = new CommandContext { Out = new StringWriter(), Err = error, ReadDescriptors = () => [server.Descriptor()] };
        Assert.NotEqual(0, await Program.Run(["verify", "--no-compile", "--test"], context));
        Assert.Single(server.Requests);
        Assert.Contains("test_run_busy", error.ToString());
    }

    [Fact]
    public async Task MissingStartJobDoesNotReplayTestRun()
    {
        using var server = new FakeUnityServer()
            .Enqueue(202, "{\"status\":\"success\",\"result\":{\"state\":\"running\",\"jobId\":\"test-start-old\"}}")
            .Enqueue(404, "{\"status\":\"error\",\"error\":{\"code\":\"job_not_found\",\"message\":\"gone\"}}");
        var error = new StringWriter();
        var context = new CommandContext { Out = new StringWriter(), Err = error, ReadDescriptors = () => [server.Descriptor()] };
        Assert.Equal(1, await Program.Run(["verify", "--no-compile", "--test"], context));
        Assert.Single(server.Requests, request => request.Path == "/tools/test_run");
        Assert.Contains("test_run_unconfirmed", error.ToString());
    }

    [Fact]
    public async Task RejectedTestRunCanRefreshCredentialsAndStartOnce()
    {
        using var server = new FakeUnityServer()
            .Enqueue(401, "{\"status\":\"error\",\"error\":{\"code\":\"unauthorized\"}}")
            .Enqueue(200, "{\"status\":\"success\",\"result\":{\"started\":true}}")
            .Enqueue(200, "{\"status\":\"success\",\"result\":{\"status\":\"completed\",\"passed\":1,\"failed\":0}}")
            .Enqueue(200, "{\"status\":\"success\",\"result\":{\"logs\":[]}}");
        var context = new CommandContext { Out = new StringWriter(), Err = new StringWriter(), ReadDescriptors = () => [server.Descriptor()] };
        Assert.Equal(0, await Program.Run(["verify", "--no-compile", "--test"], context));
        Assert.Equal(2, server.Requests.Count(request => request.Path == "/tools/test_run"));
    }

    [Fact]
    public async Task LostTestRunResponseDoesNotStartTheTestsAgain()
    {
        using var server = new FakeUnityServer().EnqueueDrop()
            .Default(200, "{\"status\":\"success\",\"result\":{\"status\":\"completed\",\"passed\":1,\"failed\":0,\"logs\":[]}}");
        var error = new StringWriter();
        var context = new CommandContext { Out = new StringWriter(), Err = error, ReadDescriptors = () => [server.Descriptor()] };
        Assert.Equal(1, await Program.Run(["verify", "--no-compile", "--test"], context));
        Assert.Single(server.Requests, request => request.Path == "/tools/test_run");
        Assert.Contains("test_results", error.ToString());
    }

    [Theory]
    [InlineData("idle", 1)]
    [InlineData("unknown", 1)]
    [InlineData(null, 1)]
    [InlineData("interrupted", 1)]
    [InlineData("completed", 0)]
    public async Task OnlyKnownCompletionCanSucceedEvenForZeroTests(string? status, int expectedExit)
    {
        using var server = new TcpReplyServer(
            TcpReplyServer.Reply.Json("{\"status\":\"success\",\"result\":{\"started\":true}}"),
            TcpReplyServer.Reply.Json("{\"status\":\"success\",\"result\":{\"status\":" + (status is null ? "null" : "\"" + status + "\"") + ",\"passed\":0,\"failed\":0}}"),
            TcpReplyServer.Reply.Json("{\"status\":\"success\",\"result\":{\"logs\":[]}}"));
        var output = new StringWriter();
        var context = new CommandContext { Out = output, Err = new StringWriter(), ReadDescriptors = () => [server.Descriptor] };
        var exit = await Program.Run(["verify", "--no-compile", "--test", "--raw"], context);
        Assert.Equal(expectedExit, exit);
        Assert.Equal(expectedExit == 0, JsonNode.Parse(output.ToString())!["ok"]!.GetValue<bool>());
    }
}
