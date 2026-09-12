using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Discovery;
using Xunit;

namespace IsuzuUnityCli.Tests;

/// <summary>
/// A command run inside a Unity project that no Editor has open must not go to the Editor that is.
/// </summary>
public sealed class ClosedProjectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "isuzu-closed-" + Guid.NewGuid().ToString("N"));

    public ClosedProjectTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Closed", "Assets", "Scripts"));
        Directory.CreateDirectory(Path.Combine(_root, "Closed", "ProjectSettings"));
        File.WriteAllText(Path.Combine(_root, "Closed", "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.0.35f1\n");
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private InstanceDescriptor Open() => new()
    {
        ProjectName = "Open",
        ProjectPath = Path.Combine(_root, "Open", "Assets"),
        Port = 27180,
        Token = "token",
    };

    [Fact]
    public void ACommandRunInsideAProjectThatIsNotOpenIsRefused()
    {
        var error = Assert.Throws<CliException>(
            () => InstanceResolver.Resolve([Open()], null, Path.Combine(_root, "Closed", "Assets", "Scripts")));

        Assert.Equal(3, error.ExitCode);
        Assert.Contains(Path.Combine(_root, "Closed"), error.Message);
    }

    [Fact]
    public void OutsideEveryProjectTheOnlyEditorIsStillChosen()
    {
        var open = Open();

        Assert.Same(open, InstanceResolver.Resolve([open], null, _root));
    }
}
