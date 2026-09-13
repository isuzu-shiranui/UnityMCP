using System.Text.Json;
using IsuzuUnityCli.Http;

namespace IsuzuUnityCli.Discovery;

public static class DescriptorStore
{
    /// <summary>
    /// Reads every descriptor currently published. Half-written or stale files are skipped rather
    /// than reported: the Editor rewrites its descriptor on every start, and one left behind by a
    /// crash would otherwise register a phantom instance forever.
    /// </summary>
    /// <param name="answers">
    /// Asked instead of the pid for a descriptor a Windows Editor published, read by a CLI that is
    /// not on Windows, as under WSL: that pid names a process this host cannot see.
    /// </param>
    public static List<InstanceDescriptor> ReadAll(
        IEnumerable<string>? directories = null,
        Func<int, bool>? isAlive = null,
        Func<InstanceDescriptor, bool>? answers = null)
    {
        directories ??= StatePaths.DescriptorDirectories();
        isAlive ??= ProcessLiveness.IsAlive;
        answers ??= descriptor => HealthProbe.Answers(descriptor, TimeSpan.FromSeconds(1));
        var found = new List<InstanceDescriptor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

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
                byte[] bytes;

                try
                {
                    bytes = File.ReadAllBytes(file);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                // Identical contents describe one Editor, whichever spelling of a directory reached
                // the file. Whether two spellings name one folder depends on the file system, so the
                // contents decide.
                if (!seen.Add(Convert.ToBase64String(bytes)))
                {
                    continue;
                }

                InstanceDescriptor? parsed;

                try
                {
                    parsed = Parse(bytes);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (parsed is not null && IsUsable(parsed) && IsRunning(parsed, isAlive, answers))
                {
                    found.Add(parsed);
                }
            }
        }

        return found;
    }

    private static bool IsRunning(InstanceDescriptor descriptor, Func<int, bool> isAlive, Func<InstanceDescriptor, bool> answers) =>
        descriptor.Pid > 0 && !OperatingSystem.IsWindows() && ProjectKey.IsWindowsShaped(descriptor.ProjectPath)
            ? answers(descriptor)
            : isAlive(descriptor.Pid);

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

            // Refused only for what the connection is made of. A wrong type here cannot be read
            // past: the descriptor would name a port or a token that is not there.
            if (name is "projectPath" or "projectName" or "token" or "endpoint"
                && reader.TokenType is not (JsonTokenType.String or JsonTokenType.Null))
                throw new JsonException($"Descriptor field '{name}' must be a string or null.");
            if (name is "port" or "pid"
                && reader.TokenType is not (JsonTokenType.Number or JsonTokenType.String or JsonTokenType.Null))
                throw new JsonException($"Descriptor field '{name}' must be numeric.");

            // The rest is reported rather than connected through, so a field an Editor of another
            // version writes differently is dropped instead of hiding a running project.
            if (name is "unityVersion" or "protocolVersion" or "mcpUrl"
                && reader.TokenType is not (JsonTokenType.String or JsonTokenType.Null))
            {
                reader.Skip();
                continue;
            }
            if (name is "preferredPort"
                && reader.TokenType is not (JsonTokenType.Number or JsonTokenType.String or JsonTokenType.Null))
            {
                reader.Skip();
                continue;
            }
            if (name == "portMismatch" && reader.TokenType is not (JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null))
            {
                reader.Skip();
                continue;
            }

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
