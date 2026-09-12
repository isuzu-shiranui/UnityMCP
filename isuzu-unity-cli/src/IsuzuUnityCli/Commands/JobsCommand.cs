using System.Diagnostics;
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

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.Cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeout));

        Envelope? last = null;
        string? lastNotice = null;
        string? unreachable = null;
        Stopwatch? rejectedSince = null;
        var reauthenticated = false;

        try
        {
            while (true)
            {
                Envelope envelope;

                try
                {
                    envelope = await context.Client.GetAsync(instance, path, deadline.Token);
                }
                catch (UnityError e) when (e.HttpStatus is null || e.Code == "non_json" || e.HttpStatus == 401)
                {
                    // The listener going down for a domain reload, or coming back up after one on a
                    // different port with a different token. The job is still the same job, so the
                    // descriptor is re-read and polling continues against the same project.
                    var changed = false;

                    try
                    {
                        var refreshed = context.RefreshInstance(instance, cancellation: deadline.Token);
                        changed = refreshed.Endpoint != instance.Endpoint || refreshed.Token != instance.Token;
                        instance = refreshed;
                        unreachable = e.HttpStatus == 401
                            ? $"The Editor at {instance.Endpoint} rejects the token published for "
                              + $"{ProjectKey.Display(instance.ProjectPath)}. {InstanceResolver.SwitchByCommand}"
                            : e.Message;
                    }
                    catch (CliException refused)
                    {
                        // Briefly absent while the Editor rewrites its descriptor, or gone for good.
                        // Either way no other project is polled, and the reason is kept for the report.
                        unreachable = refused.Message;
                    }

                    if (e.HttpStatus == 401)
                    {
                        if (changed && !reauthenticated)
                        {
                            reauthenticated = true;
                        }
                        else
                        {
                            // A restarted Editor's descriptor can lag its restart, so a rejection is
                            // final only once the token has stayed the same for a while.
                            rejectedSince ??= Stopwatch.StartNew();

                            if (rejectedSince.ElapsedMilliseconds >= unauthorizedGraceMs)
                            {
                                throw new CliException(unreachable ?? e.Message, 3);
                            }
                        }
                    }

                    await Task.Delay(pollIntervalMs, deadline.Token);
                    continue;
                }

                unreachable = null;
                rejectedSince = null;
                reauthenticated = false;
                last = envelope;

                if (envelope.IsError || Status(envelope) != "running")
                {
                    break;
                }

                lastNotice = Announce(context, envelope, lastNotice);
                await Task.Delay(pollIntervalMs, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!context.Cancellation.IsCancellationRequested)
        {
            if (last is not null)
            {
                context.Report(last, raw);
            }

            context.Err.WriteLine(unreachable is not null
                ? $"job {id} could not be reached when the {Format(timeout)}s timeout ran out. {unreachable}"
                : $"job {id} was still running after {Format(timeout)}s. It keeps running in the "
                  + "Editor; poll it again, or read why it is stuck in the notice above.");

            return 4;
        }

        var code = context.Report(last!, raw);

        // Report answers for the request, which succeeded; the job inside it is what the caller
        // asked about, and a failed one has to reach the shell as a failure.
        return code != 0 || Status(last!) == "completed" ? code : 1;
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

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new CliException($"--timeout expects a positive number of seconds, not '{value}'.", 2);
        }

        return parsed;
    }

    private static string Format(double seconds)
    {
        return seconds.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
