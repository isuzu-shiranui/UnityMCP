using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Discovery;
using Xunit;

namespace IsuzuUnityCli.Tests;

public sealed class InstanceReconnectTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "reconnect", "Original");

    private static InstanceDescriptor Editor(string path) => new()
    {
        ProjectName = "Same product name",
        ProjectPath = path,
        Port = 27180,
        Token = "old",
    };

    [Fact]
    public void RefreshAllowsANewEndpointAndProductNameAtTheSameNormalizedRoot()
    {
        var original = Editor(Path.Combine(Root, "Assets"));
        var restarted = Editor(Path.Combine(Root, ".") + Path.DirectorySeparatorChar);
        restarted.ProjectName = "Renamed product";
        restarted.Port++;
        restarted.Pid = 123;
        restarted.Token = "fresh";
        var other = Editor(Root + "-copy");

        Assert.Same(restarted, InstanceResolver.Refresh([other, restarted], original));
    }

    [Theory]
    [InlineData("sibling")]
    [InlineData("child")]
    [InlineData("parent")]
    public void RefreshDoesNotUseNameOrContainmentToChooseAnotherRoot(string relation)
    {
        var original = Editor(Path.Combine(Root, "Assets"));
        var path = relation switch
        {
            "child" => Path.Combine(Root, "Nested", "Assets"),
            "parent" => Path.Combine(Path.GetDirectoryName(Root)!, "Assets"),
            _ => Path.Combine(Root + "-copy", "Assets"),
        };

        var error = Assert.Throws<CliException>(() => InstanceResolver.Refresh([Editor(path)], original));

        Assert.Equal(3, error.ExitCode);
        Assert.Contains(original.ProjectPath, error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative/Assets")]
    [InlineData("\0")]
    public void RefreshCannotIdentifyALegacyOrMalformedPathByName(string path)
    {
        var original = Editor(path);

        Assert.Throws<CliException>(() => InstanceResolver.Refresh([Editor(path)], original));
    }

    [Fact]
    public void UnusableCandidatesDoNotHideTheOriginalProject()
    {
        var original = Editor(Path.Combine(Root, "Assets"));
        var restarted = Editor(original.ProjectPath);

        Assert.Same(restarted, InstanceResolver.Refresh([Editor(""), Editor(Root + "\0"), restarted], original));
    }

    [Fact]
    public void MissingAndAmbiguousDescriptorsCannotChooseAnEndpoint()
    {
        var original = Editor(Path.Combine(Root, "Assets"));

        Assert.Throws<CliException>(() => InstanceResolver.Refresh([], original));
        Assert.Throws<CliException>(() => InstanceResolver.Refresh([Editor(Root), Editor(original.ProjectPath)], original));
    }

    [Fact]
    public void CaseDifferencesAreAcceptedOnlyOnWindows()
    {
        var original = Editor(Path.Combine(Root, "Assets"));
        var differentCase = Editor(original.ProjectPath.ToUpperInvariant());

        if (OperatingSystem.IsWindows())
        {
            Assert.Same(differentCase, InstanceResolver.Refresh([differentCase], original));
        }
        else
        {
            Assert.Throws<CliException>(() => InstanceResolver.Refresh([differentCase], original));
        }
    }

    [Theory]
    [InlineData("C:/Work/Original/Assets", "C:\\Work\\Original\\Assets\\")]
    [InlineData("C:\\Work\\Original\\Assets", "C:/Work/Original")]
    [InlineData("\\\\server\\share\\Original\\Assets", "\\\\server\\share\\Original")]
    public void WindowsPublishedPathsCanBeRefreshedByASplitSetup(string published, string refreshed)
    {
        var original = Editor(published);
        var restarted = Editor(refreshed);

        Assert.Same(restarted, InstanceResolver.Refresh([Editor(published.Replace("Original", "Other")), restarted], original));
    }
}
