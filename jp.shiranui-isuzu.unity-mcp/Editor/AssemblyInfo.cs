using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("UnityMCP.Editor.Tests")]

// The test-runner tools ship in their own assembly so that a project without the test
// framework loses those two tools instead of failing to compile the package. They still
// need McpToolException to report a bad argument the way every other tool does.
[assembly: InternalsVisibleTo("UnityMCP.Editor.TestRunner")]
