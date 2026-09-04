using System.Reflection;
using System.Text;
using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Commands;
using IsuzuUnityCli.Http;

namespace IsuzuUnityCli;

public static class Program
{
    public static async Task<int> Main(string[] argv)
    {
        StageTrace.Mark("main");
        Console.OutputEncoding = new UTF8Encoding(false);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var context = new CommandContext { Cancellation = cts.Token };
        return await Run(argv, context);
    }

    public static async Task<int> Run(string[] argv, CommandContext context)
    {
        var parsed = ArgParser.Parse(argv);
        StageTrace.Mark("parsed");

        if (parsed.HasFlag("version"))
        {
            context.Out.WriteLine(Version());
            return 0;
        }

        if (parsed.HasFlag("help") || parsed.Command.Length == 0 || parsed.Command == "help")
        {
            context.Out.WriteLine(Usage.Text);
            return 0;
        }

        try
        {
            switch (parsed.Command)
            {
                case "projects":
                    return ProjectsCommand.Run(parsed, context);
                case "tools":
                    return await ToolsCommand.Run(parsed, context);
                case "call":
                    return await CallCommand.Run(parsed, context);
                case "verify":
                    return await VerifyCommand.Run(parsed, context);
                case "health":
                    return await HealthCommand.Run(parsed, context);
                case "jobs":
                    return await JobsCommand.Run(parsed, context);
                case "mcp-stdio":
                    return await McpStdioCommand.Run(parsed, context);
                case "setup":
                    return SetupCommand.Run(parsed, context);
                case "doctor":
                    return DoctorCommand.Run(parsed, context);
                case "uninstall":
                    return UninstallCommand.Run(parsed, context);
                case "upgrade":
                    return await UpgradeCommand.Run(parsed, context);
            }

            context.Err.WriteLine($"Unknown command '{parsed.Command}'.");
            context.Err.WriteLine();
            context.Err.WriteLine(Usage.Text);
            return 1;
        }
        catch (CliException e)
        {
            context.Err.WriteLine(e.Message);
            return e.ExitCode;
        }
        catch (UnityError e)
        {
            context.ReportError(e.Code, e.Message);
            return 1;
        }
        catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested)
        {
            context.Err.WriteLine("interrupted");
            return 130;
        }
    }

    public static string Version()
    {
        var informational = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrEmpty(informational))
        {
            return typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        var plus = informational.IndexOf('+');
        return plus > 0 ? informational.Substring(0, plus) : informational;
    }
}
