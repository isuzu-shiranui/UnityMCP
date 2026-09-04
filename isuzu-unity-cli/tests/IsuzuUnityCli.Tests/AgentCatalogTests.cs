using System.Runtime.InteropServices;
using IsuzuUnityCli.Agents;
using IsuzuUnityCli.Tests.Fakes;
using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public sealed class AgentCatalogTests
{
    [Fact]
    public void ClaudeCodeConfigSitsBesideItsDirectory()
    {
        using var home = new TempHome();

        var agent = Find("claude-code");

        Assert.Equal(Path.Combine(home.Root, ".claude.json"), agent.ConfigPath);
        Assert.Equal(Path.Combine(home.Root, ".claude", "skills"), agent.SkillsDirectory);
        Assert.Equal(ConfigFormat.Json, agent.Format);
        Assert.Equal(McpTransport.Http, agent.Transport);
    }

    [Fact]
    public void ClaudeConfigDirMovesBothTheDirectoryAndTheFile()
    {
        using var home = new TempHome();
        var relocated = home.MakeDirectory("elsewhere");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", relocated);

        var agent = Find("claude-code");

        Assert.Equal(Path.Combine(relocated, ".claude.json"), agent.ConfigPath);
        Assert.Equal(Path.Combine(relocated, "skills"), agent.SkillsDirectory);
    }

    [Fact]
    public void CodexHonoursItsOwnHome()
    {
        using var home = new TempHome();
        Environment.SetEnvironmentVariable("CODEX_HOME", home.At("cx"));

        var agent = Find("codex");

        Assert.Equal(Path.Combine(home.At("cx"), "config.toml"), agent.ConfigPath);
        Assert.Equal(Path.Combine(home.At("cx"), "skills"), agent.SkillsDirectory);
        Assert.Equal(ConfigFormat.Toml, agent.Format);
    }

    [Fact]
    public void OnlyAgentsWithASkillMechanismAdvertiseASkillsDirectory()
    {
        using var home = new TempHome();

        Assert.NotNull(Find("claude-code").SkillsDirectory);
        Assert.NotNull(Find("codex").SkillsDirectory);
        Assert.Null(Find("cursor").SkillsDirectory);
        Assert.Null(Find("gemini").SkillsDirectory);
        Assert.Null(Find("vscode").SkillsDirectory);
    }

    [Fact]
    public void VsCodeKeepsItsConfigInsideTheProject()
    {
        using var home = new TempHome();

        var agent = Find("vscode");

        Assert.True(agent.IsProjectScoped);
        Assert.Null(agent.ConfigPath);
        Assert.Equal(
            Path.Combine("H:", "Game", ".vscode", "mcp.json"),
            agent.ConfigPathFor(Path.Combine("H:", "Game")));
    }

    [Fact]
    public void CursorAndGeminiLiveUnderTheHomeDirectory()
    {
        using var home = new TempHome();

        Assert.Equal(Path.Combine(home.Root, ".cursor", "mcp.json"), Find("cursor").ConfigPath);
        Assert.Equal(Path.Combine(home.Root, ".gemini", "settings.json"), Find("gemini").ConfigPath);
    }

    [Fact]
    public void ClaudeDesktopIsListedOnlyWherePeopleCanInstallIt()
    {
        using var home = new TempHome();
        var agent = AgentCatalog.Find("claude-desktop");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Equal(
                Path.Combine(home.At("AppData", "Roaming"), "Claude", "claude_desktop_config.json"),
                agent!.ConfigPath);
            Assert.Equal(McpTransport.Stdio, agent.Transport);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Assert.Equal(
                Path.Combine(home.Root, "Library", "Application Support", "Claude", "claude_desktop_config.json"),
                agent!.ConfigPath);
        }
        else
        {
            Assert.Null(agent);
        }
    }

    [Fact]
    public void AnAgentIsDetectedByItsOwnDirectory()
    {
        using var home = new TempHome();

        Assert.False(Find("cursor").Detected);

        home.MakeDirectory(".cursor");

        Assert.True(Find("cursor").Detected);
    }

    [Fact]
    public void AConfigFileAloneAlsoCountsAsDetection()
    {
        using var home = new TempHome();

        // The home directory itself must not count, or every machine would look like it had
        // Claude Code installed.
        Assert.False(Find("claude-code").Detected);

        File.WriteAllText(home.At(".claude.json"), "{}");

        Assert.True(Find("claude-code").Detected);
    }

    private static AgentTarget Find(string name)
    {
        return AgentCatalog.Find(name) ?? throw new InvalidOperationException($"no agent named {name}");
    }
}
