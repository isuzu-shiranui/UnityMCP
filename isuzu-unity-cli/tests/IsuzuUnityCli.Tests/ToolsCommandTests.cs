using System.Text.Json.Nodes;
using IsuzuUnityCli.Commands;
using IsuzuUnityCli.Tests.Fakes;
using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public sealed class ToolsCommandTests
{
    private const string Catalog = """
        {"status":"success","result":{"tools":[
          {"name":"chosen","description":"Read first; do not repeat a pending job.","annotations":{"readOnlyHint":true},
           "inputSchema":{"type":"object","properties":{"mode":{"type":"string","enum":["one","two"],"description":"Choose a mode."}},"required":["mode"]}},
          {"name":"other","description":"Other tool.","inputSchema":{"type":"object"}}
        ]}}
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactNamePreservesCompleteSchemaAndSafetyDescription(bool raw)
    {
        using var server = new FakeUnityServer().Enqueue(200, Catalog);
        var output = new StringWriter();
        var context = new CommandContext { Out = output, Err = new StringWriter(), ReadDescriptors = () => [server.Descriptor()] };
        var args = new List<string> { "tools", "chosen", "--group", "diagnostics" };
        if (raw) args.Add("--raw");
        Assert.Equal(0, await Program.Run(args.ToArray(), context));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Catalog)!["result"]!["tools"]![0], JsonNode.Parse(output.ToString())));
        Assert.Equal("/tools?group=diagnostics", Assert.Single(server.Requests).Path);
    }

    [Theory]
    [InlineData("chos")]
    [InlineData("Chosen")]
    public async Task UnknownOrPartialNameIsNotSilentlyIgnored(string name)
    {
        using var server = new FakeUnityServer().Enqueue(200, Catalog);
        var output = new StringWriter();
        var error = new StringWriter();
        var context = new CommandContext { Out = output, Err = error, ReadDescriptors = () => [server.Descriptor()] };
        Assert.Equal(2, await Program.Run(["tools", name], context));
        Assert.Equal("", output.ToString());
        Assert.Contains(name, error.ToString());
    }

    [Fact]
    public async Task ExtraNamesFailBeforeContactingTheEditor()
    {
        var context = new CommandContext { Out = new StringWriter(), Err = new StringWriter(), ReadDescriptors = () => throw new InvalidOperationException("must not resolve") };
        Assert.Equal(2, await Program.Run(["tools", "one", "two"], context));
    }

    [Fact]
    public async Task GroupIsPassedAsAQueryParameter()
    {
        using var server = new FakeUnityServer().Enqueue(200, """{"status":"success","result":{"tools":[]}}""");

        var context = new CommandContext
        {
            Out = new StringWriter(),
            Err = new StringWriter(),
            ReadDescriptors = () => [server.Descriptor()],
            WorkingDirectory = Path.GetTempPath(),
        };

        Assert.Equal(0, await Program.Run(["tools", "--group", "timeline,rendering"], context));
        Assert.Equal("/tools?group=timeline,rendering", Assert.Single(server.Requests).Path);
    }

    [Fact]
    public void NoGroupLeavesThePathAlone()
    {
        Assert.Equal("/tools", ToolsCommand.CatalogPath(null));
        Assert.Equal("/tools", ToolsCommand.CatalogPath("  "));
    }

    [Fact]
    public void GroupIsShownAfterTheParameters()
    {
        var result = JsonNode.Parse("""
            {"tools":[
              {"name":"timeline_evaluate","description":"Evaluate a timeline.","group":"timeline",
               "inputSchema":{"type":"object","properties":{"path":{}},"required":["path"]}},
              {"name":"console_get_count","description":"Count entries.","group":"diagnostics",
               "inputSchema":{"type":"object","properties":{}}}
            ]}
            """);

        Assert.Equal(
            "timeline_evaluate <path>  [timeline]\n    Evaluate a timeline.\nconsole_get_count  [diagnostics]\n    Count entries.\n",
            ToolsCommand.Render(result));
    }
}
