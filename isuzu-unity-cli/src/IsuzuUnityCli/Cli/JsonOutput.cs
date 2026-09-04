using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IsuzuUnityCli.Cli;

public static class JsonOutput
{
    // The default encoder escapes every non-ASCII character, which turns Japanese tool output into \uXXXX noise.
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Format(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            node.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static void Print(TextWriter output, JsonNode? node)
    {
        output.WriteLine(Format(node));
    }
}
