using System.Text;
using System.Text.Json.Nodes;
using IsuzuUnityCli.Cli;

namespace IsuzuUnityCli.Commands;

public static class ToolsCommand
{
    /// <summary>Lines a search prints. Past this the terms are too loose to pick a tool from.</summary>
    private const int SearchLimit = 10;

    public static async Task<int> Run(ParsedArgs parsed, CommandContext context)
    {
        var raw = parsed.HasFlag("raw");
        var search = parsed.Option("search");

        // PowerShell hands '--search play mode' over as three words, and the second would read as
        // a tool name that does not exist.
        var terms = search is null
            ? new List<string>()
            : new[] { search }.Concat(parsed.Positional).SelectMany(Words).ToList();
        var names = search is null ? parsed.Positional.ToList() : new List<string>();

        var instance = context.ResolveInstance(parsed);
        var envelope = await context.Client.GetAsync(instance, CatalogPath(parsed.Option("group")), context.Cancellation);

        if (envelope.IsError)
        {
            return context.Report(envelope, raw);
        }

        var tools = ((envelope.Result as JsonObject)?["tools"] as JsonArray)?.OfType<JsonObject>().ToList()
            ?? new List<JsonObject>();

        if (search is not null)
        {
            context.Out.Write(Search(tools, terms));
            return 0;
        }

        if (names.Count > 0)
        {
            var chosen = new List<JsonObject>();

            foreach (var name in names)
            {
                var tool = tools.FirstOrDefault(candidate => candidate["name"]?.ToString() == name);

                if (tool is null)
                {
                    throw new CliException(Missing(name, tools), 2);
                }

                chosen.Add(tool);
            }

            if (raw)
            {
                JsonNode printed = chosen.Count == 1
                    ? chosen[0].DeepClone()
                    : new JsonArray(chosen.Select(tool => (JsonNode?)tool.DeepClone()).ToArray());
                JsonOutput.Print(context.Out, printed, context.Indented);
            }
            else
            {
                context.Out.Write(string.Concat(chosen.Select(Signature)));
            }

            return 0;
        }

        if (raw)
            return context.Report(envelope, raw);

        context.Out.Write(parsed.HasFlag("long") ? Render(envelope.Result) : Names(tools));
        return 0;
    }

    /// <summary>The catalog path, limited to the given comma-separated groups when there are any.</summary>
    public static string CatalogPath(string? group)
    {
        // The separating commas stay literal: the Editor splits the value on them.
        return string.IsNullOrWhiteSpace(group)
            ? "/tools"
            : "/tools?group=" + string.Join(',', group.Split(',').Select(Uri.EscapeDataString));
    }

    /// <summary>Tool names, one line per group, and how to learn more about one.</summary>
    /// <remarks>
    /// The whole catalog with descriptions is past what an agent's shell tool shows in one
    /// reply, so it arrived cut off and was fetched again piece by piece through a filter.
    /// </remarks>
    public static string Names(IReadOnlyList<JsonObject> tools)
    {
        var text = new StringBuilder();

        foreach (var group in tools.GroupBy(tool => tool["group"]?.ToString() ?? ""))
        {
            var label = group.Key.Length == 0 ? "other" : group.Key;
            text.Append(label).Append(": ")
                .Append(string.Join(' ', group.Select(tool => tool["name"]?.ToString() ?? "")))
                .Append('\n');
        }

        text.Append("tools <name> [<name>...] shows arguments; tools --search <words> finds a tool.\n");
        return text.ToString();
    }

