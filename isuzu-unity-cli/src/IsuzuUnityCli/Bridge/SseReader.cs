using System.Runtime.CompilerServices;
using System.Text;

namespace IsuzuUnityCli.Bridge;

/// <summary>
/// Pulls the payloads out of a <c>text/event-stream</c> body.
/// Only <c>data:</c> is forwarded: an MCP reply arrives as one event whose data is the JSON-RPC
/// message, and the other fields carry framing the stdio side has no use for.
/// </summary>
public static class SseReader
{
    public static async IAsyncEnumerable<string> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        var data = new List<string>();

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellation);

            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (data.Count > 0)
                {
                    yield return string.Join('\n', data);
                    data.Clear();
                }

                continue;
            }

            // A line starting with a colon is a comment, and heartbeats are sent that way.
            if (line[0] == ':')
            {
                continue;
            }

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line.Substring(0, colon);

            if (field != "data")
            {
                continue;
            }

            var value = colon < 0 ? "" : line.Substring(colon + 1);

            // One optional space after the colon belongs to the framing, not the payload.
            data.Add(value.StartsWith(' ') ? value.Substring(1) : value);
        }

        // A stream that ends without its blank terminator still carries a complete message.
        if (data.Count > 0)
        {
            yield return string.Join('\n', data);
        }
    }
}
