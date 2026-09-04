using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace IsuzuUnityCli.Agents;

/// <summary>An MCP server entry read back out of a TOML config.</summary>
public sealed record TomlServerEntry(string? Url, string? Authorization);

/// <summary>
/// Edits one <c>[mcp_servers.&lt;name&gt;]</c> table of a TOML config and leaves the rest alone.
/// Codex's config holds project trust settings, plugin state and machine-generated paths, so
/// the edit goes through the syntax tree: unmodified nodes keep their original text, comments
/// and spacing included, which a parse-and-re-emit through a data model would destroy.
/// </summary>
public static class TomlConfigEditor
{
    public static string Upsert(string content, string tableName, string body)
    {
        var document = ParseOrThrow(content, "config.toml");
        RemoveTables(document, tableName);

        var current = document.ToString();
        var separator = current.Length == 0 || current.EndsWith("\n\n", StringComparison.Ordinal)
            ? ""
            : current.EndsWith('\n') ? "\n" : "\n\n";

        var block = ParseOrThrow($"{separator}[{tableName}]\n{body}", "entry.toml");
        var table = block.Tables.FirstOrDefault()
            ?? throw new TomlEditException("the generated entry did not parse as a table");

        // Detached first: a node still owned by the block document cannot be adopted.
        block.Tables.RemoveChild(table);
        document.Tables.Add(table);

        return document.ToString();
    }

    /// <summary>Returns null when the table was not there.</summary>
    public static string? Remove(string content, string tableName)
    {
        var document = ParseOrThrow(content, "config.toml");

        return RemoveTables(document, tableName) == 0 ? null : document.ToString();
    }

    /// <summary>Returns null when the table was not there.</summary>
    public static TomlServerEntry? Read(string content, string tableName)
    {
        var document = ParseOrThrow(content, "config.toml");
        var table = document.Tables.FirstOrDefault(t => NameOf(t) == tableName);

        if (table is null)
        {
            return null;
        }

        string? url = null;
        string? authorization = null;

        foreach (var item in table.Items)
        {
            var key = item.Key?.ToString().Trim();

            if (key == "url" && item.Value is StringValueSyntax value)
            {
                url = value.Value;
            }
            else if (key == "http_headers" && item.Value is InlineTableSyntax headers)
            {
                foreach (var header in headers.Items)
                {
                    if (header.KeyValue?.Key?.ToString().Trim() == "Authorization"
                        && header.KeyValue.Value is StringValueSyntax headerValue)
                    {
                        authorization = headerValue.Value;
                    }
                }
            }
        }

        return new TomlServerEntry(url, authorization);
    }

    /// <summary>One string value out of a table, or null when either the table or the key is absent.</summary>
    public static string? ReadValue(string content, string tableName, string key)
    {
        var document = ParseOrThrow(content, "config.toml");
        var table = document.Tables.FirstOrDefault(t => NameOf(t) == tableName);
        var item = table?.Items.FirstOrDefault(candidate => candidate.Key?.ToString().Trim() == key);

        return item?.Value is StringValueSyntax value ? value.Value : null;
    }

    /// <summary>Escapes a value for a TOML basic string, so a Windows path survives intact.</summary>
    public static string Quote(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

        return $"\"{escaped}\"";
    }

    /// <summary>Removes the table and every sub-table belonging to it, such as an <c>.env</c> block.</summary>
    private static int RemoveTables(DocumentSyntax document, string tableName)
    {
        var doomed = document.Tables
            .Where(table => NameOf(table) == tableName || NameOf(table).StartsWith(tableName + ".", StringComparison.Ordinal))
            .ToList();

        foreach (var table in doomed)
        {
            document.Tables.RemoveChild(table);
        }

        return doomed.Count;
    }

    private static string NameOf(TableSyntaxBase table)
    {
        return table.Name?.ToString().Trim() ?? "";
    }

    private static DocumentSyntax ParseOrThrow(string content, string sourceName)
    {
        var document = SyntaxParser.Parse(content, sourceName, validate: true);

        if (document.HasErrors)
        {
            var first = document.Diagnostics.FirstOrDefault();
            throw new TomlEditException(first?.Message ?? "the file is not valid TOML");
        }

        return document;
    }
}

public sealed class TomlEditException : Exception
{
    public TomlEditException(string message) : base(message)
    {
    }
}
