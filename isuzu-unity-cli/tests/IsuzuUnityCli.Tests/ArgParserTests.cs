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

    [Fact]
    public void AnOptionThatTakesAValueStillTakesTheNextToken()
    {
        var parsed = ArgParser.Parse(["call", "scene_browse_hierarchy", "--json", "{\"limit\":5}", "--compact"]);

        Assert.Equal("{\"limit\":5}", parsed.Option("json"));
        Assert.True(parsed.HasFlag("compact"));
        Assert.Equal("scene_browse_hierarchy", parsed.Positional[0]);
    }
}
