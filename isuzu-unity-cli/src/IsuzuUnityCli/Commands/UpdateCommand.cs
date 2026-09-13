using System.Text.Json;
using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Discovery;
using IsuzuUnityCli.Housekeeping;

namespace IsuzuUnityCli.Commands;

/// <summary>
/// Brings this machine to the newest release, or the one --release names: the CLI, and every
/// project's copy of the package.
/// </summary>
/// <remarks>
/// <c>upgrade</c> replaces this executable and stops there, which leaves the half that matters
/// most out of step — the two are released under one version and the release refuses to publish
/// them apart, so a CLI that upgraded alone is talking to a package that does not have the tools
/// it expects.
/// <para>
/// Four of the six ways the package can be installed cannot be updated from here, and each has a
/// different next step. Saying which one a project is on, and what to do about it, is most of
/// what this command is for; the projects it can move, it moves.
/// </para>
/// </remarks>
public static class UpdateCommand
{
    public static async Task<int> Run(
        ParsedArgs parsed, CommandContext context,
        Func<ParsedArgs, CommandContext, Task<int>>? upgrade = null)
    {
        context.Cancellation.ThrowIfCancellationRequested();
        upgrade ??= UpgradeCommand.Run;
        var install = CliInstall.Read(context.ExecutablePath);
        var release = parsed.Option("release");

        if (release is not null && !install.ReplacesItself)
        {
            context.Err.WriteLine(UpgradeCommand.ReleaseRefusal(install));
            return 1;
        }

        // A named release is the target for the packages and the CLI alike, an older one included:
        // going back from a bad release is what naming one is for. Checked before anything is
        // written, since every project's manifest would be pointed at it.
        var tag = release is not null ? UpgradeCommand.ReleaseTag(release) : await Latest(context);

        if (tag is null)
        {
            context.Err.WriteLine(
                "Could not reach GitHub to find out what the newest release is. "
                + "Nothing was changed.");
            return 1;
        }

        var current = Program.Version();
        var version = tag.TrimStart('v', 'V');
        var moves = release is null
            ? ReleaseCheck.IsNewer(tag, current)
            : ReleaseCheck.IsNewer(tag, current) || ReleaseCheck.IsNewer(current, tag);

        // A copy another tool installed reaches the release only when that tool offers it, which
        // for winget is after review. A package moved ahead of it in the meantime would be talking
        // to an older CLI, so until then the packages go only as far as the version this CLI runs.
        var held = moves && !install.ReplacesItself;

        context.Out.WriteLine(
            release is not null ? $"{tag} was named with --release and this is {current}."
            : moves ? $"{tag} is out and this is {current}."
            : $"{tag} is the newest release and this is {current}.");
        context.Out.WriteLine();

        var projects = Projects(context, parsed);
        var failed = false;

        // Finish downloading and verifying the planned CLI before any project points at it.
        // Keep the tag fixed even if GitHub latest changes after the cached release check.
        var installed = moves && !held && !parsed.HasFlag("dry-run");

        if (installed)
        {
            context.Out.WriteLine("CLI");
            context.Cancellation.ThrowIfCancellationRequested();

            if (await upgrade(Reparse(tag), context) != 0)
            {
                context.Err.WriteLine("CLI upgrade did not finish successfully; project manifests were not changed.");
                return 1;
            }

            context.Out.WriteLine($"  installed {tag}.");
            context.Out.WriteLine();
        }

        context.Cancellation.ThrowIfCancellationRequested();
        context.Out.WriteLine("Unity projects");

        if (projects.Count == 0)
        {
            context.Out.WriteLine("  none found. Open a project, or pass --project.");
        }
        else if (held)
        {
            context.Out.WriteLine($"  moved to {current}, the version this CLI runs, until the CLI is updated.");
        }

        try
        {
            foreach (var descriptor in projects)
            {
                context.Cancellation.ThrowIfCancellationRequested();
                failed |= !UpdateProject(
                    context, descriptor, held ? current : version, parsed.HasFlag("dry-run"), backwards: release is not null);
            }
            context.Cancellation.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested)
        {
            context.Err.WriteLine("Update interrupted; project changes reported above remain applied.");
            throw;
        }

        if (installed)
        {
            return failed ? 1 : 0;
        }

        context.Out.WriteLine();
        context.Out.WriteLine("CLI");

        if (!moves)
        {
            context.Out.WriteLine($"  already {current}.");
            return failed ? 1 : 0;
        }

        if (held)
        {
            context.Out.WriteLine(
                $"  installed {install.Description}, so it is not updated here. Update it with: {install.UpdateCommand}");

            if (install.Channel is CliChannel.Winget)
            {
                context.Out.WriteLine("  " + CliInstall.WingetDelay);
            }

            context.Out.WriteLine($"  Then run 'isuzu-unity-cli update' again to move the Unity projects to {tag}.");
            return failed ? 1 : 0;
        }

        context.Out.WriteLine($"  would install {tag}.");
        return failed ? 1 : 0;
    }