    /// <summary>One tool's description and each argument's name, type, default and meaning.</summary>
    public static string Signature(JsonObject tool)
    {
        var text = new StringBuilder();
        text.Append(tool["name"]?.ToString() ?? "");

        var group = tool["group"]?.ToString();

        if (!string.IsNullOrEmpty(group))
        {
            text.Append("  [").Append(group).Append(']');
        }

        var annotations = tool["annotations"] as JsonObject;

        if (Hint(annotations, "readOnlyHint"))
        {
            text.Append("  read-only");
        }
        else if (Hint(annotations, "destructiveHint"))
        {
            text.Append("  destructive");
        }

        text.Append('\n');
        text.Append("  ").Append(tool["description"]?.ToString() ?? "").Append('\n');

        var schema = tool["inputSchema"] as JsonObject;
        var required = Required(schema);

        if (schema?["properties"] is JsonObject properties)
        {
            foreach (var pair in properties)
            {
                var property = pair.Value as JsonObject;
                text.Append("  --").Append(pair.Key).Append(' ').Append(TypeOf(property));

                if (required.Contains(pair.Key))
                {
                    text.Append(", required");
                }

                if (property?["default"] is { } value)
                {
                    text.Append(", default ").Append(value.ToJsonString());
                }

                var meaning = property?["description"]?.ToString();

                if (!string.IsNullOrEmpty(meaning))
                {
                    text.Append(": ").Append(meaning);
                }

                text.Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>The tools whose names, descriptions or argument names hold the terms, best first.</summary>
    public static string Search(IReadOnlyList<JsonObject> tools, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
        {
            throw new CliException("tools --search needs at least one word, e.g. tools --search play mode", 2);
        }

        var ranked = tools
            .Select(tool => (tool, score: Score(tool, terms)))
            .Where(pair => pair.score > 0)
            .OrderByDescending(pair => pair.score)
            .ThenBy(pair => pair.tool["name"]?.ToString(), StringComparer.Ordinal)
            .Take(SearchLimit)
            .ToList();

        if (ranked.Count == 0)
        {
            return $"No tool mentions {string.Join(' ', terms)}. tools lists every name.\n";
        }

        var text = new StringBuilder();

        foreach (var (tool, _) in ranked)
        {
            text.Append(OneLine(tool)).Append("  ").Append(FirstSentence(tool["description"]?.ToString() ?? "")).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>One line per tool with required parameters in angle brackets, then the description indented.</summary>
    public static string Render(JsonNode? result)
    {
        var text = new StringBuilder();

        if ((result as JsonObject)?["tools"] is not JsonArray tools)
        {
            return "";
        }

        foreach (var tool in tools)
        {
            if (tool is not JsonObject obj)
            {
                continue;
            }

            text.Append(OneLine(obj));

            var group = obj["group"]?.ToString();

            if (!string.IsNullOrEmpty(group))
            {
                text.Append("  [").Append(group).Append(']');
            }

            text.Append('\n');
            text.Append("    ").Append(obj["description"]?.ToString() ?? "").Append('\n');
        }

        return text.ToString();
    }

    private static string OneLine(JsonObject tool)
    {
        var schema = tool["inputSchema"] as JsonObject;
        var required = Required(schema);
        var rendered = new List<string>();

        if (schema?["properties"] is JsonObject properties)
        {
            foreach (var pair in properties)
            {
                rendered.Add(required.Contains(pair.Key) ? $"<{pair.Key}>" : $"[{pair.Key}]");
            }
        }

        var name = tool["name"]?.ToString() ?? "";
        return rendered.Count > 0 ? name + " " + string.Join(' ', rendered) : name;
    }

    private static HashSet<string> Required(JsonObject? schema)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);

        if (schema?["required"] is JsonArray names)
        {
            foreach (var name in names)
            {
                if (name is JsonValue value && value.TryGetValue<string>(out var s))
                {
                    required.Add(s);
                }
            }
        }

        return required;
    }

    private static string TypeOf(JsonObject? property)
    {
        if (property is null)
        {
            return "any";
        }

        if (property["enum"] is JsonArray choices)
        {
            return string.Join('|', choices.Select(choice =>
                choice is JsonValue v && v.TryGetValue<string>(out var s) ? s : choice?.ToJsonString()));
        }

        var type = TypeName(property["type"]);
        return type == "array" ? TypeName((property["items"] as JsonObject)?["type"]) + "[]" : type;
    }

    private static string TypeName(JsonNode? type) => type switch
    {
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        JsonArray several => string.Join('|', several.Select(t => t?.ToString()).Where(t => t != "null")),
        _ => "any",
    };

    private static bool Hint(JsonObject? annotations, string name) =>
        annotations?[name] is JsonValue value && value.TryGetValue<bool>(out var on) && on;

    private static int Score(JsonObject tool, IReadOnlyList<string> terms)
    {
        var name = tool["name"]?.ToString() ?? "";
        var description = tool["description"]?.ToString() ?? "";
        var arguments = ((tool["inputSchema"] as JsonObject)?["properties"] as JsonObject)?.Select(pair => pair.Key).ToList()
            ?? new List<string>();
        var score = 0;

        foreach (var term in terms)
        {
            if (name.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }

            if (arguments.Any(argument => argument.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                score += 3;
            }

            if (description.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 1;
            }
        }

        return score;
    }

    private static IEnumerable<string> Words(string text) =>
        text.Split(new[] { ' ', ',', '_' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string FirstSentence(string description)
    {
        var end = description.IndexOf(". ", StringComparison.Ordinal);
        var sentence = end > 0 ? description.Substring(0, end + 1) : description;
        return sentence.Length > 160 ? sentence.Substring(0, 157) + "..." : sentence;
    }

    private static string Missing(string name, IReadOnlyList<JsonObject> tools)
    {
        var near = tools
            .Select(tool => tool["name"]?.ToString() ?? "")
            .Where(candidate => candidate.Contains(name, StringComparison.OrdinalIgnoreCase)
                || name.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .ToList();

        return near.Count > 0
            ? $"No tool named '{name}'. Did you mean: {string.Join(", ", near)}?"
            : $"No tool named '{name}' in this catalog. tools --search <words> finds one by what it does.";
    }
}
