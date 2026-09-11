using System.Diagnostics;
using System.Text.Json.Nodes;
using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Discovery;

namespace IsuzuUnityCli.Http;

/// <summary>Talks to one Editor's HTTP server with the retry classification the MCP server uses.</summary>
public sealed class UnityHttpClient
{
    private readonly RetryOptions _options;

    public UnityHttpClient(RetryOptions? options = null)
    {
        _options = options ?? new RetryOptions();
    }

    public Task<Envelope> GetAsync(InstanceDescriptor instance, string path, CancellationToken cancellation = default)
    {
        return SendAsync(instance, HttpMethod.Get, path, null, Idempotency.Safe, cancellation);
    }

    public Task<Envelope> PostAsync(InstanceDescriptor instance, string path, JsonNode body, CancellationToken cancellation = default)
    {
        return SendAsync(instance, HttpMethod.Post, path, body.ToJsonString(), Idempotency.Unsafe, cancellation);
    }

    public async Task<Envelope> SendAsync(
        InstanceDescriptor instance,
        HttpMethod method,
        string path,
        string? jsonBody,
        Idempotency idempotency,
        CancellationToken cancellation = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var backoff = _options.InitialBackoffMs;
        var attempts = 0;
        Exception? lastException = null;
        int? lastStatus = null;
        var lastBody = "";

        while (true)
        {
            cancellation.ThrowIfCancellationRequested();

            if (attempts > 0 && stopwatch.ElapsedMilliseconds >= _options.BudgetMs)
            {
                return Exhausted();
            }

            attempts++;

            // The budget decides whether another attempt starts, not how long a started one may
            // take. A tool answers within the Editor's sync wait, which Preferences can set past the
            // budget, and cutting the attempt there reported a slow call as a lost connection that
            // may already have done its work.
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            attemptCts.CancelAfter(_options.PerAttemptTimeoutMs);
            using var connectCts = new CancellationTokenSource(
                (int)Math.Max(1, _options.BudgetMs - stopwatch.ElapsedMilliseconds));

            try
            {
                StageTrace.Mark("request-built");
                var (status, body) = await LoopbackHttp.SendAsync(
                    instance.Endpoint.TrimEnd('/'), method.Method, path, instance.Token, jsonBody, attemptCts.Token,
                    connectCts.Token);
                attemptCts.Token.ThrowIfCancellationRequested();
                StageTrace.Mark("response");
                var classification = RetryPolicy.Classify(idempotency, status);

                if (classification != Classification.Retryable)
                {
                    return Envelope.Parse(status, body);
                }

                lastStatus = status;
                lastBody = body;
                lastException = null;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            // ObjectDisposedException among them: cancelling closes the socket, and a callback
            // landing between the connect returning and its registration being disposed reaches
            // the send. Uncaught it leaves the process on an exit code the contract does not name.
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException
                or IOException or ObjectDisposedException)
            {
                if (RetryPolicy.Classify(idempotency, e) == Classification.Fatal)
                {
                    var code = RetryPolicy.CodeOf(e);

                    // An answer that arrived and could not be read as HTTP is not a reload.
                    // Waiting does not resolve it, so it is reported as what it is.
                    if (e is LoopbackHttp.MalformedResponseException)
                    {
                        throw new UnityError(
                            code,
                            $"The Editor's reply could not be read as HTTP: {e.Message} Something "
                            + "other than the Editor may be listening on that port.",
                            null,
                            e);
                    }

                    // A connection that dies mid-request rather than being refused outright is
                    // what entering or leaving play mode looks like from here, and this is the one
                    // place text can still reach the caller: the Editor is gone, so its own reply
                    // cannot explain anything. Left at "Fetch failed", the caller reads a broken
                    // tool instead of a reload it only has to wait out.
                    throw new UnityError(
                        code,
                        $"Fetch failed: {code}. The Editor stops answering while it rebuilds its "
                        + "domain, which entering or leaving play mode and any script change "
                        + "trigger; it comes back on its own within a few seconds. This call was "
                        + "not repeated automatically because it changes something and may "
                        + "already have arrived - check the state before sending it again.",
                        null,
                        e);
                }

                lastException = e;
                lastStatus = null;
            }

            var elapsed = stopwatch.ElapsedMilliseconds;

            if (elapsed >= _options.BudgetMs)
            {
                return Exhausted();
            }

            var remaining = _options.BudgetMs - elapsed;
            var wait = (int)Math.Min(Math.Min(backoff, _options.MaxBackoffMs), Math.Max(1, remaining));
            await Task.Delay(wait, cancellation);
            backoff = Math.Min(backoff * 2, _options.MaxBackoffMs);
        }

        Envelope Exhausted()
        {
            if (lastStatus is int status)
            {
                return Envelope.Parse(status, lastBody);
            }

            // Reached only after an attempt, and an attempt always leaves one of the two set.
            var code = lastException is null ? "ETIMEDOUT" : RetryPolicy.CodeOf(lastException);
            var why = code is "ECONNREFUSED" or "ECONNTIMEOUT"
                ? " Nothing is listening on the port. The Editor stops serving while it rebuilds "
                  + "its domain, which a script change triggers and which ends on its own, so "
                  + "the same call usually works a few seconds later. If it does not, the Editor "
                  + "is closed or the server was stopped in Preferences."
                : string.Empty;

            throw new UnityError(
                code,
                $"Retry budget exhausted after {attempts} attempt(s) ({stopwatch.ElapsedMilliseconds}ms): {code}.{why}",
                null,
                lastException);
        }
    }
}
