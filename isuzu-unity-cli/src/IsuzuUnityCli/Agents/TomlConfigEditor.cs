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
        var name = ParseName(tableName);
        var table = document.Tables.FirstOrDefault(t => Segments(t.Name).SequenceEqual(name));

        if (table is null)
        {
            return null;
        }

        string? url = null;
        string? authorization = null;

        foreach (var item in table.Items)
        {
            if (IsKey(item.Key, "url") && item.Value is StringValueSyntax value)
            {
                url = value.Value;
            }
            else if (IsKey(item.Key, "http_headers") && item.Value is InlineTableSyntax headers)
            {
                foreach (var header in headers.Items)
                {
                    if (IsKey(header.KeyValue?.Key, "Authorization")
                        && header.KeyValue!.Value is StringValueSyntax headerValue)
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
        var name = ParseName(tableName);
        var table = document.Tables.FirstOrDefault(t => Segments(t.Name).SequenceEqual(name));
        var item = table?.Items.FirstOrDefault(candidate => IsKey(candidate.Key, key));

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
        var name = ParseName(tableName);
        var doomed = document.Tables
            .Where(table => Segments(table.Name).Take(name.Length).SequenceEqual(name))
            .ToList();

        foreach (var table in doomed)
        {
            document.Tables.RemoveChild(table);
        }

        return doomed.Count;
    }

    private static string[] ParseName(string tableName)
    {
        var table = ParseOrThrow($"[{tableName}]\n", "table-name.toml").Tables.FirstOrDefault();

        if (table is null)
        {
            throw new TomlEditException($"'{tableName}' is not a table name.");
        }

        return Segments(table.Name).ToArray();
    }

    /// <summary>A key written bare, in double quotes or in single quotes is the same key.</summary>
    private static bool IsKey(KeySyntax? key, string name) =>
        Segments(key).SequenceEqual(new[] { name }, StringComparer.Ordinal);

    // Compare decoded segments: quotes, escapes and whitespace do not change a TOML key,
    // whereas a dot inside a quoted segment is part of that key, not a sub-table separator.
    private static IEnumerable<string> Segments(KeySyntax? key)
    {
        if (key is null) yield break;
        yield return KeyValue(key.Key);
        foreach (var part in key.DotKeys) yield return KeyValue(part.Key);
    }

    private static string KeyValue(BareKeyOrStringValueSyntax? key) => key switch
    {
        BareKeySyntax bare => bare.Key?.Text ?? "",
        StringValueSyntax quoted => quoted.Value ?? "",
        _ => ""
    };

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
