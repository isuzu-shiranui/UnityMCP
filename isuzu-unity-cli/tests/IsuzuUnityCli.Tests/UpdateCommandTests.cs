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

    [Fact]
    public void ACopyWingetInstalledIsToldToUpdateThroughWinget()
    {
        Cached("v9.9.9", TimeSpan.FromMinutes(1));

        var error = new StringWriter();
        new CommandContext
        {
            Err = error,
            ReleaseCachePath = cache,
            ExecutablePath = Path.Combine(Path.GetTempPath(), "Microsoft", "WinGet", "Links", "isuzu-unity-cli.exe"),
        }.ReportNewRelease();

        Assert.Contains(CliInstall.WingetUpgrade, error.ToString());
    }
}

/// <summary>
/// The CLI half of update, and the version it moves each project to.
/// </summary>
/// <remarks>
/// The release is read from a fresh cache, and a copy the install script manages is handed an
/// upgrade that only answers, so no test downloads an installer or replaces an executable.
/// </remarks>
public sealed class UpdateCommandTests : IDisposable
{
    private static readonly string WingetLink =
        Path.Combine(Path.GetTempPath(), "Microsoft", "WinGet", "Links", "isuzu-unity-cli.exe");

    private readonly string root = Path.Combine(Path.GetTempPath(), "mcp-update-" + Guid.NewGuid().ToString("N"));

    private string Cache => Path.Combine(root, "latest-release.json");

