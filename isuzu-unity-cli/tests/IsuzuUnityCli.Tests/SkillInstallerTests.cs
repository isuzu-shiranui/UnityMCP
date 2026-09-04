using IsuzuUnityCli.Housekeeping;
using Xunit;

namespace IsuzuUnityCli.Tests;

public sealed class SkillInstallerTests : IDisposable
{
    private readonly string _skills = Path.Combine(Path.GetTempPath(), "isuzu-cli-tests", Guid.NewGuid().ToString("N"), "skills");

    public SkillInstallerTests()
    {
        Directory.CreateDirectory(_skills);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_skills)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void TheSkillIsEmbeddedInThisExecutable()
    {
        var content = SkillInstaller.Content();

        Assert.StartsWith("---", content);
        Assert.Contains("name: isuzu-unity-cli", content);
    }

    [Fact]
    public void InstallWritesTheSkillAndLeavesNoStagingDirectory()
    {
        var destination = SkillInstaller.Install(_skills);

        Assert.Equal(Path.Combine(_skills, "isuzu-unity-cli"), destination);
        Assert.Equal(SkillInstaller.Content(), File.ReadAllText(Path.Combine(destination, "SKILL.md")));
        Assert.False(Directory.Exists(destination + ".incoming"));
    }

    [Fact]
    public void InstallingTwiceIsTheSameAsInstallingOnce()
    {
        SkillInstaller.Install(_skills);
        SkillInstaller.Install(_skills);

        Assert.Single(Directory.GetFiles(SkillInstaller.DirectoryFor(_skills)));
        Assert.False(SkillInstaller.IsStale(_skills));
    }

    [Fact]
    public void AnAbsentOrEditedSkillIsStale()
    {
        Assert.True(SkillInstaller.IsStale(_skills));
        Assert.False(SkillInstaller.IsInstalled(_skills));

        SkillInstaller.Install(_skills);
        Assert.False(SkillInstaller.IsStale(_skills));

        File.AppendAllText(SkillInstaller.FileFor(_skills), "\nsomething else\n");
        Assert.True(SkillInstaller.IsStale(_skills));

        SkillInstaller.Install(_skills);
        Assert.False(SkillInstaller.IsStale(_skills));
    }

    [Fact]
    public void TheV3SkillFolderIsRemovedAndReported()
    {
        Assert.False(SkillInstaller.RemoveLegacy(_skills));

        var legacy = Path.Combine(_skills, "isuzu-unity-mcp");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "SKILL.md"), "v3");

        Assert.True(SkillInstaller.RemoveLegacy(_skills));
        Assert.False(Directory.Exists(legacy));
    }

    [Fact]
    public void AFailedInstallLeavesTheWorkingSkillInPlace()
    {
        SkillInstaller.Install(_skills);
        var installed = SkillInstaller.FileFor(_skills);
        File.WriteAllText(installed, "the copy that was already working");

        // A file where the staging directory belongs makes the copy fail before the old skill
        // is touched, which is the whole point of staging.
        File.WriteAllText(SkillInstaller.DirectoryFor(_skills) + ".incoming", "in the way");

        Assert.ThrowsAny<IOException>(() => SkillInstaller.Install(_skills));
        Assert.Equal("the copy that was already working", File.ReadAllText(installed));
    }
}
