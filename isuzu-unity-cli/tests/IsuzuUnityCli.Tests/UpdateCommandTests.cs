using IsuzuUnityCli.Commands;
using IsuzuUnityCli.Discovery;
using IsuzuUnityCli.Housekeeping;
using Xunit;

namespace IsuzuUnityCli.Tests;

/// <summary>
/// Which way the package got into a project, and what may be done about it. Four of the six
/// answers are reasons to change nothing, and getting one of those wrong rewrites a line in
/// somebody's project that does not decide anything.
/// </summary>
public sealed class PackageInstallTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mcp-install-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private string Project(string? dependency = null, string? vpm = null, string? embedded = null)
    {
        var packages = Path.Combine(root, "Packages");
        Directory.CreateDirectory(packages);

        var manifest = dependency is null
            ? "{\n  \"dependencies\": {\n    \"com.unity.ide.rider\": \"3.0.28\"\n  }\n}"
            : "{\n  \"dependencies\": {\n    \"com.unity.ide.rider\": \"3.0.28\",\n"
              + $"    \"{PackageInstall.PackageId}\": \"{dependency}\"\n  }}\n}}".Replace("}}", "}");

        File.WriteAllText(Path.Combine(packages, "manifest.json"), manifest);

        if (vpm is not null)
        {
            File.WriteAllText(
                Path.Combine(packages, "vpm-manifest.json"),
                "{\n  \"locked\": {\n    \"" + PackageInstall.PackageId
                + "\": { \"version\": \"" + vpm + "\" }\n  }\n}");
        }

        if (embedded is not null)
        {
            var folder = Path.Combine(packages, PackageInstall.PackageId);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "package.json"), "{ \"version\": \"" + embedded + "\" }");
        }

        return root;
    }

    [Fact]
    public void AGitDependencyCarriesItsVersionInTheFragment()
    {
        var install = PackageInstall.Read(
            Project("https://github.com/isuzu-shiranui/UnityMCP.git?path=jp.shiranui-isuzu.unity-mcp#v4.2.0"));

        Assert.Equal(PackageChannel.Git, install.Channel);
        Assert.Equal("v4.2.0", install.Version);
        Assert.True(install.Updatable);
    }

    [Fact]
    public void RetargetingAGitDependencyReplacesTheFragmentRatherThanAddingASecond()
    {
        var install = PackageInstall.Read(
            Project("https://github.com/isuzu-shiranui/UnityMCP.git?path=jp.shiranui-isuzu.unity-mcp#v4.2.0"));

        var retargeted = PackageInstall.Retarget(install, "4.3.0");

        Assert.Equal(
            "https://github.com/isuzu-shiranui/UnityMCP.git?path=jp.shiranui-isuzu.unity-mcp#v4.3.0",
            retargeted);

        // Two fragments is not a URL Unity resolves; it is one it reports as unreachable.
        Assert.Equal(1, retargeted!.Count(c => c == '#'));
    }

    [Fact]
    public void AGitDependencyWithNoFragmentGainsOne()
    {
        var install = PackageInstall.Read(Project("https://github.com/isuzu-shiranui/UnityMCP.git"));

        Assert.Null(install.Version);
        Assert.Equal("https://github.com/isuzu-shiranui/UnityMCP.git#v4.3.0", PackageInstall.Retarget(install, "4.3.0"));
    }

    [Fact]
    public void ARegistryDependencyIsItsOwnVersion()
    {
        var install = PackageInstall.Read(Project("4.2.0"));

        Assert.Equal(PackageChannel.Registry, install.Channel);
        Assert.Equal("4.2.0", install.Version);
        Assert.Equal("4.3.0", PackageInstall.Retarget(install, "4.3.0"));
    }

    [Fact]
    public void APathOnThisMachineIsAWorkingCopyAndIsNotUpdatable()
    {
        var install = PackageInstall.Read(Project("file:C:/Projects/UnityMCP/jp.shiranui-isuzu.unity-mcp"));

        Assert.Equal(PackageChannel.Local, install.Channel);
        Assert.False(install.Updatable);
        Assert.Null(PackageInstall.Retarget(install, "4.3.0"));
    }

    [Fact]
    public void AFolderUnderPackagesWinsOverTheManifestThatNamesAVersion()
    {
        // The trap this ordering exists for: told to update, someone changes the manifest, sees
        // the version stay where it was, and has no way to tell why.
        var install = PackageInstall.Read(Project(dependency: "4.2.0", embedded: "4.0.0"));

        Assert.Equal(PackageChannel.Embedded, install.Channel);
        Assert.Equal("4.0.0", install.Version);
        Assert.False(install.Updatable);
    }

    [Fact]
    public void AProjectVccManagesIsLeftToVcc()
    {
        var install = PackageInstall.Read(Project(dependency: "4.2.0", vpm: "4.2.0"));

        Assert.Equal(PackageChannel.Vpm, install.Channel);
        Assert.Equal("4.2.0", install.Version);
        Assert.False(install.Updatable);
    }

    [Fact]
    public void AProjectThatNeverHeardOfThePackageIsAbsent()
    {
        var install = PackageInstall.Read(Project());

        Assert.Equal(PackageChannel.Absent, install.Channel);
        Assert.False(install.Updatable);
    }

    [Fact]
    public void WritingTheDependencyLeavesEveryOtherPackageAlone()
    {
        var project = Project("4.2.0");

        PackageInstall.WriteDependency(project, "4.3.0");

        var written = File.ReadAllText(Path.Combine(project, "Packages", "manifest.json"));

        Assert.Contains("\"4.3.0\"", written);
        Assert.DoesNotContain("\"4.2.0\"", written);
        Assert.Contains("com.unity.ide.rider", written);
        Assert.Contains("3.0.28", written);
    }
}

