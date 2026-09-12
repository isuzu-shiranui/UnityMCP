using IsuzuUnityCli.Housekeeping;
using Xunit;

namespace IsuzuUnityCli.Tests;

/// <summary>
/// Nothing told anyone a release had happened, so an installation stayed where it was until
/// something broke.
/// </summary>
public sealed class ReleaseCheckTests
{
    [Theory]
    [InlineData("v4.3.0", "4.2.0")]
    [InlineData("4.3.0", "4.2.0")]
    [InlineData("v5.0.0", "4.9.9")]
    [InlineData("v4.2.1", "4.2.0")]
    public void ANewerReleaseIsRecognised(string tag, string current)
    {
        Assert.True(ReleaseCheck.IsNewer(tag, current));
    }

    /// <summary>
    /// Compared as numbers, field by field. A string comparison puts 4.10.0 before 4.9.0, which is
    /// how the newest release ends up reported as an older one and nobody updates.
    /// </summary>
    [Fact]
    public void TenComesAfterNine()
    {
        Assert.True(ReleaseCheck.IsNewer("v4.10.0", "4.9.0"));
        Assert.False(ReleaseCheck.IsNewer("v4.9.0", "4.10.0"));
    }

    [Theory]
    [InlineData("v4.2.0", "4.2.0")]
    [InlineData("v4.1.0", "4.2.0")]
    [InlineData("v3.9.9", "4.0.0")]
    [InlineData("", "4.2.0")]
    [InlineData("not-a-tag", "4.2.0")]
    public void NothingNewerMeansNothingToSay(string tag, string current)
    {
        Assert.False(ReleaseCheck.IsNewer(tag, current));
    }

    [Fact]
    public void TheTagIsReadOutOfTheReleaseJson()
    {
        Assert.Equal("v4.2.0", ReleaseCheck.TagFrom("""{"tag_name":"v4.2.0","name":"4.2.0"}"""));
        Assert.Equal("", ReleaseCheck.TagFrom("""{"name":"no tag here"}"""));
    }

    /// <summary>
    /// A failed check is not worth failing anything over: offline, rate limited, or GitHub having
    /// a bad day all mean the same thing to a caller, which is that there is nothing to report.
    /// </summary>
    [Fact]
    public async Task AFailedFetchReportsNothingRatherThanThrowing()
    {
        var cache = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "latest-release.json");

        try
        {
            var answer = await ReleaseCheck.LatestTag(
                _ => throw new HttpRequestException("no network"), CancellationToken.None, cache);

            Assert.Null(answer);
        }
        finally
        {
            var directory = Path.GetDirectoryName(cache)!;

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// A fresh answer is not asked again. Reading it back is what proves the failure above was
    /// the fetch failing rather than a cache standing in for it.
    /// </summary>
    [Fact]
    public async Task AFreshCacheAnswersWithoutFetching()
    {
        var cache = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "latest-release.json");

        try
        {
            var first = await ReleaseCheck.LatestTag(
                _ => Task.FromResult("""{"tag_name":"v9.9.9"}"""), CancellationToken.None, cache);

            var second = await ReleaseCheck.LatestTag(
                _ => throw new HttpRequestException("must not be reached"),
                CancellationToken.None,
                cache);

            Assert.Equal("v9.9.9", first);
            Assert.Equal("v9.9.9", second);
        }
        finally
        {
            var directory = Path.GetDirectoryName(cache)!;

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
