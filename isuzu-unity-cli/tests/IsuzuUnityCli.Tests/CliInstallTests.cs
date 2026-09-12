using IsuzuUnityCli.Discovery;
using Xunit;

namespace IsuzuUnityCli.Tests;

/// <summary>
/// Which tool installed this executable. A copy winget installed has to be left to winget, which
/// refuses to upgrade or uninstall a file that no longer has the hash it recorded.
/// </summary>
public sealed class CliInstallTests : IDisposable
{
    private const string WingetFolder = "IsuzuShiranui.IsuzuUnityCli_Microsoft.Winget.Source_8wekyb3d8bbwe";

    private readonly string root = Path.Combine(Path.GetTempPath(), "cli-install-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private string Executable(string folder) =>
        Path.Combine([root, .. folder.Split('/'), "isuzu-unity-cli.exe"]);

    private static CliInstall.Install Read(string path, params string[] records) =>
        CliInstall.Read(path, () => records);

    [Theory]
    [InlineData("AppData/Local/Microsoft/WinGet/Links")]
    [InlineData("Program Files/WinGet/Links")]
    public void AWingetLinkIsLeftToWinget(string folder)
    {
        var install = Read(Executable(folder));

        Assert.Equal(CliChannel.Winget, install.Channel);
        Assert.Equal("winget upgrade --id IsuzuShiranui.IsuzuUnityCli -e", install.UpdateCommand);
        Assert.False(install.ReplacesItself);
    }

    [Theory]
    [InlineData("AppData/Local/Microsoft/WinGet/Packages/" + WingetFolder)]
    [InlineData("Program Files/WinGet/Packages/" + WingetFolder)]
    [InlineData("portable/" + WingetFolder)]
    public void TheFolderWingetUnpacksThisPackageIntoIsLeftToWinget(string folder)
    {
        Assert.Equal(CliChannel.Winget, Read(Executable(folder)).Channel);
    }

    [Theory]
    [InlineData("tools/Links")]
    [InlineData("AppData/Local/Microsoft/WinGet/Packages/IsuzuShiranui.IsuzuUnityCliPreview_Microsoft.Winget.Source_8wekyb3d8bbwe")]
    [InlineData("AppData/Local/Microsoft/WinGet/Packages/IsuzuShiranui.IsuzuUnityCli.Beta_Microsoft.Winget.Source_8wekyb3d8bbwe")]
    [InlineData("AppData/Local/Programs/isuzu-unity-cli")]
    public void AnythingElseReplacesItself(string folder)
    {
        var install = Read(Executable(folder));

        Assert.Equal(CliChannel.Direct, install.Channel);
        Assert.True(install.ReplacesItself);
    }

    [Fact]
    public void ACopyInstalledWithLocationIsFoundByTheRecordWingetKeeps()
    {
        // --location puts the file in a folder of the caller's choosing, so only the record says
        // whose it is. winget may write the path in another case than the process reports.
        var path = Executable("cli");

        Assert.Equal(CliChannel.Direct, Read(path).Channel);
        Assert.Equal(CliChannel.Winget, Read(path, path.ToUpperInvariant()).Channel);
    }

    [Fact]
    public void ARecordOfAnotherFileSaysNothingAboutThisOne()
    {
        Assert.Equal(CliChannel.Direct, Read(Executable("cli"), Executable("other")).Channel);
    }

    [Fact]
    public void ALinkIsJudgedByTheFileItPointsAt()
    {
        var target = Executable("WinGet/Packages/" + WingetFolder);
        var link = Executable("bin");

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.WriteAllText(target, "");

        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
        {
            // Windows creates a symbolic link only with Developer Mode on or from an elevated process.
            // Anywhere else a failure here is a failure of the test.
            return;
        }

        Assert.Equal(CliChannel.Winget, Read(link).Channel);
    }

    [Fact]
    public void AGlobalDotnetToolIsUpdatedThroughDotnet()
    {
        var install = Read(Path.Combine("/home/u", ".dotnet", "tools", "isuzu-unity-cli"));

        Assert.Equal(CliChannel.DotnetTool, install.Channel);
        Assert.Equal("dotnet tool update -g IsuzuUnityCli", install.UpdateCommand);
    }

    [Fact]
    public void OnlyAPathInsideDotnetToolsCountsAsADotnetTool()
    {
        Assert.Equal(CliChannel.DotnetTool, Read(Path.Combine("/home/u", ".dotnet", "tools", "store", "x", "isuzu-unity-cli")).Channel);
        Assert.Equal(CliChannel.Direct, Read(Path.Combine("/usr", "local", "bin", "isuzu-unity-cli")).Channel);
        Assert.Equal(CliChannel.Direct, Read(Path.Combine("/home/u", "tools", "isuzu-unity-cli")).Channel);
    }

    [Fact]
    public void ACustomToolPathIsRecognizedFromItsPackageStore()
    {
        Directory.CreateDirectory(Path.Combine(root, ".store", "isuzuunitycli"));

        var install = Read(Path.Combine(root, "isuzu-unity-cli"));

        Assert.Equal(CliChannel.DotnetTool, install.Channel);
        Assert.Contains("--tool-path", install.UpdateCommand);
    }
}
