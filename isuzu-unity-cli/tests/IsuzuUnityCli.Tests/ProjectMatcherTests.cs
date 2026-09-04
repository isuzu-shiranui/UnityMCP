using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Discovery;
using Xunit;

namespace IsuzuUnityCli.Tests;

public sealed class ProjectMatcherTests
{
    private sealed record Project(string ProjectName, string ProjectPath) : IProjectLike;

    private static readonly List<Project> Open =
    [
        new("UnityMCP v3 Test", "/p/a/Assets"),
        new("UnityMCP v3 Test B", "/p/b/Assets"),
        new("Other", "/p/c/Assets"),
    ];

    // The Editor publishes Application.productName, while the window title the user reads is the
    // project folder. Both have to work, because the title bar is what gets typed.
    private static readonly List<Project> Renamed =
    [
        new("VRChat", Folder("UnityMCP VRChat Test")),
        new("Game", Folder("Game")),
    ];

    private static string Folder(string name) => Path.Combine(Path.GetTempPath(), "folders", name, "Assets");

    [Fact]
    public void ExactNameWinsOverSubstring()
    {
        Assert.Equal("UnityMCP v3 Test", ProjectMatcher.ByName(Open, "UnityMCP v3 Test").ProjectName);
    }

    [Fact]
    public void NameIsCaseInsensitiveAndTrimmed()
    {
        Assert.Equal("UnityMCP v3 Test B", ProjectMatcher.ByName(Open, "unitymcp v3 test b").ProjectName);
        Assert.Equal("UnityMCP v3 Test B", ProjectMatcher.ByName(Open, "  Test B  ").ProjectName);
    }

    [Fact]
    public void UniqueSubstringMatches()
    {
        Assert.Equal("UnityMCP v3 Test B", ProjectMatcher.ByName(Open, "Test B").ProjectName);
    }

    [Fact]
    public void NoMatchListsRunningProjectsWithBothNames()
    {
        var e = Assert.Throws<CliException>(() => ProjectMatcher.ByName(Open, "Nothing"));

        Assert.Equal(
            "No running Editor matches \"Nothing\". Running: UnityMCP v3 Test (folder: a), "
            + "UnityMCP v3 Test B (folder: b), Other (folder: c)",
            e.Message);
        Assert.Equal(3, e.ExitCode);
    }

    [Fact]
    public void AListingShowsOneNameWhenTheTwoAgree()
    {
        var same = new List<Project> { new("Game", Folder("Game")) };

        Assert.Equal("Game", ProjectMatcher.Names(same));
    }

    [Fact]
    public void AListingOmitsTheFolderWhenTheDescriptorCarriesNoPath()
    {
        var pathless = new List<Project> { new("Game", "") };

        Assert.Equal("Game", ProjectMatcher.Names(pathless));
    }

    [Fact]
    public void AmbiguousSubstringListsCandidates()
    {
        var e = Assert.Throws<CliException>(() => ProjectMatcher.ByName(Open, "v3"));

        Assert.Equal(
            "\"v3\" matches more than one running Editor: UnityMCP v3 Test (folder: a), "
            + "UnityMCP v3 Test B (folder: b). Use the full project name.",
            e.Message);
        Assert.Equal(3, e.ExitCode);
    }

    [Fact]
    public void ProductNameMatches()
    {
        Assert.Equal("VRChat", ProjectMatcher.ByName(Renamed, "VRChat").ProjectName);
    }

    [Fact]
    public void FolderNameMatches()
    {
        Assert.Equal("VRChat", ProjectMatcher.ByName(Renamed, "UnityMCP VRChat Test").ProjectName);
        Assert.Equal("VRChat", ProjectMatcher.ByName(Renamed, "unitymcp vrchat test").ProjectName);
    }

    [Fact]
    public void FolderNameSubstringMatches()
    {
        Assert.Equal("VRChat", ProjectMatcher.ByName(Renamed, "MCP VRChat").ProjectName);
    }

    [Fact]
    public void AnEditorAnsweringToBothNamesIsOneMatch()
    {
        var both = new List<Project> { new("VRChat Avatars", Folder("VRChat Worlds")) };

        Assert.Equal("VRChat Avatars", ProjectMatcher.ByName(both, "VRChat").ProjectName);
    }

