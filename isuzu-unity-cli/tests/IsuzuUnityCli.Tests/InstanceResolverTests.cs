using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Discovery;
using Xunit;

namespace IsuzuUnityCli.Tests;

public sealed class InstanceResolverTests
{
    private static InstanceDescriptor Editor(string name, string root) => new()
    {
        ProjectName = name,
        ProjectPath = Path.Combine(root, "Assets"),
        Port = 27180,
        Token = "t",
        Endpoint = "http://127.0.0.1:27180",
    };

    private static readonly string Base = Path.Combine(Path.GetTempPath(), "resolver");

    [Fact]
    public void NoEditorsIsExitThree()
    {
        var e = Assert.Throws<CliException>(() => InstanceResolver.Resolve([], null, Base));

        Assert.Equal(InstanceResolver.NoneRunning, e.Message);
        Assert.Equal(3, e.ExitCode);
    }

    [Fact]
    public void ProjectOptionWinsOverWorkingDirectory()
    {
        var a = Editor("A", Path.Combine(Base, "a"));
        var b = Editor("B", Path.Combine(Base, "b"));

        Assert.Same(b, InstanceResolver.Resolve([a, b], "B", Path.Combine(Base, "a")));
    }

    [Fact]
    public void WorkingDirectoryPicksTheContainingProject()
    {
        var a = Editor("A", Path.Combine(Base, "a"));
        var b = Editor("B", Path.Combine(Base, "b"));

        Assert.Same(b, InstanceResolver.Resolve([a, b], null, Path.Combine(Base, "b", "Assets", "Scripts")));
    }

    [Fact]
    public void SingleEditorIsUsedFromAnywhere()
    {
        var a = Editor("A", Path.Combine(Base, "a"));

        Assert.Same(a, InstanceResolver.Resolve([a], null, Path.GetTempPath()));
    }

    [Fact]
    public void SeveralEditorsOutsideTheWorkingDirectoryIsExitThree()
    {
        var a = Editor("A", Path.Combine(Base, "a"));
        var b = Editor("B", Path.Combine(Base, "b"));

        var e = Assert.Throws<CliException>(() => InstanceResolver.Resolve([a, b], null, Path.GetTempPath()));

        Assert.Equal("Several Editors are running and none contains the working directory: A, B. Pass --project <name>.", e.Message);
        Assert.Equal(3, e.ExitCode);
    }

    [Fact]
    public void TheAmbiguityMessageNamesTheFolderTheTitleBarShows()
    {
        var a = Editor("VRChat", Path.Combine(Base, "UnityMCP VRChat Test"));
        var b = Editor("B", Path.Combine(Base, "b"));

        var e = Assert.Throws<CliException>(() => InstanceResolver.Resolve([a, b], null, Path.GetTempPath()));

        Assert.Equal(
            "Several Editors are running and none contains the working directory: "
            + "VRChat (folder: UnityMCP VRChat Test), B. Pass --project <name>.",
            e.Message);
    }

    [Fact]
    public void TheProjectOptionTakesTheFolderName()
    {
        var a = Editor("VRChat", Path.Combine(Base, "UnityMCP VRChat Test"));
        var b = Editor("B", Path.Combine(Base, "b"));

        Assert.Same(a, InstanceResolver.Resolve([a, b], "UnityMCP VRChat Test", Path.GetTempPath()));
    }

    [Fact]
    public void AnUnexpandedPlaceholderIsTreatedAsNoProject()
    {
        // A host that fills a launch command in leaves the placeholder when the field is blank.
        var only = Editor("Only", Path.Combine(Base, "Only"));

        var resolved = InstanceResolver.Resolve([only], "${user_config.project}", Path.GetTempPath());

        Assert.Equal("Only", resolved.ProjectName);
    }

    [Fact]
    public void APlaceholderWithSeveralEditorsStillAsksWhichOne()
    {
        var error = Assert.Throws<CliException>(
            () => InstanceResolver.Resolve(
                [Editor("Only", Path.Combine(Base, "Only")), Editor("Other", Path.Combine(Base, "Other"))],
                "${user_config.project}",
                Path.GetTempPath()));

        Assert.Equal(3, error.ExitCode);
    }
}

