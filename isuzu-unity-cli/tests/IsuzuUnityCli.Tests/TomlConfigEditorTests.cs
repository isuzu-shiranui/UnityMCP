using IsuzuUnityCli.Agents;
using Xunit;

namespace IsuzuUnityCli.Tests;

/// <summary>
/// Codex's config holds project trust settings, plugin state and machine-generated paths.
/// Adding four lines must leave every byte of that alone, comments included.
/// </summary>
public sealed class TomlConfigEditorTests
{
    private const string CodexConfig = """
        model = "gpt-5.6-sol"
        notify = [ "something" ]

        # Trust settings, written by hand.
        [projects.'C:\Users\x']
        trust_level = "trusted"   # inline comment

        [mcp_servers.maya]
        command = "uv"
        args = ["--directory", "H:\\MayaMCP", "run"]

        [mcp_servers.node_repl]
        args = []
        command = "node_repl.exe"

        [mcp_servers.node_repl.env]
        CODEX_HOME = "C:\\Users\\x\\.codex"

        [shell_environment_policy]
        inherit = "core"

        """;

    private const string Table = "mcp_servers.isuzu-unity";

    private const string Body = """
        url = "http://127.0.0.1:27186/mcp"
        http_headers = { Authorization = "Bearer abc" }

        """;

    /// <summary>
    /// The guard the whole approach rests on: a parse that does not reproduce its input exactly
    /// would mean every edit silently reformats the user's file.
    /// </summary>
    [Fact]
    public void ParsingAndPrintingReproducesTheFileByteForByte()
    {
        var sample = Normalise(CodexConfig);

        Assert.Equal(sample, TomlConfigEditor.Upsert(sample, "nothing.matches.this", "x = 1\n")[..sample.Length]);
        Assert.Null(TomlConfigEditor.Remove(sample, Table));
    }

    [Fact]
    public void AppendingKeepsEveryCommentAndTableThatWasThere()
    {
        var sample = Normalise(CodexConfig);

        var after = TomlConfigEditor.Upsert(sample, Table, Normalise(Body));

        Assert.StartsWith(sample.TrimEnd('\n'), after);
        Assert.Contains("# Trust settings, written by hand.", after);
        Assert.Contains("trust_level = \"trusted\"   # inline comment", after);
        Assert.Contains("[mcp_servers.node_repl.env]", after);
        Assert.Contains("[shell_environment_policy]", after);
        Assert.Contains("[mcp_servers.isuzu-unity]", after);
        Assert.Contains("url = \"http://127.0.0.1:27186/mcp\"", after);
    }

    [Fact]
    public void UpsertingTwiceLeavesOneTable()
    {
        var once = TomlConfigEditor.Upsert(Normalise(CodexConfig), Table, "url = \"old\"\n");
        var twice = TomlConfigEditor.Upsert(once, Table, "url = \"new\"\n");

        Assert.Single(twice.Split("[mcp_servers.isuzu-unity]").Skip(1));
        Assert.Contains("url = \"new\"", twice);
        Assert.DoesNotContain("url = \"old\"", twice);
    }

    [Fact]
    public void ReplacingATableInTheMiddleLeavesTheFollowingOneIntact()
    {
        var reordered = Normalise(CodexConfig).Replace(
            "[shell_environment_policy]",
            "[mcp_servers.isuzu-unity]\nurl = \"stale\"\n\n[shell_environment_policy]");

        var after = TomlConfigEditor.Upsert(reordered, Table, "url = \"fresh\"\n");

        Assert.Contains("[shell_environment_policy]", after);
        Assert.Contains("inherit = \"core\"", after);
        Assert.DoesNotContain("stale", after);
    }

    [Fact]
    public void RemovingTakesTheSubTablesWithItAndNothingElse()
    {
        var config = Normalise("""
            [mcp_servers.isuzu-unity]
            url = "u"

            [mcp_servers.isuzu-unity.env]
            FOO = "bar"

            [other]
            keep = true

            """);

        var after = TomlConfigEditor.Remove(config, Table)!;

        Assert.DoesNotContain("FOO", after);
        Assert.DoesNotContain("isuzu-unity", after);
        Assert.Contains("[other]", after);
        Assert.Contains("keep = true", after);
    }

    [Fact]
    public void RemovingWhatIsNotThereReportsNothingRatherThanRewriting()
    {
        Assert.Null(TomlConfigEditor.Remove(Normalise(CodexConfig), Table));
    }

    [Fact]
    public void AppendingToAFileWithNoTrailingNewlineStillProducesValidToml()
    {
        var after = TomlConfigEditor.Upsert("model = \"x\"", Table, Normalise(Body));

        Assert.Contains("model = \"x\"\n", after);
        Assert.Contains("\n[mcp_servers.isuzu-unity]\n", after);
        Assert.Equal("http://127.0.0.1:27186/mcp", TomlConfigEditor.Read(after, Table)!.Url);
    }

    [Fact]
    public void AppendingToAnEmptyFileWorks()
    {
        var after = TomlConfigEditor.Upsert("", Table, Normalise(Body));

        Assert.StartsWith("[mcp_servers.isuzu-unity]", after);
    }

    [Fact]
    public void WindowsPathsAreQuotedSoTheBackslashesSurvive()
    {
        var quoted = TomlConfigEditor.Quote(@"C:\Users\x\isuzu-unity-cli.exe");

        Assert.Equal("\"C:\\\\Users\\\\x\\\\isuzu-unity-cli.exe\"", quoted);

        var after = TomlConfigEditor.Upsert("", "mcp_servers.test", $"command = {quoted}\n");

        Assert.Equal(@"C:\Users\x\isuzu-unity-cli.exe", TomlConfigEditor.ReadValue(after, "mcp_servers.test", "command"));
    }

    [Fact]
    public void ReadingBackTheEntryGivesTheUrlAndTheHeader()
    {
        var after = TomlConfigEditor.Upsert(Normalise(CodexConfig), Table, Normalise(Body));
        var entry = TomlConfigEditor.Read(after, Table);

        Assert.Equal("http://127.0.0.1:27186/mcp", entry!.Url);
        Assert.Equal("Bearer abc", entry.Authorization);
        Assert.Null(TomlConfigEditor.Read(after, "mcp_servers.absent"));
    }

    [Fact]
    public void AFileThatIsNotTomlIsRefusedRatherThanReplaced()
    {
        Assert.Throws<TomlEditException>(() => TomlConfigEditor.Upsert("this = = broken", Table, "url = \"u\"\n"));
    }

    /// <summary>Raw string literals pick up the platform's line endings; TOML tests need one form.</summary>
    private static string Normalise(string text) => text.ReplaceLineEndings("\n");
}
