using System.Diagnostics;
using System.Text;
using IsuzuUnityCli.Cli;

namespace IsuzuUnityCli.Commands;

public static class UpgradeCommand
{
    public const string WindowsScriptUrl = "https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.ps1";
    public const string UnixScriptUrl = "https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.sh";

    public const string DotnetToolMessage = "Installed as a dotnet tool; run: dotnet tool update -g IsuzuUnityCli";

    public static async Task<int> Run(ParsedArgs parsed, CommandContext context)
    {
        if (IsDotnetTool(context.ExecutablePath))
        {
            // The installer writes to its own directory and would leave two copies behind.
            context.Out.WriteLine(DotnetToolMessage);
            return 0;
        }

        var windows = OperatingSystem.IsWindows();
        var url = windows ? WindowsScriptUrl : UnixScriptUrl;
        var script = Path.Combine(Path.GetTempPath(), windows ? "isuzu-unity-cli-install.ps1" : "isuzu-unity-cli-install.sh");

        try
        {
            using var http = new HttpClient();
            var body = await http.GetStringAsync(url, context.Cancellation);
            await File.WriteAllTextAsync(script, body, new UTF8Encoding(windows), context.Cancellation);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            context.Err.WriteLine($"Could not download the installer from {url}: {e.Message}");
            return 1;
        }

        var exit = await RunScript(script, parsed.Option("version"), windows, context);

        try
        {
            File.Delete(script);
        }
        catch (IOException)
        {
        }

        if (exit != 0)
        {
            context.Err.WriteLine($"The installer exited with {exit}.");
            return 1;
        }

        context.Out.WriteLine();

        // The new binary is on disk but this process is still the old one, so the check below
        // reports on what the freshly installed executable will find.
        return DoctorCommand.Run(ArgParser.Parse(["doctor", "--fix"]), context);
    }

    /// <summary>True when this executable lives in a dotnet tools directory, which owns its own updates.</summary>
    public static bool IsDotnetTool(string executablePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath));

        while (!string.IsNullOrEmpty(directory))
        {
            if (string.Equals(Path.GetFileName(directory), "tools", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), ".dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return false;
    }

    private static async Task<int> RunScript(string script, string? version, bool windows, CommandContext context)
    {
        var info = new ProcessStartInfo
        {
            FileName = windows ? "powershell" : "sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (windows)
        {
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-ExecutionPolicy");
            info.ArgumentList.Add("Bypass");
            info.ArgumentList.Add("-File");
        }

        info.ArgumentList.Add(script);

        if (!string.IsNullOrEmpty(version))
        {
            // Passed through the environment because the scripts read it either way, and the
            // Windows one binds parameters only when it is not piped.
            info.Environment["ISUZU_UNITY_CLI_VERSION"] = version;
        }

        using var process = Process.Start(info)
            ?? throw new CliException($"Could not start {info.FileName}.");

        var output = Relay(process.StandardOutput, context.Out, context.Cancellation);
        var errors = Relay(process.StandardError, context.Err, context.Cancellation);

        await process.WaitForExitAsync(context.Cancellation);
        await Task.WhenAll(output, errors);
        return process.ExitCode;
    }

    private static async Task Relay(TextReader reader, TextWriter writer, CancellationToken cancellation)
    {
        while (await reader.ReadLineAsync(cancellation) is { } line)
        {
            writer.WriteLine(line);
        }
    }
}
