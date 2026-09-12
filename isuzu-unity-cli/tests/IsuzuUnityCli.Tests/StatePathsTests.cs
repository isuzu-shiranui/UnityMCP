using IsuzuUnityCli.Discovery;
using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public sealed class StatePathsTests
{
    private static readonly string[] Names =
        ["LOCALAPPDATA", "XDG_DATA_HOME", "USERPROFILE", "HOME", "CLAUDE_CONFIG_DIR", "CODEX_HOME", "UNITY_MCP_STATE_DIR"];

    private static void WithEnvironment(Dictionary<string, string?> values, Action body)
    {
        var saved = Names.ToDictionary(n => n, Environment.GetEnvironmentVariable);

        try
        {
            foreach (var name in Names)
            {
                Environment.SetEnvironmentVariable(name, values.TryGetValue(name, out var v) ? v : null);
            }

            body();
        }
        finally
        {
            foreach (var pair in saved)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }

    [Fact]
    public void RootsFollowPreferenceOrder()
    {
        WithEnvironment(new()
        {
            ["LOCALAPPDATA"] = "/local",
            ["XDG_DATA_HOME"] = "/xdg",
            ["USERPROFILE"] = "/home/u",
        }, () =>
        {
            var expected = new[]
            {
                Path.Combine("/local", "UnityMCP"),
                Path.Combine("/xdg", "UnityMCP"),
                Path.Combine("/home/u", ".local", "share", "UnityMCP"),
                Path.Combine("/home/u", "Library", "Application Support", "UnityMCP"),
            };

            Assert.Equal(expected, StatePaths.Roots());
            Assert.Equal(expected.Select(r => Path.Combine(r, "instances")), StatePaths.DescriptorDirectories());
        });
    }

    [Fact]
    public void StateDirectoryOverrideComesFirstAndIsUsedVerbatim()
    {
        WithEnvironment(new()
        {
            ["UNITY_MCP_STATE_DIR"] = string.Join(Path.PathSeparator, "/mnt/c/state/UnityMCP", "/second/UnityMCP"),
            ["LOCALAPPDATA"] = "/local",
        }, () =>
        {
            Assert.Equal(
            [
                "/mnt/c/state/UnityMCP",
                "/second/UnityMCP",
                Path.Combine("/local", "UnityMCP"),
            ], StatePaths.Roots());

            Assert.Equal(Path.Combine("/mnt/c/state/UnityMCP", "instances"), StatePaths.DescriptorDirectories()[0]);
        });
    }

    [Fact]
    public void EmptyVariablesAreSkippedAndDuplicatesCollapse()
    {
        WithEnvironment(new()
        {
            ["LOCALAPPDATA"] = "",
            ["XDG_DATA_HOME"] = Path.Combine("/home/u", ".local", "share"),
            ["HOME"] = "/home/u",
        }, () =>
        {
            Assert.Equal(
            [
                Path.Combine("/home/u", ".local", "share", "UnityMCP"),
                Path.Combine("/home/u", "Library", "Application Support", "UnityMCP"),
            ], StatePaths.Roots());
        });
    }

    [Fact]
    public void HomePrefersUserProfileOverHome()
    {
        WithEnvironment(new() { ["USERPROFILE"] = "/profile", ["HOME"] = "/home" }, () => Assert.Equal("/profile", StatePaths.Home()));
        WithEnvironment(new() { ["HOME"] = "/home" }, () => Assert.Equal("/home", StatePaths.Home()));
    }

    [Fact]
    public void AgentDirectoriesHonourOverrides()
    {
        WithEnvironment(new() { ["HOME"] = "/home" }, () =>
        {
            Assert.Equal(Path.Combine("/home", ".claude"), StatePaths.ClaudeConfigDir());
            Assert.Equal(Path.Combine("/home", ".codex"), StatePaths.CodexHome());
        });

        WithEnvironment(new() { ["HOME"] = "/home", ["CLAUDE_CONFIG_DIR"] = "/cc", ["CODEX_HOME"] = "/cx" }, () =>
        {
            Assert.Equal("/cc", StatePaths.ClaudeConfigDir());
            Assert.Equal("/cx", StatePaths.CodexHome());
        });
    }
}
