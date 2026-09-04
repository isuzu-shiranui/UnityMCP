using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IsuzuUnityCli.Agents;

/// <summary>
/// Edits an agent's JSON config in place.
/// These files hold every MCP server the user has registered and, in Claude Code's case, the
/// history and settings of every project they have opened. Adding one entry must leave all of
/// that exactly as it was.
/// </summary>
public static class JsonConfigEditor
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Parses a config, treating an absent or blank file as an empty object.</summary>
    public static JsonObject Parse(string? content)
    {
        if (content is null)
        {
            return new JsonObject();
        }

        // A BOM would make the parser reject the first character; it is dropped rather than
        // written back, because none of these agents write one.
        var text = content.TrimStart('﻿');

        if (text.Trim().Length == 0)
        {
            return new JsonObject();
        }

        return JsonNode.Parse(text) as JsonObject
            ?? throw new JsonException("the config is not a JSON object");
    }

    public static JsonObject Read(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (FileNotFoundException)
        {
            return new JsonObject();
        }
        catch (DirectoryNotFoundException)
        {
            return new JsonObject();
        }
    }

    /// <summary>Sets a value at a nested path, creating the objects along the way.</summary>
    public static void Upsert(JsonObject root, IReadOnlyList<string> path, JsonNode value)
    {
        var node = Descend(root, path, create: true)!;
        node[path[^1]] = value;
    }

    public static JsonNode? Find(JsonObject root, IReadOnlyList<string> path)
    {
        var node = Descend(root, path, create: false);
        return node?[path[^1]];
    }

    /// <summary>Returns false when there was nothing there to remove.</summary>
    public static bool Remove(JsonObject root, IReadOnlyList<string> path)
    {
        var node = Descend(root, path, create: false);

        if (node is null || !node.ContainsKey(path[^1]))
        {
            return false;
        }

        node.Remove(path[^1]);
        return true;
    }

    public static string Serialize(JsonObject root)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            root.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    public static void Write(string path, JsonObject root)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, Serialize(root), new UTF8Encoding(false));
        RestrictToOwner(path);
    }

    /// <summary>
    /// These files carry a bearer token for a server that runs arbitrary code in the Editor,
    /// so on Unix they are readable only by their owner.
    /// </summary>
    public static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static JsonObject? Descend(JsonObject root, IReadOnlyList<string> path, bool create)
    {
        if (path.Count == 0)
        {
            throw new ArgumentException("a config path needs at least one segment", nameof(path));
        }

        var node = root;

        for (var i = 0; i < path.Count - 1; i++)
        {
            if (node[path[i]] is JsonObject child)
            {
                node = child;
                continue;
            }

            if (!create)
            {
                return null;
            }

            var created = new JsonObject();
            node[path[i]] = created;
            node = created;
        }

        return node;
    }
}
