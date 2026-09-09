using System.Text.Json.Nodes;

namespace IsuzuUnityCli.Cli;

/// <summary>
/// Moves a base64 image out of a reply and onto disk before the reply is printed.
/// </summary>
/// <remarks>
/// An MCP client turns that base64 into an image block, which a model is charged for by the
/// picture's dimensions: a 825x845 screenshot costs about 940 tokens. Printed to stdout it is
/// just a very long string, and whatever reads the terminal pays for it as text — 89,000 tokens
/// for the same screenshot, ninety-five times the price for the same picture. The tool cannot
/// tell which way its answer will travel, so the CLI decides here, on the way out.
/// </remarks>
public static class InlineImage
{
    /// <summary>Base64 of the eight-byte PNG signature. The ninth character varies with byte 9.</summary>
    private const string PngPrefix = "iVBORw0KGg";

    private const string Key = "image";

    /// <summary>
    /// Writes an inline PNG to <paramref name="directory"/> and replaces it with the path.
    /// Returns the file written, or null when the reply carried no image.
    /// </summary>
    public static string? Externalise(JsonNode? result, string directory)
    {
        var carrier = Carrier(result);

        if (carrier is null)
        {
            return null;
        }

        var encoded = carrier[Key]!.GetValue<string>();
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return null;
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        File.WriteAllBytes(path, bytes);

        carrier.Remove(Key);
        carrier["path"] = path;
        carrier["note"] = "Written to disk rather than printed: inline the picture would be "
                        + "tens of thousands of tokens of base64. Read this file to look at it.";

        return path;
    }

    /// <summary>The object holding an <c>image</c> that is base64 of a PNG, or null.</summary>
    /// <remarks>
    /// Searched for rather than looked up in a fixed place. A capture answered inline carries the
    /// PNG at the top, a job's detail nests it under <c>result</c>, and input_replay puts it under
    /// <c>capture</c>. Each position that was hardcoded here printed the next tool's base64.
    /// </remarks>
    private static JsonObject? Carrier(JsonNode? node)
    {
        if (node is JsonObject body)
        {
            if (body[Key] is JsonValue value
                && value.TryGetValue<string>(out var encoded)
                && encoded.StartsWith(PngPrefix, StringComparison.Ordinal))
            {
                return body;
            }

            foreach (var property in body)
            {
                var found = Carrier(property.Value);

                if (found is not null)
                {
                    return found;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var found = Carrier(item);

                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }
}
