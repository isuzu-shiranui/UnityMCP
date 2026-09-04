namespace IsuzuUnityCli.Discovery;

/// <summary>
/// Every directory this tool reads from or writes to.
/// The Editor writes to .NET's LocalApplicationData, which Mono and .NET have not always mapped
/// the same way on macOS, so reads scan every plausible root instead of recomputing that mapping.
/// </summary>
public static class StatePaths
{
    public static string Home()
    {
        return NonEmpty(Environment.GetEnvironmentVariable("USERPROFILE"))
            ?? NonEmpty(Environment.GetEnvironmentVariable("HOME"))
            ?? "";
    }

    /// <summary>Candidate roots in preference order; the first one is where this process writes.</summary>
    public static IReadOnlyList<string> Roots()
    {
        var roots = new List<string>();
        var home = Home();

        // A split setup puts the Editor's state somewhere this process would never guess: from
        // WSL2 the Windows Editor writes to /mnt/c/Users/<you>/AppData/Local/UnityMCP. The
        // variable holds complete roots, not base directories, so nothing is appended to them.
        foreach (var overridden in Environment.GetEnvironmentVariable("UNITY_MCP_STATE_DIR")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
        {
            AddRoot(roots, overridden);
        }

        Add(roots, Environment.GetEnvironmentVariable("LOCALAPPDATA"));
        Add(roots, Environment.GetEnvironmentVariable("XDG_DATA_HOME"));

        if (home.Length > 0)
        {
            Add(roots, Path.Combine(home, ".local", "share"));
            Add(roots, Path.Combine(home, "Library", "Application Support"));
        }

        return roots;
    }

    public static string PrimaryRoot()
    {
        var roots = Roots();
        return roots.Count > 0 ? roots[0] : Path.Combine(Directory.GetCurrentDirectory(), "UnityMCP");
    }

    public static IReadOnlyList<string> DescriptorDirectories()
    {
        return Roots().Select(root => Path.Combine(root, "instances")).ToList();
    }

    public static IReadOnlyList<string> TokenDirectories()
    {
        return Roots().Select(root => Path.Combine(root, "tokens")).ToList();
    }

    public static IReadOnlyList<string> CacheDirectories()
    {
        return Roots().Select(root => Path.Combine(root, "cache")).ToList();
    }

    /// <summary>Paths earlier versions wrote and this one does not, reported so an upgrade leaves no residue.</summary>
    public static IReadOnlyList<string> LegacyPaths()
    {
        var home = Home();
        return home.Length == 0 ? [] : new[] { Path.Combine(home, ".unity-mcp") };
    }

    public static string ClaudeConfigDir()
    {
        return NonEmpty(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")) ?? Path.Combine(Home(), ".claude");
    }

    /// <summary>
    /// Claude Code's user settings file, which normally sits beside <c>~/.claude</c> rather than
    /// inside it. <c>CLAUDE_CONFIG_DIR</c> moves both the directory and this file into itself.
    /// </summary>
    public static string ClaudeUserConfigFile()
    {
        var overridden = NonEmpty(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"));
        return Path.Combine(overridden ?? Home(), ".claude.json");
    }

    public static string CodexHome()
    {
        return NonEmpty(Environment.GetEnvironmentVariable("CODEX_HOME")) ?? Path.Combine(Home(), ".codex");
    }

    private static void Add(List<string> roots, string? baseDirectory)
    {
        if (NonEmpty(baseDirectory) is null)
        {
            return;
        }

        AddRoot(roots, Path.Combine(baseDirectory!, "UnityMCP"));
    }

    private static void AddRoot(List<string> roots, string root)
    {
        if (!roots.Contains(root, StringComparer.Ordinal))
        {
            roots.Add(root);
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
