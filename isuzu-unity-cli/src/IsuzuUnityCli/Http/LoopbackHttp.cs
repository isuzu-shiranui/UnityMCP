using System.Net;
using System.Net.Sockets;
using System.Text;
using IsuzuUnityCli.Cli;

namespace IsuzuUnityCli.Http;

/// <summary>
/// HTTP/1.1 over a plain socket, for the loopback server only. The general-purpose client spends
/// tens of milliseconds on its first request setting up handlers, connection pools and
/// diagnostics that a one-shot process never reuses, and that time was most of a call.
/// </summary>
public static class LoopbackHttp
{
    private static readonly byte[] HeaderEnd = "\r\n\r\n"u8.ToArray();

    /// <summary>Per-exchange socket timeout; the retry loop above it enforces the shorter budget.</summary>
    private const int TimeoutMs = 30000;

    /// <summary>
    /// Synchronous on purpose: the asynchronous socket path starts a completion-port engine and
    /// its thread on first use, several milliseconds that a single loopback exchange never earns
    /// back. Cancellation is honoured between steps and through the socket timeouts.
    /// </summary>
    public static Task<(int Status, string Body)> SendAsync(
        string endpoint,
        string method,
        string path,
        string? bearer,
        string? jsonBody,
        CancellationToken cancellation)
    {
        return Task.FromResult(Send(endpoint, method, path, bearer, jsonBody, cancellation));
    }

    public static (int Status, string Body) Send(
        string endpoint,
        string method,
        string path,
        string? bearer,
        string? jsonBody,
        CancellationToken cancellation)
    {
        var (host, port) = HostAndPort(endpoint);
        var address = IPAddress.TryParse(host, out var literal) ? literal : null;
        cancellation.ThrowIfCancellationRequested();

        using var socket = new Socket(address?.AddressFamily ?? AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        socket.SendTimeout = TimeoutMs;
        socket.ReceiveTimeout = TimeoutMs;

        try
        {
            if (address is not null)
            {
                socket.Connect(new IPEndPoint(address, port));
            }
            else
            {
                socket.Connect(host, port);
            }
        }
        catch (SocketException e)
        {
            throw new IOException($"Connect to {host}:{port} failed: {e.SocketErrorCode}", e);
        }

        StageTrace.Mark("connected");
        var request = new StringBuilder(256)
            .Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(host).Append(':').Append(port).Append("\r\n")
            .Append("Connection: close\r\n")
            .Append("Accept: application/json\r\n");

        if (bearer is not null)
        {
            request.Append("Authorization: Bearer ").Append(bearer).Append("\r\n");
        }

        var bodyBytes = jsonBody is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(jsonBody);

        if (jsonBody is not null)
        {
            request.Append("Content-Type: application/json; charset=utf-8\r\n")
                .Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
        }

        request.Append("\r\n");
        var head = Encoding.ASCII.GetBytes(request.ToString());

        try
        {
            socket.Send(head);

            if (bodyBytes.Length > 0)
            {
                socket.Send(bodyBytes);
            }

            return Parse(ReadAll(socket, cancellation));
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
        {
            throw new OperationCanceledException($"No reply from {host}:{port} within {TimeoutMs} ms.", e, cancellation);
        }
        catch (SocketException e)
        {
            throw new IOException($"Request to {host}:{port} failed: {e.SocketErrorCode}", e);
        }
    }

    /// <summary>"http://127.0.0.1:27400" without the cost of the general URI parser.</summary>
    private static (string Host, int Port) HostAndPort(string endpoint)
    {
        var span = endpoint.AsSpan();
        var scheme = span.IndexOf("://", StringComparison.Ordinal);
        var authority = scheme >= 0 ? span.Slice(scheme + 3) : span;
        var slash = authority.IndexOf('/');

        if (slash >= 0)
        {
            authority = authority.Slice(0, slash);
        }

        var colon = authority.LastIndexOf(':');

        if (colon < 0 || !int.TryParse(authority.Slice(colon + 1), out var port))
        {
            return (authority.ToString(), 80);
        }

        return (authority.Slice(0, colon).ToString(), port);
    }

    /// <summary>Reads until the peer closes; the request asked for that with Connection: close.</summary>
    private static byte[] ReadAll(Socket socket, CancellationToken cancellation)
    {
        var buffer = new byte[16 * 1024];
        var received = new MemoryStream();

        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var n = socket.Receive(buffer);

            if (n == 0)
            {
                break;
            }

            received.Write(buffer, 0, n);

            // A server that keeps the connection open despite Connection: close still ends the
            // message with Content-Length; stop as soon as that many body bytes are in.
            if (TryFindHeaderEnd(received, out var headerEnd, out var contentLength) && contentLength >= 0
                && received.Length >= headerEnd + contentLength)
            {
                break;
            }
        }

        return received.ToArray();
    }

    private static bool TryFindHeaderEnd(MemoryStream stream, out int headerEnd, out long contentLength)
    {
        contentLength = -1;
        var span = new ReadOnlySpan<byte>(stream.GetBuffer(), 0, (int)stream.Length);
        var at = span.IndexOf(HeaderEnd);

        if (at < 0)
        {
            headerEnd = -1;
            return false;
        }

        headerEnd = at + HeaderEnd.Length;
        var headers = Encoding.ASCII.GetString(span.Slice(0, at));

        foreach (var line in headers.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(line.AsSpan(15).Trim(), out var length))
            {
                contentLength = length;
            }
        }

        return true;
    }

    private static (int Status, string Body) Parse(byte[] response)
    {
        if (response.Length == 0)
        {
            throw new IOException("The server closed the connection without answering.");
        }

        var span = new ReadOnlySpan<byte>(response);
        var headerEnd = span.IndexOf(HeaderEnd);

        if (headerEnd < 0)
        {
            throw new IOException("The server closed the connection before the response headers ended.");
        }

        var headerText = Encoding.ASCII.GetString(span.Slice(0, headerEnd));
        var lines = headerText.Split("\r\n");
        var statusLine = lines[0].Split(' ', 3);

        if (statusLine.Length < 2 || !int.TryParse(statusLine[1], out var status))
        {
            throw new IOException($"Malformed status line: {lines[0]}");
        }

        var body = span.Slice(headerEnd + HeaderEnd.Length);
        var chunked = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)
                && line.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                chunked = true;
            }
        }

        return (status, chunked ? DecodeChunked(body) : Encoding.UTF8.GetString(body));
    }

    private static string DecodeChunked(ReadOnlySpan<byte> body)
    {
        var output = new MemoryStream(body.Length);
        var offset = 0;

        while (offset < body.Length)
        {
            var lineEnd = body.Slice(offset).IndexOf("\r\n"u8);

            if (lineEnd < 0)
            {
                break;
            }

            var sizeText = Encoding.ASCII.GetString(body.Slice(offset, lineEnd));
            var semicolon = sizeText.IndexOf(';');
            var size = Convert.ToInt32(semicolon >= 0 ? sizeText[..semicolon] : sizeText, 16);
            offset += lineEnd + 2;

            if (size == 0)
            {
                break;
            }

            output.Write(body.Slice(offset, Math.Min(size, body.Length - offset)));
            offset += size + 2;
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }
}
