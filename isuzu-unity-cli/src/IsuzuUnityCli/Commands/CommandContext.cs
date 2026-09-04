using System.Text.Json.Nodes;
using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Discovery;
using IsuzuUnityCli.Http;

namespace IsuzuUnityCli.Commands;

/// <summary>Everything a command touches outside its arguments, so tests can substitute each piece.</summary>
public sealed class CommandContext
{
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;
    public TextReader In { get; init; } = Console.In;
    public string WorkingDirectory { get; init; } = Directory.GetCurrentDirectory();
    public Func<IReadOnlyList<InstanceDescriptor>> ReadDescriptors { get; init; } = () => DescriptorStore.ReadAll();

    /// <summary>
    /// Every descriptor on disk, including those of Editors that have since closed.
    /// Uninstall needs them to find the project roots whose configs it wrote into, which the
    /// live list cannot supply because it refuses to run while an Editor is open.
    /// </summary>
    public Func<IReadOnlyList<InstanceDescriptor>> ReadAllDescriptors { get; init; } =
        () => DescriptorStore.ReadAll(isAlive: _ => true);

    /// <summary>This executable's own path, which the stdio agents record as the command to run.</summary>
    public string ExecutablePath { get; init; } = Environment.ProcessPath ?? "isuzu-unity-cli";

    public UnityHttpClient Client { get; init; } = new();
    public CancellationToken Cancellation { get; init; }

    public InstanceDescriptor ResolveInstance(ParsedArgs parsed)
    {
        return InstanceResolver.Resolve(ReadDescriptors(), parsed.Option("project"), WorkingDirectory);
    }

    /// <summary>
    /// Prints an envelope the way the user asked for it and returns the exit code. A call that is
    /// still running also gets the Editor's explanation on stderr, where a reader watching the
    /// terminal sees why nothing is coming back.
    /// </summary>
    public int Report(Envelope envelope, bool raw)
    {
        if (raw)
        {
            JsonOutput.Print(Out, envelope.Raw);
            ReportRunning(envelope);
            return envelope.IsError ? 1 : 0;
        }

        if (envelope.IsError)
        {
            ReportError(envelope.ErrorCode, envelope.ErrorMessage ?? "unknown");
            return 1;
        }

        JsonOutput.Print(Out, envelope.Result);
        ReportRunning(envelope);
        return 0;
    }

    /// <summary>The <c>message</c> of a running job or a 202 envelope, or null for anything else.</summary>
    public static string? RunningMessage(JsonNode? result)
    {
        if (result is not JsonObject body)
        {
            return null;
        }

        var state = body["state"] is JsonValue s && s.TryGetValue<string>(out var stateText) ? stateText : null;
        var status = body["status"] is JsonValue t && t.TryGetValue<string>(out var statusText) ? statusText : null;

        if (state != "running" && status != "running")
        {
            return null;
        }

        return body["message"] is JsonValue m && m.TryGetValue<string>(out var message) && message.Length > 0 ? message : null;
    }

    private void ReportRunning(Envelope envelope)
    {
        var message = RunningMessage(envelope.Result);

        if (message is not null)
        {
            Err.WriteLine(message);
        }
    }

    public void ReportError(string? code, string message)
    {
        Err.WriteLine(string.IsNullOrEmpty(code) ? $"error: {message}" : $"error [{code}]: {message}");
    }
}
