using System.Net;
using System.Net.Sockets;
using System.Text;
using IsuzuUnityCli.Discovery;

namespace IsuzuUnityCli.Tests.Fakes;

/// <summary>Raw responses let transport tests exercise framing errors without HTTP.sys.</summary>
internal sealed class TcpReplyServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serve;
    private int _requests;

    public int Requests => Volatile.Read(ref _requests);
    public InstanceDescriptor Descriptor { get; }

    public TcpReplyServer(params Reply[] replies)
    {
        _listener.Start();
        Descriptor = new InstanceDescriptor
        {
            Endpoint = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port,
            ProjectName = "Mock",
            Token = "test"
        };
        _serve = Task.Run(async () =>
        {
            foreach (var reply in replies)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var stream = client.GetStream();
                var header = new StringBuilder();
                var one = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    await stream.ReadExactlyAsync(one, _stop.Token);
                    header.Append((char)one[0]);
                }
                foreach (var line in header.ToString().Split("\r\n"))
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        await stream.ReadExactlyAsync(new byte[int.Parse(line[15..].Trim())], _stop.Token);
                Interlocked.Increment(ref _requests);
                await Task.Delay(reply.DelayMs, _stop.Token);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(reply.Raw), _stop.Token);
                if (reply.KeepOpen)
                    await Task.Delay(Timeout.Infinite, _stop.Token);
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try { _serve.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        _stop.Dispose();
    }

    internal sealed record Reply(string Raw, int DelayMs = 0, bool KeepOpen = false)
    {
        public static Reply Json(string json, int status = 200, int delayMs = 0) =>
            new($"HTTP/1.1 {status} Reply\r\nContent-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}", delayMs);
    }
}
