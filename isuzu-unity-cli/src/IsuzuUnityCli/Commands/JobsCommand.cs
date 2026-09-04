using IsuzuUnityCli.Cli;

namespace IsuzuUnityCli.Commands;

public static class JobsCommand
{
    public static async Task<int> Run(ParsedArgs parsed, CommandContext context)
    {
        var id = parsed.Positional.Count > 0 ? parsed.Positional[0] : "";
        var path = id.Length > 0 ? "/jobs/" + Uri.EscapeDataString(id) : "/jobs";
        var instance = context.ResolveInstance(parsed);
        var envelope = await context.Client.GetAsync(instance, path, context.Cancellation);
        return context.Report(envelope, parsed.HasFlag("raw"));
    }
}
