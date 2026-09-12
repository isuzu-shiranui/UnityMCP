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

    /// <summary>
    /// A guide from before the MCP interface is found, and the current one is not mistaken for it.
    /// </summary>
    /// <remarks>
    /// An installed copy from an earlier release stays where every agent reads it, and the one
    /// that turned up in a hands-on run documented endpoints on 127.0.0.1:27182 that no longer
    /// answer. The folder name cannot decide it: other Unity MCP servers use names like
    /// "unity-mcp" as well.
    /// </remarks>
    [Fact]
    public void AGuideForTheInterfaceThisServerReplacedIsReported()
    {
        var obsolete = Path.Combine(_skills, "unity-mcp");
        Directory.CreateDirectory(obsolete);
        File.WriteAllText(Path.Combine(obsolete, "SKILL.md"),
            "Interact with a running Unity Editor via HTTP on `http://127.0.0.1:27182/`. "
            + "| `/execute_code` | POST | Run C# code |");

        SkillInstaller.Install(_skills);

        Assert.Equal(
            new[] { Path.Combine(obsolete, "SKILL.md") },
            SkillInstaller.ObsoleteGuides(_skills).ToArray());
    }

    [Fact]
    public void AnotherProjectsGuideIsNotReportedAsThisOne()
    {
        var other = Path.Combine(_skills, "unity-mcp");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "SKILL.md"),
            "Some other Unity MCP server, with its own tools and no fixed port.");

        Assert.Empty(SkillInstaller.ObsoleteGuides(_skills));
    }

    [Fact]
    public void NoGuidesAreReportedWhereThereIsNoSkillsDirectory()
    {
        Assert.Empty(SkillInstaller.ObsoleteGuides(Path.Combine(_skills, "nothing here")));
    }

    /// <summary>
    /// The guide is ASCII, because of how the agents that read it read files.
    /// </summary>
    /// <remarks>
    /// It is UTF-8 with no BOM, which is what the skill loaders want, and Windows PowerShell 5.1
    /// decodes exactly that as the ANSI codepage: an em dash arrives as "a-hat euro" and the
    /// reader, seeing mojibake, fetches the whole file again with -Encoding utf8. Four hands-on
    /// runs in a row read 26 KB twice for the sake of six em dashes.
    /// </remarks>
    [Fact]
    public void TheSkillIsAsciiSoThatAnAnsiReaderSeesItWhole()
    {
        var content = SkillInstaller.Content();
        var offending = content.Where(c => c > 127).Distinct().ToArray();

        Assert.True(
            offending.Length == 0,
            "Replace with ASCII: " + string.Join(", ", offending.Select(c => $"U+{(int)c:X4} '{c}'")));
    }

    /// <summary>
    /// The pages the guide points at are installed with it.
    /// </summary>
    /// <remarks>
    /// The guide sends the reader to reference/tools.md and reference/workflows.md rather than
    /// carrying both, which is what keeps the first read to a third of the file. Installed
    /// without them, those lines point at files that are not there.
    /// </remarks>
    [Fact]
    public void TheReferencePagesAreInstalledBesideTheGuide()
    {
        SkillInstaller.Install(_skills);

        var directory = SkillInstaller.DirectoryFor(_skills);

        Assert.True(File.Exists(Path.Combine(directory, "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(directory, "reference", "tools.md")));
        Assert.True(File.Exists(Path.Combine(directory, "reference", "workflows.md")));
        Assert.False(SkillInstaller.IsStale(_skills));
    }

    [Fact]
    public void AnEditedReferencePageIsStaleToo()
    {
        SkillInstaller.Install(_skills);

        var page = Path.Combine(SkillInstaller.DirectoryFor(_skills), "reference", "tools.md");
        File.AppendAllText(page, "\nedited by hand\n");

        Assert.True(SkillInstaller.IsStale(_skills), "staleness has to cover every file installed");

        SkillInstaller.Install(_skills);

        Assert.False(SkillInstaller.IsStale(_skills));
    }

    /// <summary>Every page the skill ships is ASCII, for the reason above.</summary>
    [Fact]
    public void EveryPageOfTheSkillIsAscii()
    {
        foreach (var (relative, text) in SkillInstaller.Files())
        {
            var offending = text.Where(c => c > 127).Distinct().ToArray();

            Assert.True(
                offending.Length == 0,
                relative + " - replace with ASCII: "
                + string.Join(", ", offending.Select(c => $"U+{(int)c:X4} '{c}'")));
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
