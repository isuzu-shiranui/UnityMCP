using System.Text.Json;

namespace IsuzuUnityCli.Discovery;

public static class DescriptorStore
{
    /// <summary>
    /// Reads every descriptor currently published. Half-written or stale files are skipped rather
    /// than reported: the Editor rewrites its descriptor on every start, and one left behind by a
    /// crash would otherwise register a phantom instance forever.
    /// </summary>
    public static List<InstanceDescriptor> ReadAll(IEnumerable<string>? directories = null, Func<int, bool>? isAlive = null)
    {
        directories ??= StatePaths.DescriptorDirectories();
        isAlive ??= ProcessLiveness.IsAlive;
        var found = new List<InstanceDescriptor>();

        foreach (var directory in directories)
        {
            string[] files;

            try
            {
                files = Directory.GetFiles(directory, "*.json");
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                InstanceDescriptor? parsed;

                try
                {
                    parsed = Parse(File.ReadAllBytes(file));
                }
                catch (JsonException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }

                if (parsed is not null && IsUsable(parsed) && isAlive(parsed.Pid))
                {
                    found.Add(parsed);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// A hand-rolled read of the few fields the descriptor has. Going through the serializer costs
    /// about twelve milliseconds of one-time set-up per process, which is a fifth of a whole call.
    /// </summary>
    public static InstanceDescriptor? Parse(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }

        var d = new InstanceDescriptor();

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read();

            switch (name)
            {
                case "projectPath":
                    d.ProjectPath = reader.GetString() ?? "";
                    break;
                case "projectName":
                    d.ProjectName = reader.GetString() ?? "";
                    break;
                case "unityVersion":
                    d.UnityVersion = reader.GetString() ?? "";
                    break;
                case "port":
                    d.Port = ReadInt(ref reader);
                    break;
                case "token":
                    d.Token = reader.GetString() ?? "";
                    break;
                case "pid":
                    d.Pid = ReadInt(ref reader);
                    break;
                case "protocolVersion":
                    d.ProtocolVersion = reader.GetString() ?? "";
                    break;
                case "endpoint":
                    d.Endpoint = reader.GetString() ?? "";
                    break;
                case "mcpUrl":
                    d.McpUrl = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                case "preferredPort":
                    d.PreferredPort = reader.TokenType == JsonTokenType.Null ? null : ReadInt(ref reader);
                    break;
                case "portMismatch":
                    d.PortMismatch = reader.TokenType switch
                    {
                        JsonTokenType.True => true,
                        JsonTokenType.False => false,
                        _ => null,
                    };
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return d;
    }

    private static int ReadInt(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
        {
            return number;
        }

        if (reader.TokenType == JsonTokenType.String && int.TryParse(reader.GetString(), out var fromText))
        {
            return fromText;
        }

        return 0;
    }

    private static bool IsUsable(InstanceDescriptor d)
    {
        return d.Port > 0 && !string.IsNullOrEmpty(d.Token) && !string.IsNullOrEmpty(d.ProjectName);
    }
}
