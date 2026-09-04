using IsuzuUnityCli.Discovery;
using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public sealed class InstanceDescriptorTests
{
    private static void WithHost(string? host, Action body)
    {
        var saved = Environment.GetEnvironmentVariable("UNITY_MCP_HOST");

        try
        {
            Environment.SetEnvironmentVariable("UNITY_MCP_HOST", host);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("UNITY_MCP_HOST", saved);
        }
    }

    private static InstanceDescriptor Descriptor()
    {
        return new InstanceDescriptor
        {
            ProjectName = "Game",
            Port = 27180,
            Token = "t",
            Endpoint = "http://127.0.0.1:27180",
            McpUrl = "http://127.0.0.1:27180/mcp",
        };
    }

    [Fact]
    public void HostOverrideRewritesEndpointAndMcpUrl()
    {
        WithHost("172.24.16.1", () =>
        {
            var descriptor = Descriptor();

            Assert.Equal("http://172.24.16.1:27180", descriptor.Endpoint);
            Assert.Equal("http://172.24.16.1:27180/mcp", descriptor.McpUrl);
            Assert.Equal("http://172.24.16.1:27180/mcp", descriptor.McpUrlOrDefault);
        });
    }

    [Fact]
    public void HostOverrideAlsoReachesTheDefaultMcpUrl()
    {
        WithHost("windows.host", () =>
        {
            var descriptor = Descriptor();
            descriptor.McpUrl = null;

            Assert.Equal("http://windows.host:27180/mcp", descriptor.McpUrlOrDefault);
        });
    }

    [Fact]
    public void WithoutTheVariableTheDescriptorIsUnchanged()
    {
        WithHost(null, () =>
        {
            var descriptor = Descriptor();

            Assert.Equal("http://127.0.0.1:27180", descriptor.Endpoint);
            Assert.Equal("http://127.0.0.1:27180/mcp", descriptor.McpUrl);
        });
    }
}