    public UpdateCommandTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllLines(Cache, new[] { "v99.0.0", DateTimeOffset.UtcNow.ToString("o") });
    }

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

    private (CommandContext Context, StringWriter Out) Context(string executablePath, params InstanceDescriptor[] running)
    {
        var output = new StringWriter();

        return (new CommandContext
        {
            Out = output,
            Err = new StringWriter(),
            ReadDescriptors = () => running,
            ExecutablePath = executablePath,
            ReleaseCachePath = Cache,
            FetchRelease = _ => Task.FromResult("""{"tag_name":"v99.0.0"}"""),
            Cancellation = CancellationToken.None,
        }, output);
    }

    /// <summary>A project whose manifest asks for the package at <paramref name="dependency"/>.</summary>
    private InstanceDescriptor Project(string name, string dependency)
    {
        var packages = Path.Combine(root, name, "Packages");
        Directory.CreateDirectory(packages);
        File.WriteAllText(
            Path.Combine(packages, "manifest.json"),
            "{\n  \"dependencies\": {\n    \"" + PackageInstall.PackageId + "\": \"" + dependency + "\"\n  }\n}\n");

        return new InstanceDescriptor { ProjectName = name, ProjectPath = Path.Combine(root, name, "Assets") };
    }

    private string? Dependency(string name)
    {
        using var manifest = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, name, "Packages", "manifest.json")));

        return manifest.RootElement.GetProperty("dependencies").GetProperty(PackageInstall.PackageId).GetString();
    }

    [Fact]
    public async Task UntilWingetHasTheReleaseAProjectMovesOnlyAsFarAsTheCli()
    {
        var (context, output) = Context(WingetLink, Project("Behind", "0.0.1"), Project("Ahead", "98.0.0"));

        Assert.Equal(0, await Program.Run(["update"], context));
        Assert.Equal(Program.Version(), Dependency("Behind"));
        Assert.Equal("98.0.0", Dependency("Ahead"));
        Assert.Contains("'isuzu-unity-cli update' again", output.ToString());
    }

    [Fact]
    public async Task ReleaseIsRefusedForACopyWingetInstalledBeforeAnyProjectIsTouched()
    {
        var (context, _) = Context(WingetLink, Project("Game", "0.0.1"));

        Assert.Equal(1, await Program.Run(["update", "--release", "v4.2.0"], context));
        Assert.Equal("0.0.1", Dependency("Game"));
    }

    [Fact]
    public async Task ACopyWingetInstalledIsLeftToWinget()
    {
        var (context, output) = Context(
            Path.Combine(Path.GetTempPath(), "Microsoft", "WinGet", "Links", "isuzu-unity-cli.exe"));

        Assert.Equal(0, await Program.Run(["update"], context));
        Assert.Contains(CliInstall.WingetUpgrade, output.ToString());
    }

    [Fact]
    public async Task ADryRunOverADotnetToolNamesDotnetInsteadOfSayingItWouldInstall()
    {
        var (context, output) = Context(
            Path.Combine(Path.GetTempPath(), ".dotnet", "tools", "isuzu-unity-cli.exe"));

        Assert.Equal(0, await Program.Run(["update", "--dry-run"], context));
        Assert.Contains("dotnet tool update -g IsuzuUnityCli", output.ToString());
        Assert.DoesNotContain("would install", output.ToString());
    }

    [Fact]
    public async Task ANamedReleaseIsWhereEveryProjectGoesAnOlderOneIncluded()
    {
        var (context, _) = Context(
            Path.Combine(root, "bin", "isuzu-unity-cli.exe"), Project("Ahead", "98.0.0"), Project("Behind", "0.0.1"));

        Assert.Equal(0, await UpdateCommand.Run(Args("update", "--release", "v1.2.3"), context, (_, _) => Task.FromResult(0)));
        Assert.Equal("1.2.3", Dependency("Ahead"));
        Assert.Equal("1.2.3", Dependency("Behind"));
    }

    /// <summary>
    /// The CLI is installed first, so a release whose CLI cannot be installed is never written into
    /// a manifest, and the installer is asked for the release the check found rather than for
    /// whatever is newest by the time it runs.
    /// </summary>
    [Theory]
    [InlineData(null, "v99.0.0")]
    [InlineData("1.2.3", "v1.2.3")]
    public async Task AFailedUpgradeLeavesEveryManifestAndAsksForThePlannedRelease(string? release, string expected)
    {
        var (context, _) = Context(Path.Combine(root, "bin", "isuzu-unity-cli.exe"), Project("Game", "4.0.0"));
        var asked = new List<string?>();

        var exit = await UpdateCommand.Run(
            Args(release is null ? new[] { "update" } : new[] { "update", "--release", release }),
            context,
            (args, _) =>
            {
                asked.Add(args.Option("release"));
                return Task.FromResult(1);
            });

        Assert.Equal(1, exit);
        Assert.Equal(expected, Assert.Single(asked));
        Assert.Equal("4.0.0", Dependency("Game"));
    }

    [Fact]
    public async Task ADryRunInstallsNothingAndLeavesTheManifest()
    {
        var (context, _) = Context(Path.Combine(root, "bin", "isuzu-unity-cli.exe"), Project("Game", "4.0.0"));

        Assert.Equal(0, await UpdateCommand.Run(
            Args("update", "--release", "v1.2.3", "--dry-run"),
            context,
            (_, _) => throw new InvalidOperationException("A dry run started the installer.")));
        Assert.Equal("4.0.0", Dependency("Game"));
    }

    /// <summary>
    /// A manifest that cannot be read is a failure to report. It is not a project that does not use
    /// the package, which is passed over without one.
    /// </summary>
    [Fact]
    public async Task AnUnreadableManifestFailsWhereOneWithoutThePackageIsPassedOver()
    {
        var broken = Project("Broken", "4.0.0");
        var absent = Project("Absent", "4.0.0");
        File.WriteAllText(Path.Combine(root, "Broken", "Packages", "manifest.json"), "not json");
        File.WriteAllText(
            Path.Combine(root, "Absent", "Packages", "manifest.json"),
            "{\"dependencies\":{\"com.unity.ide.rider\":\"3.0.28\"}}");
        var (context, output) = Context(Path.Combine(root, "bin", "isuzu-unity-cli.exe"), broken, absent);

        Assert.Equal(1, await UpdateCommand.Run(Args("update", "--release", "v1.2.3"), context, (_, _) => Task.FromResult(0)));
        Assert.Contains("Broken: could not be read", output.ToString());
        Assert.DoesNotContain("Absent: could not be read", output.ToString());
    }

    /// <summary>
    /// The release notice's cached answer stands for six hours. Taken here, this would report the
    /// release it is being run to install as the one already installed, for most of a day.
    /// </summary>
    [Fact]
    public async Task TheNewestReleaseIsAskedForRatherThanReadFromTheNoticesCache()
    {
        // The release that has just come out, against the answer the notice cached minutes before it.
        File.WriteAllLines(Cache, new[] { "v0.0.1", DateTimeOffset.UtcNow.ToString("o") });
        var (context, output) = Context(Path.Combine(root, "bin", "isuzu-unity-cli.exe"), Project("Game", "0.0.1"));

        Assert.Equal(0, await UpdateCommand.Run(Args("update", "--dry-run"), context, (_, _) => Task.FromResult(0)));
        Assert.Contains("v99.0.0", output.ToString());
    }

    /// <summary>
    /// The install scripts take the value of --release as the tag. Only a lowercase 'v' counts as
    /// one already being there, so an uppercase one is asked for as a tag GitHub does not have.
    /// </summary>
    [Theory]
    [InlineData("v4.3.1")]
    [InlineData("V4.3.1")]
    [InlineData("4.3.1")]
    public void AReleaseIsNamedByItsTagWhicheverWayItIsTyped(string typed)
    {
        Assert.Equal("v4.3.1", UpgradeCommand.ReleaseTag(typed));
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("4.3")]
    [InlineData("v4.3.1.2")]
    public void AValueThatIsNotAVersionIsRefused(string typed)
    {
        Assert.Equal(2, Assert.Throws<IsuzuUnityCli.Cli.CliException>(() => UpgradeCommand.ReleaseTag(typed)).ExitCode);
    }

    /// <summary>
    /// A stream a process the installer left behind is holding open does not hold the command.
    /// </summary>
    /// <remarks>
    /// Reading to the end of a child's output waits for every handle on the pipe, and a process
    /// it spawned inherits those. Unity's asset database service outlives the Editor by minutes,
    /// during which the command shows nothing and looks hung.
    /// </remarks>
    // Without the grace period this waits for a task that never completes, so it has to fail on a
    // clock rather than hang the run.
    [Fact(Timeout = 30000)]
    public async Task OutputSomeoneElseStillHoldsOpenDoesNotHoldTheCommand()
    {
        var held = new TaskCompletionSource().Task;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await UpgradeCommand.Drain(held, Task.CompletedTask, CancellationToken.None);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"waited {clock.Elapsed}");
    }

    /// <summary>The grace period is not a way to ignore the caller pressing Ctrl+C.</summary>
    [Fact]
    public async Task ACancelledCommandStillReportsTheCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => UpgradeCommand.Drain(new TaskCompletionSource().Task, Task.CompletedTask, cancelled.Token));
    }

    private static IsuzuUnityCli.Cli.ParsedArgs Args(params string[] argv) => IsuzuUnityCli.Cli.ArgParser.Parse(argv);
}
