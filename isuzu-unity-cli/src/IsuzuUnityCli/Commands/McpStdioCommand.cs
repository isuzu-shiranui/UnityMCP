using IsuzuUnityCli.Bridge;
using IsuzuUnityCli.Cli;

namespace IsuzuUnityCli.Commands;

public static class McpStdioCommand
{
    public static async Task<int> Run(ParsedArgs parsed, CommandContext context)
    {
        // Resolution is deferred to the bridge: an MCP client starts this process before any
        // Editor is open, and failing here would make the server look permanently broken.
        using var bridge = new McpStdioBridge(
            context.In,
            context.Out,
            () => context.ResolveInstance(parsed),
            parsed.Option("project"));

        return await bridge.RunAsync(context.Cancellation);
    }
}
