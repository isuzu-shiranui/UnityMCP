using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IsuzuUnityCli.Cli;

/// <summary>
/// Assembles a tool's arguments from <c>--args-file</c>, <c>--json</c>, individual
/// <c>--name value</c> pairs, bare flags, and <c>--file</c>, later sources overriding earlier ones.
/// </summary>
public static class ToolArguments
{
    public static JsonObject Build(string tool, ParsedArgs parsed, Func<string, string>? readFile = null)
    {
        readFile ??= path => File.ReadAllText(path, Encoding.UTF8);
        var args = new JsonObject();

        // A file is the one spelling of structured arguments that no shell rewrites: Windows
        // PowerShell strips the double quotes out of an argument, and what is left cannot say
        // whether "true" was a string.
        var argsFile = parsed.Option("args-file");
        if (argsFile is not null)
        {
            string text;

            try
            {
                text = readFile(argsFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new CliException($"--args-file could not be read: {e.Message}", 2);
            }

            Merge(args, ParseObject(text.TrimStart('﻿'), "--args-file"));
        }

        var json = parsed.Option("json");
        if (json is not null)
        {
            Merge(args, ParseObject(json, "--json"));
        }

        foreach (var pair in parsed.Options)
        {
            if (ArgParser.CallReservedOptions.Contains(pair.Key))
            {
                continue;
            }

            // An option named more than once is a list. Quoting a JSON array through a shell that
            // strips quotes is the thing this avoids, and dropping all but the last was silent.
            if (parsed.Repeats.TryGetValue(pair.Key, out var given) && given.Count > 1)
            {
                var many = new JsonArray();

                foreach (var value in given)
                {
                    many.Add(ScalarCoercion.ToJsonNode(value));
                }

                Set(args, pair.Key, many);
                continue;
            }

            Set(args, pair.Key, ScalarCoercion.ToJsonNode(pair.Value));
        }

        foreach (var flag in parsed.Flags)
        {
            // A name given both ways is a value that also appeared bare, as a trailing
            // '--type' does. Taking the flag would drop what was typed before it.
            if (!ArgParser.CallReservedOptions.Contains(flag) && !args.ContainsKey(flag))
            {
                Set(args, flag, JsonValue.Create(true));
            }
        }

        var file = parsed.Option("file");
        if (file is not null)
        {
            string source;

            try
            {
                source = readFile(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new CliException($"--file could not be read: {e.Message}", 2);
            }

            // Base64 keeps backslashes in C# string literals intact across the shell and JSON layers.
            if (tool == "execute_code")
            {
                args["code_base64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(source));
            }
            else
            {
                args["code"] = source;
            }
        }

        return args;
    }

    private static JsonObject ParseObject(string text, string option)
    {
        JsonNode? decoded;

        try
        {
            decoded = JsonNode.Parse(text);
        }
        catch (JsonException e)
        {
            throw new CliException($"{option} is not valid JSON: {e.Message}");
        }

        return decoded as JsonObject ?? throw new CliException($"{option} must be a JSON object.");
    }

    private static void Merge(JsonObject into, JsonObject from)
    {
        foreach (var pair in from.ToList())
        {
            into[pair.Key] = pair.Value?.DeepClone();
        }
    }

    /// <summary>Sets an argument, reading <c>a.b</c> as the field <c>b</c> of the object argument <c>a</c>.</summary>
    /// <remarks>
    /// Only the first dot nests, because serialized property paths carry dots of their own:
    /// <c>--values.m_LocalPosition.x 2</c> is the key <c>m_LocalPosition.x</c> inside <c>values</c>.
    /// </remarks>
    private static void Set(JsonObject args, string name, JsonNode value)
    {
        var dot = name.IndexOf('.');

        if (dot <= 0 || dot == name.Length - 1)
        {
            args[name] = value;
            return;
        }

        var parent = name.Substring(0, dot);

        if (args[parent] is null)
        {
            args[parent] = new JsonObject();
        }

        if (args[parent] is not JsonObject fields)
        {
            throw new CliException(
                $"--{name} sets a field of '{parent}', but '{parent}' was also given as a value that is not an object.", 2);
        }

        fields[name.Substring(dot + 1)] = value;
    }
}
