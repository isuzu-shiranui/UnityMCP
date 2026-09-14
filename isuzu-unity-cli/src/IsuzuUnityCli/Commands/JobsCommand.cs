using System.Globalization;
using System.Text.Json.Nodes;

using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Discovery;
using IsuzuUnityCli.Http;

namespace IsuzuUnityCli.Commands;

public static class JobsCommand
{
    public static async Task<int> Run(
        ParsedArgs parsed,
        CommandContext context,
        int pollIntervalMs = 500,
        int unauthorizedGraceMs = 15000)
    {
        var id = parsed.Positional.Count > 0 ? parsed.Positional[0] : "";
        var path = id.Length > 0 ? "/jobs/" + Uri.EscapeDataString(id) : "/jobs";
        var instance = context.ResolveInstance(parsed);

        if (!parsed.HasFlag("wait"))
        {
            var envelope = await context.Client.GetAsync(instance, path, context.Cancellation);
            return context.Report(envelope, parsed.HasFlag("raw"));
        }

        if (id.Length == 0)
        {
            throw new CliException(
                "--wait needs a job id: jobs <id> --wait. Without one this lists every job, and a "
                + "list never finishes. The id is in the 'jobId' of the answer that said the call "
                + "was still running.",
                2);
        }

        return await Wait(parsed, context, instance, path, id, pollIntervalMs, unauthorizedGraceMs);
    }

    private static async Task<int> Wait(
        ParsedArgs parsed,
        CommandContext context,
        InstanceDescriptor instance,
        string path,
        string id,
        int pollIntervalMs,
        int unauthorizedGraceMs)
    {
        var timeout = Seconds(parsed.Option("timeout"), 300);
        var raw = parsed.HasFlag("raw");
        string? lastNotice = null;

        // The listener going down for a domain reload, or coming back up after one on a different
        // port with a different token, leaves the job the same job; the poller re-reads the
        // descriptor and keeps asking the same project.
        var outcome = await EditorPoller.Poll(
            context,
            instance,
            (target, token) => context.Client.GetAsync(target, path, token),
            envelope => envelope.IsError || Status(envelope) != "running",
            envelope => lastNotice = Announce(context, envelope, lastNotice),
            timeout,
            pollIntervalMs,
            unauthorizedGraceMs);

        if (outcome.TimedOut)
        {
            if (outcome.Last is not null)
            {
                context.Report(outcome.Last, raw, announced: true);
            }

            context.Err.WriteLine(outcome.Unreachable is not null
                ? $"job {id} could not be reached when the {Format(timeout)}s timeout ran out. {outcome.Unreachable}"
                : $"job {id} was still running after {Format(timeout)}s. It keeps running in the "
                  + "Editor; poll it again, or read why it is stuck in the notice above.");

            return 4;
        }

        var last = outcome.Last!;
        var code = context.Report(last, raw);

        // Report answers for the request, which succeeded; the job inside it is what the caller
        // asked about, and a failed one has to reach the shell as a failure.
        return code != 0 || Status(last) == "completed" ? code : 1;
    }

    /// <summary>
    /// Prints what holds the main thread the first time a running answer says, and again only when
    /// a dialog appears after a plainer notice. A stall notice counts the seconds up every poll, so
    /// printing every change would fill the terminal with the same sentence.
    /// </summary>
    private static string? Announce(CommandContext context, Envelope envelope, string? lastNotice)
    {
        var notice = CommandContext.RunningMessage(envelope.Result);

        if (string.IsNullOrEmpty(notice) || notice == lastNotice)
        {
            return lastNotice;
        }

        if (lastNotice is not null && !notice.Contains("showing a dialog", StringComparison.Ordinal))
        {
            return lastNotice;
        }

        context.Err.WriteLine(notice);
        return notice;
    }

    private static string? Status(Envelope envelope)
    {
        return envelope.Result is JsonObject body
               && body["status"] is JsonValue value
               && value.TryGetValue<string>(out var status)
            ? status
            : null;
    }

    private static double Seconds(string? value, double fallback)
    {
        if (value is null)
        {
            return fallback;
        }

        // NaN and infinity parse, and a timer takes at most int.MaxValue milliseconds; past either
        // the wait would throw instead of starting.
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || !double.IsFinite(parsed) || parsed <= 0 || parsed > int.MaxValue / 1000)
        {
            throw new CliException(
                $"--timeout expects a positive number of seconds, at most {int.MaxValue / 1000}, not '{value}'.", 2);
        }

        return parsed;
    }

    private static string Format(double seconds)
    {
        return seconds.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
