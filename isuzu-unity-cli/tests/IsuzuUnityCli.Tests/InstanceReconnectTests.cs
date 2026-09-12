using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Discovery;
using Xunit;

namespace IsuzuUnityCli.Tests;

public sealed class InstanceReconnectTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "reconnect", "Original");

    private static InstanceDescriptor Editor(string path, int port = 27180) => new()
    {
        ProjectName = "Same product name",
        ProjectPath = path,
        Port = port,
        Token = "old",
    };

    [Fact]
    public void RefreshAllowsANewEndpointAndProductNameAtTheSameNormalizedRoot()
    {
        var original = Editor(Path.Combine(Root, "Assets"));
        var restarted = Editor(Path.Combine(Root, ".") + Path.DirectorySeparatorChar);
        restarted.ProjectName = "Renamed product";
        restarted.Port++;
        restarted.Pid = 123;
        restarted.Token = "fresh";
        var other = Editor(Root + "-copy");

        Assert.Same(restarted, InstanceResolver.Refresh([other, restarted], original));
    }

    [Theory]
    [InlineData("sibling")]
    [InlineData("child")]
    [InlineData("parent")]
    public void RefreshDoesNotUseNameOrContainmentToChooseAnotherRoot(string relation)
    {
        var original = Editor(Path.Combine(Root, "Assets"));
        var path = relation switch
        {
            "child" => Path.Combine(Root, "Nested", "Assets"),
            "parent" => Path.Combine(Path.GetDirectoryName(Root)!, "Assets"),
            _ => Path.Combine(Root + "-copy", "Assets"),
        };

        var error = Assert.Throws<CliException>(() => InstanceResolver.Refresh([Editor(path)], original));

        Assert.Equal(3, error.ExitCode);
        Assert.Contains($"No running Editor has {Root} open.", error.Message);
        Assert.EndsWith(InstanceResolver.SwitchByCommand, error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/Assets")]
    public void RefreshCannotIdentifyALegacyOrMalformedPathByName(string path)
    {
        var original = Editor(path);

        Assert.Throws<CliException>(() => InstanceResolver.Refresh([Editor(path)], original));
    }

    [Fact]
    public void OfSeveralDescriptorsTheOneThatAnswersIsChosen()
    {
        var original = Editor(Path.Combine(Root, "Assets"));
        var stale = Editor(Root, port: 27181);
        var live = Editor(original.ProjectPath, port: 27182);

        Assert.Same(live, InstanceResolver.Refresh([stale, live], original, answers: d => ReferenceEquals(d, live)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeveralDescriptorsThatAllOrNoneAnswerAreRefused(bool answer)
    {
        var original = Editor(Path.Combine(Root, "Assets"));

        Assert.Throws<CliException>(() => InstanceResolver.Refresh(
            [Editor(Root, port: 27181), Editor(original.ProjectPath, port: 27182)], original, answers: _ => answer));
    }
}
