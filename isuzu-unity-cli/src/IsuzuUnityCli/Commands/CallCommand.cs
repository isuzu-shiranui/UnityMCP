using IsuzuUnityCli.Cli;

namespace IsuzuUnityCli.Commands;

public static class CallCommand
{
    public static async Task<int> Run(ParsedArgs parsed, CommandContext context)
    {
        var tool = parsed.Positional.Count > 0 ? parsed.Positional[0] : "";

        if (tool.Length == 0)
        {
            context.Err.WriteLine("Which tool? Run `isuzu-unity-cli tools` to see what this Editor publishes.");
            return 2;
        }

        var args = ToolArguments.Build(tool, parsed);
        var instance = context.ResolveInstance(parsed);
        StageTrace.Mark("resolved");
        var envelope = await context.Client.PostAsync(instance, "/tools/" + tool, args, context.Cancellation);
        var code = context.Report(envelope, parsed.HasFlag("raw"));
        StageTrace.Mark("reported");
        return code;
    }
}
