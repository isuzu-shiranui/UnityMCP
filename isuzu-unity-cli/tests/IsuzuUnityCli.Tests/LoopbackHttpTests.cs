using System.Diagnostics;
using IsuzuUnityCli.Http;
using IsuzuUnityCli.Tests.Fakes;
using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public sealed class LoopbackHttpTests
{
    [Fact]
    public async Task CancellationInterruptsAConnectedSilentServer()
    {
        using var server = new TcpReplyServer(TcpReplyServer.Reply.Json("{}", delayMs: 5000));
        using var cancel = new CancellationTokenSource();
        var call = Task.Run(() => LoopbackHttp.SendAsync(server.Descriptor.Endpoint, "GET", "/", null, null, cancel.Token));
        await RecordingWriter.WaitFor(() => server.Requests == 1, "request arrival");
        var elapsed = Stopwatch.StartNew();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AnUnsafeAttemptPastItsOwnTimeoutFailsWithoutResending()
    {
        using var server = new TcpReplyServer(TcpReplyServer.Reply.Json("{}", delayMs: 5000));
        var client = new UnityHttpClient(new RetryOptions { BudgetMs = 5000, PerAttemptTimeoutMs = 100 });
        var elapsed = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<UnityError>(() => Task.Run(() => client.PostAsync(server.Descriptor, "/tools/write", new System.Text.Json.Nodes.JsonObject())).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("ETIMEDOUT", error.Code);
        Assert.Equal(1, server.Requests);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// The retry budget decides whether another attempt starts; it does not cut one that has.
    /// </summary>
    /// <remarks>
    /// The Editor answers within its sync wait, which Preferences can set past the budget, and a
    /// call cut off there was reported as a lost connection after the tool may already have run.
    /// </remarks>
    [Fact]
    public async Task AStartedAttemptIsNotCutShortByTheRetryBudget()
    {
        using var server = new TcpReplyServer(TcpReplyServer.Reply.Json("{\"ok\":true,\"result\":{}}", delayMs: 400));
        var client = new UnityHttpClient(new RetryOptions { BudgetMs = 50, PerAttemptTimeoutMs = 5000 });

        var reply = await Task.Run(() => client.PostAsync(server.Descriptor, "/tools/write", new System.Text.Json.Nodes.JsonObject())).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(reply.IsError);
        Assert.Equal(1, server.Requests);
    }

    [Theory]
    [InlineData("Content-Length: 100\r\n", "{}")]
    [InlineData("Content-Length: -1\r\n", "{}")]
    [InlineData("Content-Length: 2\r\nContent-Length: 3\r\n", "{}")]
    [InlineData("Transfer-Encoding: chunked\r\n", "2\r\n{}\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\n", "4\r\n{}\r\n0\r\n\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\n", "zz\r\n{}\r\n0\r\n\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\n", "2\r\n{}xx0\r\n\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\n", "2\r\n{}\r\n0\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\nContent-Length: 2\r\n", "{}")]
    public async Task IncompleteOrMalformedFramingIsRejected(string headers, string body)
    {
        using var server = new TcpReplyServer(new TcpReplyServer.Reply("HTTP/1.1 200 OK\r\n" + headers + "\r\n" + body));

        // Any IOException: a reply that stops early is a connection that ended, while one that is
        // framed wrongly is reported as its own kind, because the advice for the two differs.
        await Assert.ThrowsAnyAsync<IOException>(() => LoopbackHttp.SendAsync(server.Descriptor.Endpoint, "GET", "/", null, null, default));
    }

    /// <summary>
    /// A legal Transfer-Encoding that is not chunked is read, not refused.
    /// </summary>
    /// <remarks>
    /// The Editor always sends a Content-Length, so these only arrive through the proxy that
    /// UNITY_MCP_HOST exists for. Refusing them read as the Editor being broken.
    /// </remarks>
    [Theory]
    [InlineData("Transfer-Encoding: identity\r\nContent-Length: 2\r\n", "{}")]
    [InlineData("Transfer-Encoding: gzip, chunked\r\n", "2\r\n{}\r\n0\r\n\r\n")]
    public async Task ATransferEncodingOtherThanChunkedIsAccepted(string headers, string body)
    {
        using var server = new TcpReplyServer(new TcpReplyServer.Reply("HTTP/1.1 200 OK\r\n" + headers + "\r\n" + body, KeepOpen: true));
        var (status, text) = await LoopbackHttp.SendAsync(server.Descriptor.Endpoint, "GET", "/", null, null, default);

        Assert.Equal(200, status);
        Assert.Equal("{}", text);
    }

    [Theory]
    [InlineData("Content-Length: 2\r\n", "{}")]
    [InlineData("Transfer-Encoding: chunked\r\n", "2;extension=yes\r\n{}\r\n0\r\n\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\n", "2\r\n{}\r\n0\r\nTrailer: yes\r\n\r\n")]
    public async Task CompleteFramingFinishesWithoutWaitingForConnectionClose(string headers, string body)
    {
        using var server = new TcpReplyServer(new TcpReplyServer.Reply("HTTP/1.1 200 OK\r\n" + headers + "\r\n" + body, KeepOpen: true));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var response = await LoopbackHttp.SendAsync(server.Descriptor.Endpoint, "GET", "/", null, null, cancel.Token);
        Assert.Equal("{}", response.Body);
        Assert.False(cancel.IsCancellationRequested);
    }

    [Fact]
    public async Task SafeReadRetriesTransientStatus()
    {
        using var server = new TcpReplyServer(TcpReplyServer.Reply.Json("{}", 503), TcpReplyServer.Reply.Json("{\"status\":\"success\",\"result\":{\"ok\":true}}"));
        var result = await new UnityHttpClient().GetAsync(server.Descriptor, "/health");
        Assert.True(result.Result!["ok"]!.GetValue<bool>());
        Assert.Equal(2, server.Requests);
    }
}
