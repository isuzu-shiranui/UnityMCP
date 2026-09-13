using IsuzuUnityCli.Discovery;
using Xunit;

namespace IsuzuUnityCli.Tests;

/// <summary>
/// Whether two published paths are one project. A reconnect trusts nothing else, so a key that
/// joined two projects would move a command to the wrong Editor, and one that split a project would
/// only refuse a reconnect.
/// </summary>
public sealed class ProjectKeyTests
{
    [Theory]
    [InlineData("C:/Work/Game/Assets", @"C:\Work\Game")]
    [InlineData("C:/Work/Game/Assets", "c:/Work/Game/")]
    [InlineData("C:/Work/./Game/Tools/../Assets", "C:/Work/Game")]
    [InlineData(@"\\server\share\Game\Assets", "//server/share/Game")]
    public void SpellingsOfOneWindowsFolderShareAKeyOnEveryHost(string published, string other)
    {
        Assert.NotNull(ProjectKey.Of(published));
        Assert.Equal(ProjectKey.Of(published), ProjectKey.Of(other));
    }

    [Theory]
    [InlineData("C:/Work/Game/Assets", "C:/Work/game/Assets")]
    [InlineData("C:/Work/Game/Assets", "D:/Work/Game/Assets")]
    [InlineData("C:/Work/Game/Assets", "C:/Work/Game/Tools/Assets")]
    [InlineData("C:/Work/Game/Assets", "C:/Work/Game-copy/Assets")]
    public void DifferentWindowsFoldersDoNotShareAKey(string published, string other)
    {
        Assert.NotEqual(ProjectKey.Of(published), ProjectKey.Of(other));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("relative/Assets")]
    [InlineData("C:/..")]
    [InlineData("//server")]
    [InlineData("a\0b")]
    public void APathThatCannotBeReadAsAbsoluteHasNoKey(string path)
    {
        Assert.Null(ProjectKey.Of(path));
    }

    [Fact]
    public void PosixPathsAreComparedExactly()
    {
        // On Windows a path starting with a slash is read against the current drive instead.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(ProjectKey.Of("/work/Game/Assets"), ProjectKey.Of("/work/Game/"));
        Assert.Equal(ProjectKey.Of("/work/x/../Game/Assets"), ProjectKey.Of("/work/Game"));
        Assert.NotEqual(ProjectKey.Of("/work/Game/Assets"), ProjectKey.Of("/work/game/Assets"));
        Assert.NotEqual(ProjectKey.Of("C:/work/Game"), ProjectKey.Of("/work/Game"));
    }
}
