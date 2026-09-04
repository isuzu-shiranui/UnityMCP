using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using IsuzuUnityCli.Discovery;

namespace IsuzuUnityCli.Bridge;

/// <summary>
/// Carries JSON-RPC between an MCP client's stdio and the Editor's HTTP endpoint.
/// Claude Desktop and the other stdio-only clients cannot open a localhost server themselves,
/// so this process stands in for one.
/// </summary>
public sealed class McpStdioBridge : IDisposable
{
    // Tool calls run inside the Editor and a domain reload alone can take a minute, so the
    // transport must not be the thing that gives up on them.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    private const string SessionHeader = "Mcp-Session-Id";

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly Channel<string> _outbound = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<InstanceDescriptor> _resolve;
    private readonly string? _projectOption;
    private readonly Lock _gate = new();

    private InstanceDescriptor? _descriptor;
    private string? _lastKnownProject;
    private string? _sessionId;

    public McpStdioBridge(
        TextReader input,
        TextWriter output,
        Func<InstanceDescriptor> resolve,
        string? projectOption = null,
        HttpMessageHandler? handler = null)
    {
        _input = input;
        _output = output;
        _resolve = resolve;
        _projectOption = projectOption;
        _ownsHttp = handler is null;

        // The target is always loopback, so a system proxy would only turn every call into a
        // slow failure.
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false }, disposeHandler: _ownsHttp)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<int> RunAsync(CancellationToken cancellation = default)
    {
        var writer = Task.Run(DrainAsync, CancellationToken.None);
        var pending = new List<Task>();

        while (!cancellation.IsCancellationRequested)
        {
            string? line;

            try
            {
                line = await _input.ReadLineAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            if (line.Trim().Length == 0)
            {
                continue;
            }

            // Handled concurrently so a slow tool call cannot block the cancellation notice
            // that would have stopped it.
            pending.Add(HandleAsync(line, cancellation));
            pending.RemoveAll(task => task.IsCompleted);
        }

        await Task.WhenAll(pending);
        await EndSessionAsync();

        _outbound.Writer.TryComplete();
        await writer;
        return 0;
    }

    private async Task DrainAsync()
    {
        await foreach (var line in _outbound.Reader.ReadAllAsync(CancellationToken.None))
        {
            // Written by hand rather than with WriteLine: the framing is \n, and on Windows
            // WriteLine would put a \r in front of it.
            await _output.WriteAsync(line);
            await _output.WriteAsync('\n');
            await _output.FlushAsync();
        }
    }

    private async Task HandleAsync(string message, CancellationToken cancellation)
    {
        try
        {
            var descriptor = Descriptor();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, descriptor.McpUrlOrDefault);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", descriptor.Token);
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");

            var session = Session();
            if (session is not null)
            {
                request.Headers.TryAddWithoutValidation(SessionHeader, session);
            }

            // Sent as the bytes that arrived, so nothing the client wrote is reinterpreted here.
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(message));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content = content;

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            CaptureSession(response);

            if ((int)response.StatusCode == 202)
            {
                return;
            }

            if (string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);

                await foreach (var payload in SseReader.ReadAsync(stream, timeout.Token))
                {
                    Emit(payload);
                }

                return;
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (body.Trim().Length > 0)
            {
                Emit(body.TrimEnd('\r', '\n'));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // The Editor may have restarted on another port, so the next message resolves the
            // descriptor again instead of the client having to be restarted.
            Invalidate();
            EmitTransportError(message);
        }
    }

    private void Emit(string line)
    {
        _outbound.Writer.TryWrite(line);
    }

    private void EmitTransportError(string message)
    {
        var id = PeekId(message);

        // A notification has no id, and JSON-RPC has nowhere to put an error for one.
        if (id is null)
        {
            return;
        }

        var project = JsonEncodedText.Encode(ProjectLabel());

        Emit("{\"jsonrpc\":\"2.0\",\"id\":" + id
            + ",\"error\":{\"code\":-32000,\"message\":\"Unity Editor for " + project + " is not running\"}}");
    }

    /// <summary>
    /// Returns the request's <c>id</c> as the JSON text it was written as, or null when there is none.
    /// Read rather than round-tripped so the reply carries back exactly the id that came in:
    /// re-serialising would turn 1.0 into 1 and a large integer id into something else again.
    /// </summary>
    public static string? PeekId(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        var reader = new Utf8JsonReader(bytes);

        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isId = reader.ValueTextEquals("id");

                if (!reader.Read())
                {
                    return null;
                }

                if (isId)
                {
                    return reader.TokenType switch
                    {
                        JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
                        JsonTokenType.String => "\"" + Encoding.UTF8.GetString(reader.ValueSpan) + "\"",
                        _ => null,
                    };
                }

                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && !reader.TrySkip())
                {
                    return null;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private async Task EndSessionAsync()
    {
        var session = Session();

        if (session is null)
        {
            return;
        }

        InstanceDescriptor descriptor;

        try
        {
            descriptor = Descriptor();
        }
        catch (Exception)
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, descriptor.McpUrlOrDefault);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", descriptor.Token);
            request.Headers.TryAddWithoutValidation(SessionHeader, session);

            using var response = await _http.SendAsync(request, CancellationToken.None);
        }
        catch (Exception)
        {
            // Closing down; a session the Editor has already forgotten is not worth reporting.
        }
    }

    private InstanceDescriptor Descriptor()
    {
        lock (_gate)
        {
            if (_descriptor is null)
            {
                _descriptor = _resolve();
                _lastKnownProject = _descriptor.ProjectName;
            }

            return _descriptor;
        }
    }

    private void Invalidate()
    {
        lock (_gate)
        {
            _descriptor = null;
        }
    }

    private string ProjectLabel()
    {
        lock (_gate)
        {
            return _projectOption ?? _lastKnownProject ?? "this project";
        }
    }

    private string? Session()
    {
        lock (_gate)
        {
            return _sessionId;
        }
    }

    private void CaptureSession(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(SessionHeader, out var values))
        {
            return;
        }

        var session = values.FirstOrDefault();

        if (string.IsNullOrEmpty(session))
        {
            return;
        }

        lock (_gate)
        {
            _sessionId = session;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
