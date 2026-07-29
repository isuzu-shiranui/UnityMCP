using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("UnityMCP.Editor.Tests")]

// The test-runner tools ship in their own assembly so that a project without the test
// framework loses those two tools instead of failing to compile the package. They still
// need McpToolException to report a bad argument the way every other tool does.
[assembly: InternalsVisibleTo("UnityMCP.Editor.TestRunner")]

// Timeline tools live in their own assembly too, constrained to projects that have
// com.unity.timeline. They reach ObjectResolve, McpToolException and EntityIdCompat the same
// way every other tool does.
[assembly: InternalsVisibleTo("UnityMCP.Editor.Timeline")]

// Recorder tools add a Recorder track to a Timeline; their assembly is constrained to projects
// that have both com.unity.recorder and com.unity.timeline, and reaches the same shared internals.
[assembly: InternalsVisibleTo("UnityMCP.Editor.Recorder")]

// Those two carry their own test assemblies, under the same package constraints, which address
// objects through EntityIdCompat and assert on McpToolException like the other suites do.
[assembly: InternalsVisibleTo("UnityMCP.Editor.Timeline.Tests")]
[assembly: InternalsVisibleTo("UnityMCP.Editor.Recorder.Tests")]
