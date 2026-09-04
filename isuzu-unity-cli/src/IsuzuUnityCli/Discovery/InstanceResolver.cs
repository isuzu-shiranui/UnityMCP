using IsuzuUnityCli.Cli;

namespace IsuzuUnityCli.Discovery;

public static class InstanceResolver
{
    public const string NoneRunning =
        "No running Unity Editor found. Open a project with the Unity MCP package installed; " +
        "the Editor publishes a descriptor file once its server starts.";

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
