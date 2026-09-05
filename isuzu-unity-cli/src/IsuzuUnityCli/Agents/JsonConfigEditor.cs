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

        var parsed = JsonNode.Parse(text) as JsonObject
            ?? throw new JsonException("the config is not a JSON object");

        try
        {
            // JSON permits duplicate keys and JsonNode accepts them, then throws when the
            // dictionary is built on the first lookup — somewhere deep in a caller rather than
            // here. Forcing it now turns that into a JsonException callers already handle.
            _ = parsed.Count;
        }
        catch (ArgumentException e)
        {
            throw new JsonException("the config has a duplicate key: " + e.Message, e);
        }

        return parsed;
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

        PruneEmptyAncestors(root, path);

        return true;
    }

    /// <summary>
    /// Drops the containers a removal has just emptied, innermost first.
    /// </summary>
    /// <remarks>
    /// Removing the only entry a project key held left the key behind holding an empty
    /// <c>mcpServers</c>, permanently, on every config that migrated. What is taken away is the
    /// residue this tool created; the agent's own top-level map stays whether or not it is empty,
    /// because absent and empty are not the same thing to the program that reads it.
    /// </remarks>
    private static void PruneEmptyAncestors(JsonObject root, IReadOnlyList<string> path)
    {
        var chain = new List<JsonObject> { root };
        var node = root;

        for (var i = 0; i < path.Count - 1; i++)
        {
            if (node[path[i]] is not JsonObject child)
            {
                return;
            }

            node = child;
            chain.Add(child);
        }

        // chain[i] is the object path[i] names, so its parent is chain[i - 1]. The walk stops at
        // 2 to leave the top-level map alone.
        for (var i = chain.Count - 1; i >= 2; i--)
        {
            if (chain[i].Count > 0)
            {
                return;
            }

            chain[i - 1].Remove(path[i - 1]);
        }
    }

    public static string Serialize(JsonObject root)
    {
        using var buffer = new MemoryStream();

        try
        {
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                root.WriteTo(writer);
            }
        }
        catch (InvalidOperationException e)
        {
            // A string holding a lone surrogate is legal JSON and cannot be encoded as UTF-8.
            // Callers handle a JsonException; an InvalidOperationException reaches them as a
            // stack trace and a dead process.
            throw new JsonException("the config holds text that cannot be written back: " + e.Message, e);
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>Writes the config without the old one ever ceasing to exist.</summary>
    /// <remarks>
    /// <c>File.WriteAllText</c> truncates in place: the file passes through every size from near
    /// zero on its way to the new content, in the location the agent reads from, with no second
    /// copy anywhere. Losing that window costs the whole file — for Claude Code that is the login,
    /// every project key, every tool grant and the prompt history, none of it reconstructable.
    /// Writing beside it and renaming over it means a reader sees either the old file or the new
    /// one, and the rename is atomic on Windows as well as on POSIX.
    /// <para>
    /// The previous content is kept as <c>.isuzu-bak</c> before the rename, for the failures a
    /// rename cannot cover: a config already damaged when it was read, or an edit that turns out
    /// to be wrong.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Replaces a config's text without the old one ever ceasing to exist. Used for the formats
    /// this class does not parse.
    /// </summary>
    public static void WriteText(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".isuzu-tmp";

        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        RestrictToOwner(temporary);

        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            try
            {
                File.Copy(path, path + ".isuzu-bak", overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A backup that cannot be taken is not a reason to refuse the write: the rename
                // below is what actually protects the file.
            }
        }

        File.Move(temporary, path, overwrite: true);
        RestrictToOwner(path);
    }

    public static void Write(string path, JsonObject root)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Serialised first, so a config that cannot be written back fails with the file untouched.
        WriteText(path, Serialize(root));
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
