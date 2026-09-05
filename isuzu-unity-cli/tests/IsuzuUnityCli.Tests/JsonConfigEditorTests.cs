using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IsuzuUnityCli.Agents;
using IsuzuUnityCli.Tests.Fakes;
using Xunit;

namespace IsuzuUnityCli.Tests;

/// <summary>
/// These files hold every MCP server the user has registered and, for Claude Code, the settings
/// of every project they have opened. A bug here costs configuration nobody backed up.
/// </summary>
public sealed class JsonConfigEditorTests
{
    [Fact]
    public void AnEmptyOrMissingFileReadsAsAnEmptyObject()
    {
        Assert.Empty(JsonConfigEditor.Parse(null));
        Assert.Empty(JsonConfigEditor.Parse(""));
        Assert.Empty(JsonConfigEditor.Parse("   \n"));
    }

    [Fact]
    public void ABomIsStrippedOnReadAndNotWrittenBack()
    {
        var root = JsonConfigEditor.Parse("\uFEFF{\"theme\":\"dark\"}");

        Assert.Equal("dark", root["theme"]!.GetValue<string>());
        Assert.DoesNotContain('\uFEFF', JsonConfigEditor.Serialize(root));
    }

    [Fact]
    public void UpsertCreatesTheObjectsAlongTheWay()
    {
        var root = new JsonObject();

        JsonConfigEditor.Upsert(root, ["projects", "H:\\Game", "mcpServers", "isuzu-unity"], new JsonObject { ["url"] = "u" });

        Assert.Equal("u", root["projects"]!["H:\\Game"]!["mcpServers"]!["isuzu-unity"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void UpsertLeavesEverythingElseAlone()
    {
        var root = JsonConfigEditor.Parse("""
            {"numStartups":12,"projects":{"H:\\Other":{"history":[1,2]}},"mcpServers":{"maya":{"command":"uv"}}}
            """);

        JsonConfigEditor.Upsert(root, ["mcpServers", "isuzu-unity"], new JsonObject { ["url"] = "u" });

        Assert.Equal(12, root["numStartups"]!.GetValue<int>());
        Assert.Equal(2, root["projects"]!["H:\\Other"]!["history"]!.AsArray().Count);
        Assert.Equal("uv", root["mcpServers"]!["maya"]!["command"]!.GetValue<string>());
        Assert.Equal("u", root["mcpServers"]!["isuzu-unity"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void UpsertReplacesAnEntryRatherThanMergingIntoIt()
    {
        var root = JsonConfigEditor.Parse("""{"mcpServers":{"isuzu-unity":{"url":"old","stale":true}}}""");

        JsonConfigEditor.Upsert(root, ["mcpServers", "isuzu-unity"], new JsonObject { ["url"] = "new" });

        Assert.Equal("new", root["mcpServers"]!["isuzu-unity"]!["url"]!.GetValue<string>());
        Assert.Null(root["mcpServers"]!["isuzu-unity"]!["stale"]);
    }

    [Fact]
    public void RemoveTakesOnlyOurEntryAndReportsWhenThereWasNone()
    {
        var root = JsonConfigEditor.Parse("""{"mcpServers":{"maya":{"command":"uv"},"isuzu-unity":{"url":"u"}}}""");

        Assert.True(JsonConfigEditor.Remove(root, ["mcpServers", "isuzu-unity"]));
        Assert.NotNull(root["mcpServers"]!["maya"]);
        Assert.Null(root["mcpServers"]!["isuzu-unity"]);

        Assert.False(JsonConfigEditor.Remove(root, ["mcpServers", "isuzu-unity"]));
        Assert.False(JsonConfigEditor.Remove(root, ["nothing", "here"]));
    }

    [Fact]
    public void FindReturnsNullRatherThanCreatingThePath()
    {
        var root = new JsonObject();

        Assert.Null(JsonConfigEditor.Find(root, ["mcpServers", "isuzu-unity"]));
        Assert.Empty(root);
    }

    [Fact]
    public void OutputIsTwoSpaceIndentedWithUnixLineEndings()
    {
        var root = JsonConfigEditor.Parse("""{"mcpServers":{"isuzu-unity":{"url":"u"}}}""");

        var text = JsonConfigEditor.Serialize(root);

        Assert.DoesNotContain('\r', text);
        Assert.Contains("\n  \"mcpServers\": {\n    \"isuzu-unity\": {\n      \"url\": \"u\"", text);
        Assert.EndsWith("\n", text);
    }

    [Fact]
    public void NonAsciiSurvivesAsItself()
    {
        var root = JsonConfigEditor.Parse("""{"projects":{"H:\\プロジェクト":{}}}""");

        Assert.Contains("H:\\\\プロジェクト", JsonConfigEditor.Serialize(root));
    }

    [Fact]
    public void AFileThatIsNotAnObjectIsRefusedRatherThanReplaced()
    {
        Assert.Throws<JsonException>(() => JsonConfigEditor.Parse("[1,2,3]"));
        Assert.ThrowsAny<JsonException>(() => JsonConfigEditor.Parse("{not json"));
    }

    [Fact]
    public void WriteCreatesTheDirectoryAndLeavesNoBom()
    {
        var directory = Path.Combine(Path.GetTempPath(), "isuzu-cli-tests", Guid.NewGuid().ToString("N"));
        var file = Path.Combine(directory, "nested", "mcp.json");

        try
        {
            JsonConfigEditor.Write(file, JsonConfigEditor.Parse("""{"a":1}"""));

            var bytes = File.ReadAllBytes(file);

            Assert.Equal((byte)'{', bytes[0]);
            Assert.Equal("""{"a":1}""", JsonNode.Parse(Encoding.UTF8.GetString(bytes))!.ToJsonString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
    /// <summary>
    /// Removing the only entry a project key held used to leave the key behind holding an empty
    /// mcpServers, permanently, on every machine that migrated.
    /// </summary>
    [Fact]
    public void RemovingTheLastEntryTakesTheContainersItEmptied()
    {
        var root = JsonConfigEditor.Parse("""
        {
          "projects": {
            "H:\\Old": { "mcpServers": { "isuzu-unity": { "url": "x" } } }
          }
        }
        """);

        Assert.True(JsonConfigEditor.Remove(root, ["projects", "H:\\Old", "mcpServers", "isuzu-unity"]));

        var projects = (JsonObject)root["projects"]!;
        Assert.False(projects.ContainsKey("H:\\Old"));
        Assert.NotNull(root["projects"]);
    }

    /// <summary>
    /// A project key Claude Code also keeps its own state under is not ours to take away.
    /// </summary>
    [Fact]
    public void AProjectKeyCarryingOtherStateSurvives()
    {
        var root = JsonConfigEditor.Parse("""
        {
          "projects": {
            "H:/Live": {
              "mcpServers": { "isuzu-unity": { "url": "x" } },
              "lastSessionId": "abc"
            }
          }
        }
        """);

        Assert.True(JsonConfigEditor.Remove(root, ["projects", "H:/Live", "mcpServers", "isuzu-unity"]));

        var project = (JsonObject)((JsonObject)root["projects"]!)["H:/Live"]!;
        Assert.Equal("abc", project["lastSessionId"]!.GetValue<string>());
        Assert.False(project.ContainsKey("mcpServers"));
    }

    /// <summary>
    /// Another server in the same map keeps the map.
    /// </summary>
    [Fact]
    public void ASiblingEntryKeepsTheMap()
    {
        var root = JsonConfigEditor.Parse("""
        {
          "projects": {
            "H:/Both": { "mcpServers": { "isuzu-unity": { "url": "x" }, "other": { "url": "y" } } }
          }
        }
        """);

        Assert.True(JsonConfigEditor.Remove(root, ["projects", "H:/Both", "mcpServers", "isuzu-unity"]));

        var servers = (JsonObject)((JsonObject)((JsonObject)root["projects"]!)["H:/Both"]!)["mcpServers"]!;
        Assert.True(servers.ContainsKey("other"));
    }

    /// <summary>
    /// A top-level map the agent owns stays, empty or not: absent and empty are not the same
    /// thing to the program that reads it.
    /// </summary>
    [Fact]
    public void TheAgentsOwnTopLevelMapIsLeftAlone()
    {
        var root = JsonConfigEditor.Parse("""{ "mcpServers": { "isuzu-unity": { "url": "x" } } }""");

        Assert.True(JsonConfigEditor.Remove(root, ["mcpServers", "isuzu-unity"]));

        Assert.NotNull(root["mcpServers"]);
        Assert.Empty((JsonObject)root["mcpServers"]!);
    }
    /// <summary>
    /// The write used to truncate the real file in place, so the config passed through every size
    /// from near zero on its way to the new content with no second copy anywhere. Losing that
    /// window costs the whole file: the login, every project key, every tool grant.
    /// </summary>
    [Fact]
    public void WritingKeepsWhatItReplaces()
    {
        using var home = new TempHome();
        var path = home.At("config.json");

        File.WriteAllText(path, """{ "keep": "the old content" }""");

        JsonConfigEditor.Write(path, JsonConfigEditor.Parse("""{ "new": true }"""));

        Assert.True(File.Exists(path + ".isuzu-bak"));
        Assert.Contains("the old content", File.ReadAllText(path + ".isuzu-bak"));
        Assert.Contains("new", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".isuzu-tmp"), "the temporary file is renamed, not left");
    }

    [Fact]
    public void WritingWhereThereWasNothingLeavesNoBackup()
    {
        using var home = new TempHome();
        var path = home.At("fresh.json");

        JsonConfigEditor.Write(path, JsonConfigEditor.Parse("""{ "new": true }"""));

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".isuzu-bak"), "there was nothing to keep");
    }

    /// <summary>
    /// A lone surrogate is legal JSON and cannot be encoded as UTF-8. Left as an
    /// InvalidOperationException it reached the caller as a stack trace and a dead process.
    /// </summary>
    [Fact]
    public void TextThatCannotBeWrittenBackIsAHandledError()
    {
        var root = JsonConfigEditor.Parse("{\"note\":\"\\ud800\"}");

        var e = Assert.Throws<JsonException>(() => { _ = JsonConfigEditor.Serialize(root); });
        Assert.Contains("cannot be written back", e.Message);
    }

    /// <summary>
    /// JSON permits duplicate keys. JsonNode accepts them and throws only when the dictionary is
    /// built on the first lookup, which happened deep inside a caller rather than at the parse.
    /// </summary>
    [Fact]
    public void ADuplicateKeyIsAHandledError()
    {
        var e = Assert.Throws<JsonException>(
            () => JsonConfigEditor.Parse("""{"projects":{"a":1},"projects":{"b":2}}"""));

        Assert.Contains("duplicate key", e.Message);
    }

    /// <summary>
    /// A config that cannot be written back has to leave the file it could not replace alone.
    /// </summary>
    [Fact]
    public void AFailedWriteLeavesTheFileAsItWas()
    {
        using var home = new TempHome();
        var path = home.At("config.json");

        File.WriteAllText(path, """{ "keep": "everything" }""");

        var root = JsonConfigEditor.Parse("{\"note\":\"\\ud800\"}");

        Assert.Throws<JsonException>(() => { JsonConfigEditor.Write(path, root); });
        Assert.Contains("everything", File.ReadAllText(path));
    }
}
