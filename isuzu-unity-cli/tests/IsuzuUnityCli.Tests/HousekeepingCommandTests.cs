using System.Text;
using System.Text.Json.Nodes;
using IsuzuUnityCli.Agents;
using IsuzuUnityCli.Commands;
using IsuzuUnityCli.Discovery;
using IsuzuUnityCli.Housekeeping;
using IsuzuUnityCli.Tests.Fakes;
using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public sealed class HousekeepingCommandTests
{
    private const string Token = "49a51eb3a3ee324b048ef1c0f040a5047deaf5335f0183f8dc03db3278ba6645";

    [Fact]
    public async Task SetupInstallsTheSkillAndLeavesTheServerListAlone()
    {
        using var home = new TempHome();
        home.MakeDirectory(".claude");

        var (context, output, _) = Context(home);

        Assert.Equal(0, await Program.Run(["setup"], context));
        Assert.True(File.Exists(home.At(".claude", "skills", "isuzu-unity-cli", "SKILL.md")));
        Assert.False(File.Exists(home.At(".claude.json")));
        Assert.Contains("installed skill:", output.ToString());
        Assert.DoesNotContain("Restart the agent", output.ToString());
    }

    [Fact]
    public async Task DetectedNonSkillAgentsDoNotTurnADefaultSetupIntoAnMcpRegistration()
    {
        using var home = new TempHome();
        home.MakeDirectory(".claude");
        home.MakeDirectory(".cursor");

        var (context, output, _) = Context(home, Descriptor(home.At("Proj")));

        Assert.Equal(0, await Program.Run(["setup"], context));
        Assert.True(File.Exists(home.At(".claude", "skills", "isuzu-unity-cli", "SKILL.md")));
        Assert.False(File.Exists(home.At(".cursor", "mcp.json")), "Cursor was not named, so nothing is registered for it.");
        Assert.False(File.Exists(home.At(".claude.json")));
        Assert.Contains("skipped Cursor", output.ToString());

        Assert.Equal(0, await Program.Run(["setup", "--agent", "cursor"], context));
        Assert.True(File.Exists(home.At(".cursor", "mcp.json")), "Naming a non-skill agent asks for the MCP entry.");
    }

    [Fact]
    public async Task SetupWithMcpWritesAnEntryKeyedByProjectRoot()
    {
        using var home = new TempHome();
        home.MakeDirectory(".claude");
        var project = home.MakeDirectory("Game");

        var (context, output, _) = Context(home, Descriptor(project));

        Assert.Equal(0, await Program.Run(["setup", "--agent", "claude-code", "--mcp"], context));

        var config = JsonConfigEditor.Read(home.At(".claude.json"));
        var entry = JsonConfigEditor.Find(config, ["projects", McpServerEntry.ClaudeCodeProjectKey(project), "mcpServers", "isuzu-unity"]);

        Assert.Equal("http", entry!["type"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:27186/mcp", entry["url"]!.GetValue<string>());
        Assert.Equal("Bearer " + Token, entry["headers"]!["Authorization"]!.GetValue<string>());
        Assert.Contains("registered with Claude Code:", output.ToString());
        Assert.Contains("Restart the agent so it picks up the new server.", output.ToString());
    }

    [Fact]
    public async Task AnAgentThatWasOnlySkippedDoesNotAskForARestart()
    {
        using var home = new TempHome();
        home.MakeDirectory(".claude");
        var project = home.MakeDirectory("Game");

        var (context, output, _) = Context(home, Descriptor(project));

        // VS Code keeps its config in the Unity project, so --scope project has nothing to write
        // for it and the run registers no server anywhere.
        Assert.Equal(0, await Program.Run(["setup", "--agent", "vscode", "--scope", "project"], context));

        Assert.Contains("skipped VS Code", output.ToString());
        Assert.DoesNotContain("Restart the agent", output.ToString());
    }

    [Fact]
    public async Task SetupWithoutARunningEditorStillInstallsTheSkill()
    {
        using var home = new TempHome();
        home.MakeDirectory(".claude");

        var (context, _, error) = Context(home);

        Assert.Equal(3, await Program.Run(["setup", "--agent", "claude-code", "--mcp"], context));
        Assert.True(File.Exists(home.At(".claude", "skills", "isuzu-unity-cli", "SKILL.md")));
        Assert.Contains("setup --mcp needs a running Editor", error.ToString());
    }

    [Fact]
    public async Task ProjectScopeNeverWritesTheTokenUnderTheProjectRoot()
    {
        using var home = new TempHome();
        home.MakeDirectory(".claude");
        var project = home.MakeDirectory("Game");

        var (context, output, _) = Context(home, Descriptor(project));

        Assert.Equal(0, await Program.Run(["setup", "--agent", "claude-code", "--scope", "project"], context));

        var written = File.ReadAllText(Path.Combine(project, ".mcp.json"));

        Assert.DoesNotContain(Token, written);
        Assert.Contains("Bearer ${UNITY_MCP_TOKEN}", written);

        // The token still has to reach the agent somehow, so setup says how.
        Assert.Contains("UNITY_MCP_TOKEN", output.ToString());
        Assert.Contains(Token, output.ToString());
    }

    [Fact]
    public async Task CodexGetsATomlTableAndKeepsTheRestOfItsConfig()
    {
        using var home = new TempHome();
        var codex = home.MakeDirectory(".codex");
        File.WriteAllText(Path.Combine(codex, "config.toml"), "model = \"gpt-5.6-sol\"\n\n# keep me\n[mcp_servers.maya]\ncommand = \"uv\"\n");
        var project = home.MakeDirectory("Game");

        var (context, _, _) = Context(home, Descriptor(project));

        Assert.Equal(0, await Program.Run(["setup", "--agent", "codex", "--mcp"], context));

        var written = File.ReadAllText(Path.Combine(codex, "config.toml"), Encoding.UTF8);

        Assert.Contains("# keep me", written);
        Assert.Contains("[mcp_servers.maya]", written);
        Assert.Equal("http://127.0.0.1:27186/mcp", TomlConfigEditor.Read(written, "mcp_servers.isuzu-unity")!.Url);
        Assert.True(File.Exists(Path.Combine(codex, "skills", "isuzu-unity-cli", "SKILL.md")));
    }

    [Fact]
    public async Task VsCodeGetsThePromptRatherThanTheToken()
    {
        using var home = new TempHome();
        var project = home.MakeDirectory("Game");

        var (context, _, _) = Context(home, Descriptor(project));

        Assert.Equal(0, await Program.Run(["setup", "--agent", "vscode"], context));

        var written = File.ReadAllText(Path.Combine(project, ".vscode", "mcp.json"));
        var config = JsonNode.Parse(written)!;

        Assert.DoesNotContain(Token, written);
        Assert.Equal("Bearer ${input:isuzu-unity-token}", config["servers"]!["isuzu-unity"]!["headers"]!["Authorization"]!.GetValue<string>());
        Assert.Equal("isuzu-unity-token", config["inputs"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnUnknownAgentIsRefusedRatherThanGuessed()
    {
        using var home = new TempHome();
        var (context, _, error) = Context(home);

        Assert.Equal(1, await Program.Run(["setup", "--agent", "emacs"], context));
        Assert.Contains("Unknown agent 'emacs'", error.ToString());
    }

    [Fact]
    public async Task DoctorReportsAStaleTokenAndFixRepairsIt()
    {
        using var home = new TempHome();
        home.MakeDirectory(".claude");
        var project = home.MakeDirectory("Game");

        var (setup, _, _) = Context(home, Descriptor(project));
        Assert.Equal(0, await Program.Run(["setup", "--agent", "claude-code", "--mcp"], setup));

        // The Editor regenerated its token, which is what a rotation looks like from here.
        var rotated = Descriptor(project, token: "0000000000000000000000000000000000000000000000000000000000000000");
        var (doctor, report, _) = Context(home, rotated);

        Assert.Equal(0, await Program.Run(["doctor"], doctor));
        Assert.Contains("stale: the token differs", report.ToString());

        var (fix, fixReport, _) = Context(home, rotated);
        Assert.Equal(0, await Program.Run(["doctor", "--fix"], fix));
        Assert.Contains("rewritten from the running Editor", fixReport.ToString());

        var entry = JsonConfigEditor.Find(
            JsonConfigEditor.Read(home.At(".claude.json")),
            ["projects", McpServerEntry.ClaudeCodeProjectKey(project), "mcpServers", "isuzu-unity"]);

        Assert.Equal("Bearer " + rotated.Token, entry!["headers"]!["Authorization"]!.GetValue<string>());
    }

    [Fact]
    public async Task DoctorReinstallsAStaleSkillOnlyWhenAskedTo()
    {
        using var home = new TempHome();
        var skills = home.MakeDirectory(".claude", "skills");
        SkillInstaller.Install(skills);
        File.AppendAllText(SkillInstaller.FileFor(skills), "\nedited by hand\n");

        var (doctor, report, _) = Context(home);
        Assert.Equal(0, await Program.Run(["doctor"], doctor));
        Assert.Contains("[stale]", report.ToString());
        Assert.Contains("edited by hand", File.ReadAllText(SkillInstaller.FileFor(skills)));

        var (fix, fixReport, _) = Context(home);
        Assert.Equal(0, await Program.Run(["doctor", "--fix"], fix));
        Assert.Contains("[fixed]", fixReport.ToString());
        Assert.False(SkillInstaller.IsStale(skills));
    }

    [Fact]
    public async Task DoctorWarnsWhenAnEditorDidNotGetItsPreferredPort()
    {
        using var home = new TempHome();
        var project = home.MakeDirectory("Game");
        var descriptor = Descriptor(project);
        descriptor.PreferredPort = 27185;
        descriptor.PortMismatch = true;

        var (context, report, _) = Context(home, descriptor);

        Assert.Equal(0, await Program.Run(["doctor"], context));
        Assert.Contains("wanted port 27185 and took 27186", report.ToString());
    }

    [Fact]
    public async Task UninstallRefusesWhileAnEditorIsRunning()
    {
        using var home = new TempHome();
        var project = home.MakeDirectory("Game");

        // Written where DescriptorStore looks, with this test's own pid, so the liveness check
        // has something real to find.
        home.WriteDescriptor("live", $$"""
            {"projectPath":{{Quoted(project)}},"projectName":"Game","unityVersion":"6000.5.10f1",
             "port":27186,"token":"{{Token}}","pid":{{Environment.ProcessId}},"protocolVersion":"3.3.1",
             "endpoint":"http://127.0.0.1:27186","mcpUrl":"http://127.0.0.1:27186/mcp"}
            """);

        var output = new StringWriter();
        var error = new StringWriter();
        var context = new CommandContext { Out = output, Err = error, WorkingDirectory = home.Root };

        Assert.Equal(1, await Program.Run(["uninstall"], context));
        Assert.Contains("These Editors are still running", error.ToString());
        Assert.Contains("Close them first.", error.ToString());
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public async Task UninstallListsWhatItWouldRemoveBeforeRemovingIt()
    {
        using var home = new TempHome();
        home.MakeDirectory(".claude");
        var project = home.MakeDirectory("Game");
        var descriptor = Descriptor(project);

        var (setup, _, _) = Context(home, descriptor);
        Assert.Equal(0, await Program.Run(["setup", "--agent", "claude-code", "--mcp"], setup));

        home.WriteDescriptor("closed", "{\"projectPath\":" + Quoted(project) + ",\"projectName\":\"Game\",\"port\":27186,\"token\":\"t\",\"pid\":0,\"endpoint\":\"http://127.0.0.1:27186\"}");

        // Nothing is running any more, but the descriptor file still names the project whose
        // config was written into.
        var (dry, listing, _) = Closed(home, descriptor);
        Assert.Equal(0, await Program.Run(["uninstall"], dry));

        Assert.Contains("Would remove:", listing.ToString());
        Assert.Contains(home.At(".claude.json"), listing.ToString());
        Assert.Contains("isuzu-unity-cli", listing.ToString());
        Assert.Contains("Re-run with --yes", listing.ToString());
        Assert.True(File.Exists(home.At(".claude", "skills", "isuzu-unity-cli", "SKILL.md")));

        var (real, removals, _) = Closed(home, descriptor);
        Assert.Equal(0, await Program.Run(["uninstall", "--yes"], real));

        Assert.False(Directory.Exists(home.At(".claude", "skills", "isuzu-unity-cli")));
        Assert.Null(JsonConfigEditor.Find(JsonConfigEditor.Read(home.At(".claude.json")), ["projects", McpServerEntry.ClaudeCodeProjectKey(project), "mcpServers", "isuzu-unity"]));
        Assert.Contains("The Unity package itself is removed through the Package Manager.", removals.ToString());
    }

    [Fact]
    public async Task UninstallLeavesSkillsAloneWhenAsked()
    {
        using var home = new TempHome();
        var skills = home.MakeDirectory(".claude", "skills");
        SkillInstaller.Install(skills);

        var (context, _, _) = Closed(home);
        Assert.Equal(0, await Program.Run(["uninstall", "--yes", "--no-skill"], context));

        Assert.True(File.Exists(SkillInstaller.FileFor(skills)));
    }

    private static string Quoted(string value) => JsonValue.Create(value)!.ToJsonString();

    private static InstanceDescriptor Descriptor(string projectRoot, string token = Token, int port = 27186)
    {
        return new InstanceDescriptor
        {
            ProjectName = "Game",
            ProjectPath = Path.Combine(projectRoot, "Assets"),
            UnityVersion = "6000.5.10f1",
            Port = port,
            Token = token,
            Pid = 0,
            ProtocolVersion = "3.3.1",
            Endpoint = $"http://127.0.0.1:{port}",
            McpUrl = $"http://127.0.0.1:{port}/mcp",
        };
    }

    private static (CommandContext Context, StringWriter Out, StringWriter Err) Context(TempHome home, params InstanceDescriptor[] descriptors)
    {
        return Build(home, descriptors, descriptors);
    }

    /// <summary>A machine where the Editors have closed but their descriptor files remain.</summary>
    private static (CommandContext Context, StringWriter Out, StringWriter Err) Closed(TempHome home, params InstanceDescriptor[] descriptors)
    {
        return Build(home, [], descriptors);
    }

    private static (CommandContext Context, StringWriter Out, StringWriter Err) Build(
        TempHome home,
        InstanceDescriptor[] running,
        InstanceDescriptor[] known)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        return (new CommandContext
        {
            Out = output,
            Err = error,
            WorkingDirectory = home.Root,
            ReadDescriptors = () => running,
            ReadAllDescriptors = () => known,
            ExecutablePath = Path.Combine(home.Root, "bin", "isuzu-unity-cli"),
        }, output, error);
    }
}
