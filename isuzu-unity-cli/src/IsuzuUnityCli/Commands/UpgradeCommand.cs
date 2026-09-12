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
        var toolDirectory = DotnetToolDirectory(context.ExecutablePath);
        if (toolDirectory is not null)
        {
            // The installer writes to its own directory and would leave two copies behind.
            context.Out.WriteLine(IsGlobalToolDirectory(toolDirectory)
                ? DotnetToolMessage
                : "Installed as a dotnet tool; run: dotnet tool update IsuzuUnityCli --tool-path " + QuotePath(toolDirectory));
            return 0;
        }

        var windows = OperatingSystem.IsWindows();
        var url = windows ? WindowsScriptUrl : UnixScriptUrl;
        var script = Path.Combine(Path.GetTempPath(), "isuzu-unity-cli-install-" + Guid.NewGuid().ToString("N") + (windows ? ".ps1" : ".sh"));

        try
        {
            return await DownloadAndInstall();
        }
        finally
        {
            try { File.Delete(script); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        async Task<int> DownloadAndInstall()
        {
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

            // Not --version: that one is read before any command runs and prints this executable's
            // own version, so 'upgrade --version v4.0.0' printed 4.2.0 and exited 0 without
            // upgrading anything. The way back from a bad release has to be reachable.
            var exit = await RunScript(script, parsed.Option("release"), windows, context);

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
    }

    /// <summary>True when this executable lives in a dotnet tools directory, which owns its own updates.</summary>
    public static bool IsDotnetTool(string executablePath) => DotnetToolDirectory(executablePath) is not null;

    private static bool IsGlobalToolDirectory(string directory) =>
        string.Equals(Path.GetFileName(directory), "tools", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), ".dotnet", StringComparison.OrdinalIgnoreCase);

    private static string QuotePath(string path) => OperatingSystem.IsWindows()
        ? "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'"
        : "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string? DotnetToolDirectory(string executablePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath));

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

    private static async Task<int> RunScript(string script, string? version, bool windows, CommandContext context)
    {
        context.Cancellation.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo
        {
            FileName = windows ? "powershell" : "sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (windows)
        {
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-ExecutionPolicy");
            info.ArgumentList.Add("Bypass");
            info.ArgumentList.Add("-File");
        }

        info.ArgumentList.Add(script);
        info.Environment["ISUZU_UNITY_CLI_DIR"] = Path.GetDirectoryName(Path.GetFullPath(context.ExecutablePath))!;

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

        try
        {
            await process.WaitForExitAsync(context.Cancellation);
            await Task.WhenAll(output, errors);
        }
        catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                // Every failure to kill it is the same situation: the installer is still running
                // and this process is leaving. Letting one of them out replaces the cancellation
                // the caller is being told about with an error about the kill.
                try { process.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) { }
            }

            // Bounded: an installer that will not exit must not hold the process open for good.
            try { await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token); }
            catch (OperationCanceledException) { }
            try { await Task.WhenAll(output, errors); }
            catch (OperationCanceledException) { }
            throw;
        }
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
