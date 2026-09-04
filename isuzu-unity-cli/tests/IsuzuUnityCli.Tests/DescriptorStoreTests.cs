using System.Diagnostics;
using IsuzuUnityCli.Discovery;
using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public sealed class DescriptorStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "isuzu-cli-tests", Guid.NewGuid().ToString("N"));

    public DescriptorStoreTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    private void Write(string name, string json) => File.WriteAllText(Path.Combine(_dir, name), json);

    private static string Descriptor(string name, int port = 27180, string token = "tok", int pid = 0, string extra = "")
    {
        return $$"""
            {"projectPath":"C:/p/{{name}}/Assets","projectName":"{{name}}","unityVersion":"6000.0.1f1",
             "port":{{port}},"token":"{{token}}","pid":{{pid}},"protocolVersion":"3.3.1","endpoint":"http://127.0.0.1:{{port}}"{{extra}}}
            """;
    }

    [Fact]
    public void ValidDescriptorIsRead()
    {
        Write("a.json", Descriptor("Alpha", extra: ",\"mcpUrl\":\"http://127.0.0.1:27180/mcp\",\"preferredPort\":27180,\"portMismatch\":false"));
        Write("notes.txt", "ignored");

        var all = DescriptorStore.ReadAll([_dir]);

        var d = Assert.Single(all);
        Assert.Equal("Alpha", d.ProjectName);
        Assert.Equal(27180, d.Port);
        Assert.Equal("http://127.0.0.1:27180/mcp", d.McpUrlOrDefault);
        Assert.Equal(27180, d.PreferredPort);
        Assert.False(d.PortMismatch);
    }

    [Fact]
    public void McpUrlDefaultsToEndpointPlusMcp()
    {
        Write("a.json", Descriptor("Alpha"));

        Assert.Equal("http://127.0.0.1:27180/mcp", Assert.Single(DescriptorStore.ReadAll([_dir])).McpUrlOrDefault);
    }

    [Fact]
    public void UnparsableAndIncompleteFilesAreSkipped()
    {
        Write("half.json", "{\"projectName\":\"Half\",\"port\":2718");
        Write("notoken.json", Descriptor("NoToken", token: ""));
        Write("noport.json", Descriptor("NoPort", port: 0));
        Write("noname.json", Descriptor(""));
        Write("ok.json", Descriptor("Ok"));

        Assert.Equal("Ok", Assert.Single(DescriptorStore.ReadAll([_dir])).ProjectName);
    }

    [Fact]
    public void MissingDirectoryIsNotAnError()
    {
        Assert.Empty(DescriptorStore.ReadAll([Path.Combine(_dir, "missing")]));
    }

    [Fact]
    public void DeadProcessDropsTheDescriptor()
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 0")
            : new ProcessStartInfo("true");
        info.UseShellExecute = false;
        info.CreateNoWindow = true;

        int deadPid;
        using (var process = Process.Start(info)!)
        {
            deadPid = process.Id;
            process.WaitForExit();
        }

        Write("dead.json", Descriptor("Dead", pid: deadPid));
        Write("self.json", Descriptor("Self", port: 27181, pid: Environment.ProcessId));
        Write("nopid.json", Descriptor("NoPid", port: 27182));

        var names = DescriptorStore.ReadAll([_dir]).Select(d => d.ProjectName).OrderBy(n => n).ToList();

        Assert.Equal(["NoPid", "Self"], names);
        Assert.False(ProcessLiveness.IsAlive(deadPid));
        Assert.True(ProcessLiveness.IsAlive(0));
    }
}
