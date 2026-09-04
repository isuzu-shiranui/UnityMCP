namespace IsuzuUnityCli.Cli;

/// <summary>A failure reported to the user as a plain message with a specific exit code.</summary>
public sealed class CliException : Exception
{
    public int ExitCode { get; }

    public CliException(string message, int exitCode = 1) : base(message)
    {
        ExitCode = exitCode;
    }
}
