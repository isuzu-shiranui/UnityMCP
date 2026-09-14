using System.Globalization;
using System.Text.Json.Nodes;
using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Discovery;
using IsuzuUnityCli.Http;

namespace IsuzuUnityCli.Commands;

/// <remarks>
/// A call returns once its work is over, not once the Editor has accepted it. Answering 'deferred'
/// or a job id sent the caller back for another look, and each look was another round trip for an
/// agent that re-sends its whole context every time; a caller that moved on instead read state
/// from before the change.
/// </remarks>
public static class CallCommand
{
    private const double PlayWaitSeconds = 60;
    private const double JobWaitSeconds = 120;

    public static async Task<int> Run(
        ParsedArgs parsed,
        CommandContext context,
        int pollIntervalMs = 250,
        int unauthorizedGraceMs = 15000)
    {
        var tool = parsed.Positional.Count > 0 ? parsed.Positional[0] : "";

        if (tool.Length == 0)
        {
            context.Err.WriteLine("Which tool? Run `isuzu-unity-cli tools` to see what this Editor publishes.");
            return 2;
        }

        if (parsed.Positional.Count > 1)
        {
            // Dropping them sent the tool something other than what was typed, and it answered
            // about that without a word. A PowerShell function passing its arguments on splits
            // 1,0,2 into three words, which is the usual way to get here.
            throw new CliException(
                $"Unused words after '{tool}': {string.Join(' ', parsed.Positional.Skip(1))}. Every "
                + "argument is --name value; quote a value that contains spaces or commas: --position '1,0,2'.",
                2);
        }

        var args = ToolArguments.Build(tool, parsed);
        var timeout = WaitSeconds(parsed.Option("wait-timeout"));
        var instance = context.ResolveInstance(parsed);
        var raw = parsed.HasFlag("raw");
        StageTrace.Mark("resolved");
        Envelope envelope;
        (envelope, instance) = await Send(context, instance, tool, args, pollIntervalMs);

        if (!parsed.HasFlag("no-wait") && !envelope.IsError && envelope.Result is JsonObject result)
        {
            if (Text(result["state"]) == "running" && Text(result["jobId"]) is { Length: > 0 } jobId)
            {
                return await FollowJob(context, instance, jobId, raw, timeout ?? JobWaitSeconds, pollIntervalMs, unauthorizedGraceMs);
            }

            if (tool is "play_mode_play" or "play_mode_stop" && Flag(result["deferred"]))
            {
                var play = tool == "play_mode_play";
                return await AwaitPlayMode(
                    context, instance, play, play && Flag(result["paused"]), raw, timeout ?? PlayWaitSeconds,
                    pollIntervalMs, unauthorizedGraceMs);
            }
        }

        var code = context.Report(envelope, raw);
        StageTrace.Mark("reported");
        return code;
    }

