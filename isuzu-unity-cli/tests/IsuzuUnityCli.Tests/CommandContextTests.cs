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

    // A one-pixel PNG, base64-encoded: the signature is what the check reads.
    private const string Shot = """
        {"status":"success","result":{"view":"game","width":2,
         "image":"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="}}
        """;

    // Indentation is stated rather than inherited: its default reads Console.IsOutputRedirected,
    // which depends on how the test host was started.
    private static (CommandContext Context, StringWriter Out, StringWriter Err) Context(bool indented = true)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        return (new CommandContext { Out = output, Err = error, Indented = indented }, output, error);
    }

    // The Editor hoists these onto the envelope, so the printed result carries neither unless
    // something puts them back.
    private const string Cut = """
        {"status":"success","result":{"scenes":[],"total":523},
         "truncated":true,"next":{"offset":3,"limit":3}}
        """;

    // A tool may say what to do about the cut rather than only that there was one.
    private const string CutWithAdvice = """
        {"status":"success","result":{"section":"player","properties":[]},
         "truncated":"stopped at 200; narrow it with 'property'"}
        """;

    /// <summary>
    /// A listing that stopped early has to print differently from a complete one. Only --raw shows
    /// the envelope, so without this the caller reads a partial answer as the whole answer.
    /// </summary>
    [Fact]
    public void APrintedListingSaysThatItStoppedEarly()
    {
        var (context, output, _) = Context();

        Assert.Equal(0, context.Report(Envelope.Parse(200, Cut), raw: false));

        var printed = output.ToString();

        Assert.Contains("\"truncated\"", printed);
        Assert.Contains("\"offset\"", printed);
    }

    [Fact]
    public void AdviceAboutTheCutSurvivesInsteadOfBecomingTrue()
    {
        var (context, output, _) = Context();

        Assert.Equal(0, context.Report(Envelope.Parse(200, CutWithAdvice), raw: false));

        Assert.Contains("narrow it with", output.ToString());
        Assert.True(Envelope.Parse(200, CutWithAdvice).Truncated);
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
    public void RawOutputWritesAPictureToDiskRatherThanPrintingIt()
    {
        // --raw prints the envelope instead of the result, and used to return before the picture
        // was moved, so the same screenshot cost ninety times as much through that one flag.
        var directory = Path.Combine(Path.GetTempPath(), "raw-image-" + Guid.NewGuid().ToString("N"));
        var output = new StringWriter();
        var error = new StringWriter();
        var context = new CommandContext
        {
            Out = output,
            Err = error,
            Indented = false,
            CaptureDirectory = directory,
        };

        try
        {
            context.Report(Envelope.Parse(200, Shot), raw: true);

            Assert.DoesNotContain("iVBORw0KGg", output.ToString());
            Assert.Contains("\"width\":2", output.ToString());
            Assert.Contains("image written to", error.ToString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
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
