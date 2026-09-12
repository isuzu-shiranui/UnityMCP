using System;
using System.Collections.Generic;
using System.IO;

using NUnit.Framework;

using UnityMCP.Editor.Settings;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Where the Settings window looks for the CLI. An Editor started before winget put its folder
    /// of links on PATH does not find that folder on its own PATH.
    /// </summary>
    [TestFixture]
    internal sealed class IsuzuCliLocatorTests
    {
        private static readonly string Root = Path.GetTempPath();

        private static Func<string, string> Variables(Dictionary<string, string> values)
        {
            return name => values.TryGetValue(name, out var value) ? value : null;
        }

        [Test]
        public void PathComesFirstThenWingetsFoldersThenTheInstallScriptsFolder()
        {
            var bin = Path.Combine(Root, "bin");
            var local = Path.Combine(Root, "Local");
            var programFiles = Path.Combine(Root, "Program Files");
            var package = IsuzuCliLocator.WingetPackageFolder;

            var candidates = IsuzuCliLocator.Candidates(Variables(new Dictionary<string, string>
            {
                ["PATH"] = bin,
                ["LOCALAPPDATA"] = local,
                ["ProgramFiles"] = programFiles,
            }), isWindows: true);

            Assert.That(candidates, Is.EqualTo(new[]
            {
                Path.Combine(bin, "isuzu-unity-cli.exe"),
                Path.Combine(local, "Microsoft", "WinGet", "Links", "isuzu-unity-cli.exe"),
                Path.Combine(programFiles, "WinGet", "Links", "isuzu-unity-cli.exe"),
                Path.Combine(local, "Microsoft", "WinGet", "Packages", package, "isuzu-unity-cli.exe"),
                Path.Combine(programFiles, "WinGet", "Packages", package, "isuzu-unity-cli.exe"),
                Path.Combine(local, "Programs", "isuzu-unity-cli", "isuzu-unity-cli.exe"),
            }));
        }

        [Test]
        public void APathEntryInDoubleQuotesNamesTheFolderInside()
        {
            var bin = Path.Combine(Root, "quoted bin");

            var candidates = IsuzuCliLocator.Candidates(
                Variables(new Dictionary<string, string> { ["PATH"] = "\"" + bin + "\"" }),
                isWindows: true);

            Assert.That(candidates, Is.EqualTo(new[] { Path.Combine(bin, "isuzu-unity-cli.exe") }));
        }

        [Test]
        public void AnEntryThatIsNotAPathDoesNotStopTheSearch()
        {
            var bin = Path.Combine(Root, "bin");

            var candidates = IsuzuCliLocator.Candidates(
                Variables(new Dictionary<string, string> { ["PATH"] = "a|<b>" + Path.PathSeparator + bin }),
                isWindows: true);

            Assert.That(candidates, Does.Contain(Path.Combine(bin, "isuzu-unity-cli.exe")));
        }

        [Test]
        public void AVariableThatIsNotSetAddsNoRelativePath()
        {
            var candidates = IsuzuCliLocator.Candidates(
                Variables(new Dictionary<string, string> { ["PATH"] = string.Empty, ["LOCALAPPDATA"] = string.Empty }),
                isWindows: true);

            Assert.That(candidates, Is.Empty);
        }

        [Test]
        public void OutsideWindowsOnlyTheInstallScriptsFolderIsAdded()
        {
            var home = Path.Combine(Root, "home");

            var candidates = IsuzuCliLocator.Candidates(
                Variables(new Dictionary<string, string> { ["HOME"] = home }),
                isWindows: false);

            Assert.That(candidates, Is.EqualTo(new[] { Path.Combine(home, ".local", "bin", "isuzu-unity-cli") }));
        }
    }
}
