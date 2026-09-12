namespace IsuzuUnityCli.Discovery;

/// <summary>
/// The identity of a project, read from the path its Editor published, for deciding whether two
/// descriptors belong to the same project.
/// </summary>
/// <remarks>
/// Case is kept. A directory on Windows can be case-sensitive, and so can a volume on macOS, and the
/// Editor already gives a differently cased path a token file of its own. Only a drive letter is
/// folded: it names no directory entry, and launchers write it either way.
/// <para>
/// A Windows-shaped path is read by the same rules on every host, because a CLI under WSL reads the
/// descriptors a Windows Editor published. A path that cannot be read as absolute has no key, so it
/// can never stand in for another project.
/// </para>
/// </remarks>
public static class ProjectKey
{
    /// <summary>
    /// The key of the project whose root or <c>Assets</c> folder is <paramref name="projectPath"/>,
    /// or null when the path cannot identify one.
    /// </summary>
    public static string? Of(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || projectPath.Contains('\0'))
        {
            return null;
        }

        var path = projectPath.Trim();

        if (IsWindowsShaped(path))
        {
            return WindowsRoot(path) is { } windows ? "win:" + windows : null;
        }

        if (OperatingSystem.IsWindows())
        {
            // A rooted path without a drive, such as \Work\Game, names a folder on the current drive.
            return Path.IsPathRooted(path) && Full(path) is { } full && IsWindowsShaped(full) && WindowsRoot(full) is { } rooted
                ? "win:" + rooted
                : null;
        }

        return PosixRoot(path) is { } posix ? "posix:" + posix : null;
    }

    /// <summary>The project folder as it was published, for messages.</summary>
    public static string Display(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return "";
        }

        var trimmed = projectPath.Trim().TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);

        return cut > 0 && trimmed[(cut + 1)..] == "Assets" ? trimmed[..cut] : trimmed;
    }

    /// <summary>A path with a drive letter, or a UNC path.</summary>
    /// <remarks>A UNC path starts with exactly two separators. POSIX reads three or more as one.</remarks>
    public static bool IsWindowsShaped(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\')
        || (path.Length >= 3 && IsSeparator(path[0]) && IsSeparator(path[1]) && !IsSeparator(path[2]));

    private static bool IsSeparator(char c) => c is '/' or '\\';

    /// <summary>Resolved by hand, so a Windows path names the same folder on a host that is not Windows.</summary>
    private static string? WindowsRoot(string path)
    {
        var slashed = path.Replace('\\', '/');

        if (slashed.StartsWith("//", StringComparison.Ordinal))
        {
            // The server and share are the root of a UNC path; nothing above them can be reached.
            var parts = slashed[2..].Split('/', 3);

            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                return null;
            }

            return Joined("//" + parts[0] + "/" + parts[1], parts.Length == 3 ? parts[2] : "");
        }

        return Joined(char.ToLowerInvariant(slashed[0]) + ":", slashed[2..]);
    }

    private static string? PosixRoot(string path) => path.StartsWith('/') ? Joined("", path) : null;

    /// <summary>
    /// The folders of <paramref name="rest"/> under <paramref name="prefix"/>, with <c>.</c> and
    /// <c>..</c> resolved and a trailing <c>Assets</c> removed. Null when <c>..</c> climbs above the root.
    /// </summary>
    private static string? Joined(string prefix, string rest)
    {
        var segments = new List<string>();

        foreach (var segment in rest.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return null;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        if (segments.Count > 0 && segments[^1] == "Assets")
        {
            segments.RemoveAt(segments.Count - 1);
        }

        return prefix + "/" + string.Join('/', segments);
    }

    private static string? Full(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
