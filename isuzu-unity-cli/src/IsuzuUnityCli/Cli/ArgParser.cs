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
    /// <summary>Options the CLI consumes itself; they are never forwarded to a tool.</summary>
    public static readonly IReadOnlySet<string> CliOnlyOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "json", "project", "file", "raw", "help", "agent", "client", "yes", "no-skill", "mcp", "scope", "fix", "version",
        "group",
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

            if (next is null || next.StartsWith("--", StringComparison.Ordinal))
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
