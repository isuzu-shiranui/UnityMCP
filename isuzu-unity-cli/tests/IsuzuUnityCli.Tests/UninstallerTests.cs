using System.Text.Json;
using System.Text.Json.Nodes;

using IsuzuUnityCli.Agents;
using IsuzuUnityCli.Discovery;
using IsuzuUnityCli.Housekeeping;
using IsuzuUnityCli.Tests.Fakes;

using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public class UninstallerTests
{
    /// <summary>
    /// Taking the entry away leaves a backup of the config as it was, which is the config with
    /// the entry still in it — bearer token and all. Uninstall exists to take that credential
    /// away, so it has to take the copy with it.
    /// </summary>
    [Fact]
    public void RemovingAnEntryTakesTheBackupOfItToo()
    {
        using var home = new TempHome();
        var config = home.At("claude.json");

        var content = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["isuzu-unity"] = new JsonObject
                {
                    ["url"] = "http://127.0.0.1:27000/mcp",
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer SECRET" },
                },
            },
        };

        JsonConfigEditor.Write(config, content);
        Assert.Contains("SECRET", File.ReadAllText(config));

        var plan = new UninstallPlan();
        plan.ConfigEntries.Add(new ConfigEntryRemoval
        {
            Target = AgentCatalog.Find("cursor")!,
            ConfigPath = config,
            JsonPath = ["mcpServers", "isuzu-unity"],
        });

        Uninstaller.Apply(plan);

        Assert.DoesNotContain("SECRET", File.ReadAllText(config));
        Assert.False(File.Exists(JsonConfigEditor.BackupFor(config)),
            "the backup holds the entry that was just removed, token and all");
    }

    /// <summary>
    /// An ordinary edit keeps its backup: that is what it is for.
    /// </summary>
    [Fact]
    public void AnEditThatIsNotARemovalKeepsItsBackup()
    {
        using var home = new TempHome();
        var config = home.At("claude.json");

        File.WriteAllText(config, """{ "mcpServers": { "other": {} } }""");
        JsonConfigEditor.Write(config, JsonConfigEditor.Parse("""{ "mcpServers": { "other": { "url": "x" } } }"""));

        Assert.True(File.Exists(JsonConfigEditor.BackupFor(config)));
    }

    /// <summary>
    /// JsonObject builds its dictionary per object, so forcing the root left a duplicate two
    /// levels down to throw at whatever first walked that far — outside the filter that handles
    /// it, which took the whole command down.
    /// </summary>
    [Fact]
    public void ADuplicateKeyAnywhereIsAHandledError()
    {
        var e = Assert.Throws<JsonException>(
            () => JsonConfigEditor.Parse("""{"mcpServers":{"a":{},"a":{}}}"""));

        Assert.Contains("duplicate key", e.Message);
    }

    [Fact]
    public void ADuplicateKeyDeeperStillIsAHandledError()
    {
        var e = Assert.Throws<JsonException>(
            () => JsonConfigEditor.Parse("""{"projects":{"p":{"mcpServers":{"a":1,"a":2}}}}"""));

        Assert.Contains("duplicate key", e.Message);
    }

    /// <summary>
    /// A backup a run left behind is the config with the entry still in it. The run that finds no
    /// entry to remove is exactly the run after one that was interrupted, so it has to be the one
    /// that cleans up rather than the one that walks past.
    /// </summary>
    /// <remarks>
    /// The plan is built by hand rather than through <c>Uninstaller.Plan</c>. That method reaches
    /// the real state directory, and applying what it returns would delete the developer's own
    /// descriptors and cache.
    /// </remarks>
    [Fact]
    public void AStrayBackupIsRemovedEvenWithNoEntryLeft()
    {
        using var home = new TempHome();
        var config = home.At("mcp.json");
        var backup = JsonConfigEditor.BackupFor(config);

        File.WriteAllText(config, """{ "mcpServers": {} }""");
        File.WriteAllText(backup,
            """{ "mcpServers": { "isuzu-unity": { "headers": { "Authorization": "Bearer SECRET" } } } }""");

        var plan = new UninstallPlan();
        plan.State.Add(backup);

        Uninstaller.Apply(plan);

        Assert.False(File.Exists(backup), "the backup holds the token this command exists to remove");
        Assert.True(File.Exists(config), "the config it sat beside is not this command's to delete");
    }

    /// <summary>
    /// Every config this tool writes gets a backup beside it, so every config it writes has to be
    /// swept for one. Hooking the sweep into the shared helper missed the one caller that does not
    /// use that helper, which is the per-repository .mcp.json.
    /// </summary>
    [Fact]
    public void APerRepositoryConfigsBackupIsPlannedToo()
    {
        using var home = new TempHome();
        var project = home.MakeDirectory("Game");
        var config = Path.Combine(project, ".mcp.json");

        File.WriteAllText(config, """{ "mcpServers": {} }""");
        File.WriteAllText(JsonConfigEditor.BackupFor(config), """{ "mcpServers": { "isuzu-unity": {} } }""");

        var descriptor = new InstanceDescriptor
        {
            ProjectName = "Game",
            ProjectPath = Path.Combine(project, "Assets"),
            Port = 27180,
            Token = "t",
            Endpoint = "http://127.0.0.1:27180",
            McpUrl = "http://127.0.0.1:27180/mcp",
        };
        var plan = Uninstaller.Plan([AgentCatalog.Find("claude-code")!], [descriptor], includeSkills: false);

        Assert.Contains(JsonConfigEditor.BackupFor(config), plan.State);
    }

    /// <summary>
    /// A config that cannot be read is the one case where the copy beside it is the only way
    /// back — and it is also the case that reads as "no entry to remove", so the run would have
    /// taken the copy and reported success.
    /// </summary>
    [Fact]
    public void ABackupBesideAConfigThatCannotBeReadIsLeftAlone()
    {
        using var home = new TempHome();
        var project = home.MakeDirectory("Game");
        var config = Path.Combine(project, ".mcp.json");

        File.WriteAllText(config, "{ this is not json");
        File.WriteAllText(JsonConfigEditor.BackupFor(config), """{ "mcpServers": { "other": {} } }""");

        var descriptor = new InstanceDescriptor
        {
            ProjectName = "Game",
            ProjectPath = Path.Combine(project, "Assets"),
            Port = 27180,
            Token = "t",
            Endpoint = "http://127.0.0.1:27180",
            McpUrl = "http://127.0.0.1:27180/mcp",
        };

        var plan = Uninstaller.Plan([AgentCatalog.Find("claude-code")!], [descriptor], includeSkills: false);

        Assert.DoesNotContain(JsonConfigEditor.BackupFor(config), plan.State);
    }
}