    /// <summary>Sends the call, and sends it again while the Editor has not run it because it is reloading.</summary>
    /// <remarks>
    /// Each of these means the tool never ran: a refused connection delivered nothing, a rejected
    /// token stopped the request at the door, and a server going down for a domain reload fails
    /// what it had queued before starting it. A reload is what the call before this one - a
    /// compile, entering play mode - usually set off, and the Editor comes back on another port
    /// with another token, so the descriptor is read again each time.
    /// </remarks>
    private static async Task<(Envelope Envelope, InstanceDescriptor Instance)> Send(
        CommandContext context,
        InstanceDescriptor instance,
        string tool,
        JsonObject args,
        int retryIntervalMs)
    {
        var since = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            try
            {
                return (await context.Client.PostAsync(instance, "/tools/" + tool, args, context.Cancellation), instance);
            }
            catch (UnityError e) when ((e.Code is "server_stopped" or "ECONNREFUSED" or "ECONNTIMEOUT" || e.HttpStatus == 401)
                && since.Elapsed.TotalSeconds < ReloadGraceSeconds)
            {
                await Task.Delay(Math.Max(retryIntervalMs, 1), context.Cancellation);

                try
                {
                    instance = context.RefreshInstance(instance);
                }
                catch (CliException)
                {
                    // The descriptor is rewritten during the reload; the next attempt reads it again.
                }
            }
        }
    }

    private const double ReloadGraceSeconds = 30;

    private static async Task<int> FollowJob(
        CommandContext context,
        InstanceDescriptor instance,
        string jobId,
        bool raw,
        double timeout,
        int pollIntervalMs,
        int unauthorizedGraceMs)
    {
        var path = "/jobs/" + Uri.EscapeDataString(jobId);
        var announced = false;

        var outcome = await EditorPoller.Poll(
            context,
            instance,
            (target, token) => context.Client.GetAsync(target, path, token),
            envelope => envelope.IsError || Text((envelope.Result as JsonObject)?["status"]) != "running",
            envelope =>
            {
                if (!announced && CommandContext.RunningMessage(envelope.Result) is { } notice)
                {
                    context.Err.WriteLine(notice);
                    announced = true;
                }
            },
            timeout,
            pollIntervalMs,
            unauthorizedGraceMs,
            readFirst: false);

        if (outcome.TimedOut)
        {
            context.Err.WriteLine(outcome.Unreachable is not null
                ? $"job {jobId} could not be reached when the {Format(timeout)}s wait ran out. {outcome.Unreachable}"
                : $"job {jobId} is still running after {Format(timeout)}s and keeps running in the Editor. "
                  + $"isuzu-unity-cli jobs {jobId} --wait picks it up again.");
            return 4;
        }

        var last = outcome.Last!;

        if (raw || last.IsError)
        {
            var code = context.Report(last, raw);
            return code != 0 || Text((last.Result as JsonObject)?["status"]) == "completed" ? code : 1;
        }

        var job = last.Result as JsonObject ?? new JsonObject();

        switch (Text(job["status"]))
        {
            case "completed":
                JsonOutput.Print(context.Out, job["result"], context.Indented);
                return 0;

            case "cancelled":
                context.ReportError("job_cancelled", $"Job {jobId} was cancelled before it ran.");
                return 1;

            default:
                context.ReportError(Text(job["errorCode"]) ?? "job_failed", Text(job["error"]) ?? $"Job {jobId} failed.");
                return 1;
        }
    }

    private static async Task<int> AwaitPlayMode(
        CommandContext context,
        InstanceDescriptor instance,
        bool play,
        bool paused,
        bool raw,
        double timeout,
        int pollIntervalMs,
        int unauthorizedGraceMs)
    {
        var action = play ? "play" : "stop";

        var outcome = await EditorPoller.Poll(
            context,
            instance,
            (target, token) => context.Client.SendAsync(
                target, HttpMethod.Post, "/tools/play_mode_status", "{}", Idempotency.Safe, token),
            envelope => envelope.IsError || Refused(envelope.Result, action) is not null || Reached(envelope.Result, play, paused),
            null,
            timeout,
            pollIntervalMs,
            unauthorizedGraceMs,
            readFirst: false);

        if (outcome.TimedOut)
        {
            if (outcome.Last is not null)
            {
                context.Report(outcome.Last, raw);
            }

            context.Err.WriteLine(outcome.Unreachable is not null
                ? $"The Editor could not be reached when the {Format(timeout)}s wait for play mode to {action} ran out. {outcome.Unreachable}"
                : $"Play mode had not finished changing after {Format(timeout)}s. play_mode_status shows where it is.");
            return 4;
        }

        var last = outcome.Last!;

        if (!last.IsError && Refused(last.Result, action) is { } reason)
        {
            context.Report(last, raw);
            context.ReportError("play_refused", reason);
            return 1;
        }

        return context.Report(last, raw);
    }

    /// <summary>Whether the status shows the requested state with nothing left pending.</summary>
    /// <remarks>
    /// An Editor without the 'pending' field is judged by the state alone, which a request made a
    /// moment earlier from somewhere else can also satisfy.
    /// </remarks>
    private static bool Reached(JsonNode? result, bool play, bool paused)
    {
        if (result is not JsonObject status || Flag(status["isPlaying"]) != play)
        {
            return false;
        }

        if (status["pending"] is not null)
        {
            return false;
        }

        return !paused || Flag(status["isPaused"]);
    }

    private static string? Refused(JsonNode? result, string action)
    {
        if ((result as JsonObject)?["refused"] is not JsonObject refused || Text(refused["action"]) != action)
        {
            return null;
        }

        return Text(refused["reason"]) ?? $"The Editor did not {action} play mode.";
    }

    private static double? WaitSeconds(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || !double.IsFinite(parsed) || parsed <= 0 || parsed > int.MaxValue / 1000)
        {
            throw new CliException(
                $"--wait-timeout expects a positive number of seconds, at most {int.MaxValue / 1000}, not '{value}'.", 2);
        }

        return parsed;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool Flag(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var on) && on;

    private static string Format(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);
}
