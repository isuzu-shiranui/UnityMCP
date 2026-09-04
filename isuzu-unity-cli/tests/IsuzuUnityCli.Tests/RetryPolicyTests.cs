using System.Net.Sockets;
using IsuzuUnityCli.Http;
using Xunit;

namespace IsuzuUnityCli.Tests;

public sealed class RetryPolicyTests
{
    private static readonly Exception Refused =
        new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused));

    private static readonly Exception Reset =
        new HttpRequestException("reset", new IOException("reset", new SocketException((int)SocketError.ConnectionReset)));

    [Fact]
    public void ConnectionRefusedIsRetryableEvenForUnsafe()
    {
        Assert.Equal(Classification.Retryable, RetryPolicy.Classify(Idempotency.Unsafe, Refused));
        Assert.Equal(Classification.Retryable, RetryPolicy.Classify(Idempotency.Safe, Refused));
        Assert.Equal("ECONNREFUSED", RetryPolicy.CodeOf(Refused));
    }

    [Fact]
    public void GatewayErrorsAreRetryableForSafeOnly()
    {
        Assert.Equal(Classification.Retryable, RetryPolicy.Classify(Idempotency.Safe, 502));
        Assert.Equal(Classification.Retryable, RetryPolicy.Classify(Idempotency.Safe, 503));
        Assert.Equal(Classification.Retryable, RetryPolicy.Classify(Idempotency.Safe, 504));
        Assert.Equal(Classification.Fatal, RetryPolicy.Classify(Idempotency.Unsafe, 502));
        Assert.Equal(Classification.Fatal, RetryPolicy.Classify(Idempotency.Unsafe, 503));
        Assert.Equal(Classification.Fatal, RetryPolicy.Classify(Idempotency.Unsafe, 504));
    }

    [Fact]
    public void ToolExceptionsAreNeverRetried()
    {
        Assert.Equal(Classification.Fatal, RetryPolicy.Classify(Idempotency.Safe, 500));
        Assert.Equal(Classification.Fatal, RetryPolicy.Classify(Idempotency.Unsafe, 500));
        Assert.Equal(Classification.Fatal, RetryPolicy.Classify(Idempotency.Safe, 501));
        Assert.Equal(Classification.Fatal, RetryPolicy.Classify(Idempotency.Safe, 507));
    }

    [Fact]
    public void ClientErrorIsFatalForBoth()
    {
        Assert.Equal(Classification.Fatal, RetryPolicy.Classify(Idempotency.Safe, 400));
        Assert.Equal(Classification.Fatal, RetryPolicy.Classify(Idempotency.Unsafe, 404));
        Assert.Equal(Classification.Fatal, RetryPolicy.Classify(Idempotency.Safe, 302));
        Assert.Equal(Classification.Success, RetryPolicy.Classify(Idempotency.Unsafe, 202));
    }

    [Fact]
    public void TimeoutAndResetFollowIdempotency()
    {
        var timeout = new TaskCanceledException("timed out");

        Assert.Equal(Classification.Retryable, RetryPolicy.Classify(Idempotency.Safe, timeout));
        Assert.Equal(Classification.Fatal, RetryPolicy.Classify(Idempotency.Unsafe, timeout));
        Assert.Equal("ETIMEDOUT", RetryPolicy.CodeOf(timeout));

        Assert.Equal(Classification.Retryable, RetryPolicy.Classify(Idempotency.Safe, Reset));
        Assert.Equal(Classification.Fatal, RetryPolicy.Classify(Idempotency.Unsafe, Reset));
        Assert.Equal("ConnectionReset", RetryPolicy.CodeOf(Reset));
    }
}
