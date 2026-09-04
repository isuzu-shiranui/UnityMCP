using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Commands;
using IsuzuUnityCli.Discovery;
using IsuzuUnityCli.Http;
using IsuzuUnityCli.Tests.Fakes;
using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public sealed class VerifyCommandTests
{
    private const string NoErrors = """{"status":"success","result":{"logs":[],"total":0,"errors":0,"warnings":0}}""";

    private static readonly VerifyPolling Fast = new(CompileIntervalMs: 5, TestIntervalMs: 5, CompileStartGraceMs: 60);

    private static string Idle(string completedAt) => $$$"""
        {"status":"success","result":{"isCompiling":false,"isUpdating":false,"succeeded":true,
         "errorCount":0,"completedAt":"{{{completedAt}}}","truncated":false,"messages":[]}}
        """;

    private const string Compiling = """
        {"status":"success","result":{"isCompiling":true,"isUpdating":false,"succeeded":null,
         "errorCount":0,"completedAt":null,"truncated":false,"messages":[]}}
        """;

    private const string Requested = """{"status":"success","result":{"requested":true,"message":"queued"}}""";

    private static (CommandContext Context, StringWriter Out, StringWriter Err) Context(
        Func<IReadOnlyList<InstanceDescriptor>> descriptors)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var context = new CommandContext
        {
            Out = output,
            Err = error,
            ReadDescriptors = descriptors,
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

    private static (CommandContext Context, StringWriter Out, StringWriter Err) Context(FakeUnityServer server)
    {
        return Context(() => [server.Descriptor()]);
    }

    private static Task<int> Verify(CommandContext context, params string[] argv)
    {
        return VerifyCommand.Run(ArgParser.Parse(["verify", .. argv]), context, Fast);
    }

    [Fact]
    public async Task CompileOnlyReportsSuccess()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, Idle("2026-09-04T00:00:00Z"))
            .Enqueue(200, Requested)
            .Enqueue(200, Compiling)
            .Enqueue(200, Idle("2026-09-04T00:00:09Z"))
            .Enqueue(200, NoErrors);

        var (context, output, error) = Context(server);

        Assert.Equal(0, await Verify(context));
        Assert.Contains("compile: ok (0 errors,", output.ToString());
        Assert.Contains("console: 0 errors", output.ToString());
        Assert.DoesNotContain("tests:", output.ToString());
        Assert.Equal("", error.ToString());

        Assert.Equal(
            ["/tools/compile_status", "/tools/compile_request", "/tools/compile_status", "/tools/compile_status", "/tools/console_read_logs"],
            server.Requests.Select(r => r.Path));
    }

    [Fact]
    public async Task AStatusReadThatBecameAJobIsFollowedAndTheDialogIsPrintedOnce()
    {
        const string dialog = "The Editor is showing a dialog \"Scene(s) Have Been Modified\" (Save?) with buttons Save / Don't Save / Cancel. Nothing proceeds until it is answered; use editor_dialog_press or answer it in the Editor.";
        var escaped = dialog.Replace("\"", "\\\"");
        var running = $$$"""
            {"status":"success","result":{"state":"running","jobId":"compile_status-1","poll":"/jobs/compile_status-1",
             "message":"'compile_status' is still running on the Editor main thread. {{{escaped}}}","notice":"{{{escaped}}}"}}
            """;
        var jobRunning = $$$"""
            {"status":"success","result":{"id":"compile_status-1","label":"compile_status","status":"running","ageSec":1.2,
             "message":"{{{escaped}}}","notice":"{{{escaped}}}"}}
            """;
        var jobDone = """
            {"status":"success","result":{"id":"compile_status-1","label":"compile_status","status":"completed","ageSec":2.5,
             "result":{"isCompiling":false,"isUpdating":false,"succeeded":true,"errorCount":0,"completedAt":"T0","truncated":false,"messages":[]}}}
            """;

        using var server = new FakeUnityServer()
            .Enqueue(202, running)
            .Enqueue(200, jobRunning)
            .Enqueue(200, jobRunning)
            .Enqueue(200, jobDone)
            .Enqueue(200, Requested)
            .Enqueue(200, Compiling)
            .Enqueue(200, Idle("T1"))
            .Enqueue(200, NoErrors);

        var (context, output, error) = Context(server);

        Assert.Equal(0, await Verify(context));
        Assert.Contains("compile: ok", output.ToString());
        Assert.Equal(dialog + Environment.NewLine, error.ToString());
        Assert.Equal(
            ["/tools/compile_status", "/jobs/compile_status-1", "/jobs/compile_status-1", "/jobs/compile_status-1", "/tools/compile_request", "/tools/compile_status", "/tools/compile_status", "/tools/console_read_logs"],
            server.Requests.Select(r => r.Path));
    }

    [Fact]
    public async Task AJobLostToADomainReloadRepeatsTheCall()
    {
        const string running = """
            {"status":"success","result":{"state":"running","jobId":"compile_status-1","poll":"/jobs/compile_status-1",
             "message":"'compile_status' is still running on the Editor main thread."}}
            """;

        using var server = new FakeUnityServer()
            .Enqueue(202, running)
            .Enqueue(404, """{"status":"error","error":{"code":"job_not_found","message":"No job with id 'compile_status-1'."}}""")
            .Enqueue(200, Idle("T0"))
            .Enqueue(200, Requested)
            .Enqueue(200, Idle("T1"))
            .Enqueue(200, NoErrors);

        var (context, output, error) = Context(server);

        Assert.Equal(0, await Verify(context));
        Assert.Contains("compile: ok", output.ToString());
        Assert.Equal("", error.ToString(), ignoreLineEndingDifferences: true);
        Assert.Equal("/tools/compile_status", server.Requests[2].Path);
    }

    [Fact]
    public async Task AnEmptyReplyDuringTheReloadIsRetriedLikeARefusedConnection()
    {
        // Mono's listener answers an empty 200 while the domain unloads and again the moment
        // it restarts; the next request already finds a healthy, settled server, so no probe
        // is consulted and the call is simply repeated.
        using var server = new FakeUnityServer()
            .Enqueue(200, Idle("T0"))
            .Enqueue(200, Requested)
            .Enqueue(200, "")
            .Enqueue(200, Idle("T1"))
            .Enqueue(200, "")
            .Enqueue(200, NoErrors);

        var (context, output, _) = Context(server);

        Assert.Equal(0, await Verify(context));
        Assert.Contains("compile: ok", output.ToString());
        Assert.Equal(
            ["/tools/compile_status", "/tools/compile_request", "/tools/compile_status", "/tools/compile_status", "/tools/console_read_logs", "/tools/console_read_logs"],
            server.Requests.Select(r => r.Path));
    }

    [Fact]
    public async Task AServerErrorWhileTheEditorIsStillCompilingIsRetried()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, Idle("T0"))
            .Enqueue(200, Requested)
            .Enqueue(500, """{"status":"error","error":{"code":"tool_failed","message":"domain unloading"}}""")
            .Enqueue(200, """{"status":"success","result":{"status":"ok"}}""")
            .Enqueue(200, Compiling)
            .Enqueue(200, Idle("T1"))
            .Enqueue(200, NoErrors);

        var (context, output, _) = Context(server);

        Assert.Equal(0, await Verify(context));
        Assert.Contains("compile: ok", output.ToString());
        Assert.Equal("/health", server.Requests[3].Path);
    }

    [Fact]
    public async Task AToolFailureOnASettledEditorIsReportedNotRetried()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, Idle("T0"))
            .Enqueue(200, Requested)
            .Enqueue(200, Idle("T1"))
            .Enqueue(500, """{"status":"error","error":{"code":"tool_failed","message":"boom"}}""")
            .Enqueue(200, """{"status":"success","result":{"status":"ok"}}""")
            .Enqueue(200, Idle("T1"));

        var (context, _, error) = Context(server);

        Assert.Equal(1, await Program.Run(["verify", "--timeout", "5"], context));
        Assert.Equal("error [tool_failed]: boom" + Environment.NewLine, error.ToString());
        Assert.Equal(
            ["/tools/compile_status", "/tools/compile_request", "/tools/compile_status", "/tools/console_read_logs", "/health", "/tools/compile_status"],
            server.Requests.Select(r => r.Path));
    }

    [Fact]
    public async Task ACompilationAlreadyInFlightIsWaitedForEvenWithoutANewTimestamp()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, Idle("T0"))
            .Enqueue(200, """{"status":"success","result":{"requested":false,"message":"already in progress"}}""")
            .Enqueue(200, Idle("T0"))
            .Enqueue(200, NoErrors);

        var (context, output, _) = Context(server);

        Assert.Equal(0, await Verify(context));
        Assert.Contains("compile: ok", output.ToString());
        Assert.Equal(4, server.Requests.Count);
    }

    [Fact]
    public async Task ARequestThatCompilesNothingReportsTheStandingResultAfterTheGrace()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, Idle("T0"))
            .Enqueue(200, Requested)
            .Default(200, Idle("T0"));

        var (context, output, error) = Context(server);

        Assert.Equal(0, await Verify(context, "--timeout", "5"));
        Assert.Contains("compile: ok", output.ToString());
        Assert.Equal("", error.ToString());
        Assert.Equal("/tools/console_read_logs", server.Requests[^1].Path);
    }

    [Fact]
    public async Task CompileErrorsAreListedAndFailTheRun()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, Idle("T0"))
            .Enqueue(200, Requested)
            .Enqueue(200, Compiling)
            .Enqueue(200, """
                {"status":"success","result":{"isCompiling":false,"isUpdating":false,"succeeded":false,
                 "errorCount":2,"completedAt":"T1","truncated":false,"messages":[
                   {"assembly":"Assembly-CSharp.dll","type":"error","file":"Assets/Foo.cs","line":12,"column":5,
                    "message":"Assets/Foo.cs(12,5): error CS0103: The name 'x' does not exist"},
                   {"assembly":"Assembly-CSharp.dll","type":"error","file":"Assets/Bar.cs","line":3,"column":1,
                    "message":"CS1002: ; expected"}]}}
                """)
            .Enqueue(200, NoErrors);

        var (context, output, _) = Context(server);

        Assert.Equal(1, await Verify(context));
        Assert.Contains("compile: FAILED (2 errors)", output.ToString());
        Assert.Contains("  Assets/Foo.cs(12,5): CS0103: The name 'x' does not exist", output.ToString());
        Assert.Contains("  Assets/Bar.cs(3,1): CS1002: ; expected", output.ToString());
    }

    [Fact]
    public async Task DroppedConnectionsDuringPollingAreRetried()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, Idle("T0"))
            .Enqueue(200, Requested)
            .EnqueueDrop()
            .EnqueueDrop()
            .Enqueue(200, Idle("T1"))
            .Enqueue(200, NoErrors);

        var (context, output, _) = Context(server);

        Assert.Equal(0, await Verify(context));
        Assert.Contains("compile: ok", output.ToString());
        Assert.Equal(6, server.Requests.Count);
    }

    [Fact]
    public async Task UnauthorizedRereadsTheDescriptorForANewToken()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, Idle("T0"))
            .Enqueue(200, Requested)
            .Enqueue(401, """{"status":"error","error":{"code":"unauthorized","message":"bad token"}}""")
            .Enqueue(200, Idle("T1"))
            .Enqueue(200, NoErrors);

        var reads = 0;
        var (context, output, _) = Context(() =>
        {
            reads++;
            return [server.Descriptor(token: reads == 1 ? "stale-token" : "fresh-token")];
        });

        Assert.Equal(0, await Verify(context));
        Assert.Contains("compile: ok", output.ToString());
        Assert.Equal("Bearer stale-token", server.Requests[2].Authorization);
        Assert.Equal("Bearer fresh-token", server.Requests[3].Authorization);
    }

    [Fact]
    public async Task FailingTestsAreNamedAndFailTheRun()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, """{"status":"success","result":{"started":true,"mode":"EditMode","message":"queued"}}""")
            .Enqueue(200, """{"status":"success","result":{"status":"running","passed":0,"failed":0,"skipped":0,"results":[]}}""")
            .Enqueue(200, """
                {"status":"success","result":{"status":"completed","mode":"EditMode","passed":12,"failed":1,
                 "skipped":0,"results":[{"name":"PortIsReleased","fullName":"A.PortIsReleased","status":"failed",
                 "message":"Expected: 1\nBut was: 2"}]}}
                """)
            .Enqueue(200, NoErrors);

        var (context, output, _) = Context(server);

        Assert.Equal(1, await Verify(context, "--no-compile", "--test", "--filter", "PortIs"));
        Assert.Contains("tests: 12 passed, 1 failed (edit)", output.ToString());
        Assert.Contains("  PortIsReleased: Expected: 1 But was: 2", output.ToString());
        Assert.Equal("""{"mode":"edit","filter":"PortIs"}""", server.Requests[0].Body);
    }

    [Fact]
    public async Task NoCompileSkipsEveryCompileCall()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, """{"status":"success","result":{"started":true,"mode":"EditMode"}}""")
            .Enqueue(200, """{"status":"success","result":{"status":"completed","passed":3,"failed":0,"skipped":0,"results":[]}}""")
            .Enqueue(200, NoErrors);

        var (context, output, _) = Context(server);

        Assert.Equal(0, await Verify(context, "--no-compile", "--test"));
        Assert.Contains("compile: skipped", output.ToString());
        Assert.Contains("tests: 3 passed, 0 failed (edit)", output.ToString());
        Assert.Equal(["/tools/test_run", "/tools/test_results", "/tools/console_read_logs"], server.Requests.Select(r => r.Path));
    }

    [Fact]
    public async Task ConsoleErrorsAreReportedWithoutFailingTheRun()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, Idle("T0"))
            .Enqueue(200, Requested)
            .Enqueue(200, Idle("T1"))
            .Enqueue(200, """
                {"status":"success","result":{"logs":[{"t":"E","m":"NullReferenceException","f":"Assets/A.cs","l":9}],
                 "total":1,"errors":1,"warnings":0}}
                """);

        var (context, output, _) = Context(server);

        Assert.Equal(0, await Verify(context));
        Assert.Contains("console: 1 errors (last: NullReferenceException)", output.ToString());
    }

    [Fact]
    public async Task CompilationThatNeverFinishesTimesOut()
    {
        using var server = new FakeUnityServer();

        var (context, output, error) = Context(server);

        Assert.Equal(4, await Verify(context, "--timeout", "0.4"));
        Assert.Contains("compile: unfinished", output.ToString());
        Assert.DoesNotContain("console:", output.ToString());
        Assert.Equal("verify timed out after 0.4s" + Environment.NewLine, error.ToString());
    }

    [Fact]
    public async Task RawPrintsTheSummary()
    {
        using var server = new FakeUnityServer()
            .Enqueue(200, Idle("T0"))
            .Enqueue(200, Requested)
            .Enqueue(200, Idle("T1"))
            .Enqueue(200, NoErrors);

        var (context, output, _) = Context(server);

        Assert.Equal(0, await Verify(context, "--raw"));

        var summary = System.Text.Json.Nodes.JsonNode.Parse(output.ToString())!;
        Assert.Equal("Fake", summary["project"]!.GetValue<string>());
        Assert.True(summary["ok"]!.GetValue<bool>());
        Assert.True(summary["compile"]!["succeeded"]!.GetValue<bool>());
        Assert.Null(summary["tests"]);
        Assert.Empty(summary["consoleErrors"]!.AsArray());
    }

    [Fact]
    public async Task ToolErrorsAreSurfaced()
    {
        using var server = new FakeUnityServer()
            .Enqueue(400, """{"status":"error","error":{"code":"invalid_params","message":"'both' is not a test mode."}}""");

        var (context, _, error) = Context(server);

        Assert.Equal(1, await Program.Run(["verify", "--no-compile", "--test", "--test-mode", "both"], context));
        Assert.Equal("error [invalid_params]: 'both' is not a test mode." + Environment.NewLine, error.ToString());
    }
}
