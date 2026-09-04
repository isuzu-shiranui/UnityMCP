using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Discovery;
using IsuzuUnityCli.Http;

namespace IsuzuUnityCli.Commands;

/// <summary>
/// How often the polling loops ask the Editor, and how long a requested compilation may
/// take to start before the standing result is taken as the answer.
/// </summary>
public sealed record VerifyPolling(
    int CompileIntervalMs = 500,
    int TestIntervalMs = 1000,
    int CompileStartGraceMs = 5000,
    int JobIntervalMs = 500);

/// <summary>
/// Recompiles, optionally runs a test suite, reads the console, and answers with one exit code.
/// </summary>
/// <remarks>
/// The Editor cannot offer this as a single tool: compiling triggers a domain reload that takes
/// its HTTP server down mid-request, so the caller has to survive the connection dropping and
/// come back to a server that may have moved port and rotated its token.
/// </remarks>
public static class VerifyCommand
{
    public static async Task<int> Run(ParsedArgs parsed, CommandContext context, VerifyPolling? polling = null)
    {
        var intervals = polling ?? new VerifyPolling();
        var timeout = Seconds(parsed.Option("timeout"), 300);
        var logLimit = Count(parsed.Option("logs"), 20);
        var startedAt = DateTimeOffset.UtcNow;
        var elapsed = Stopwatch.StartNew();
        var run = new Verifier(context, parsed, context.ResolveInstance(parsed), intervals.JobIntervalMs);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.Cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeout));

        try
        {
            if (!parsed.HasFlag("no-compile"))
            {
                await run.Compile(intervals.CompileIntervalMs, intervals.CompileStartGraceMs, deadline.Token);
            }

            if (parsed.HasFlag("test"))
            {
                await run.RunTests(intervals.TestIntervalMs, deadline.Token);
            }

            await run.ReadConsole(logLimit, deadline.Token);
        }
        catch (OperationCanceledException) when (!context.Cancellation.IsCancellationRequested)
        {
            run.TimedOut = true;
        }

        if (parsed.HasFlag("raw"))
        {
            JsonOutput.Print(context.Out, run.Summary(startedAt, elapsed.Elapsed));
        }
        else
        {
            context.Out.Write(run.Render());
        }

        if (run.TimedOut)
        {
            context.Err.WriteLine($"verify timed out after {Format(timeout)}s");
            return 4;
        }

        return run.Ok ? 0 : 1;
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

    private static int Count(string? value, int fallback)
    {
        if (value is null)
        {
            return fallback;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            throw new CliException($"--logs expects a count, not '{value}'.", 2);
        }

        return parsed;
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static bool? OptionalFlag(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
    }

    private static string? Text(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
    }

    private static int Number(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<int>(out var i) ? i : 0;
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    private sealed record CompileError(string File, int Line, int Column, string Message);

    private sealed record TestFailure(string Name, string Message);

    private sealed record ConsoleEntry(string Message, string File, int Line);

    private sealed class Verifier
    {
        private const int TransportRetryMs = 200;

        private readonly CommandContext _context;
        private readonly ParsedArgs _parsed;
        private readonly int _jobIntervalMs;
        private InstanceDescriptor _instance;
        private string? _lastNotice;

        public Verifier(CommandContext context, ParsedArgs parsed, InstanceDescriptor instance, int jobIntervalMs = 500)
        {
            _context = context;
            _parsed = parsed;
            _instance = instance;
            _jobIntervalMs = jobIntervalMs;
        }

        public bool TimedOut { get; set; }

        private bool CompileRan { get; set; }

        private bool? CompileSucceeded { get; set; }

        private int CompileErrorCount { get; set; }

        private double CompileSeconds { get; set; }

        private List<CompileError> CompileErrors { get; } = new();

        private bool TestsRan { get; set; }

        private string TestMode { get; set; } = "edit";

        private string TestStatus { get; set; } = "idle";

        private int Passed { get; set; }

        private int Failed { get; set; }

        private int Skipped { get; set; }

        private List<TestFailure> Failures { get; } = new();

        private bool ConsoleRead { get; set; }

        private List<ConsoleEntry> ConsoleErrors { get; } = new();

        public bool Ok =>
            !TimedOut
            && (!CompileRan || CompileSucceeded == true)
            && (!TestsRan || (Failed == 0 && TestStatus != "failed" && TestStatus != "interrupted"));

        public async Task Compile(int intervalMs, int startGraceMs, CancellationToken cancellation)
        {
            CompileRan = true;
            var elapsed = Stopwatch.StartNew();

            // Taken before the request so the poll can tell a finished compile from the previous
            // one: the Editor still reports the old result until this one actually starts.
            var before = Text((await Call("compile_status", StatusBody(), cancellation))["completedAt"]);

            // "requested": false means a compilation was already in flight; its completion is
            // the one to report, even when it ends with the same timestamp that was read above.
            var started = OptionalFlag((await Call("compile_request", new JsonObject(), cancellation))["requested"]) == false;
            var sinceRequest = Stopwatch.StartNew();

            while (true)
            {
                var status = await Call("compile_status", StatusBody(), cancellation);

                if (Flag(status["isCompiling"]) || Flag(status["isUpdating"]))
                {
                    started = true;
                }
                else
                {
                    var completedAt = Text(status["completedAt"]);

                    // The Editor gives no signal when the request compiles nothing, so a
                    // compilation that has not begun within the grace period is taken as already
                    // done and the standing result is the answer.
                    var stale = !started && sinceRequest.ElapsedMilliseconds >= startGraceMs;

                    if (completedAt is not null && (started || completedAt != before || stale))
                    {
                        CompileSucceeded = OptionalFlag(status["succeeded"]);
                        CompileErrorCount = Number(status["errorCount"]);
                        CollectCompileErrors(status["messages"] as JsonArray);
                        CompileSeconds = elapsed.Elapsed.TotalSeconds;
                        return;
                    }
                }

                await Task.Delay(intervalMs, cancellation);
            }
        }

        public async Task RunTests(int intervalMs, CancellationToken cancellation)
        {
            TestsRan = true;
            TestMode = _parsed.Option("test-mode") ?? "edit";

            var body = new JsonObject { ["mode"] = TestMode };
            AddOption(body, "assembly");
            AddOption(body, "filter");
            AddOption(body, "category");

            await Call("test_run", body, cancellation);

            while (true)
            {
                var results = await Call(
                    "test_results",
                    new JsonObject { ["include_passed"] = false, ["limit"] = 200 },
                    cancellation);

                var status = Text(results["status"]) ?? "idle";

                if (status != "running")
                {
                    TestStatus = status;
                    Passed = Number(results["passed"]);
                    Failed = Number(results["failed"]);
                    Skipped = Number(results["skipped"]);
                    CollectFailures(results["results"] as JsonArray);
                    return;
                }

                await Task.Delay(intervalMs, cancellation);
            }
        }

        public async Task ReadConsole(int limit, CancellationToken cancellation)
        {
            var body = new JsonObject { ["type"] = "error", ["limit"] = limit };
            var result = await Call("console_read_logs", body, cancellation);
            ConsoleRead = true;

            foreach (var entry in result["logs"] as JsonArray ?? [])
            {
                if (entry is JsonObject log)
                {
                    ConsoleErrors.Add(new ConsoleEntry(Text(log["m"]) ?? "", Text(log["f"]) ?? "", Number(log["l"])));
                }
            }
        }

        public JsonObject Summary(DateTimeOffset startedAt, TimeSpan duration)
        {
            return new JsonObject
            {
                ["project"] = _instance.ProjectName,
                ["startedAt"] = startedAt.ToString("o", CultureInfo.InvariantCulture),
                ["finishedAt"] = startedAt.Add(duration).ToString("o", CultureInfo.InvariantCulture),
                ["durationSec"] = Math.Round(duration.TotalSeconds, 3),
                ["compile"] = CompileRan
                    ? new JsonObject
                    {
                        ["ran"] = true,
                        ["succeeded"] = CompileSucceeded,
                        ["errorCount"] = CompileErrorCount,
                        ["errors"] = new JsonArray(CompileErrors
                            .Select(e => (JsonNode)new JsonObject
                            {
                                ["file"] = e.File,
                                ["line"] = e.Line,
                                ["message"] = e.Message,
                            })
                            .ToArray()),
                    }
                    : null,
                ["tests"] = TestsRan
                    ? new JsonObject
                    {
                        ["ran"] = true,
                        ["status"] = TestStatus,
                        ["passed"] = Passed,
                        ["failed"] = Failed,
                        ["skipped"] = Skipped,
                        ["failures"] = new JsonArray(Failures
                            .Select(f => (JsonNode)new JsonObject { ["name"] = f.Name, ["message"] = f.Message })
                            .ToArray()),
                    }
                    : null,
                ["consoleErrors"] = new JsonArray(ConsoleErrors
                    .Select(e => (JsonNode)new JsonObject
                    {
                        ["message"] = e.Message,
                        ["file"] = e.File,
                        ["line"] = e.Line,
                    })
                    .ToArray()),
                ["ok"] = Ok,
            };
        }

        public string Render()
        {
            var text = new StringBuilder();

            if (!CompileRan)
            {
                text.Append("compile: skipped\n");
            }
            else if (CompileSucceeded is null)
            {
                text.Append("compile: unfinished\n");
            }
            else if (CompileSucceeded == true)
            {
                text.Append($"compile: ok ({CompileErrorCount} errors, {CompileSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s)\n");
            }
            else
            {
                text.Append($"compile: FAILED ({CompileErrorCount} errors)\n");

                foreach (var error in CompileErrors)
                {
                    text.Append("  ").Append(Describe(error)).Append('\n');
                }
            }

            if (TestsRan)
            {
                text.Append($"tests: {Passed} passed, {Failed} failed ({TestMode})");
                text.Append(TestStatus == "completed" ? "\n" : $", status {TestStatus}\n");

                foreach (var failure in Failures)
                {
                    text.Append("  ").Append(failure.Name).Append(": ").Append(OneLine(failure.Message)).Append('\n');
                }
            }

            if (ConsoleRead)
            {
                text.Append(ConsoleErrors.Count == 0
                    ? "console: 0 errors\n"
                    : $"console: {ConsoleErrors.Count} errors (last: {Excerpt(ConsoleErrors[0].Message)})\n");
            }

            return text.ToString();
        }

        /// <summary>
        /// One tool call that outlives a domain reload. The server is simply gone while the reload
        /// runs, for longer than the HTTP client's own retry budget, so a refused connection or a
        /// reset is retried here until the command's deadline rather than reported.
        /// Safe even for compile_request and test_run, because repeating either only answers that
        /// one is already in flight.
        /// </summary>
        private async Task<JsonObject> Call(string tool, JsonObject body, CancellationToken cancellation)
        {
            var json = body.ToJsonString();
            var reauthenticated = false;

            while (true)
            {
                Envelope envelope;

                try
                {
                    envelope = await _context.Client.SendAsync(
                        _instance, HttpMethod.Post, "/tools/" + tool, json, Idempotency.Safe, cancellation);
                }
                catch (UnityError e) when (e.HttpStatus == 401 && !reauthenticated)
                {
                    // A restarted server publishes a new token; a second rejection is the real thing.
                    reauthenticated = true;
                    Rediscover();
                    continue;
                }
                catch (UnityError e) when (e.HttpStatus is null || e.Code == "non_json")
                {
                    // A listener that is shutting down for the domain reload, or one that has
                    // just restarted after it, answers an empty 200 before it can serve; by the
                    // time a probe could look, the server is already healthy, so the reply is
                    // treated as the same window as a refused connection.
                    Rediscover();
                    await Task.Delay(TransportRetryMs, cancellation);
                    continue;
                }
                catch (UnityError e) when (e.HttpStatus >= 500)
                {
                    // A 5xx that carries an error body came from a tool that ran. It is repeated
                    // only while the Editor is still compiling; from an Editor that is up and
                    // settled it is the tool's own failure, and repeating it until the deadline
                    // would only hide it.
                    Rediscover();

                    if (!await IsReloading(cancellation))
                    {
                        throw;
                    }

                    await Task.Delay(TransportRetryMs, cancellation);
                    continue;
                }

                if (envelope.IsError)
                {
                    throw new UnityError(envelope.ErrorCode ?? "tool_failed", envelope.ErrorMessage ?? $"{tool} failed");
                }

                var result = envelope.Result as JsonObject ?? new JsonObject();

                // Even a status read becomes a job while the main thread is held, by a dialog most
                // often. The result arrives through the job once the Editor is free again; a job
                // that vanished went with a domain reload, and the call is simply made again.
                if (Text(result["state"]) == "running" && Text(result["jobId"]) is { Length: > 0 } jobId)
                {
                    Notice(result);

                    var outcome = await AwaitJob(jobId, cancellation);
                    if (outcome is null)
                    {
                        continue;
                    }

                    return outcome;
                }

                return result;
            }
        }

        /// <summary>
        /// Polls a job to its end. Returns its result, throws its failure, or returns null when
        /// the job is no longer known so the caller repeats the original call.
        /// </summary>
        private async Task<JsonObject?> AwaitJob(string jobId, CancellationToken cancellation)
        {
            var path = "/jobs/" + Uri.EscapeDataString(jobId);

            while (true)
            {
                await Task.Delay(_jobIntervalMs, cancellation);

                JsonObject job;

                try
                {
                    job = (await _context.Client.GetAsync(_instance, path, cancellation)).Result as JsonObject ?? new JsonObject();
                }
                catch (UnityError e) when (e.Code == "job_not_found")
                {
                    return null;
                }
                catch (UnityError e) when (e.HttpStatus is null || e.Code == "non_json" || e.HttpStatus == 401)
                {
                    Rediscover();
                    return null;
                }

                switch (Text(job["status"]))
                {
                    case "running":
                        Notice(job);
                        continue;

                    case "completed":
                        return job["result"] as JsonObject ?? new JsonObject();

                    case "cancelled":
                        throw new UnityError("job_cancelled", $"Job {jobId} was cancelled before it ran.");

                    default:
                        throw new UnityError("job_failed", Text(job["error"]) ?? $"Job {jobId} failed.");
                }
            }
        }

        /// <summary>
        /// Prints what holds the main thread the first time a running answer says, and again only
        /// when a dialog appears after a plainer notice. A stall notice counts up every poll, so
        /// printing every change would fill the terminal with the same sentence.
        /// </summary>
        private void Notice(JsonObject running)
        {
            var notice = Text(running["notice"]);

            if (string.IsNullOrEmpty(notice) || notice == _lastNotice)
            {
                return;
            }

            if (_lastNotice is not null && !notice.Contains("showing a dialog", StringComparison.Ordinal))
            {
                return;
            }

            _lastNotice = notice;
            _context.Err.WriteLine(notice);
        }

        /// <summary>
        /// True while the Editor is unreachable or reports a compilation in progress; false once
        /// /health answers and compile_status says the domain is settled.
        /// </summary>
        private async Task<bool> IsReloading(CancellationToken cancellation)
        {
            try
            {
                await _context.Client.GetAsync(_instance, "/health", cancellation);

                var status = await _context.Client.SendAsync(
                    _instance, HttpMethod.Post, "/tools/compile_status", StatusBody().ToJsonString(), Idempotency.Safe, cancellation);
                var result = status.Result as JsonObject;

                return Flag(result?["isCompiling"]) || Flag(result?["isUpdating"]);
            }
            catch (UnityError)
            {
                return true;
            }
        }

        /// <summary>Re-reads the descriptor, whose port and token both change when the server restarts.</summary>
        private void Rediscover()
        {
            try
            {
                _instance = _context.ResolveInstance(_parsed);
            }
            catch (CliException)
            {
                // The descriptor is rewritten rather than updated, so it is briefly absent.
            }
        }

        private void AddOption(JsonObject body, string name)
        {
            var value = _parsed.Option(name);

            if (!string.IsNullOrEmpty(value))
            {
                body[name] = value;
            }
        }

        private void CollectCompileErrors(JsonArray? messages)
        {
            foreach (var message in messages ?? [])
            {
                if (message is JsonObject entry && Text(entry["type"]) == "error")
                {
                    CompileErrors.Add(new CompileError(
                        Text(entry["file"]) ?? "",
                        Number(entry["line"]),
                        Number(entry["column"]),
                        Text(entry["message"]) ?? ""));
                }
            }
        }

        private void CollectFailures(JsonArray? results)
        {
            foreach (var result in results ?? [])
            {
                if (result is not JsonObject test)
                {
                    continue;
                }

                var status = Text(test["status"]);

                if (status != "passed" && status != "skipped")
                {
                    Failures.Add(new TestFailure(Text(test["name"]) ?? "", Text(test["message"]) ?? ""));
                }
            }
        }

        private static JsonObject StatusBody() => new() { ["include_warnings"] = false, ["limit"] = 200 };

        /// <summary>
        /// Unity's compiler messages already carry the file and position, so the composed prefix
        /// is dropped when it would be repeated.
        /// </summary>
        private static string Describe(CompileError error)
        {
            var position = $"{error.File}({error.Line},{error.Column}):";
            var message = OneLine(error.Message);

            if (message.StartsWith(position, StringComparison.Ordinal))
            {
                message = message.Substring(position.Length).TrimStart();
            }

            if (message.StartsWith("error ", StringComparison.Ordinal))
            {
                message = message.Substring("error ".Length);
            }

            return error.File.Length == 0 ? message : $"{position} {message}";
        }

        private static string Excerpt(string message)
        {
            var line = OneLine(message);
            return line.Length > 120 ? line.Substring(0, 120) : line;
        }
    }
}
