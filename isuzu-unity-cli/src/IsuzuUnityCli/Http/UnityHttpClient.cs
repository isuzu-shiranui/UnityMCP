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
            attempts++;
            cancellation.ThrowIfCancellationRequested();

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            attemptCts.CancelAfter(_options.PerAttemptTimeoutMs);

            try
            {
                StageTrace.Mark("request-built");
                var (status, body) = await LoopbackHttp.SendAsync(
                    instance.Endpoint.TrimEnd('/'), method.Method, path, instance.Token, jsonBody, attemptCts.Token);
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
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
            {
                if (RetryPolicy.Classify(idempotency, e) == Classification.Fatal)
                {
                    var code = RetryPolicy.CodeOf(e);
                    throw new UnityError(code, $"Fetch failed: {code}", null, e);
                }

                lastException = e;
                lastStatus = null;
            }

            var elapsed = stopwatch.ElapsedMilliseconds;

            if (elapsed >= _options.BudgetMs)
            {
                if (lastStatus is int exhaustedStatus)
                {
                    return Envelope.Parse(exhaustedStatus, lastBody);
                }

                var code = lastException is null ? "unknown" : RetryPolicy.CodeOf(lastException);
                throw new UnityError(code, $"Retry budget exhausted after {attempts} attempt(s) ({elapsed}ms): {code}", null, lastException);
            }

            var remaining = _options.BudgetMs - elapsed;
            var wait = (int)Math.Min(Math.Min(backoff, _options.MaxBackoffMs), Math.Max(1, remaining));
            await Task.Delay(wait, cancellation);
            backoff = Math.Min(backoff * 2, _options.MaxBackoffMs);
        }
    }
}