/// <summary>
/// The advisory every command carries. It reads a file and never asks the network, because a
/// whole call finishes in about twenty milliseconds and asking carries a five-second timeout.
/// </summary>
public sealed class ReleaseNoticeTests : IDisposable
{
    private readonly string cache = Path.Combine(
        Path.GetTempPath(), "mcp-release-" + Guid.NewGuid().ToString("N"), "latest-release.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(cache)!, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private void Cached(string tag, TimeSpan age)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllLines(cache, new[] { tag, (DateTimeOffset.UtcNow - age).ToString("o") });
    }

    [Fact]
    public void AnEmptyCacheSaysNothing()
    {
        Assert.Null(ReleaseCheck.KnownTag(cache));
    }

    [Fact]
    public void AStaleEntryIsStillUsed()
    {
        // It cannot name a release that does not exist, and going quiet about one that does
        // helps nobody.
        Cached("v9.9.9", TimeSpan.FromDays(30));

        Assert.Equal("v9.9.9", ReleaseCheck.KnownTag(cache));
    }

    [Fact]
    public void AFailedCheckIsCachedAsNothingRatherThanAsATag()
    {
        Cached("", TimeSpan.FromMinutes(1));

        Assert.Null(ReleaseCheck.KnownTag(cache));
    }

    [Fact]
    public void TheNoticeNamesTheReleaseAndWhatToRun()
    {
        Cached("v9.9.9", TimeSpan.FromMinutes(1));

        var error = new StringWriter();
        new CommandContext { Err = error, ReleaseCachePath = cache }.ReportNewRelease();

        Assert.Contains("v9.9.9", error.ToString());
        Assert.Contains("update", error.ToString());
    }

    [Fact]
    public void ThereIsNothingToSayAboutAReleaseOlderThanThisOne()
    {
        Cached("v0.0.1", TimeSpan.FromMinutes(1));

        var error = new StringWriter();
        new CommandContext { Err = error, ReleaseCachePath = cache }.ReportNewRelease();

        Assert.Equal("", error.ToString());
    }
}
