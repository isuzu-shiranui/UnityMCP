using System.Diagnostics;
using System.Text;
using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Discovery;

namespace IsuzuUnityCli.Commands;

public static class UpgradeCommand
{
    public const string WindowsScriptUrl = "https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.ps1";
    public const string UnixScriptUrl = "https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.sh";

    public static async Task<int> Run(ParsedArgs parsed, CommandContext context)
    {
        var install = CliInstall.Read(context.ExecutablePath);
        var release = parsed.Option("release") is { } named ? ReleaseTag(named) : null;

        if (!install.ReplacesItself)
        {
            if (parsed.Option("release") is not null)
            {
                context.Err.WriteLine(ReleaseRefusal(install));
                return 1;
            }

            context.Out.WriteLine($"Installed {install.Description}; run: {install.UpdateCommand}");

            if (CliInstall.Delay(install.Channel) is { } delay)
            {
                context.Out.WriteLine(delay);
            }

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
            var exit = await RunScript(script, release, windows, context);

            if (exit != 0)
            {
                context.Err.WriteLine($"The installer exited with {exit}.");
                return 1;
            }

            context.Out.WriteLine();

            // Run through the executable the installer just wrote rather than in this process,
            // which is still the old one. What doctor --fix rewrites is decided by comparing the
            // installed files with the copy embedded in the running binary, so the old process
            // finds its own skill current and leaves the new release's on disk unwritten.
            return await RunInstalled(context);
        }
    }

    /// <summary>The freshly installed executable's own <c>doctor --fix</c>.</summary>
    /// <remarks>
    /// Both installers write over the path this process was started from, so that path now holds
    /// the new binary; on Windows the running one is renamed out of the way first.
    /// </remarks>
    private static async Task<int> RunInstalled(CommandContext context)
    {
        var info = new ProcessStartInfo
        {
            FileName = context.ExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        info.ArgumentList.Add("doctor");
        info.ArgumentList.Add("--fix");

        using var process = Process.Start(info);

        if (process is null)
        {
            context.Err.WriteLine($"Could not start {context.ExecutablePath} to check the installation.");
            return 1;
        }

        var output = Relay(process.StandardOutput, context.Out, context.Cancellation);
        var errors = Relay(process.StandardError, context.Err, context.Cancellation);

        await process.WaitForExitAsync(context.Cancellation);
        await Drain(output, errors, context.Cancellation);

        return process.ExitCode;
    }

    /// <summary>The tag a release is downloaded under, from what the caller typed.</summary>
    /// <remarks>
    /// The install scripts take this value as the tag. Only a lowercase 'v' counts as one already
    /// being there, so 'V4.3.1' is asked for as 'vV4.3.1' by one script and as 'V4.3.1' by the
    /// other, and GitHub answers 404 for both.
    /// </remarks>
    public static string ReleaseTag(string release)
    {
        var version = release.TrimStart('v', 'V');
        var dash = version.IndexOf('-');
        var core = (dash < 0 ? version : version[..dash]).Split('.');
        var prerelease = dash < 0 ? null : version[(dash + 1)..];

        var shaped = core.Length == 3
                     && core.All(part => part.Length > 0 && part.All(char.IsAsciiDigit))
                     && (prerelease is null
                         || (prerelease.Length > 0
                             && prerelease.All(c => char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-')));

        if (!shaped)
        {
            throw new CliException($"--release expects a version such as v4.3.1, not '{release}'.", 2);
        }

        return "v" + version;
    }

    /// <summary>Why --release does nothing for a copy another tool installed.</summary>
    public static string ReleaseRefusal(CliInstall.Install install) =>
        $"This CLI was installed {install.Description}, and --release works only for a copy the install script manages. "
        + "Install that version with the tool that installed this one.";

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
            await Drain(output, errors, context.Cancellation);
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
            try { await Drain(output, errors, CancellationToken.None); }
            catch (OperationCanceledException) { }
            throw;
        }
        return process.ExitCode;
    }

    /// <summary>What the child wrote, once it has exited.</summary>
    /// <remarks>
    /// Reading to the end of the stream waits for every handle on it to close, and a process the
    /// child left behind inherits those handles and holds them for as long as it lives. Unity's
    /// asset database service outlives the Editor that spawned it by minutes, so the wait is not
    /// bounded by anything the caller can see: the command looks hung with nothing running.
    /// The child's own lines are already through by the time it exits, so what is left is someone
    /// else's pipe.
    /// </remarks>
    public static async Task Drain(Task output, Task errors, CancellationToken cancellation)
    {
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        grace.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            await Task.WhenAll(output, errors).WaitAsync(grace.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
        }
    }

    private static async Task Relay(TextReader reader, TextWriter writer, CancellationToken cancellation)
    {
        while (await reader.ReadLineAsync(cancellation) is { } line)
        {
            writer.WriteLine(line);
        }
    }
}
