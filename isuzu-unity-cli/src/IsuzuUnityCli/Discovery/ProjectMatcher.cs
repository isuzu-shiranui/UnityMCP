using IsuzuUnityCli.Cli;

namespace IsuzuUnityCli.Discovery;

public static class ProjectMatcher
{
    /// <summary>The Editor publishes <c>Application.dataPath</c>, which is <c>&lt;project&gt;/Assets</c>.</summary>
    public static string ProjectRootOf(string projectPath)
    {
        // A descriptor may carry no path at all: only the port, token and project name decide
        // whether it is usable. GetFullPath throws on a blank one rather than returning it.
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return "";
        }

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath));

        if (string.Equals(Path.GetFileName(normalized), "Assets", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(normalized) ?? normalized;
        }

        return normalized;
    }

    /// <summary>
    /// The name Unity puts in the window title. It is the project folder, which is not
    /// <c>Application.productName</c> and often differs from it, so a user reading the title bar
    /// types a name the descriptor never mentions.
    /// </summary>
    public static string FolderNameOf(string projectPath)
    {
        var root = ProjectRootOf(projectPath);

        return root.Length == 0 ? "" : Path.GetFileName(root);
    }

    public static bool IsInside(string directory, string projectPath)
    {
        var root = ProjectRootOf(projectPath);

        if (root.Length == 0)
        {
            return false;
        }

        // GetRelativePath compares case-insensitively on Windows and returns an absolute path
        // when the two are on different drives.
        var relative = Path.GetRelativePath(root, Path.GetFullPath(directory));

        return relative == "."
            || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    /// <summary>Deepest root wins when projects nest, since that is the one the caller is working in.</summary>
    public static T? ByWorkingDirectory<T>(IReadOnlyList<T> candidates, string directory) where T : class, IProjectLike
    {
        T? deepest = null;
        var deepestLength = -1;

        foreach (var candidate in candidates)
        {
            if (!IsInside(directory, candidate.ProjectPath))
            {
                continue;
            }

            var length = ProjectRootOf(candidate.ProjectPath).Length;

            if (length > deepestLength)
            {
                deepest = candidate;
                deepestLength = length;
            }
        }

        return deepest;
    }

    /// <summary>
    /// Accepts either name the user can see: the product name the descriptor carries, or the
    /// folder name the window title shows. The first attempt that matches anything decides, so an
    /// exact name is never overruled by a substring, and an attempt matching two Editors is an
    /// error rather than a silent pick. Matching is by Editor, so one Editor answering to the
    /// query under both of its names is still a single match.
    /// </summary>
    public static T ByName<T>(IReadOnlyList<T> candidates, string query) where T : IProjectLike
    {
        var trimmed = query.Trim();

        foreach (var attempt in Attempts<T>(trimmed))
        {
            var matches = candidates.Where(attempt).ToList();

            if (matches.Count == 1)
            {
                return matches[0];
            }

            if (matches.Count > 1)
            {
                throw new CliException(
                    $"\"{query}\" matches more than one running Editor: {Names(matches)}. Use the full project name.", 3);
            }
        }

        throw new CliException($"No running Editor matches \"{query}\". Running: {Names(candidates)}", 3);
    }

    private static IEnumerable<Func<T, bool>> Attempts<T>(string query) where T : IProjectLike
    {
        yield return c => string.Equals(c.ProjectName, query, StringComparison.OrdinalIgnoreCase);
        yield return c => string.Equals(FolderNameOf(c.ProjectPath), query, StringComparison.OrdinalIgnoreCase);
        yield return c => c.ProjectName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || FolderNameOf(c.ProjectPath).Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Both names the user can type, so a listing never hides the one they are reading.</summary>
    public static string Names<T>(IEnumerable<T> candidates) where T : IProjectLike
    {
        return string.Join(", ", candidates.Select(c => Label(c)));
    }

    private static string Label(IProjectLike candidate)
    {
        var folder = FolderNameOf(candidate.ProjectPath);

        return folder.Length == 0 || string.Equals(folder, candidate.ProjectName, StringComparison.OrdinalIgnoreCase)
            ? candidate.ProjectName
            : $"{candidate.ProjectName} (folder: {folder})";
    }
}