    /// <summary>Moves one project to <paramref name="version"/>, or says why it cannot.</summary>
    /// <param name="backwards">Whether a project already past <paramref name="version"/> goes back to it.</param>
    private static bool UpdateProject(
        CommandContext context, InstanceDescriptor descriptor, string version, bool dryRun, bool backwards)
    {
        var name = descriptor.ProjectName.Length > 0 ? descriptor.ProjectName : descriptor.ProjectPath;

        // The descriptor carries Unity's dataPath, which is the Assets folder rather than the
        // project. Packages/ sits beside it, so reading the descriptor's path as the root finds
        // no manifest and reports every project as one that never heard of the package.
        var root = ProjectMatcher.ProjectRootOf(descriptor.ProjectPath);

        if (root.Length == 0)
        {
            context.Out.WriteLine($"  {name}: its descriptor carries no project path.");
            return false;
        }

        PackageInstall.Install install;

        try
        {
            install = PackageInstall.Read(root);
            if (install.Channel is PackageChannel.Absent)
            {
                // Discovery treats unreadable manifests as absent. An update must distinguish
                // that from a valid manifest which simply does not name this package.
                using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Packages", "manifest.json")));
                if (manifest.RootElement.ValueKind != JsonValueKind.Object
                    || (manifest.RootElement.TryGetProperty("dependencies", out var dependencies)
                        && (dependencies.ValueKind != JsonValueKind.Object
                            || (dependencies.TryGetProperty(PackageInstall.PackageId, out var dependency)
                                && dependency.ValueKind != JsonValueKind.String))))
                {
                    throw new JsonException("manifest.json must contain an object with string package dependencies.");
                }
            }
        }
        catch (Exception e)
        {
            context.Out.WriteLine($"  {name}: could not be read ({e.Message}).");
            return false;
        }

        if (install.Version is not null && install.Version.TrimStart('v', 'V') == version)
        {
            context.Out.WriteLine($"  {name}: already {version}.");
            return true;
        }

        if (!install.Updatable)
        {
            context.Out.WriteLine($"  {name}: {Refusal(install)}");
            return true;
        }

        // Moving a project back is not what update means unless a release was named. With a CLI
        // another tool updates, the target is the version the CLI runs, and a project can already
        // be past it.
        if (!backwards && install.Version is not null && ReleaseCheck.IsNewer(install.Version, version))
        {
            context.Out.WriteLine($"  {name}: already at {install.Version}, which is past {version}. Left as it is.");
            return true;
        }

        var reference = PackageInstall.Retarget(install, version);

        if (reference is null)
        {
            context.Out.WriteLine($"  {name}: its dependency '{install.Reference}' cannot be retargeted by hand.");
            return false;
        }

        if (dryRun)
        {
            context.Out.WriteLine($"  {name}: would become '{reference}'.");
            return true;
        }

        try
        {
            PackageInstall.WriteDependency(root, reference);
        }
        catch (Exception e)
        {
            context.Out.WriteLine($"  {name}: its manifest could not be written ({e.Message}).");
            return false;
        }

        context.Out.WriteLine(
            $"  {name}: now asks for '{reference}'. "
            + "Unity resolves it when the Editor next has focus, or call package_resolve to do it now.");

        return true;
    }

    /// <summary>The one sentence that follows from a channel this cannot touch.</summary>
    private static string Refusal(PackageInstall.Install install) => install.Channel switch
    {
        PackageChannel.Absent =>
            "nothing in Packages/manifest.json names this package, so there is nothing to update.",

        PackageChannel.Local =>
            $"its dependency is '{install.Reference}', a path on this machine. "
            + "That is a working copy rather than an installation, and pulling it forward is git's job.",

        PackageChannel.Embedded =>
            $"a real folder sits at Packages/{PackageInstall.PackageId}"
            + (install.Version is null ? "" : $" holding {install.Version}")
            + ". Unity loads that and ignores the manifest, so changing the manifest would move a "
            + "line nobody reads. Delete the folder, or replace its contents with the release zip.",

        PackageChannel.Vpm =>
            $"VCC or ALCOM installed it{(install.Version is null ? "" : $" at {install.Version}")} "
            + "and keeps its own record in vpm-manifest.json. Update it there; the repository is "
            + "https://unity-mcp.shiranui-isuzu.dev/vpm.json",

        _ => "its install channel was not recognised.",
    };

    /// <remarks>
    /// Asked of GitHub rather than read from the cache the release notice uses. A cached answer
    /// stands for six hours, which is most of a day in which this would report the release it is
    /// being run to install as the one already installed, and a failed check is cached as no
    /// release at all.
    /// </remarks>
    private static async Task<string?> Latest(CommandContext context)
    {
        try
        {
            return await ReleaseCheck.LatestTag(
                context.FetchRelease, context.Cancellation, context.ReleaseCachePath, TimeSpan.Zero);
        }
        catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Every running Editor, or the one --project names.</summary>
    private static List<InstanceDescriptor> Projects(CommandContext context, ParsedArgs parsed)
    {
        if (parsed.Option("project") is not null)
        {
            // The manifest of the project this names is rewritten, so a name that is only part of
            // another open project's must not pick that one.
            return new List<InstanceDescriptor> { context.ResolveInstance(parsed, exactOnly: true) };
        }

        return context.ReadDescriptors().ToList();
    }

    /// <summary>
    /// The arguments upgrade takes, without the ones it does not.
    /// </summary>
    /// <remarks>
    /// upgrade refuses an option it does not declare, and --dry-run and --project are this
    /// command's own.
    /// </remarks>
    private static ParsedArgs Reparse(string? release)
    {
        var argv = new List<string> { "upgrade" };

        if (release is not null)
        {
            argv.Add("--release");
            argv.Add(release);
        }

        return ArgParser.Parse(argv);
    }
}
