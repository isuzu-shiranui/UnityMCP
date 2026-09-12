using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Discovery;
using Xunit;

namespace IsuzuUnityCli.Tests;

public sealed class InstanceReconnectTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "reconnect", "Original");

    private static InstanceDescriptor Editor(string path, int port = 27180, int pid = 0) => new()
    {
        ProjectName = "Same product name",
        ProjectPath = path,
        Port = port,
        Pid = pid,
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
        Assert.Contains($"No running Editor has {Root} open.", error.Message);
        Assert.EndsWith(InstanceResolver.SwitchByCommand, error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative/Assets")]
    [InlineData("\0")]
    public void RefreshCannotIdentifyALegacyOrMalformedPathByName(string path)
    {
        var original = Editor(path);

        var error = Assert.Throws<CliException>(() => InstanceResolver.Refresh([Editor(path)], original));

        Assert.Contains("no absolute project path", error.Message);
    }

    [Fact]
    public void UnusableCandidatesDoNotHideTheOriginalProject()
    {
        var original = Editor(Path.Combine(Root, "Assets"));
        var restarted = Editor(original.ProjectPath);

        Assert.Same(restarted, InstanceResolver.Refresh([Editor(""), Editor(Root + "\0"), restarted], original));
    }

    [Fact]
    public void WhenNoEditorHasTheProjectOpenTheMessageSaysHowToSwitch()
    {
        var original = Editor(Path.Combine(Root, "Assets"));

        var none = Assert.Throws<CliException>(
            () => InstanceResolver.Refresh([], original, InstanceResolver.SwitchByRestart));
        var another = Assert.Throws<CliException>(
            () => InstanceResolver.Refresh([Editor(Path.Combine(Root + "-copy", "Assets"))], original, InstanceResolver.SwitchByRestart));

        Assert.Equal($"No running Editor has {Root} open. No Editor is running. {InstanceResolver.SwitchByRestart}", none.Message);
        Assert.Contains("Running: Same product name (folder: Original-copy).", another.Message);
        Assert.EndsWith(InstanceResolver.SwitchByRestart, another.Message);
    }

    [Fact]
    public void SeveralDescriptorsForTheProjectAreRefusedWhenNoneCanBeAsked()
    {
        var original = Editor(Path.Combine(Root, "Assets"));

        var error = Assert.Throws<CliException>(() => InstanceResolver.Refresh(
            [Editor(Root, port: 27181, pid: 11), Editor(original.ProjectPath, port: 27182, pid: 12)], original));

        Assert.Equal(3, error.ExitCode);
        Assert.Contains("pid 11, port 27181; pid 12, port 27182", error.Message);
    }

    [Fact]
    public void OfSeveralDescriptorsTheOneThatAnswersIsChosen()
    {
        var original = Editor(Path.Combine(Root, "Assets"));
        var stale = Editor(Root, port: 27181);
        var live = Editor(original.ProjectPath, port: 27182);

        Assert.Same(live, InstanceResolver.Refresh([stale, live], original, answers: d => ReferenceEquals(d, live)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeveralDescriptorsThatAllOrNoneAnswerAreRefused(bool answer)
    {
        var original = Editor(Path.Combine(Root, "Assets"));

        Assert.Throws<CliException>(() => InstanceResolver.Refresh(
            [Editor(Root, port: 27181), Editor(original.ProjectPath, port: 27182)], original, answers: _ => answer));
    }

    [Fact]
    public void ASingleDescriptorIsUsedWithoutAskingIt()
    {
        var original = Editor(Path.Combine(Root, "Assets"));
        var restarted = Editor(original.ProjectPath, port: 27181);

        Assert.Same(restarted, InstanceResolver.Refresh(
            [restarted], original, answers: _ => throw new InvalidOperationException("asked")));
    }

    [Fact]
    public void AFolderNameInAnotherCaseIsAnotherProject()
    {
        var original = Editor(Path.Combine(Root, "Assets"));
        var differentCase = Editor(Path.Combine(Root.ToUpperInvariant(), "Assets"));

        Assert.Throws<CliException>(() => InstanceResolver.Refresh([differentCase], original));
    }

    [Fact]
    public void TheDriveLetterMayBeWrittenInEitherCase()
    {
        var original = Editor("c:/Work/Original/Assets");
        var restarted = Editor(@"C:\Work\Original");

        Assert.Same(restarted, InstanceResolver.Refresh([restarted], original));
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
