using System.Text.Json.Nodes;
using IsuzuUnityCli.Commands;
using IsuzuUnityCli.Tests.Fakes;
using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public sealed class ToolsCommandTests
{
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
