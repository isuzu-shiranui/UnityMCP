using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IsuzuUnityCli.Cli;

public static class JsonOutput
{
    // The default encoder escapes every non-ASCII character, which turns Japanese tool output into \uXXXX noise.
    private static readonly JsonWriterOptions Pretty = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Indentation is most of a large response: a 523-object scene prints as 346 KB indented and
    // 124 KB packed, and whatever reads it pays for the whitespace.
    private static readonly JsonWriterOptions Packed = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Format(JsonNode? node, bool indented)
    {
        if (node is null)
        {
            return "null";
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, indented ? Pretty : Packed))
        {
            node.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static void Print(TextWriter output, JsonNode? node, bool indented)
    {
        output.WriteLine(Format(node, indented));
    }
}
