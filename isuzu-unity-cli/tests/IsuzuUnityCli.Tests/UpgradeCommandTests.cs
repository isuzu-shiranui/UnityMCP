using IsuzuUnityCli.Commands;
using IsuzuUnityCli.Tests.Fakes;
using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace IsuzuUnityCli.Tests;

public sealed class UpgradeCommandTests
{
    // Given to every run of upgrade over a copy another tool installed. Should the channel be
    // misread, the download of the real installer is refused at once, so nothing on this machine
    // is replaced and no PATH is rewritten.
    private static readonly CancellationToken Cancelled = new(canceled: true);

    [Fact]
    public async Task CustomToolPathGetsItsOwnUpdateCommandWithoutRunningInstaller()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cli upgrade test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, ".store", "isuzuunitycli"));
        var output = new StringWriter();
        try
        {
            var context = new CommandContext { Out = output, Err = new StringWriter(), ExecutablePath = Path.Combine(directory, "isuzu-unity-cli"), Cancellation = Cancelled };
            Assert.Equal(0, await Program.Run(["upgrade"], context));
            Assert.Contains("--tool-path '" + directory + "'", output.ToString());
            Assert.DoesNotContain(" -g ", output.ToString());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task InstallerReceivesTheActualCustomBinaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cli-upgrade-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, OperatingSystem.IsWindows() ? "mock.ps1" : "mock.sh");
        await File.WriteAllTextAsync(script, OperatingSystem.IsWindows()
            ? "Write-Output $env:ISUZU_UNITY_CLI_DIR"
            : "printf '%s\\n' \"$ISUZU_UNITY_CLI_DIR\"");
        var output = new StringWriter();
        var context = new CommandContext { Out = output, Err = new StringWriter(), ExecutablePath = Path.Combine(directory, "isuzu-unity-cli") };
        try
        {
            Assert.Equal(0, await Script(script, context));
            Assert.Equal(directory, output.ToString().Trim());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CancellationStopsTheInstallerProcess()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cli-upgrade-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, OperatingSystem.IsWindows() ? "mock.ps1" : "mock.sh");
        await File.WriteAllTextAsync(script, OperatingSystem.IsWindows()
            ? "$child = Start-Process powershell -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 30' -WindowStyle Hidden -PassThru; Write-Output $PID; Write-Output $child.Id; [Console]::Out.Flush(); Start-Sleep -Seconds 30"
            : "sleep 30 &\necho $$\necho $!\nwait");
        using var cancellation = new CancellationTokenSource();
        var output = new RecordingWriter();
        var context = new CommandContext { Out = output, Err = new StringWriter(), Cancellation = cancellation.Token };
        Process? child = null;
        Process? descendant = null;
        try
        {
            var run = Script(script, context);
            await RecordingWriter.WaitFor(() => output.Lines.Count >= 2, "installer startup");
            child = Process.GetProcessById(int.Parse(output.Lines[0]));
            descendant = Process.GetProcessById(int.Parse(output.Lines[1]));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(child.HasExited, "The canceled installer must stop before returning.");
            Assert.True(descendant.WaitForExit(2000), "The canceled installer's child must also stop.");
        }
        finally
        {
            if (child is not null && !child.HasExited) child.Kill();
            if (descendant is not null && !descendant.HasExited) descendant.Kill();
            child?.Dispose();
            descendant?.Dispose();
            Directory.Delete(directory, true);
        }
    }

    private static Task<int> Script(string script, CommandContext context) =>
        (Task<int>)typeof(UpgradeCommand).GetMethod("RunScript", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [script, null, OperatingSystem.IsWindows(), context])!;

    [Fact]
    public async Task ADotnetToolIsUpdatedThroughDotnetRatherThanTheInstaller()
    {
        var output = new StringWriter();
        var context = new CommandContext
        {
            Out = output,
            Err = new StringWriter(),
            ExecutablePath = Path.Combine(Path.GetTempPath(), ".dotnet", "tools", "isuzu-unity-cli.exe"),
            Cancellation = Cancelled,
        };

        Assert.Equal(0, await Program.Run(["upgrade"], context));
        Assert.Contains("dotnet tool update -g IsuzuUnityCli", output.ToString());
    }

    [Fact]
    public async Task AWingetCopyIsUpdatedThroughWingetRatherThanReplaced()
    {
        var output = new StringWriter();
        var context = new CommandContext
        {
            Out = output,
            Err = new StringWriter(),
            ExecutablePath = Path.Combine(Path.GetTempPath(), "Microsoft", "WinGet", "Links", "isuzu-unity-cli.exe"),
            Cancellation = Cancelled,
        };

        Assert.Equal(0, await Program.Run(["upgrade"], context));
        Assert.Contains("winget upgrade --id IsuzuShiranui.IsuzuUnityCli -e", output.ToString());
    }

    [Fact]
    public async Task ReleaseIsRefusedForACopyWingetInstalledRatherThanIgnored()
    {
        var error = new StringWriter();
        var context = new CommandContext
        {
            Out = new StringWriter(),
            Err = error,
            ExecutablePath = Path.Combine(Path.GetTempPath(), "Microsoft", "WinGet", "Links", "isuzu-unity-cli.exe"),
            Cancellation = Cancelled,
        };

        Assert.Equal(1, await Program.Run(["upgrade", "--release", "v4.2.0"], context));
        Assert.Contains("--release", error.ToString());
    }

    [Fact]
    public void TheInstallerIsFetchedFromTheProjectItself()
    {
        Assert.StartsWith("https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/", UpgradeCommand.WindowsScriptUrl);
        Assert.EndsWith("install.ps1", UpgradeCommand.WindowsScriptUrl);
        Assert.EndsWith("install.sh", UpgradeCommand.UnixScriptUrl);
    }
}
