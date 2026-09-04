using System.Reflection;
using System.Text.Json.Nodes;
using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Commands;
using IsuzuUnityCli.Discovery;
using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public sealed class ProgramTests
{
    private static (CommandContext Context, StringWriter Out, StringWriter Err) Context(params InstanceDescriptor[] descriptors)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var context = new CommandContext
        {
            Out = output,
            Err = error,
            ReadDescriptors = () => descriptors,
            WorkingDirectory = Path.GetTempPath(),
        };

        return (context, output, error);
    }

    [Fact]
    public async Task HelpAndNoCommandPrintUsageOnStdout()
    {
        foreach (var argv in new[] { new[] { "--help" }, ["-h"], [] })
        {
            var (context, output, error) = Context();

            Assert.Equal(0, await Program.Run(argv, context));
            Assert.StartsWith("isuzu-unity-cli - drive a running Unity Editor from the terminal", output.ToString());
            Assert.Contains("  mcp-stdio ", output.ToString());
            Assert.Equal("", error.ToString());
        }
    }

    [Fact]
    public async Task VersionPrintsAssemblyVersion()
    {
        var (context, output, _) = Context();

        Assert.Equal(0, await Program.Run(["--version"], context));

        // Read from the assembly rather than written here as a literal. A literal has to be
        // edited on every release, and the edit that gets forgotten fails the build over the
        // test's own staleness rather than over anything the CLI did. What is left to assert is
        // the part with logic in it: the build metadata after '+' is stripped.
        var informational = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.Equal(informational.Split('+')[0] + Environment.NewLine, output.ToString());
        Assert.DoesNotContain("+", output.ToString());
        Assert.Matches(@"^\d+\.\d+\.\d+", output.ToString());
    }

    [Fact]
    public async Task UnknownCommandPrintsUsageOnStderr()
    {
        var (context, output, error) = Context();

        Assert.Equal(1, await Program.Run(["bogus"], context));
        Assert.Equal("", output.ToString());
        Assert.StartsWith("Unknown command 'bogus'.", error.ToString());
        Assert.Contains("USAGE", error.ToString());
    }

    [Fact]
    public async Task McpStdioExitsCleanlyOnEmptyInput()
    {
        var (context, output, error) = Context();
        var bridged = new CommandContext { Out = output, Err = error, In = new StringReader(""), ReadDescriptors = () => [] };

        Assert.Equal(0, await Program.Run(["mcp-stdio"], bridged));
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public async Task CallWithoutToolAsksWhichTool()
    {
        var (context, _, error) = Context();

        Assert.Equal(2, await Program.Run(["call"], context));
        Assert.Equal("Which tool? Run `isuzu-unity-cli tools` to see what this Editor publishes." + Environment.NewLine, error.ToString());
    }

    [Fact]
    public async Task ProjectsWithNoEditorIsExitThreeLikeEveryOtherCommand()
    {
        var (context, _, error) = Context();

        Assert.Equal(3, await Program.Run(["projects"], context));
        Assert.Equal(InstanceResolver.NoneRunning + Environment.NewLine, error.ToString());
    }

    [Fact]
    public async Task ProjectsListsRowsWithoutContactingTheEditor()
    {
        var root = Path.Combine(Path.GetTempPath(), "Game");
        var (context, output, _) = Context(new InstanceDescriptor
        {
            ProjectName = "Game",
            ProjectPath = Path.Combine(root, "Assets"),
            UnityVersion = "6000.0.1f1",
            Port = 1,
            Token = "t",
            Pid = 42,
            ProtocolVersion = "3.3.1",
            Endpoint = "http://127.0.0.1:1",
        });

        Assert.Equal(0, await Program.Run(["projects"], context));

        var row = Assert.Single(JsonNode.Parse(output.ToString())!.AsArray());
        Assert.Equal("Game", row!["projectName"]!.GetValue<string>());
        Assert.Equal(Path.GetFullPath(root), row["projectRoot"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:1/mcp", row["mcpUrl"]!.GetValue<string>());
        Assert.Equal(42, row["pid"]!.GetValue<int>());
        Assert.False(row["containsWorkingDirectory"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ResolverFailureIsExitThree()
    {
        var (context, _, error) = Context();

        Assert.Equal(3, await Program.Run(["health"], context));
        Assert.Equal(InstanceResolver.NoneRunning + Environment.NewLine, error.ToString());
    }

    [Fact]
    public void ToolsRenderingMarksRequiredParameters()
    {
        var result = JsonNode.Parse("""
            {"tools":[
              {"name":"asset_find","description":"Search the project.","inputSchema":{"type":"object","properties":{"type":{},"name":{},"limit":{}},"required":["type"]}},
              {"name":"play_mode_status","description":"Report play mode.","inputSchema":{"type":"object","properties":{}}}
            ]}
            """);

        Assert.Equal(
            "asset_find <type> [name] [limit]\n    Search the project.\nplay_mode_status\n    Report play mode.\n",
            ToolsCommand.Render(result));
    }
}