    [Fact]
    public void TwoEditorsMatchingOnDifferentNamesIsAmbiguous()
    {
        var clash = new List<Project>
        {
            new("Avatars", Folder("VRChat Worlds")),
            new("VRChat Avatars", Folder("Worlds")),
        };

        var e = Assert.Throws<CliException>(() => ProjectMatcher.ByName(clash, "VRChat"));

        Assert.Equal(
            "\"VRChat\" matches more than one running Editor: Avatars (folder: VRChat Worlds), "
            + "VRChat Avatars (folder: Worlds). Use the full project name.",
            e.Message);
        Assert.Equal(3, e.ExitCode);
    }

    [Fact]
    public void ProductNameOutranksAnotherEditorsFolderName()
    {
        var clash = new List<Project>
        {
            new("Shared", Folder("Alpha")),
            new("Beta", Folder("Shared")),
        };

        Assert.Equal("Shared", ProjectMatcher.ByName(clash, "Shared").ProjectName);
    }

    [Fact]
    public void ExactFolderNameOutranksAnotherEditorsSubstring()
    {
        var clash = new List<Project>
        {
            new("Alpha", Folder("Prototype")),
            new("Prototype Two", Folder("Beta")),
        };

        Assert.Equal("Alpha", ProjectMatcher.ByName(clash, "Prototype").ProjectName);
    }

    [Fact]
    public void FolderNameStripsTheAssetsSegment()
    {
        Assert.Equal("Game", ProjectMatcher.FolderNameOf(Folder("Game")));
        Assert.Equal("", ProjectMatcher.FolderNameOf(""));
        Assert.Equal("", ProjectMatcher.FolderNameOf("   "));
    }

    [Fact]
    public void TrailingAssetsSegmentIsStripped()
    {
        var root = Path.Combine(Path.GetTempPath(), "Game");

        Assert.Equal(Path.GetFullPath(root), ProjectMatcher.ProjectRootOf(Path.Combine(root, "Assets")));
        Assert.Equal(Path.GetFullPath(root), ProjectMatcher.ProjectRootOf(Path.Combine(root, "assets") + Path.DirectorySeparatorChar));
        Assert.Equal(Path.GetFullPath(root), ProjectMatcher.ProjectRootOf(root));
        Assert.Equal("", ProjectMatcher.ProjectRootOf(""));
    }

    [Fact]
    public void DirectoryEqualToRootIsInside()
    {
        var root = Path.Combine(Path.GetTempPath(), "Game");

        Assert.True(ProjectMatcher.IsInside(root, Path.Combine(root, "Assets")));
        Assert.True(ProjectMatcher.IsInside(Path.Combine(root, "Packages", "x"), Path.Combine(root, "Assets")));
    }

    [Fact]
    public void EscapingWithDotDotIsOutside()
    {
        var root = Path.Combine(Path.GetTempPath(), "Game");

        Assert.False(ProjectMatcher.IsInside(Path.Combine(root, "..", "Sibling"), Path.Combine(root, "Assets")));
        Assert.False(ProjectMatcher.IsInside(Path.Combine(Path.GetTempPath(), "Game2"), Path.Combine(root, "Assets")));
    }

    [Fact]
    public void NestedProjectsPickTheDeepestRoot()
    {
        var outer = Path.Combine(Path.GetTempPath(), "Outer");
        var inner = Path.Combine(outer, "Packages", "Inner");
        var candidates = new List<Project>
        {
            new("Outer", Path.Combine(outer, "Assets")),
            new("Inner", Path.Combine(inner, "Assets")),
        };

        Assert.Equal("Inner", ProjectMatcher.ByWorkingDirectory(candidates, Path.Combine(inner, "Assets", "Scripts"))!.ProjectName);
        Assert.Equal("Outer", ProjectMatcher.ByWorkingDirectory(candidates, Path.Combine(outer, "Assets"))!.ProjectName);
        Assert.Null(ProjectMatcher.ByWorkingDirectory(candidates, Path.GetTempPath()));
    }

    [Fact]
    public void CaseInsensitiveOnlyOnWindows()
    {
        var root = Path.Combine(Path.GetTempPath(), "Game");
        var upper = Path.Combine(Path.GetTempPath(), "GAME", "Assets");

        Assert.Equal(OperatingSystem.IsWindows(), ProjectMatcher.IsInside(upper, Path.Combine(root, "Assets")));
    }
}
