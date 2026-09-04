namespace IsuzuUnityCli.Agents;

public enum ConfigFormat
{
    Json,
    Toml,
}

public enum McpTransport
{
    /// <summary>The agent connects to the Editor's HTTP endpoint itself.</summary>
    Http,

    /// <summary>The agent can only speak stdio, so it launches this executable as a bridge.</summary>
    Stdio,
}

/// <summary>
/// One agent this tool knows how to register itself with: where its MCP server list lives,
/// what format that file is in, and where its skills go. Those are the three things setup
/// writes and the three things uninstall has to undo.
/// </summary>
public sealed class AgentTarget
{
    public required string Name { get; init; }
    public required string Label { get; init; }

    /// <summary>Null when the config lives inside the Unity project rather than in the home directory.</summary>
    public string? ConfigPath { get; init; }

    /// <summary>Path under the project root, used when <see cref="ConfigPath"/> is null.</summary>
    public string? ProjectRelativeConfigPath { get; init; }

    public ConfigFormat Format { get; init; }

    /// <summary>Null when the agent has no skill mechanism.</summary>
    public string? SkillsDirectory { get; init; }

    public McpTransport Transport { get; init; }

    /// <summary>True when the agent appears to be installed on this machine.</summary>
    public bool Detected { get; init; }

    public bool IsProjectScoped => ConfigPath is null;

    public string ConfigPathFor(string projectRoot)
    {
        return ConfigPath ?? Path.Combine(projectRoot, ProjectRelativeConfigPath ?? "");
    }
}
