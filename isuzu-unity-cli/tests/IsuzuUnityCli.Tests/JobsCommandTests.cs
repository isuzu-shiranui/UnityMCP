using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Commands;
using IsuzuUnityCli.Discovery;
using IsuzuUnityCli.Http;
using IsuzuUnityCli.Tests.Fakes;
using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public sealed class JobsCommandTests
{
    private const string Completed =
        """{"status":"success","result":{"id":"j1","label":"execute_code","status":"completed","result":{"value":7}}}""";

    private const string Failed =
        """{"status":"success","result":{"id":"j1","label":"execute_code","status":"failed","error":"boom"}}""";

    private const string Cancelled =
        """{"status":"success","result":{"id":"j1","label":"execute_code","status":"cancelled"}}""";

    private static string Running(string message) => $$$"""
        {"status":"success","result":{"id":"j1","label":"execute_code","status":"running",
         "message":"{{{message}}}","notice":"{{{message}}}"}}
        """;

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

    private static Task<int> Jobs(CommandContext context, params string[] argv)
    {
        return JobsCommand.Run(ArgParser.Parse(["jobs", .. argv]), context, pollIntervalMs: 5);
    }

    [Fact]
    public async Task WithoutWaitOneRequestIsSent()
    {
        using var server = new FakeUnityServer().Enqueue(200, Running("still going"));
        var (context, output, error) = Context(server);

        Assert.Equal(0, await Jobs(context, "j1"));
        Assert.Single(server.Requests);
        Assert.Contains("\"running\"", output.ToString());
        Assert.Contains("still going", error.ToString());
    }

    [Fact]
    public async Task WaitingPollsUntilTheJobEndsAndPrintsOnlyItsLastAnswer()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, Running("compiling"))
            .Enqueue(200, Running("compiling"))
            .Enqueue(200, Completed);

        var (context, output, _) = Context(server);

        Assert.Equal(0, await Jobs(context, "j1", "--wait"));
        Assert.Equal(3, server.Requests.Count);
        Assert.Contains("\"value\"", output.ToString());
        Assert.DoesNotContain("running", output.ToString());
    }

    [Fact]
    public async Task AJobThatFailedReachesTheShellAsAFailure()
    {
        using var server = new FakeUnityServer().Enqueue(200, Failed);
        var (context, _, _) = Context(server);

        Assert.Equal(1, await Jobs(context, "j1", "--wait"));
    }
    [Fact]
    public async Task AJobThatWasCancelledReachesTheShellAsAFailure()
    {
        using var server = new FakeUnityServer().Enqueue(200, Cancelled);
        var (context, _, _) = Context(server);

        Assert.Equal(1, await Jobs(context, "j1", "--wait"));
    }

    [Fact]
    public async Task TheSameNoticeIsPrintedOnce()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, Running("the Editor main thread has not run for 6 s"))
            .Enqueue(200, Running("the Editor main thread has not run for 7 s"))
            .Enqueue(200, Running("the Editor main thread has not run for 8 s"))
            .Enqueue(200, Completed);

        var (context, _, error) = Context(server);

        Assert.Equal(0, await Jobs(context, "j1", "--wait"));

        // A stall notice counts the seconds up every poll; only a dialog appearing is worth a
        // second line.
        var lines = error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.Contains("6 s", lines[0]);
    }

    [Fact]
    public async Task ADialogAppearingAfterAPlainerNoticeIsPrinted()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, Running("the Editor main thread has not run for 6 s"))
            .Enqueue(200, Running("The Editor is showing a dialog 'Save Scene'"))
            .Enqueue(200, Completed);

        var (context, _, error) = Context(server);

        Assert.Equal(0, await Jobs(context, "j1", "--wait"));
        Assert.Equal(2, error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task GivingUpSaysTheJobIsStillRunning()
    {
        using var server = new FakeUnityServer().Default(200, Running("compiling"));
        var (context, _, error) = Context(server);

        Assert.Equal(4, await Jobs(context, "j1", "--wait", "--timeout", "0.05"));
        Assert.Contains("still running after", error.ToString());
        Assert.Contains("keeps running in the Editor", error.ToString());
    }

    [Fact]
    public async Task WaitingWithoutAnIdIsRefused()
    {
        using var server = new FakeUnityServer();
        var (context, _, _) = Context(server);

        var thrown = await Assert.ThrowsAsync<CliException>(() => Jobs(context, "--wait"));

        Assert.Equal(2, thrown.ExitCode);
        Assert.Contains("needs a job id", thrown.Message);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void WaitDoesNotSwallowTheIdThatFollowsIt()
    {
        var parsed = ArgParser.Parse(["jobs", "--wait", "j1"]);

        Assert.True(parsed.HasFlag("wait"));
        Assert.Equal("j1", parsed.Positional[0]);
    }

    [Fact]
    public async Task ATimeoutThatIsNotANumberIsRefused()
    {
        using var server = new FakeUnityServer();
        var (context, _, _) = Context(server);

        var thrown = await Assert.ThrowsAsync<CliException>(() => Jobs(context, "j1", "--wait", "--timeout", "soon"));

        Assert.Equal(2, thrown.ExitCode);
    }

    [Fact]
    public async Task PollingSurvivesTheListenerGoingDownForADomainReload()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, Running("compiling"))
            .EnqueueDrop()
            .Enqueue(200, Completed);

        var (context, output, _) = Context(server);

        Assert.Equal(0, await Jobs(context, "j1", "--wait"));
        Assert.Contains("completed", output.ToString());
    }
}
