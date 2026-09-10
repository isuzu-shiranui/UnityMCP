using IsuzuUnityCli.Cli;
using Xunit;

namespace IsuzuUnityCli.Tests;

public sealed class ArgParserTests
{
    [Fact]
    public void FirstPositionalIsTheCommand()
    {
        Assert.Equal("tools", ArgParser.Parse(["tools"]).Command);
        Assert.Equal("", ArgParser.Parse([]).Command);
    }

    [Fact]
    public void OptionsTakeTheFollowingValue()
    {
        var parsed = ArgParser.Parse(["call", "console_read_logs", "--type", "error", "--limit", "20"]);

        Assert.Equal("call", parsed.Command);
        Assert.Equal(["console_read_logs"], parsed.Positional);
        Assert.Equal("error", parsed.Option("type"));
        Assert.Equal("20", parsed.Option("limit"));
    }

    [Fact]
    public void TrailingOptionWithoutValueIsAFlag()
    {
        var parsed = ArgParser.Parse(["health", "--raw"]);

        Assert.True(parsed.HasFlag("raw"));
        Assert.Null(parsed.Option("raw"));
    }

    [Fact]
    public void OptionFollowedByAnotherOptionIsAFlag()
    {
        var parsed = ArgParser.Parse(["tools", "--raw", "--project", "MyGame"]);

        Assert.True(parsed.HasFlag("raw"));
        Assert.Equal("MyGame", parsed.Option("project"));
    }

    [Fact]
    public void ShortHelpMapsToHelpFlag()
    {
        Assert.True(ArgParser.Parse(["-h"]).HasFlag("help"));
        Assert.True(ArgParser.Parse(["call", "--help"]).HasFlag("help"));
    }

    [Theory]
    [InlineData(new[] { "--compact", "projects" }, "projects")]
    [InlineData(new[] { "projects", "--compact" }, "projects")]
    [InlineData(new[] { "--compact", "call", "scene_browse_hierarchy" }, "call")]
    [InlineData(new[] { "call", "--compact", "scene_browse_hierarchy" }, "call")]
    [InlineData(new[] { "call", "scene_browse_hierarchy", "--compact" }, "call")]
    public void AValuelessOptionIsAFlagWhereverItSits(string[] argv, string command)
    {
        // Taking the next token as a value loses the command or the tool name, and the flag
        // then reads as unset: the caller gets indented output and a puzzling error.
        var parsed = ArgParser.Parse(argv);

        Assert.True(parsed.HasFlag("compact"));
        Assert.Equal(command, parsed.Command);
    }

    /// <summary>
    /// A name another command owns is still an argument when a tool declares it.
    /// </summary>
    /// <remarks>
    /// 'setup --scope project' put scope on the list of names the CLI keeps to itself, so
    /// 'call asset_broken_references --scope assets' reached the tool as nothing. The tool ran
    /// its default and answered "scope": "scene" as a success, and the only way to notice was to
    /// read the reply and see it disagree with the request.
    /// </remarks>
    [Fact]
    public void AnotherCommandsOptionNameIsStillAToolArgument()
    {
        var parsed = ArgParser.Parse(
            ["call", "asset_broken_references", "--project", "Bench", "--scope", "assets"]);

        var args = ToolArguments.Build("asset_broken_references", parsed);

        Assert.Equal("assets", args["scope"]!.GetValue<string>());
        Assert.False(args.ContainsKey("project"), "the CLI picks the Editor with that one");
    }

    [Fact]
    public void TheOptionsACallItselfConsumesDoNotReachTheTool()
    {
        var parsed = ArgParser.Parse(
            ["call", "scene_browse_hierarchy", "--project", "Bench", "--raw", "--compact"]);

        var args = ToolArguments.Build("scene_browse_hierarchy", parsed);

        Assert.False(args.ContainsKey("project"));
        Assert.False(args.ContainsKey("raw"));
        Assert.False(args.ContainsKey("compact"));
    }

    [Fact]
    public void AnOptionThatTakesAValueStillTakesTheNextToken()
    {
        var parsed = ArgParser.Parse(["call", "scene_browse_hierarchy", "--json", "{\"limit\":5}", "--compact"]);

        Assert.Equal("{\"limit\":5}", parsed.Option("json"));
        Assert.True(parsed.HasFlag("compact"));
        Assert.Equal("scene_browse_hierarchy", parsed.Positional[0]);
    }
}
