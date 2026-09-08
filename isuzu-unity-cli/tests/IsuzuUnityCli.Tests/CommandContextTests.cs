using IsuzuUnityCli.Commands;
using IsuzuUnityCli.Http;
using Xunit;

namespace IsuzuUnityCli.Tests;

public sealed class CommandContextTests
{
    private const string Running = """
        {"status":"success","result":{"state":"running","jobId":"execute_code-3","poll":"/jobs/execute_code-3",
         "message":"'execute_code' is still running on the Editor main thread. The Editor is showing a dialog \"MCP probe\" (Pick one) with buttons Yes / No."}}
        """;

    // Indentation is stated rather than inherited: its default reads Console.IsOutputRedirected,
    // which depends on how the test host was started.
    private static (CommandContext Context, StringWriter Out, StringWriter Err) Context(bool indented = true)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        return (new CommandContext { Out = output, Err = error, Indented = indented }, output, error);
    }

    [Fact]
    public void ARunningCallPrintsItsMessageOnStderr()
    {
        var (context, output, error) = Context();

        Assert.Equal(0, context.Report(Envelope.Parse(202, Running), raw: false));

        Assert.Contains("\"jobId\": \"execute_code-3\"", output.ToString());
        Assert.Contains("showing a dialog \"MCP probe\"", error.ToString());
    }

    [Fact]
    public void PackedOutputCarriesTheSameFieldsWithoutTheWhitespace()
    {
        var (indented, prettyOut, _) = Context();
        var (packed, packedOut, _) = Context(indented: false);

        indented.Report(Envelope.Parse(202, Running), raw: false);
        packed.Report(Envelope.Parse(202, Running), raw: false);

        Assert.Contains("\"jobId\":\"execute_code-3\"", packedOut.ToString());
        Assert.DoesNotContain("\"jobId\": ", packedOut.ToString());
        Assert.True(packedOut.ToString().Length < prettyOut.ToString().Length);
    }

    [Fact]
    public void RawOutputStillExplainsARunningCallOnStderr()
    {
        var (context, output, error) = Context();

        Assert.Equal(0, context.Report(Envelope.Parse(202, Running), raw: true));

        Assert.Contains("\"status\": \"success\"", output.ToString());
        Assert.Contains("showing a dialog", error.ToString());
    }

    [Fact]
    public void ACompletedResultPrintsNothingOnStderr()
    {
        var (context, _, error) = Context();

        context.Report(Envelope.Parse(200, """{"status":"success","result":{"state":"completed","message":"done"}}"""), raw: false);
        context.Report(Envelope.Parse(200, """{"status":"success","result":{"status":"completed","result":{"x":1}}}"""), raw: false);

        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void ARunningJobDetailPrintsItsMessage()
    {
        var (context, _, error) = Context();

        context.Report(Envelope.Parse(200, """{"status":"success","result":{"id":"j-1","status":"running","message":"The Editor main thread has not run for 12 s; it may be showing a dialog this tool cannot see, importing, or compiling."}}"""), raw: false);

        Assert.Contains("has not run for 12 s", error.ToString());
    }
}
