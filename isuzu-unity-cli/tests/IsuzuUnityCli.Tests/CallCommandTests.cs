using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Commands;
using IsuzuUnityCli.Discovery;
using IsuzuUnityCli.Http;
using IsuzuUnityCli.Tests.Fakes;
using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public sealed class CallCommandTests
{
    private static (CommandContext Context, StringWriter Out, StringWriter Err) Context(FakeUnityServer server)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var context = new CommandContext
        {
            Out = output,
            Err = error,
            ReadDescriptors = () => new List<InstanceDescriptor> { server.Descriptor() },
            WorkingDirectory = Path.GetTempPath(),
            Client = new UnityHttpClient(new RetryOptions
            {
                InitialBackoffMs = 10,
                MaxBackoffMs = 50,
                BudgetMs = 3000,
                PerAttemptTimeoutMs = 3000,
            }),
        };

        return (context, output, error);
    }

    private static Task<int> Call(CommandContext context, params string[] argv)
    {
        return CallCommand.Run(ArgParser.Parse(["call", .. argv]), context, pollIntervalMs: 5);
    }

    /// <summary>
    /// Entering play mode returns once the Editor is playing, and the reload in between is waited out.
    /// </summary>
    /// <remarks>
    /// The deferred answer sent the caller back to ask again, and a caller that stepped straight
    /// away was told play mode had not started.
    /// </remarks>
    [Fact]
    public async Task PlayReturnsTheStatusOnceTheEditorIsPlaying()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, """{"status":"success","result":{"deferred":true,"action":"play","paused":true}}""")
            .Enqueue(200, """{"status":"success","result":{"isPlaying":false,"isPaused":true,"pending":"play","refused":null}}""")
            .EnqueueDrop()
            .Enqueue(200, """{"status":"success","result":{"isPlaying":true,"isPaused":true,"pending":null,"refused":null}}""");
        var (context, output, _) = Context(server);

        Assert.Equal(0, await Call(context, "play_mode_play", "--paused"));
        Assert.Contains("\"isPlaying\":true", output.ToString());
        Assert.DoesNotContain("deferred", output.ToString());
        Assert.Equal("/tools/play_mode_play", server.Requests[0].Path);
        Assert.All(server.Requests.Skip(1), request => Assert.Equal("/tools/play_mode_status", request.Path));
    }

    /// <summary>A refusal ends the wait at once and reaches the shell as a failure.</summary>
    /// <remarks>Unity says nothing when compile errors keep it out of play mode, so a wait would run to its end.</remarks>
    [Fact]
    public async Task PlayThatTheEditorRefusedFailsWithTheReason()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, """{"status":"success","result":{"deferred":true,"action":"play"}}""")
            .Enqueue(200, """{"status":"success","result":{"isPlaying":false,"pending":null,"refused":{"action":"play","reason":"There are compile errors."}}}""");
        var (context, _, error) = Context(server);

        Assert.Equal(1, await Call(context, "play_mode_play"));
        Assert.Contains("play_refused", error.ToString());
        Assert.Contains("compile errors", error.ToString());
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task PlayStillChangingWhenTheWaitRunsOutExitsFour()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, """{"status":"success","result":{"deferred":true,"action":"play"}}""");
        server.Default(200, """{"status":"success","result":{"isPlaying":false,"pending":"play"}}""");
        var (context, _, error) = Context(server);

        Assert.Equal(4, await Call(context, "play_mode_play", "--wait-timeout", "0.2"));
        Assert.Contains("had not finished", error.ToString());
    }

    /// <summary>A call that became a job prints the job's result, as if it had answered in time.</summary>
    [Fact]
    public async Task AJobIsFollowedToItsResult()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, """{"status":"success","result":{"state":"running","jobId":"j1","message":"Still running on the Editor main thread"}}""")
            .Enqueue(200, """{"status":"success","result":{"id":"j1","status":"running"}}""")
            .Enqueue(200, """{"status":"success","result":{"id":"j1","status":"completed","result":{"passed":3}}}""");
        var (context, output, _) = Context(server);

        Assert.Equal(0, await Call(context, "test_run", "--mode", "edit"));
        Assert.Equal("{\"passed\":3}", output.ToString().Trim());
        Assert.Equal("/jobs/j1", server.Requests[^1].Path);
    }

    /// <summary>A call the reloading server failed before running is sent again.</summary>
    /// <remarks>A compile or play mode change a moment earlier sets off that reload, so the next call in a chain met it.</remarks>
    [Fact]
    public async Task ACallTheServerDroppedBeforeRunningIsSentAgain()
    {
        using var server = new FakeUnityServer()
            .Enqueue(409, """{"status":"error","error":{"code":"server_stopped","message":"Unity MCP server stopped before this work started."}}""")
            .Enqueue(200, """{"status":"success","result":{"written":true}}""");
        var (context, output, _) = Context(server);

        Assert.Equal(0, await Call(context, "inspect_write", "--object_path", "/A", "--property_path", "m_Name", "--value", "B"));
        Assert.Contains("\"written\":true", output.ToString());
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task NoWaitPrintsTheFirstAnswer()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, """{"status":"success","result":{"deferred":true,"action":"play"}}""");
        var (context, output, _) = Context(server);

        Assert.Equal(0, await Call(context, "play_mode_play", "--no-wait"));
        Assert.Contains("deferred", output.ToString());
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task WordsLeftAfterTheToolNameAreRefusedBeforeAnythingIsSent()
    {
        var context = new CommandContext
        {
            Out = new StringWriter(),
            Err = new StringWriter(),
            ReadDescriptors = () => throw new InvalidOperationException("must not resolve"),
        };

        Assert.Equal(2, await Program.Run(["call", "gameobject_create", "--name", "Spawner", "Child"], context));
    }
}
