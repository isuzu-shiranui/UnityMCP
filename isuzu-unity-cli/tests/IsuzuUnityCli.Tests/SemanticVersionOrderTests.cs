using IsuzuUnityCli.Housekeeping;
using Xunit;

namespace IsuzuUnityCli.Tests;

/// <summary>
/// A prerelease comes before the release of the same number. Read the other way round, update
/// refuses to move a project from 4.3.1-1 to 4.3.1 as though that were a downgrade.
/// </summary>
public sealed class SemanticVersionOrderTests
{
    [Theory]
    [InlineData("4.3.1", "4.3.1-1")]
    [InlineData("v4.3.1", "4.3.1-rc.2")]
    [InlineData("4.3.1-rc.10", "4.3.1-rc.9")]
    [InlineData("4.3.1-rc", "4.3.1-1")]
    [InlineData("4.3.1-rc.1", "4.3.1-rc")]
    [InlineData("4.3.2-1", "4.3.1")]
    public void TheFirstIsNewer(string newer, string older)
    {
        Assert.True(ReleaseCheck.IsNewer(newer, older));
        Assert.False(ReleaseCheck.IsNewer(older, newer));
    }

    [Theory]
    [InlineData("4.3.1+build.7", "4.3.1")]
    [InlineData("v4.3.1", "4.3.1+abc")]
    public void BuildMetadataTakesNoPart(string a, string b)
    {
        Assert.False(ReleaseCheck.IsNewer(a, b));
        Assert.False(ReleaseCheck.IsNewer(b, a));
    }
}
