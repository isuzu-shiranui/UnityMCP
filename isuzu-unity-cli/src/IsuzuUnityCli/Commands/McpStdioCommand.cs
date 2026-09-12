using IsuzuUnityCli.Bridge;
using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Discovery;

namespace IsuzuUnityCli.Commands;

public static class McpStdioCommand
{
    public static async Task<int> Run(ParsedArgs parsed, CommandContext context)
    {
        // Resolution is deferred to the bridge: an MCP client starts this process before any
        // Editor is open, and failing here would make the server look permanently broken. The
        // first selection binds the whole session, so it takes no substring match.
        InstanceDescriptor? selected = null;
        using var bridge = new McpStdioBridge(
            context.In,
            context.Out,
            () => selected = selected is null
                ? context.ResolveInstance(parsed, exactOnly: true)
                : context.RefreshInstance(selected, InstanceResolver.SwitchByRestart),
            parsed.Option("project"),
            groups: parsed.Option("group"));

        return await bridge.RunAsync(context.Cancellation);
    }
}
