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
    private readonly HttpListener _listener = new();
    private readonly Queue<ScriptedResponse> _scripted = new();
    private readonly object _gate = new();
    private readonly Task _loop;
    private ScriptedResponse _fallback = new(200, """{"status":"success","result":{}}""");

    public int Port { get; }
    public string Endpoint => $"http://127.0.0.1:{Port}";
    public List<RecordedRequest> Requests { get; } = new();

    public FakeUnityServer(int? port = null)
    {
        Port = port ?? FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
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
        while (_listener.IsListening)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }

            ScriptedResponse response;

            lock (_gate)
            {
                Requests.Add(new RecordedRequest(
                    context.Request.HttpMethod,
                    context.Request.Url?.PathAndQuery ?? "",
                    context.Request.Headers["Authorization"],
                    body,
                    context.Request.Headers["Mcp-Session-Id"]));

                response = _scripted.Count > 0 ? _scripted.Dequeue() : _fallback;
            }

            if (response.Drop)
            {
                context.Response.Abort();
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(response.Body);
            context.Response.StatusCode = response.Status;

            foreach (var header in response.Headers)
            {
                context.Response.Headers[header.Key] = header.Value;
            }

            if (bytes.Length > 0)
            {
                context.Response.ContentType = response.ContentType;
            }

            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        _loop.Wait(TimeSpan.FromSeconds(2));
    }
}
