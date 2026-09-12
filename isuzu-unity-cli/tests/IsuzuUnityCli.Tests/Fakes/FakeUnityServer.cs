using System.Net;
using System.Net.Sockets;
using System.Text;
using IsuzuUnityCli.Discovery;

namespace IsuzuUnityCli.Tests.Fakes;

public sealed record RecordedRequest(string Method, string Path, string? Authorization, string Body, string? SessionId = null);

/// <summary>One scripted reply: the status, body, content type and any headers to send with it.</summary>
public sealed record ScriptedResponse(
    int Status,
    string Body,
    string ContentType = "application/json",
    IReadOnlyDictionary<string, string>? Headers = null)
{
    public IReadOnlyDictionary<string, string> Headers { get; init; } = Headers ?? new Dictionary<string, string>();

    /// <summary>Closes the connection without answering, the way a domain reload does.</summary>
    public bool Drop { get; init; }
}

/// <summary>Loopback HTTP server that answers with scripted responses and records what it received.</summary>
public sealed class FakeUnityServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Queue<ScriptedResponse> _scripted = new();
    private readonly object _gate = new();
    private readonly Task _loop;
    private ScriptedResponse _fallback = new(200, """{"status":"success","result":{}}""");

    public int Port { get; }
    public string Endpoint => $"http://127.0.0.1:{Port}";
    public List<RecordedRequest> Requests { get; } = new();

    public FakeUnityServer(int? port = null)
    {
        // TCP works in restricted Windows sessions where HTTP.sys cannot create a
        // request queue. Binding port zero also removes the probe/bind port race.
        _listener = new TcpListener(IPAddress.Loopback, port ?? 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(ServeAsync);
    }

    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public FakeUnityServer Enqueue(int status, string body)
    {
        return Enqueue(new ScriptedResponse(status, body));
    }

    public FakeUnityServer EnqueueDrop()
    {
        return Enqueue(new ScriptedResponse(0, "") { Drop = true });
    }

    /// <summary>The reply for every request once the scripted queue is empty.</summary>
    public FakeUnityServer Default(int status, string body)
    {
        lock (_gate)
        {
            _fallback = new ScriptedResponse(status, body);
        }

        return this;
    }

    public FakeUnityServer Enqueue(ScriptedResponse response)
    {
        lock (_gate)
        {
            _scripted.Enqueue(response);
        }

        return this;
    }

    public InstanceDescriptor Descriptor(string projectName = "Fake", string token = "secret-token")
    {
        return DescriptorFor(Port, projectName, token);
    }

    public static InstanceDescriptor DescriptorFor(int port, string projectName = "Fake", string token = "secret-token")
    {
        return new InstanceDescriptor
        {
            ProjectName = projectName,
            ProjectPath = Path.Combine(Path.GetTempPath(), projectName, "Assets"),
            UnityVersion = "6000.0.0f1",
            Port = port,
            Token = token,
            Pid = 0,
            ProtocolVersion = "3.3.1",
            Endpoint = $"http://127.0.0.1:{port}",
        };
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            using (client)
            {
                try
                {
                    await AnswerAsync(client);
                }
                catch (IOException)
                {
                    // A caller that gives up closes its connection partway through a request,
                    // which is what --timeout does, and that can land while the test is already
                    // disposing the server. It ends this connection, not the server.
                }
            }
        }
    }

    private async Task AnswerAsync(TcpClient client)
    {
        var stream = client.GetStream();
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            await stream.ReadExactlyAsync(one, _stop.Token);
            head.Append((char)one[0]);
        }
        var lines = head.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var request = lines[0].Split(' ', 3);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            headers[line[..colon]] = line[(colon + 1)..].Trim();
        }
        var bodyBytes = new byte[headers.TryGetValue("Content-Length", out var size) ? int.Parse(size) : 0];
        await stream.ReadExactlyAsync(bodyBytes, _stop.Token);
        ScriptedResponse response;

        lock (_gate)
        {
            Requests.Add(new RecordedRequest(request[0], request[1], headers.GetValueOrDefault("Authorization"),
                Encoding.UTF8.GetString(bodyBytes), headers.GetValueOrDefault("Mcp-Session-Id")));

            response = _scripted.Count > 0 ? _scripted.Dequeue() : _fallback;
        }

        if (response.Drop)
        {
            client.Client.LingerState = new LingerOption(true, 0);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(response.Body);
        var reply = new StringBuilder($"HTTP/1.1 {response.Status} Reply\r\nConnection: close\r\nContent-Length: {bytes.Length}\r\n");

        foreach (var header in response.Headers)
            reply.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");

        if (bytes.Length > 0)
            reply.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        reply.Append("\r\n");
        await stream.WriteAsync(Encoding.UTF8.GetBytes(reply.ToString()), _stop.Token);
        await stream.WriteAsync(bytes, _stop.Token);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try { _loop.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
