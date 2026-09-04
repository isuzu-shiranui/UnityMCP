using IsuzuUnityCli.Commands;
using Xunit;

namespace IsuzuUnityCli.Tests;

public sealed class UpgradeCommandTests
{
    [Fact]
    public async Task ADotnetToolIsUpdatedThroughDotnetRatherThanTheInstaller()
    {
        var output = new StringWriter();
        var context = new CommandContext
        {
            Out = output,
            Err = new StringWriter(),
            ExecutablePath = Path.Combine(Path.GetTempPath(), ".dotnet", "tools", "isuzu-unity-cli.exe"),
        };

        Assert.Equal(0, await Program.Run(["upgrade"], context));
        Assert.Contains("dotnet tool update -g IsuzuUnityCli", output.ToString());
    }

    [Fact]
    public void OnlyAPathInsideDotnetToolsCountsAsADotnetTool()
    {
        Assert.True(UpgradeCommand.IsDotnetTool(Path.Combine("/home/u", ".dotnet", "tools", "isuzu-unity-cli")));
        Assert.True(UpgradeCommand.IsDotnetTool(Path.Combine("/home/u", ".dotnet", "tools", "store", "x", "isuzu-unity-cli")));
        Assert.False(UpgradeCommand.IsDotnetTool(Path.Combine("/usr", "local", "bin", "isuzu-unity-cli")));
        Assert.False(UpgradeCommand.IsDotnetTool(Path.Combine("/home/u", "tools", "isuzu-unity-cli")));
    }

    [Fact]
    public void TheInstallerIsFetchedFromTheProjectItself()
    {
        Assert.StartsWith("https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/", UpgradeCommand.WindowsScriptUrl);
        Assert.EndsWith("install.ps1", UpgradeCommand.WindowsScriptUrl);
        Assert.EndsWith("install.sh", UpgradeCommand.UnixScriptUrl);
    }
}
