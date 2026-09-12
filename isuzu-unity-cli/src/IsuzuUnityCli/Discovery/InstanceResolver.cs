using IsuzuUnityCli.Cli;

namespace IsuzuUnityCli.Discovery;

public static class InstanceResolver
{
    public const string NoneRunning =
        "No running Unity Editor found. Open a project with the Unity MCP package installed; " +
        "the Editor publishes a descriptor file once its server starts.";

    /// <summary>Refreshes an endpoint without repeating the initial, possibly fuzzy project selection.</summary>
    public static InstanceDescriptor Refresh(IReadOnlyList<InstanceDescriptor> descriptors, InstanceDescriptor selected)
    {
        var root = Root(selected.ProjectPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var matches = root is null
            ? []
            : descriptors.Where(candidate => string.Equals(root, Root(candidate.ProjectPath), comparison)).ToList();

        if (matches.Count != 1)
        {
            throw new CliException(
                $"Cannot reconnect to the selected project \"{selected.ProjectName}\" at \"{selected.ProjectPath}\": " +
                "expected one running Editor with the same project path. Reopen that project or start a new command to select another.", 3);
        }

        return matches[0];

        static string? Root(string path)
        {
            // Published paths are absolute. A missing or malformed legacy path cannot identify
            // a project safely, even when another Editor has the same product name.
            if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
            {
                return null;
            }

            // WSL can read descriptors published by a Windows Editor. Its absolute path is
            // not a native Linux path, and must not be resolved against this process's cwd.
            if (!OperatingSystem.IsWindows()
                && ((path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\')
                    || path.StartsWith("\\\\", StringComparison.Ordinal)))
            {
                var published = path.Replace('\\', '/').TrimEnd('/');
                return published.EndsWith("/Assets", StringComparison.OrdinalIgnoreCase) ? published[..^7] : published;
            }

            if (!Path.IsPathFullyQualified(path))
            {
                return null;
            }

            try
            {
                return ProjectMatcher.ProjectRootOf(path);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }
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

    public static InstanceDescriptor Resolve(IReadOnlyList<InstanceDescriptor> descriptors, string? projectOption, string workingDirectory)
    {
        if (descriptors.Count == 0)
        {
            throw new CliException(NoneRunning, 3);
        }

        if (!string.IsNullOrWhiteSpace(projectOption) && !IsUnexpanded(projectOption))
        {
            return ProjectMatcher.ByName(descriptors, projectOption);
        }

        var fromCwd = ProjectMatcher.ByWorkingDirectory(descriptors, workingDirectory);
        if (fromCwd is not null)
        {
            return fromCwd;
        }

        if (descriptors.Count == 1)
        {
            return descriptors[0];
        }

        throw new CliException(
            "Several Editors are running and none contains the working directory: " +
            $"{ProjectMatcher.Names(descriptors)}. Pass --project <name>.", 3);
    }
}
