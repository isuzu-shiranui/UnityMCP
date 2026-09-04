using System.Threading.Channels;

namespace IsuzuUnityCli.Tests.Fakes;

/// <summary>
/// Stdin the test feeds one line at a time, so a bridge that dispatches messages concurrently
/// can still be driven in a known order.
/// </summary>
public sealed class GatedReader : TextReader
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

    public void Send(string line) => _lines.Writer.TryWrite(line);

    public void CloseInput() => _lines.Writer.TryComplete();

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellation)
    {
        try
        {
            return await _lines.Reader.ReadAsync(cancellation);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();

    public override string? ReadLine() => ReadLineAsync().GetAwaiter().GetResult();
}

/// <summary>Stdout split back into the lines that were written to it.</summary>
public sealed class RecordingWriter : TextWriter
{
    private readonly List<string> _lines = new();
    private readonly Lock _gate = new();
    private string _partial = "";

    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return _lines.ToList();
            }
        }
    }

    public override void Write(char value)
    {
        lock (_gate)
        {
            if (value == '\n')
            {
                _lines.Add(_partial);
                _partial = "";
                return;
            }

            _partial += value;
        }
    }

    public override void Write(string? value)
    {
        foreach (var c in value ?? "")
        {
            Write(c);
        }
    }

    /// <summary>Waits for a condition the bridge reaches on another thread, or gives up.</summary>
    public static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"timed out waiting for {what}");
    }
}
