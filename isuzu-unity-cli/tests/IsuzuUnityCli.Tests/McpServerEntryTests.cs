using System.Text.Json.Nodes;
using IsuzuUnityCli.Agents;
using IsuzuUnityCli.Discovery;
using IsuzuUnityCli.Tests.Fakes;
using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public sealed class McpServerEntryTests
{
    private const string Token = "49a51eb3a3ee324b048ef1c0f040a5047deaf5335f0183f8dc03db3278ba6645";

    private static InstanceDescriptor Descriptor()
    {
        return new InstanceDescriptor
        {
            ProjectName = "UnityMCP 65 Test",
            ProjectPath = Path.Combine(Path.GetTempPath(), "UnityMCP 65 Test", "Assets"),
            UnityVersion = "6000.5.10f1",
            Port = 27186,
            Token = Token,
            Pid = 643300,
            ProtocolVersion = "3.3.1",
            Endpoint = "http://127.0.0.1:27186",
            McpUrl = "http://127.0.0.1:27186/mcp",
        };
    }

    /// <summary>
    /// Claude Code files a project under a forward-slash key on every platform. A Windows path
    /// keyed verbatim lands somewhere it never reads, so setup reports success and the server
    /// never appears.
    /// </summary>
    [Fact]
    public void TheClaudeCodeProjectKeyUsesForwardSlashesOnWindowsPaths()
    {
        var agent = AgentCatalog.Find("claude-code")!;

        var path = McpServerEntry.PathFor(agent, @"H:\Unity Projects\Game Unity6.5");

        Assert.Equal("H:/Unity Projects/Game Unity6.5", path[1]);
        Assert.DoesNotContain('\\', path[1]);
    }

    [Fact]
    public void APathThatAlreadyUsesForwardSlashesIsLeftAlone()
    {
        var agent = AgentCatalog.Find("claude-code")!;

        Assert.Equal("/home/u/Game", McpServerEntry.PathFor(agent, "/home/u/Game")[1]);
        Assert.Null(McpServerEntry.SupersededClaudeCodePath(agent, "/home/u/Game"));
    }

    /// <summary>
    /// An entry an earlier Windows build filed under the backslash key stays visible to doctor,
    /// so setup has to take it away rather than leave two registrations for one project.
    /// </summary>
    [Fact]
    public void AWindowsPathReportsTheOldKeyToCleanUp()
    {
        var agent = AgentCatalog.Find("claude-code")!;

        var superseded = McpServerEntry.SupersededClaudeCodePath(agent, @"H:\Unity Projects\Game");

        Assert.NotNull(superseded);
        Assert.Equal(@"H:\Unity Projects\Game", superseded![1]);
        Assert.NotEqual(superseded[1], McpServerEntry.PathFor(agent, @"H:\Unity Projects\Game")[1]);
    }

    [Fact]
    public void OnlyClaudeCodeHasAnOldKeyToCleanUp()
    {
        foreach (var name in new[] { "cursor", "codex", "vscode", "gemini", "claude-desktop" })
        {
            var agent = AgentCatalog.Find(name);

            if (agent != null)
            {
                Assert.Null(McpServerEntry.SupersededClaudeCodePath(agent, @"H:\Unity Projects\Game"));
            }
        }
    }

    [Fact]
    public void ClaudeCodeGetsATypedHttpEntryKeyedByProject()
    {
        using var home = new TempHome();
        var agent = AgentCatalog.Find("claude-code")!;
        var root = Path.Combine(Path.GetTempPath(), "UnityMCP 65 Test");

        Assert.Equal(
            new[] { "projects", root.Replace('\\', '/'), "mcpServers", "isuzu-unity" },
            McpServerEntry.PathFor(agent, root));

        var entry = McpServerEntry.For(agent, Descriptor(), "exe");

        Assert.Equal("http", entry["type"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:27186/mcp", entry["url"]!.GetValue<string>());
        Assert.Equal("Bearer " + Token, entry["headers"]!["Authorization"]!.GetValue<string>());
    }

    [Fact]
    public void CursorGetsTheSameShapeWithoutTheTypeField()
    {
        using var home = new TempHome();
        var entry = McpServerEntry.For(AgentCatalog.Find("cursor")!, Descriptor(), "exe");

        Assert.Null(entry["type"]);
        Assert.Equal("http://127.0.0.1:27186/mcp", entry["url"]!.GetValue<string>());
        Assert.Equal("Bearer " + Token, entry["headers"]!["Authorization"]!.GetValue<string>());
    }

    [Fact]
    public void GeminiNamesTheFieldHttpUrl()
    {
        using var home = new TempHome();
        var entry = McpServerEntry.For(AgentCatalog.Find("gemini")!, Descriptor(), "exe");

        Assert.Null(entry["url"]);
        Assert.Equal("http://127.0.0.1:27186/mcp", entry["httpUrl"]!.GetValue<string>());
    }

    [Fact]
    public void ClaudeDesktopLaunchesThisExecutableAsABridge()
    {
        using var home = new TempHome();
        var agent = AgentCatalog.Find("claude-desktop");

        if (agent is null)
        {
            return;
        }

        var entry = McpServerEntry.For(agent, Descriptor(), @"C:\tools\isuzu-unity-cli.exe");

        Assert.Equal(@"C:\tools\isuzu-unity-cli.exe", entry["command"]!.GetValue<string>());
        Assert.Equal(new[] { "mcp-stdio", "--project", "UnityMCP 65 Test" }, entry["args"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.DoesNotContain(Token, entry.ToJsonString());
    }

    [Fact]
    public void VsCodeNeverHoldsTheTokenBecauseItsConfigIsCommitted()
    {
        using var home = new TempHome();
        var agent = AgentCatalog.Find("vscode")!;
        var entry = McpServerEntry.For(agent, Descriptor(), "exe");

        Assert.Equal(new[] { "servers", "isuzu-unity" }, McpServerEntry.PathFor(agent, "anything"));
        Assert.Equal("Bearer ${input:isuzu-unity-token}", entry["headers"]!["Authorization"]!.GetValue<string>());
        Assert.DoesNotContain(Token, entry.ToJsonString());
    }

    [Fact]
    public void VsCodeAlsoDeclaresThePromptThatSuppliesTheToken()
    {
        var root = new JsonObject();

        McpServerEntry.EnsureTokenInput(root);
        McpServerEntry.EnsureTokenInput(root);

        var input = Assert.Single(root["inputs"]!.AsArray());

        Assert.Equal("isuzu-unity-token", input!["id"]!.GetValue<string>());
        Assert.Equal("promptString", input["type"]!.GetValue<string>());
        Assert.True(input["password"]!.GetValue<bool>());

        McpServerEntry.RemoveTokenInput(root);
        Assert.Null(root["inputs"]);
    }

    [Fact]
    public void VsCodeLeavesSomebodyElsesInputsAlone()
    {
        var root = JsonConfigEditor.Parse("""{"inputs":[{"id":"other","type":"promptString"}]}""");

        McpServerEntry.EnsureTokenInput(root);
        Assert.Equal(2, root["inputs"]!.AsArray().Count);

        McpServerEntry.RemoveTokenInput(root);
        var remaining = Assert.Single(root["inputs"]!.AsArray());
        Assert.Equal("other", remaining!["id"]!.GetValue<string>());
    }

    [Fact]
    public void AProjectScopedConfigReadsTheTokenFromTheEnvironment()
    {
        var config = McpServerEntry.ProjectScopedConfig("http://127.0.0.1:27186/mcp");
        var text = config.ToJsonString();

        Assert.Equal("Bearer ${UNITY_MCP_TOKEN}", config["mcpServers"]!["isuzu-unity"]!["headers"]!["Authorization"]!.GetValue<string>());
        Assert.DoesNotContain(Token, text);
    }

    [Fact]
    public void TheCodexTableCarriesTheUrlAndTheHeader()
    {
        var body = McpServerEntry.TomlBody(Descriptor());
        var written = TomlConfigEditor.Upsert("", McpServerEntry.TomlTableName(), body);
        var entry = TomlConfigEditor.Read(written, "mcp_servers.isuzu-unity")!;

        Assert.Equal("http://127.0.0.1:27186/mcp", entry.Url);
        Assert.Equal("Bearer " + Token, entry.Authorization);
    }

    [Fact]
    public void DescribeReadsBothUrlSpellingsAndIgnoresRubbish()
    {
        Assert.Equal(("http://a/mcp", "Bearer t"), McpServerEntry.Describe(JsonNode.Parse("""{"url":"http://a/mcp","headers":{"Authorization":"Bearer t"}}""")));
        Assert.Equal(("http://a/mcp", null), McpServerEntry.Describe(JsonNode.Parse("""{"httpUrl":"http://a/mcp"}""")));
        Assert.Equal((null, null), McpServerEntry.Describe(JsonNode.Parse("""{"url":42}""")));
        Assert.Equal((null, null), McpServerEntry.Describe(null));
    }
}
