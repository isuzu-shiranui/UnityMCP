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

        // A terminal gets indentation because a person is reading it; a pipe or a redirect,
        // which is how an agent reads it, gets the packed form. --compact forces packed anywhere.
        var indented = !Console.IsOutputRedirected
            && !ArgParser.Parse(argv).HasFlag("compact");

        var context = new CommandContext { Cancellation = cts.Token, Indented = indented };
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
            RefuseMissingValues(parsed);
            // 'call' forwards what it does not recognise to the tool, which refuses what it does
            // not declare. Every other command consumes its options itself and had nowhere to put
            // a misspelling, so it dropped it: 'setup --agnet codex' reads as no --agent at all,
            // and --agent defaults to every installed agent — writing to configs the caller never
            // named, under a reply that reads like success.
            if (parsed.Command != "call")
            {
                RefuseUnknownOptions(parsed);
            }

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

    /// <summary>What each command accepts, beyond the three every command takes.</summary>
    /// <remarks>
    /// CliOnlyOptions cannot stand in for this: it says which options are not forwarded to a
    /// tool, which is a different question and leaves out verify's own eight. Listing them per
    /// command is also what makes the refusal useful — telling someone who mistyped a doctor
    /// option that --group exists sends them somewhere doctor does not go.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string[]> OptionsPerCommand =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["projects"] = new[] { "raw" },
            ["tools"] = new[] { "group", "project", "raw" },
            ["verify"] = new[]
            {
                "project", "no-compile", "test", "test-mode", "assembly", "filter", "category",
                "timeout", "logs", "raw",
            },
            ["health"] = new[] { "project", "raw" },
            ["jobs"] = new[] { "project", "raw" },
            ["mcp-stdio"] = new[] { "project", "group" },
            ["setup"] = new[] { "agent", "client", "mcp", "scope", "no-skill", "project" },
            ["doctor"] = new[] { "fix" },
            ["uninstall"] = new[] { "yes", "no-skill" },
            ["upgrade"] = new[] { "release" },
        };

    /// <summary>Options every command takes.</summary>
    private static readonly string[] AlwaysAccepted = { "help", "version", "compact" };

    private static void RefuseMissingValues(ParsedArgs parsed)
    {
        var required = parsed.Command == "call"
            ? new[] { "project", "json", "file" }
            : parsed.Command switch
            {
                "tools" => new[] { "project", "group" },
                "verify" => new[] { "project", "test-mode", "assembly", "filter", "category", "timeout", "logs" },
                "health" or "jobs" => new[] { "project" },
                "mcp-stdio" => new[] { "project", "group" },
                "setup" => new[] { "agent", "client", "scope", "project" },
                "upgrade" => new[] { "release" },
                _ => Array.Empty<string>(),
            };
        var missing = required.Where(parsed.HasFlag).ToArray();
        if (missing.Length > 0)
            throw new CliException("An option value is required for " + string.Join(", ", missing.Select(name => "--" + name)) + ".", 2);
    }

    /// <summary>Refuses an option the named command does not have.</summary>
    /// <remarks>
    /// The parser keeps every <c>--name</c> it sees so that 'call' can hand the ones it does not
    /// own to the tool. For the other commands there is no such destination, and a dropped option
    /// is worse than a refused one: 'setup --agnet codex' reads as no --agent at all, and --agent
    /// defaults to every installed agent — so it writes to configs the caller never named, and
    /// says nothing about having done so.
    /// </remarks>
    private static void RefuseUnknownOptions(ParsedArgs parsed)
    {
        if (!OptionsPerCommand.TryGetValue(parsed.Command, out var accepted))
        {
            return;
        }

        var known = new HashSet<string>(accepted, StringComparer.Ordinal);
        known.UnionWith(AlwaysAccepted);

        var unknown = parsed.Options.Keys
            .Concat(parsed.Flags)
            .Where(name => !known.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (unknown.Count == 0)
        {
            return;
        }

        var takes = accepted.Length == 0
            ? "no options of its own"
            : string.Join(", ", accepted.OrderBy(n => n, StringComparer.Ordinal).Select(n => "--" + n));

        throw new CliException(
            $"Unknown option{(unknown.Count > 1 ? "s" : string.Empty)} "
            + string.Join(", ", unknown.Select(name => "--" + name))
            + $" for '{parsed.Command}'. It takes {takes}.",
            2);
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
