using System.Runtime.Versioning;
using Microsoft.Win32;

namespace IsuzuUnityCli.Discovery;

/// <summary>Which tool put this executable where it is, which decides what may replace it.</summary>
public enum CliChannel
{
    /// <summary>The install script, or a file copied into place. This CLI replaces it itself.</summary>
    Direct,

    /// <summary>A dotnet tool, which dotnet updates.</summary>
    DotnetTool,

    /// <summary>A winget portable package, which winget updates.</summary>
    Winget,
}

/// <summary>
/// Reads where this executable sits to find out which tool installed it.
/// </summary>
/// <remarks>
/// A copy another tool installed is updated through that tool. The install script writes into a
/// directory of its own, so over a dotnet tool it leaves a second copy behind and the older one
/// stays first on PATH. winget keeps the hash of the file it installed and checks it before an
/// upgrade or an uninstall, and refuses both for a file that has since been replaced.
/// </remarks>
public static class CliInstall
{
    public const string WingetPackageId = "IsuzuShiranui.IsuzuUnityCli";

    public const string WingetUpgrade = "winget upgrade --id " + WingetPackageId + " -e";

    /// <summary>Said beside the winget command, because the release notice comes from GitHub first.</summary>
    public const string WingetDelay =
        "winget offers a release only after its manifest has been reviewed and merged into winget-pkgs.";

    /// <summary>How the executable was installed, and the command that updates it.</summary>
    public sealed record Install(CliChannel Channel, string UpdateCommand)
    {
        /// <summary>Whether <c>upgrade</c> may replace the executable.</summary>
        public bool ReplacesItself => Channel is CliChannel.Direct;

        /// <summary>How it got here, worded to follow "installed".</summary>
        public string Description => Channel switch
        {
            CliChannel.DotnetTool => "as a dotnet tool",
            CliChannel.Winget => "with winget",
            _ => "by the install script or copied into place",
        };
    }

    /// <summary>Reads <paramref name="executablePath"/> and says which tool installed it.</summary>
    public static Install Read(string executablePath) => Read(executablePath, UninstallRecords);

    /// <param name="executablePath">The path this process was started by.</param>
    /// <param name="wingetRecords">The file and link paths winget recorded for this package.</param>
    public static Install Read(string executablePath, Func<IReadOnlyList<string>> wingetRecords)
    {
        var path = Path.GetFullPath(executablePath);
        var toolDirectory = DotnetToolDirectory(path);

        if (toolDirectory is not null)
        {
            return new Install(
                CliChannel.DotnetTool,
                IsGlobalToolDirectory(toolDirectory)
                    ? "dotnet tool update -g IsuzuUnityCli"
                    : "dotnet tool update IsuzuUnityCli --tool-path " + QuotePath(toolDirectory));
        }

        // winget puts a link on PATH, and on Windows a process started through a link reports the
        // link's path. The file behind it is asked about as well, for a link made somewhere else.
        var names = new List<string> { path };

        if (LinkTarget(path) is { } target)
        {
            names.Add(target);
        }

        if (names.Any(InWingetFolder) || Recorded(names, wingetRecords()))
        {
            return new Install(CliChannel.Winget, WingetUpgrade);
        }

        return new Install(CliChannel.Direct, "isuzu-unity-cli update");
    }

    /// <summary>A folder winget chose: its folder of links, or the one it unpacks this package into.</summary>
    /// <remarks>
    /// Compared by folder name, which holds for a per-user install, a machine-wide one, and a
    /// portable package root moved in winget's settings, which moves the package folder but keeps
    /// its name.
    /// </remarks>
    private static bool InWingetFolder(string path)
    {
        var folder = Path.GetDirectoryName(path);

        if (string.IsNullOrEmpty(folder))
        {
            return false;
        }

        var name = Path.GetFileName(folder);

        return name.StartsWith(WingetPackageId + "_", StringComparison.OrdinalIgnoreCase)
               || (string.Equals(name, "Links", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(
                       Path.GetFileName(Path.GetDirectoryName(folder)), "WinGet", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether winget recorded one of <paramref name="names"/> as this package's file or its link.</summary>
    private static bool Recorded(IReadOnlyList<string> names, IReadOnlyList<string> records)
    {
        foreach (var record in records)
        {
            string full;

            try
            {
                full = Path.GetFullPath(record);
            }
            catch (ArgumentException)
            {
                continue;
            }

            // Compared without case, as Windows compares paths: the records exist only there, and
            // the drive letter winget wrote need not match the one this process was started by.
            if (names.Any(name => string.Equals(name, full, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> UninstallRecords()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        return ReadUninstallKeys();
    }

    /// <summary>The file and link paths winget wrote into this package's Uninstall keys.</summary>
    [SupportedOSPlatform("windows")]
    private static List<string> ReadUninstallKeys()
    {
        var paths = new List<string>();

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var uninstall = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");

                if (uninstall is null)
                {
                    continue;
                }

                foreach (var name in uninstall.GetSubKeyNames())
                {
                    if (!name.StartsWith(WingetPackageId + "_", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    using var entry = uninstall.OpenSubKey(name);

                    foreach (var value in new[] { "TargetFullPath", "SymlinkFullPath" })
                    {
                        if (entry?.GetValue(value) is string recorded && recorded.Length > 0)
                        {
                            paths.Add(recorded);
                        }
                    }
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                // A key this user cannot read records nothing this process can act on.
            }
        }

        return paths;
    }

    private static string? LinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsGlobalToolDirectory(string directory) =>
        string.Equals(Path.GetFileName(directory), "tools", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), ".dotnet", StringComparison.OrdinalIgnoreCase);

    private static string QuotePath(string path) => OperatingSystem.IsWindows()
        ? "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'"
        : "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string? DotnetToolDirectory(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);

        while (!string.IsNullOrEmpty(directory))
        {
            if (IsGlobalToolDirectory(directory)
                || Directory.Exists(Path.Combine(directory, ".store", "isuzuunitycli")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }
}
