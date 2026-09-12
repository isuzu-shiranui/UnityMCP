using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Discovery;
using IsuzuUnityCli.Housekeeping;

namespace IsuzuUnityCli.Commands;

/// <summary>
/// Brings this machine to the newest release: the CLI, and every project's copy of the package.
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
    public static async Task<int> Run(ParsedArgs parsed, CommandContext context)
    {
        var install = CliInstall.Read(context.ExecutablePath);

        if (parsed.Option("release") is not null && !install.ReplacesItself)
        {
            context.Err.WriteLine(UpgradeCommand.ReleaseRefusal(install));
            return 1;
        }

        var tag = await Latest(context);

        if (tag is null)
        {
            context.Err.WriteLine(
                "Could not reach GitHub to find out what the newest release is. "
                + "Nothing was changed.");
            return 1;
        }

        var current = Program.Version();
        var version = tag.TrimStart('v', 'V');
        var behind = ReleaseCheck.IsNewer(tag, current);

        // A copy another tool installed reaches the release only when that tool offers it, which
        // for winget is after review. A package moved ahead of it in the meantime would be talking
        // to an older CLI, so until then the packages go only as far as the version this CLI runs.
        var held = behind && !install.ReplacesItself;

        context.Out.WriteLine(behind
            ? $"{tag} is out and this is {current}."
            : $"{tag} is the newest release and this is {current}.");
        context.Out.WriteLine();

        var projects = Projects(context, parsed);
        var failed = false;

        // The package first. Upgrading the CLI replaces the running executable, and on Windows
        // that leaves the old one behind under another name; anything this process still had to
        // do would be running from a binary the next install is going to delete.
        context.Out.WriteLine("Unity projects");

        if (projects.Count == 0)
        {
            context.Out.WriteLine("  none found. Open a project, or pass --project.");
        }
        else if (held)
        {
            context.Out.WriteLine($"  moved to {current}, the version this CLI runs, until the CLI is updated.");
        }

        foreach (var descriptor in projects)
        {
            failed |= !UpdateProject(context, descriptor, held ? current : version, parsed.HasFlag("dry-run"));
        }

        context.Out.WriteLine();
        context.Out.WriteLine("CLI");

        if (!behind)
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

        if (parsed.HasFlag("dry-run"))
        {
            context.Out.WriteLine($"  would install {tag}.");
            return failed ? 1 : 0;
        }

        var upgrade = await UpgradeCommand.Run(Reparse(parsed), context);

        return upgrade != 0 || failed ? 1 : 0;
    }

    /// <summary>Moves one project to <paramref name="version"/>, or says why it cannot.</summary>
    private static bool UpdateProject(
        CommandContext context, InstanceDescriptor descriptor, string version, bool dryRun)
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

        // Moving a project back is never what update means. With a CLI another tool updates, the
        // target is the version the CLI runs, and a project can already be past it.
        if (install.Version is not null && ReleaseCheck.IsNewer(install.Version, version))
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

    private static async Task<string?> Latest(CommandContext context)
    {
        try
        {
            return await ReleaseCheck.LatestTag(
                ReleaseCheck.FromGitHub, context.Cancellation, context.ReleaseCachePath);
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
            return new List<InstanceDescriptor> { context.ResolveInstance(parsed) };
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
    private static ParsedArgs Reparse(ParsedArgs parsed)
    {
        var argv = new List<string> { "upgrade" };

        if (parsed.Option("release") is { } release)
        {
            argv.Add("--release");
            argv.Add(release);
        }

        return ArgParser.Parse(argv);
    }
}
