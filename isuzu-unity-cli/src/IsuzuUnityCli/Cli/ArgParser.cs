using System.Collections.Generic;

namespace IsuzuUnityCli.Cli;

public sealed class ParsedArgs
{
    public string Command { get; init; } = "";
    public List<string> Positional { get; } = new();
    /// <summary>Insertion order is kept because it decides which value wins when tool arguments are merged.</summary>
    public OrderedDictionary<string, string> Options { get; } = new(StringComparer.Ordinal);
    public List<string> Flags { get; } = new();

    public bool HasFlag(string name) => Flags.Contains(name, StringComparer.Ordinal);

    public string? Option(string name) => Options.TryGetValue(name, out var value) ? value : null;
}

public static class ArgParser
{
    /// <summary>
    /// Options a tool call consumes itself. Everything else on the command line is an argument
    /// for the tool.
    /// </summary>
    /// <remarks>
    /// Narrower than <see cref="CliOnlyOptions"/> on purpose. That set covers every command, so
    /// a name another command owns used to swallow a tool argument spelled the same way:
    /// 'setup --scope' meant 'call --scope assets' reached the tool as nothing, and the tool ran
    /// its default and answered 'scope: scene' as a success. A silently different answer is
    /// worse than a refusal.
    /// </remarks>
    public static readonly IReadOnlySet<string> CallReservedOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "json", "project", "file", "raw", "help", "version", "compact",
    };

    /// <summary>Options the CLI consumes itself; they are never forwarded to a tool.</summary>
    public static readonly IReadOnlySet<string> CliOnlyOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "json", "project", "file", "raw", "help", "agent", "client", "yes", "no-skill", "mcp", "scope", "fix", "version",
        "group", "compact", "release",
    };

    /// <summary>
    /// Options that are on or off. Without this the parser takes whatever follows as the value,
    /// so <c>--compact projects</c> loses the command and <c>call --compact scene_browse_hierarchy</c>
    /// loses the tool name, and neither reads as set.
    /// </summary>
    private static readonly IReadOnlySet<string> ValuelessOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "compact", "raw", "help", "version", "yes", "no-skill", "fix",
    };

    public static ParsedArgs Parse(IReadOnlyList<string> argv)
    {
        var positional = new List<string>();
        var options = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        var flags = new List<string>();

        for (var i = 0; i < argv.Count; i++)
        {
            var token = argv[i];

            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                if (token == "-h")
                {
                    AddFlag(flags, "help");
                    continue;
                }

                positional.Add(token);
                continue;
            }

            var name = token.Substring(2);
            var next = i + 1 < argv.Count ? argv[i + 1] : null;

            if (next is null || next.StartsWith("--", StringComparison.Ordinal)
                || ValuelessOptions.Contains(name))
            {
                AddFlag(flags, name);
            }
            else
            {
                options[name] = next;
                i++;
            }
        }

        var result = new ParsedArgs { Command = positional.Count > 0 ? positional[0] : "" };
        result.Positional.AddRange(positional.Skip(1));

        foreach (var pair in options)
        {
            result.Options[pair.Key] = pair.Value;
        }

        result.Flags.AddRange(flags);
        return result;
    }

    private static void AddFlag(List<string> flags, string name)
    {
        if (!flags.Contains(name, StringComparer.Ordinal))
        {
            flags.Add(name);
        }
    }
}
