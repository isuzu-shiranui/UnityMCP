using IsuzuUnityCli.Cli;

namespace IsuzuUnityCli.Discovery;

public static class InstanceResolver
{
    public const string NoneRunning =
        "No running Unity Editor found. Open a project with the Unity MCP package installed; " +
        "the Editor publishes a descriptor file once its server starts.";

    public const string SwitchByCommand =
        "Open that project again, or run the command again with --project to choose another.";

    public const string SwitchByRestart =
        "Open that project again, or restart this MCP server to choose another; in Claude Desktop, turn the extension off and on.";

    /// <summary>The Editor now serving the project <paramref name="selected"/> was published for.</summary>
    /// <remarks>
    /// Only a descriptor for the same project path is a candidate, so a command never moves to
    /// another project on its own. A new port, token, pid or product name is accepted, because a
    /// restarted Editor changes them. When several descriptors name the project, the one that
    /// answers /health with its own token is the running Editor. A token or pid shared with the
    /// earlier descriptor proves nothing: a descriptor left behind by a crash carries both.
    /// </remarks>
    /// <param name="howToSwitch">The sentence that tells the reader how to reach another project from where they are.</param>
    /// <param name="answers">Whether an Editor answers /health with its own token.</param>
    public static InstanceDescriptor Refresh(
        IReadOnlyList<InstanceDescriptor> descriptors,
        InstanceDescriptor selected,
        string howToSwitch = SwitchByCommand,
        Func<InstanceDescriptor, bool>? answers = null)
    {
        var key = ProjectKey.Of(selected.ProjectPath);
        var folder = ProjectKey.Display(selected.ProjectPath);

        if (key is null)
        {
            throw new CliException(
                $"Cannot reconnect to \"{selected.ProjectName}\": its descriptor has no absolute project path, "
                + $"so another Editor could not be told apart from it. {howToSwitch}",
                3);
        }

        var matches = descriptors.Where(candidate => ProjectKey.Of(candidate.ProjectPath) == key).ToList();

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count == 0)
        {
            var running = descriptors.Count == 0 ? "No Editor is running." : $"Running: {ProjectMatcher.Names(descriptors)}.";

            throw new CliException($"No running Editor has {folder} open. {running} {howToSwitch}", 3);
        }

        if (answers is not null)
        {
            // Asked together, so Editors that do not answer cost one timeout rather than one each.
            var answered = new bool[matches.Count];
            Parallel.For(0, matches.Count, i => answered[i] = answers(matches[i]));

            if (matches.Where((_, i) => answered[i]).ToList() is [var live])
            {
                return live;
            }
        }

        var listed = string.Join("; ", matches.Select(m => $"pid {m.Pid}, port {m.Port}"));

        throw new CliException(
            $"{matches.Count} descriptors name {folder} ({listed}), and not exactly one of them answers as a running Editor. {howToSwitch}",
            3);
    }

    /// <summary>
    /// A host that substitutes values into a launch command leaves the placeholder in place when
    /// the field is empty, so the project would be searched for under a name like
    /// <c>${user_config.project}</c>. Treating it as unset picks the single running Editor, which
    /// is what the person leaving the field blank meant.
    /// </summary>
    public static bool IsUnexpanded(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal);
    }

    public static InstanceDescriptor Resolve(
        IReadOnlyList<InstanceDescriptor> descriptors,
        string? projectOption,
        string workingDirectory,
        bool exactOnly = false)
    {
        if (descriptors.Count == 0)
        {
            throw new CliException(NoneRunning, 3);
        }

        if (!string.IsNullOrWhiteSpace(projectOption) && !IsUnexpanded(projectOption))
        {
            return ProjectMatcher.ByName(descriptors, projectOption, exactOnly, workingDirectory);
        }

        var fromCwd = ProjectMatcher.ByWorkingDirectory(descriptors, workingDirectory);
        if (fromCwd is not null)
        {
            return fromCwd;
        }

        // A command run inside a project that is not open would otherwise go to whichever Editor
        // is. Under WSL a published Windows path never contains the working directory, so there
        // the check would refuse every command.
        if (!ReadFromAnotherHost(descriptors) && UnityProjectAround(workingDirectory) is { } closed)
        {
            throw new CliException(
                $"The working directory is inside the Unity project {closed}, which no running Editor has open. "
                + $"Open it, or pass --project to choose another. Running: {ProjectMatcher.Names(descriptors)}",
                3);
        }

        if (descriptors.Count == 1)
        {
            return descriptors[0];
        }

        throw new CliException(
            "Several Editors are running and none contains the working directory: " +
            $"{ProjectMatcher.Names(descriptors)}. Pass --project <name>.", 3);
    }

    /// <summary>The nearest folder at or above <paramref name="directory"/> that holds a Unity project.</summary>
    public static string? UnityProjectAround(string directory)
    {
        try
        {
            for (var folder = new DirectoryInfo(Path.GetFullPath(directory)); folder is not null; folder = folder.Parent)
            {
                if (Directory.Exists(Path.Combine(folder.FullName, "Assets"))
                    && File.Exists(Path.Combine(folder.FullName, "ProjectSettings", "ProjectVersion.txt")))
                {
                    return folder.FullName;
                }
            }
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A folder that cannot be read says nothing about a project around it.
        }

        return null;
    }

    private static bool ReadFromAnotherHost(IReadOnlyList<InstanceDescriptor> descriptors) =>
        !OperatingSystem.IsWindows() && descriptors.Any(d => ProjectKey.IsWindowsShaped(d.ProjectPath));
}
