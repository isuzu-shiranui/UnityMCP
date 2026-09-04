namespace IsuzuUnityCli.Tests.Fakes;

/// <summary>
/// A throwaway home directory with every environment variable this tool reads pointed at it,
/// so a test that writes agent configs cannot reach the ones the developer actually uses.
/// </summary>
public sealed class TempHome : IDisposable
{
    private static readonly string[] Names =
    [
        "LOCALAPPDATA", "XDG_DATA_HOME", "USERPROFILE", "HOME", "APPDATA", "CLAUDE_CONFIG_DIR", "CODEX_HOME",
        "UNITY_MCP_STATE_DIR", "UNITY_MCP_HOST",
    ];

    private readonly Dictionary<string, string?> _saved;

    public string Root { get; }

    public TempHome(IReadOnlyDictionary<string, string?>? overrides = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "isuzu-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        _saved = Names.ToDictionary(name => name, Environment.GetEnvironmentVariable);

        foreach (var name in Names)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        Environment.SetEnvironmentVariable("USERPROFILE", Root);
        Environment.SetEnvironmentVariable("HOME", Root);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", At("AppData", "Local"));
        Environment.SetEnvironmentVariable("APPDATA", At("AppData", "Roaming"));

        if (overrides is null)
        {
            return;
        }

        foreach (var pair in overrides)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    public string At(params string[] parts) => Path.Combine([Root, .. parts]);

    public string MakeDirectory(params string[] parts)
    {
        var path = At(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes a descriptor file where <see cref="Discovery.DescriptorStore"/> will find it.</summary>
    public string WriteDescriptor(string name, string json)
    {
        var directory = MakeDirectory("AppData", "Local", "UnityMCP", "instances");
        var file = Path.Combine(directory, name + ".json");
        File.WriteAllText(file, json);
        return file;
    }

    public void Dispose()
    {
        foreach (var pair in _saved)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
