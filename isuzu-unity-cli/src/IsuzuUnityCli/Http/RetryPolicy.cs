using System.Net.Sockets;

namespace IsuzuUnityCli.Http;

public enum Idempotency
{
    Safe,
    Unsafe,
}

public enum Classification
{
    Success,
    Retryable,
    Fatal,
}

public sealed class RetryOptions
{
    public int InitialBackoffMs { get; init; } = 100;
    public int MaxBackoffMs { get; init; } = 1000;
    public int BudgetMs { get; init; } = 15000;
    public int PerAttemptTimeoutMs { get; init; } = 30000;
}

public static class RetryPolicy
{
    /// <summary>
    /// Only the gateway statuses mean the request never reached a tool. A status a tool chose for
    /// itself describes a condition that a repeat would meet again, so repeating it would burn the
    /// whole budget on a deterministic failure.
    /// </summary>
    public static Classification Classify(Idempotency idempotency, int httpStatus)
    {
        if (httpStatus >= 200 && httpStatus < 300)
        {
            return Classification.Success;
        }

        if (httpStatus is 502 or 503 or 504)
        {
            return idempotency == Idempotency.Safe ? Classification.Retryable : Classification.Fatal;
        }

        return Classification.Fatal;
    }

    /// <summary>
    /// A refused connection failed before any byte was sent, so even a non-idempotent request can be
    /// repeated. Everything else may have reached the Editor and is retried only for Safe requests.
    /// </summary>
    public static Classification Classify(Idempotency idempotency, Exception exception)
    {
        if (IsConnectionRefused(exception))
        {
            return Classification.Retryable;
        }

        return idempotency == Idempotency.Safe ? Classification.Retryable : Classification.Fatal;
    }

    public static string CodeOf(Exception exception)
    {
        if (IsConnectionRefused(exception))
        {
            return "ECONNREFUSED";
        }

        if (exception is OperationCanceledException)
        {
            return "ETIMEDOUT";
        }

        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            if (e is SocketException socket)
            {
                return socket.SocketErrorCode.ToString();
            }
        }

        return exception.GetType().Name;
    }

    public static bool IsConnectionRefused(Exception exception)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            if (e is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            {
                return true;
            }
        }

        return false;
    }
}
