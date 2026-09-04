using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IsuzuUnityCli.Cli;

/// <summary>Assembles a tool's arguments from <c>--json</c>, individual <c>--name value</c> pairs, bare flags, and <c>--file</c>.</summary>
public static class ToolArguments
{
    public static JsonObject Build(string tool, ParsedArgs parsed, Func<string, string>? readFile = null)
    {
        readFile ??= path => File.ReadAllText(path, Encoding.UTF8);
        var args = new JsonObject();

        var json = parsed.Option("json");
        if (json is not null)
        {
            JsonNode? decoded;

            try
            {
                decoded = JsonNode.Parse(json);
            }
            catch (JsonException e)
            {
                throw new CliException($"--json is not valid JSON: {e.Message}");
            }

            if (decoded is not JsonObject obj)
            {
                throw new CliException("--json must be a JSON object.");
            }

            foreach (var pair in obj.ToList())
            {
                args[pair.Key] = pair.Value?.DeepClone();
            }
        }

        foreach (var pair in parsed.Options)
        {
            if (!ArgParser.CliOnlyOptions.Contains(pair.Key))
            {
                args[pair.Key] = ScalarCoercion.ToJsonNode(pair.Value);
            }
        }

        foreach (var flag in parsed.Flags)
        {
            if (!ArgParser.CliOnlyOptions.Contains(flag))
            {
                args[flag] = true;
            }
        }

        var file = parsed.Option("file");
        if (file is not null)
        {
            var source = readFile(file);

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
}
