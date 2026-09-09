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

        if (carrier is null
            || carrier[Key] is not JsonValue value
            || !value.TryGetValue<string>(out var encoded)
            || !encoded.StartsWith(PngPrefix, StringComparison.Ordinal))
        {
            return null;
        }

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

    /// <summary>
    /// The object holding the image. A job's detail nests the tool's answer under
    /// <c>result</c>, so the image sits one level down there.
    /// </summary>
    private static JsonObject? Carrier(JsonNode? result)
    {
        if (result is not JsonObject body)
        {
            return null;
        }

        return body[Key] is null && body["result"] is JsonObject nested ? nested : body;
    }
}
