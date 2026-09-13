using System.Text.Json.Nodes;
using IsuzuUnityCli.Commands;
using IsuzuUnityCli.Discovery;
using IsuzuUnityCli.Tests.Fakes;
using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public sealed class ProjectReconnectTests
{
    private const string Unauthorized = """{"status":"error","error":{"code":"unauthorized","message":"token changed"}}""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyDoesNotStartTestsInAnotherProjectAfterReauthentication(bool explicitProject)
    {
        using var original = new FakeUnityServer().Default(401, Unauthorized);
        using var other = new FakeUnityServer()
            .Enqueue(200, """{"status":"success","result":{"started":true}}""")
            .Enqueue(200, """{"status":"success","result":{"status":"completed","passed":1,"results":[]}}""")
            .Enqueue(200, """{"status":"success","result":{"logs":[]}}""");
        var reads = 0;
        var otherDescriptor = other.Descriptor("Other");
        otherDescriptor.ProjectName = "Original";
        var error = new StringWriter();
        var context = new CommandContext
        {
            Out = new StringWriter(),
            Err = error,
            ReadDescriptors = () => ++reads == 1 ? [original.Descriptor("Original")] : [otherDescriptor],
        };

        string[] args = ["verify", "--no-compile", "--test", "--timeout", "2"];
        if (explicitProject)
        {
            args = [.. args, "--project", "Original"];
        }
        var code = await Program.Run(args, context);

        Assert.Empty(other.Requests);
        Assert.Equal(4, code);
        Assert.Contains("No running Editor has", error.ToString());
        Assert.Equal("/tools/test_run", original.Requests[0].Path);
    }

    [Fact]
    public async Task WaitingForAJobDoesNotPollAnotherProject()
    {
        using var original = new FakeUnityServer().Default(401, Unauthorized);
        using var other = new FakeUnityServer()
            .Default(404, """{"status":"error","error":{"code":"job_not_found","message":"Unknown job"}}""");
        var reads = 0;
        var error = new StringWriter();
        var context = new CommandContext
        {
            Out = new StringWriter(),
            Err = error,
            ReadDescriptors = () => ++reads == 1 ? [original.Descriptor("Original")] : [other.Descriptor("Other")],
        };

        var code = await Program.Run(["jobs", "j1", "--wait", "--timeout", "2"], context);

        Assert.Empty(other.Requests);
        Assert.Equal(4, code);
        Assert.Contains("No running Editor has", error.ToString());
    }

    [Fact]
    public async Task StdioDoesNotForwardTheNextToolCallToAnotherProject()
    {
        using var original = new FakeUnityServer().Default(401, Unauthorized);
        using var other = new FakeUnityServer().Default(200, """{"jsonrpc":"2.0","id":2,"result":{}}""");
        var input = new GatedReader();
        var output = new RecordingWriter();
        var reads = 0;
        var context = new CommandContext
        {
            In = input,
            Out = output,
            Err = new StringWriter(),
            ReadDescriptors = () => ++reads == 1 ? [original.Descriptor("Original")] : [other.Descriptor("Other")],
        };
        var run = Program.Run(["mcp-stdio"], context);

        try
        {
            input.Send("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
            await RecordingWriter.WaitFor(() => output.Lines.Count == 1, "the first connection failure");
            input.Send("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"gameobject_create","arguments":{"name":"MustStayInOriginal"}}}""");
            await RecordingWriter.WaitFor(() => output.Lines.Count == 2, "the next tool reply");
        }
        finally
        {
            input.CloseInput();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var refusal = JsonNode.Parse(output.Lines[1])!["error"]!;

        Assert.Empty(other.Requests);
        Assert.Equal(-32000, refusal["code"]!.GetValue<int>());
        Assert.Contains("No running Editor has", refusal["message"]!.GetValue<string>());
        Assert.EndsWith(InstanceResolver.SwitchByRestart, refusal["message"]!.GetValue<string>());
        Assert.Single(original.Requests);
    }

    [Theory]
    [InlineData("verify")]
    [InlineData("jobs")]
    public async Task CommandsReconnectToANewEndpointForTheSameProject(string command)
    {
        using var original = new FakeUnityServer().Default(401, Unauthorized);
        using var restarted = new FakeUnityServer();
        using var other = new FakeUnityServer();
        if (command == "verify")
        {
            restarted.Enqueue(200, """{"status":"success","result":{"started":true}}""")
                .Enqueue(200, """{"status":"success","result":{"status":"completed","passed":1,"results":[]}}""")
                .Enqueue(200, """{"status":"success","result":{"logs":[]}}""");
        }
        else
        {
            restarted.Enqueue(200, """{"status":"success","result":{"id":"j1","status":"completed","result":{}}}""");
        }
        var first = original.Descriptor("Original");
        var fresh = restarted.Descriptor("Renamed", token: "fresh");
        fresh.ProjectPath = first.ProjectPath;
        var reads = 0;
        var context = new CommandContext
        {
            Out = new StringWriter(),
            Err = new StringWriter(),
            ReadDescriptors = () => ++reads == 1 ? [first] : [other.Descriptor("Other"), fresh],
        };
        string[] args = command == "verify"
            ? ["verify", "--no-compile", "--test", "--timeout", "2", "--project", "Original"]
            : ["jobs", "j1", "--wait", "--timeout", "2", "--project", "Original"];

        Assert.Equal(0, await Program.Run(args, context));
        Assert.Empty(other.Requests);
        Assert.NotEmpty(restarted.Requests);
        Assert.All(restarted.Requests, request => Assert.Equal("Bearer fresh", request.Authorization));
    }

    /// <summary>
    /// The project's descriptor is missing for a while and only another project is open, as while
    /// its Editor restarts next to a second window.
    /// </summary>
    [Theory]
    [InlineData("verify")]
    [InlineData("jobs")]
    public async Task CommandsWaitForTheirProjectWhileOnlyAnotherIsOpen(string command)
    {
        using var original = new FakeUnityServer().Default(401, Unauthorized);
        using var restarted = new FakeUnityServer();
        using var other = new FakeUnityServer();
        if (command == "verify")
        {
            restarted.Enqueue(200, """{"status":"success","result":{"started":true}}""")
                .Enqueue(200, """{"status":"success","result":{"status":"completed","passed":1,"results":[]}}""")
                .Enqueue(200, """{"status":"success","result":{"logs":[]}}""");
        }
        else
        {
            restarted.Enqueue(200, """{"status":"success","result":{"id":"j1","status":"completed","result":{}}}""");
        }
        var first = original.Descriptor("Original");
        var fresh = restarted.Descriptor("Original", token: "fresh");
        var reads = 0;
        var context = new CommandContext
        {
            Out = new StringWriter(),
            Err = new StringWriter(),
            ReadDescriptors = () => ++reads == 1 ? [first] : reads == 2 ? [other.Descriptor("Other")] : [other.Descriptor("Other"), fresh],
        };
        string[] args = command == "verify"
            ? ["verify", "--no-compile", "--test", "--timeout", "10", "--project", "Original"]
            : ["jobs", "j1", "--wait", "--timeout", "10", "--project", "Original"];

        Assert.Equal(0, await Program.Run(args, context));
        Assert.Empty(other.Requests);
        Assert.NotEmpty(restarted.Requests);
        Assert.All(restarted.Requests, request => Assert.Equal("Bearer fresh", request.Authorization));
    }

    [Fact]
    public async Task StdioCanStartBeforeAnEditorAndRecoverWhenItsOriginalProjectReturns()
    {
        using var original = new FakeUnityServer().Default(401, Unauthorized);
        using var other = new FakeUnityServer();
        using var restarted = new FakeUnityServer().Default(200, """{"jsonrpc":"2.0","id":4,"result":{}}""");
        var first = original.Descriptor("Original");
        var fresh = restarted.Descriptor("Renamed", token: "fresh");
        fresh.ProjectPath = first.ProjectPath;
        var input = new GatedReader();
        var output = new RecordingWriter();
        var stage = 0;
        var context = new CommandContext
        {
            In = input,
            Out = output,
            Err = new StringWriter(),
            ReadDescriptors = () => Volatile.Read(ref stage) switch
            {
                0 => [],
                1 => [first],
                2 => [other.Descriptor("Other")],
                _ => [other.Descriptor("Other"), fresh],
            },
        };
        var run = Program.Run(["mcp-stdio"], context);
        try
        {
            for (var i = 0; i < 4; i++)
            {
                Volatile.Write(ref stage, i);
                input.Send($$"""{"jsonrpc":"2.0","id":{{i + 1}},"method":"tools/list"}""");
                await RecordingWriter.WaitFor(() => output.Lines.Count == i + 1, "the tool reply");
            }
        }
        finally
        {
            input.CloseInput();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Empty(other.Requests);
        Assert.Single(original.Requests);
        Assert.Equal("Bearer fresh", Assert.Single(restarted.Requests).Authorization);
        Assert.EndsWith(InstanceResolver.SwitchByRestart, JsonNode.Parse(output.Lines[2])!["error"]!["message"]!.GetValue<string>());
        Assert.NotNull(JsonNode.Parse(output.Lines[3])!["result"]);
    }
}
