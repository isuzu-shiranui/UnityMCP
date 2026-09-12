using System.Diagnostics;
using IsuzuUnityCli.Discovery;
using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public sealed class DescriptorStoreTests : IDisposable
{
    [Theory]
    [InlineData("projectName", "42")]
    [InlineData("token", "{}")]
    [InlineData("endpoint", "[]")]
    [InlineData("projectPath", "true")]
    [InlineData("pid", "{}")]
    [InlineData("portMismatch", "[]")]
    public void WrongFieldTypesDoNotHideHealthyInstances(string field, string value)
    {
        Write("bad.json", "{\"" + field + "\":" + value + "}");
        Write("healthy.json", Descriptor("Healthy"));
        Assert.Equal("Healthy", Assert.Single(DescriptorStore.ReadAll([_dir], _ => true)).ProjectName);
    }

    /// <summary>
    /// A field the CLI only reports is dropped, rather than taking the Editor with it.
    /// </summary>
    /// <remarks>
    /// The port and the token are what a connection is made of, and a wrong type there cannot be
    /// read past. The rest is shown to the reader, so an Editor of another version writing one of
    /// them differently must not answer "no Editor is running" for one that is.
    /// </remarks>
    [Theory]
    [InlineData("portMismatch", "0")]
    [InlineData("preferredPort", "\"27180\"")]
    [InlineData("mcpUrl", "42")]
    [InlineData("unityVersion", "6000")]
    public void AnInformationalFieldOfAnotherTypeStillLeavesTheEditorReachable(string field, string value)
    {
        Write("skewed.json", Descriptor("Skewed", extra: ",\"" + field + "\":" + value));

        var found = Assert.Single(DescriptorStore.ReadAll([_dir], _ => true));

        Assert.Equal("Skewed", found.ProjectName);
        Assert.Equal(27180, found.Port);
        Assert.Equal("tok", found.Token);
    }

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

    private static string Descriptor(string name, int port = 27180, string token = "tok", int pid = 0, string extra = "", string? path = null)
    {
        var projectPath = path ?? $"C:/p/{name}/Assets";

        return $$"""
            {"projectPath":"{{projectPath}}","projectName":"{{name}}","unityVersion":"6000.0.1f1",
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

        Write("dead.json", Descriptor("Dead", pid: deadPid, path: HostPath("Dead")));
        Write("self.json", Descriptor("Self", port: 27181, pid: Environment.ProcessId, path: HostPath("Self")));
        Write("nopid.json", Descriptor("NoPid", port: 27182, path: HostPath("NoPid")));

        var names = DescriptorStore.ReadAll([_dir]).Select(d => d.ProjectName).OrderBy(n => n).ToList();

        Assert.Equal(["NoPid", "Self"], names);
        Assert.False(ProcessLiveness.IsAlive(deadPid));
        Assert.True(ProcessLiveness.IsAlive(0));
    }

    [Fact]
    public void ADirectoryListedUnderSeveralSpellingsIsReadOnce()
    {
        Write("a.json", Descriptor("Alpha"));

        Assert.Single(DescriptorStore.ReadAll([_dir, _dir + Path.DirectorySeparatorChar, Path.Combine(_dir, ".")], _ => true));
    }

    /// <summary>
    /// Kept apart so the choice between them is made where it can be refused, not guessed here.
    /// </summary>
    [Fact]
    public void DescriptorsThatDisagreeAboutOneProjectAreBothKept()
    {
        Write("old.json", Descriptor("Alpha", port: 27180, token: "old"));
        Write("new.json", Descriptor("Alpha", port: 27181, token: "new"));

        Assert.Equal(2, DescriptorStore.ReadAll([_dir], _ => true).Count);
    }

    [Fact]
    public void AWindowsEditorReadFromAnotherHostIsAskedInsteadOfItsPid()
    {
        Write("windows.json", Descriptor("Windows", pid: 4242));

        var pidGoneButAnswers = DescriptorStore.ReadAll([_dir], _ => false, _ => true);
        var pidAliveButSilent = DescriptorStore.ReadAll([_dir], _ => true, _ => false);

        if (OperatingSystem.IsWindows())
        {
            Assert.Empty(pidGoneButAnswers);
            Assert.Single(pidAliveButSilent);
        }
        else
        {
            Assert.Single(pidGoneButAnswers);
            Assert.Empty(pidAliveButSilent);
        }
    }

    [Fact]
    public void TheSameDescriptorInTwoDirectoriesIsOneEditor()
    {
        var other = Path.Combine(_dir, "other");
        Directory.CreateDirectory(other);
        Write("a.json", Descriptor("Alpha"));
        File.WriteAllText(Path.Combine(other, "a.json"), Descriptor("Alpha"));

        Assert.Single(DescriptorStore.ReadAll([_dir, other], _ => true));
    }

    /// <summary>
    /// A path in this host's own form. A Windows path read on another host skips the pid check, so
    /// a test of that check needs a path the host checks.
    /// </summary>
    private static string HostPath(string name) => OperatingSystem.IsWindows() ? $"C:/p/{name}/Assets" : $"/p/{name}/Assets";
}
