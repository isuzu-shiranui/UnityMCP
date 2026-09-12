using System.Globalization;
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

    /// <summary>A connection that was never made, so nothing of the request was sent.</summary>
    public sealed class ConnectNotCompletedException(string message) : IOException(message);

    /// <summary>
    /// An answer that arrived and could not be read as HTTP.
    /// </summary>
    /// <remarks>
    /// Separate from a connection that dropped, which is what a domain reload looks like and
    /// resolves on its own. This one does not, so advising the caller to wait a few seconds and
    /// try again would send them nowhere.
    /// </remarks>
    public sealed class MalformedResponseException(string message) : IOException(message);

    /// <summary>
    /// Synchronous on purpose: the asynchronous socket path starts a completion-port engine and
    /// its thread on first use, several milliseconds that a single loopback exchange never earns
    /// back. Cancellation closes the socket to interrupt a blocked connect, send or receive.
    /// </summary>
    /// <param name="connectCancellation">
    /// Stops waiting for the connection only. A refused loopback connect takes about two seconds on
    /// Windows, and that wait belongs to the retry budget; the wait for a reply does not, because by
    /// then the request has been sent.
    /// </param>
    public static Task<(int Status, string Body)> SendAsync(
        string endpoint,
        string method,
        string path,
        string? bearer,
        string? jsonBody,
        CancellationToken cancellation,
        CancellationToken connectCancellation = default)
    {
        return Task.FromResult(Send(endpoint, method, path, bearer, jsonBody, cancellation, connectCancellation));
    }

    public static (int Status, string Body) Send(
        string endpoint,
        string method,
        string path,
        string? bearer,
        string? jsonBody,
        CancellationToken cancellation,
        CancellationToken connectCancellation = default)
    {
        var (host, port) = HostAndPort(endpoint);
        var address = IPAddress.TryParse(host, out var literal) ? literal : null;
        cancellation.ThrowIfCancellationRequested();

        using var socket = new Socket(address?.AddressFamily ?? AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        socket.SendTimeout = TimeoutMs;
        socket.ReceiveTimeout = TimeoutMs;
        using var registration = cancellation.Register(static state => ((Socket)state!).Dispose(), socket);
        var connecting = connectCancellation.Register(static state => ((Socket)state!).Dispose(), socket);

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
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
            cancellation.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception) when (connectCancellation.IsCancellationRequested)
        {
            throw new ConnectNotCompletedException($"Connect to {host}:{port} did not complete in time.");
        }
        catch (SocketException e)
        {
            throw new IOException($"Connect to {host}:{port} failed: {e.SocketErrorCode}", e);
        }
        finally
        {
            connecting.Dispose();
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
            SendAll(socket, head, cancellation);

            if (bodyBytes.Length > 0)
            {
                SendAll(socket, bodyBytes, cancellation);
            }

            var response = ReadAll(socket, cancellation);
            cancellation.ThrowIfCancellationRequested();
            return Parse(response);
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
            cancellation.ThrowIfCancellationRequested();
            throw;
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

    private static void SendAll(Socket socket, byte[] bytes, CancellationToken cancellation)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            var sent = socket.Send(bytes, offset, bytes.Length - offset, SocketFlags.None);
            if (sent == 0)
                throw new IOException("The server closed the connection during the request.");
            offset += sent;
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
        using var received = new MemoryStream();
        var headerEnd = -1;
        long contentLength = -1;
        var chunked = false;

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
            if (headerEnd < 0)
                TryFindHeaderEnd(received, out headerEnd, out contentLength, out chunked);

            if (headerEnd >= 0 && (contentLength >= 0 && received.Length - headerEnd >= contentLength
                || chunked && TryDecodeChunked(received.GetBuffer().AsSpan(headerEnd, (int)received.Length - headerEnd), null)))
            {
                break;
            }
        }

        return received.ToArray();
    }

    private static bool TryFindHeaderEnd(MemoryStream stream, out int headerEnd, out long contentLength, out bool chunked)
    {
        contentLength = -1;
        chunked = false;
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
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                // Digits only, and read the same way on every machine: the header is ASCII and
                // has nothing to do with the culture the process happens to run under.
                if (!long.TryParse(line.AsSpan(15).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                    || length < 0 || contentLength >= 0 && contentLength != length)
                    throw new MalformedResponseException("Invalid Content-Length.");
                contentLength = length;
            }
            if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))
            {
                var coding = line.AsSpan(18).Trim();

                // 'identity' names the absence of a transfer coding, and a list ends with the one
                // applied last. Read the way the body reader reads it, which looks for 'chunked'
                // anywhere; the two disagreeing meant a legal header was refused here and framed
                // there. Only the Editor's own replies come through without a proxy in between,
                // and those always carry a Content-Length.
                if (coding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                    chunked = true;
                else if (!coding.Equals("identity", StringComparison.OrdinalIgnoreCase))
                    throw new MalformedResponseException("Unsupported Transfer-Encoding.");
            }
        }

        if (chunked && contentLength >= 0)
            throw new MalformedResponseException("Ambiguous HTTP response framing.");

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
            throw new MalformedResponseException($"Malformed status line: {lines[0]}");
        }

        var body = span.Slice(headerEnd + HeaderEnd.Length);
        var chunked = false;
        long contentLength = -1;

        foreach (var line in lines)
        {
            if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)
                && line.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                chunked = true;
            }
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(line.AsSpan(15).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                && length >= 0)
                contentLength = length;
        }

        if (!chunked && contentLength >= 0)
        {
            if (body.Length < contentLength)
                throw new IOException("The server closed the connection before the response body ended.");
            body = body[..(int)contentLength];
        }

        return (status, chunked ? DecodeChunked(body) : Encoding.UTF8.GetString(body));
    }

    private static string DecodeChunked(ReadOnlySpan<byte> body)
    {
        using var output = new MemoryStream(body.Length);
        if (!TryDecodeChunked(body, output))
            throw new IOException("The server closed the connection before the chunked response ended.");
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    private static bool TryDecodeChunked(ReadOnlySpan<byte> body, MemoryStream? output)
    {
        var offset = 0;

        while (offset < body.Length)
        {
            var lineEnd = body.Slice(offset).IndexOf("\r\n"u8);

            if (lineEnd < 0)
            {
                return false;
            }

            var sizeText = Encoding.ASCII.GetString(body.Slice(offset, lineEnd));
            var semicolon = sizeText.IndexOf(';');
            if (!int.TryParse(semicolon >= 0 ? sizeText[..semicolon] : sizeText,
                    System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture, out var size) || size < 0)
                throw new MalformedResponseException("Invalid HTTP chunk size.");
            offset += lineEnd + 2;

            if (size == 0)
            {
                var trailer = body[offset..];
                return trailer.StartsWith("\r\n"u8) || trailer.IndexOf(HeaderEnd) >= 0;
            }

            if (body.Length - offset < 2 || size > body.Length - offset - 2)
                return false;
            if (!body.Slice(offset + size, 2).SequenceEqual("\r\n"u8))
                throw new MalformedResponseException("Invalid HTTP chunk delimiter.");
            output?.Write(body.Slice(offset, size));
            offset += size + 2;
        }

        return false;
    }
}
