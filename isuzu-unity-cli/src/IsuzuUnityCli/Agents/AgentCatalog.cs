using System.Runtime.InteropServices;
using IsuzuUnityCli.Discovery;

namespace IsuzuUnityCli.Agents;

public static class AgentCatalog
{
    /// <summary>The name every agent's config lists this server under.</summary>
    public const string ServerName = "isuzu-unity";

    public static IReadOnlyList<AgentTarget> All()
    {
        var home = StatePaths.Home();
        var claudeConfigDir = StatePaths.ClaudeConfigDir();
        var codexHome = StatePaths.CodexHome();
        var targets = new List<AgentTarget>
        {
            new()
            {
                Name = "claude-code",
                Label = "Claude Code",
                ConfigPath = StatePaths.ClaudeUserConfigFile(),
                Format = ConfigFormat.Json,
                SkillsDirectory = Path.Combine(claudeConfigDir, "skills"),
                Transport = McpTransport.Http,
            },
        };

        var desktopConfig = ClaudeDesktopConfigPath(home);

        if (desktopConfig is not null)
        {
            targets.Add(new AgentTarget
            {
                Name = "claude-desktop",
                Label = "Claude Desktop",
                ConfigPath = desktopConfig,
                Format = ConfigFormat.Json,
                SkillsDirectory = null,
                Transport = McpTransport.Stdio,
            });
        }

        targets.Add(new AgentTarget
        {
            Name = "codex",
            Label = "Codex",
            ConfigPath = Path.Combine(codexHome, "config.toml"),
            Format = ConfigFormat.Toml,
            SkillsDirectory = Path.Combine(codexHome, "skills"),
            Transport = McpTransport.Http,
        });

        targets.Add(new AgentTarget
        {
            Name = "cursor",
            Label = "Cursor",
            ConfigPath = Path.Combine(home, ".cursor", "mcp.json"),
            Format = ConfigFormat.Json,
            SkillsDirectory = null,
            Transport = McpTransport.Http,
        });

        targets.Add(new AgentTarget
        {
            Name = "gemini",
            Label = "Gemini CLI",
            ConfigPath = Path.Combine(home, ".gemini", "settings.json"),
            Format = ConfigFormat.Json,
            SkillsDirectory = null,
            Transport = McpTransport.Http,
        });

        targets.Add(new AgentTarget
        {
            Name = "vscode",
            Label = "VS Code",
            ConfigPath = null,
            ProjectRelativeConfigPath = Path.Combine(".vscode", "mcp.json"),
            Format = ConfigFormat.Json,
            SkillsDirectory = null,
            Transport = McpTransport.Http,
        });

        return targets.Select(WithDetection).ToList();
    }

    public static AgentTarget? Find(string name)
    {
        return All().FirstOrDefault(target => string.Equals(target.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public static string Names()
    {
        return string.Join(", ", All().Select(target => target.Name));
    }

    /// <summary>
    /// Detection looks for the agent's own directory rather than its config file: a fresh
    /// install has the directory but may not have written a config yet, and refusing to set up
    /// in that case would be wrong.
    /// </summary>
    private static AgentTarget WithDetection(AgentTarget target)
    {
        var markers = new List<string>();

        if (target.SkillsDirectory is not null)
        {
            markers.Add(Path.GetDirectoryName(target.SkillsDirectory) ?? target.SkillsDirectory);
        }

        if (target.ConfigPath is not null)
        {
            markers.Add(Path.GetDirectoryName(target.ConfigPath) ?? target.ConfigPath);
        }

        // The project-scoped agents have no config path to look beside, so the marker is the
        // per-user directory the editor itself creates.
        if (target.Name == "vscode")
        {
            markers.Add(Path.Combine(StatePaths.Home(), ".vscode"));
        }

        // The home directory is not a marker: Claude Code's config sits directly inside it, and
        // taking that as evidence would report every agent as installed on every machine.
        var home = StatePaths.Home();
        var detected = markers.Any(marker => !PathsMatch(marker, home) && Directory.Exists(marker))
            || (target.ConfigPath is not null && File.Exists(target.ConfigPath));

        return new AgentTarget
        {
            Name = target.Name,
            Label = target.Label,
            ConfigPath = target.ConfigPath,
            ProjectRelativeConfigPath = target.ProjectRelativeConfigPath,
            Format = target.Format,
            SkillsDirectory = target.SkillsDirectory,
            Transport = target.Transport,
            Detected = detected,
        };
    }

    private static bool PathsMatch(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return false;
        }

        return string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Null on Linux, where Claude Desktop does not ship.</summary>
    private static string? ClaudeDesktopConfigPath(string home)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // APPDATA normally sits under the profile; when the profile is redirected (tests,
            // or a user pointing USERPROFILE elsewhere) the roaming folder follows it, so a
            // redirected run never touches the real Claude Desktop configuration.
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            var realProfile = Environment.GetEnvironmentVariable("USERPROFILE");
            var followsProfile = string.IsNullOrEmpty(appData)
                || string.IsNullOrEmpty(realProfile)
                || !appData.StartsWith(realProfile, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.TrimEndingDirectorySeparator(realProfile), Path.TrimEndingDirectorySeparator(home), StringComparison.OrdinalIgnoreCase);

            var roaming = followsProfile || appData is null
                ? Path.Combine(home, "AppData", "Roaming")
                : appData;

            return Path.Combine(roaming, "Claude", "claude_desktop_config.json");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(home, "Library", "Application Support", "Claude", "claude_desktop_config.json");
        }

        return null;
    }
}
