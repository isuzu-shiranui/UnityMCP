using System.Diagnostics;

namespace IsuzuUnityCli.Cli;

/// <summary>
/// Stage timings on stderr when UNITY_MCP_TRACE is set. The first mark is measured from the
/// process start time the OS recorded, so the runtime's own start-up shows up as a stage.
/// </summary>
public static class StageTrace
{
    private static readonly bool Enabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UNITY_MCP_TRACE"));
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double _startupMs = -1;

    public static void Mark(string stage)
    {
        if (!Enabled)
        {
            return;
        }

        if (_startupMs < 0)
        {
            try
            {
                using var self = Process.GetCurrentProcess();
                _startupMs = (DateTime.Now - self.StartTime).TotalMilliseconds - Clock.Elapsed.TotalMilliseconds;
            }
            catch (Exception)
            {
                _startupMs = 0;
            }

            Console.Error.WriteLine($"trace runtime-start   {_startupMs,8:0.0} ms");
        }

        Console.Error.WriteLine($"trace {stage,-16}{_startupMs + Clock.Elapsed.TotalMilliseconds,8:0.0} ms");
    }
}
