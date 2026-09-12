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

    private static InstanceDescriptor Open(string path) => new()
    {
        ProjectName = "Open",
        ProjectPath = path,
        Port = 27180,
        Token = "token",
    };

    [Fact]
    public void ACommandRunInsideAProjectThatIsNotOpenIsRefused()
    {
        var open = Open(Path.Combine(_root, "Open", "Assets"));

        var error = Assert.Throws<CliException>(
            () => InstanceResolver.Resolve([open], null, Path.Combine(_root, "Closed", "Assets", "Scripts")));

        Assert.Equal(3, error.ExitCode);
        Assert.Contains(Path.Combine(_root, "Closed"), error.Message);
    }

    [Fact]
    public void OutsideEveryProjectTheOnlyEditorIsStillChosen()
    {
        var open = Open(Path.Combine(_root, "Open", "Assets"));

        Assert.Same(open, InstanceResolver.Resolve([open], null, _root));
    }

    [Fact]
    public void AProjectNamedOnTheCommandLineIsNotRefusedForTheWorkingDirectory()
    {
        var open = Open(Path.Combine(_root, "Open", "Assets"));

        Assert.Same(open, InstanceResolver.Resolve([open], "Open", Path.Combine(_root, "Closed", "Assets")));
    }

    [Fact]
    public void WindowsDescriptorsReadFromAnotherHostSkipTheCheck()
    {
        var windows = Open("C:/Work/Open/Assets");
        var directory = Path.Combine(_root, "Closed", "Assets");

        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<CliException>(() => InstanceResolver.Resolve([windows], null, directory));
        }
        else
        {
            Assert.Same(windows, InstanceResolver.Resolve([windows], null, directory));
        }
    }
}
