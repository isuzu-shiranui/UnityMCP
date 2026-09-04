using IsuzuUnityCli.Cli;

namespace IsuzuUnityCli.Commands;

public static class HealthCommand
{
    public static async Task<int> Run(ParsedArgs parsed, CommandContext context)
    {
        var instance = context.ResolveInstance(parsed);
        var envelope = await context.Client.GetAsync(instance, "/health", context.Cancellation);
        return context.Report(envelope, parsed.HasFlag("raw"));
    }
}
