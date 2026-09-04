namespace IsuzuUnityCli.Discovery;

public interface IProjectLike
{
    string ProjectName { get; }
    string ProjectPath { get; }
}

/// <summary>A running Editor as published by <c>McpInstanceDescriptor</c> on the C# side.</summary>
public sealed class InstanceDescriptor : IProjectLike
{
    private string _endpoint = "";
    private string? _mcpUrl;

    public string ProjectPath { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string UnityVersion { get; set; } = "";
    public int Port { get; set; }
    public string Token { get; set; } = "";
    public int Pid { get; set; }
    public string ProtocolVersion { get; set; } = "";

    public string Endpoint
    {
        get => WithHostOverride(_endpoint);
        set => _endpoint = value;
    }

    public string? McpUrl
    {
        get => _mcpUrl is null ? null : WithHostOverride(_mcpUrl);
        set => _mcpUrl = value;
    }

    public int? PreferredPort { get; set; }
    public bool? PortMismatch { get; set; }

    public string McpUrlOrDefault => McpUrl ?? Endpoint + "/mcp";

    /// <summary>
    /// The Editor binds loopback only, so a client in another network namespace cannot reach the
    /// address it publishes. <c>UNITY_MCP_HOST</c> names the address that a port proxy or mirrored
    /// networking exposes it on instead.
    /// </summary>
    public static string WithHostOverride(string url)
    {
        var host = Environment.GetEnvironmentVariable("UNITY_MCP_HOST");
        return string.IsNullOrEmpty(host) ? url : url.Replace("127.0.0.1", host, StringComparison.Ordinal);
    }
}
