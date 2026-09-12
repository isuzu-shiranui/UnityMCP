using System.Text.Json.Nodes;
using IsuzuUnityCli.Cli;

namespace IsuzuUnityCli.Commands;

public static class ToolsCommand
{
    public static async Task<int> Run(ParsedArgs parsed, CommandContext context)
    {
        if (parsed.Positional.Count > 1)
            throw new CliException("tools accepts at most one exact tool name.", 2);
        var name = parsed.Positional.SingleOrDefault();
        var raw = parsed.HasFlag("raw");
        var instance = context.ResolveInstance(parsed);
        var envelope = await context.Client.GetAsync(instance, CatalogPath(parsed.Option("group")), context.Cancellation);

        if (envelope.IsError)
        {
            return context.Report(envelope, raw);
        }

        if (name is not null)
        {
            // Indexed through JsonObject: the string indexer on an array or a scalar throws, and
            // a reply of another shape would leave the process on an undocumented exit code.
            var tool = ((envelope.Result as JsonObject)?["tools"] as JsonArray)?
                .OfType<JsonObject>().FirstOrDefault(candidate => candidate["name"]?.ToString() == name);
            if (tool is null)
                throw new CliException($"No tool named '{name}' in this catalog. Run tools (with the same --group, if set) to list available names.", 2);
            // Keep the complete description, schema and annotations. Filtering reduces output,
            // not the contract an agent needs to call the tool correctly. --raw is identical here.
            JsonOutput.Print(context.Out, tool, context.Indented);
            return 0;
        }

        if (raw)
            return context.Report(envelope, raw);

        context.Out.Write(Render(envelope.Result));
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

    /// <summary>One line per tool with required parameters in angle brackets, then the description indented.</summary>
    public static string Render(JsonNode? result)
    {
        var text = new System.Text.StringBuilder();

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

            var schema = obj["inputSchema"] as JsonObject;
            var required = new HashSet<string>(StringComparer.Ordinal);

            if (schema?["required"] is JsonArray requiredNames)
            {
                foreach (var name in requiredNames)
                {
                    if (name is JsonValue value && value.TryGetValue<string>(out var s))
                    {
                        required.Add(s);
                    }
                }
            }

            var rendered = new List<string>();

            if (schema?["properties"] is JsonObject properties)
            {
                foreach (var pair in properties)
                {
                    rendered.Add(required.Contains(pair.Key) ? $"<{pair.Key}>" : $"[{pair.Key}]");
                }
            }

            var name2 = obj["name"]?.ToString() ?? "";
            text.Append(name2);

            if (rendered.Count > 0)
            {
                text.Append(' ').Append(string.Join(' ', rendered));
            }

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
}
