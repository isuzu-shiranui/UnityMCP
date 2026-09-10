using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using NUnit.Framework;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Every tool the Editor publishes has a row in both tool references, and the counts agree.
    /// </summary>
    /// <remarks>
    /// The reference lagged the code four separate times in one review pass: tools added with no
    /// row, a row still promising a behaviour the tool had stopped having, and a stated total
    /// four short. A reader who checks the reference and finds nothing concludes the tool is not
    /// there, which is worse than no reference at all — so this is checked rather than
    /// remembered.
    /// </remarks>
    [TestFixture]
    internal sealed class ToolDocumentationTests
    {
        /// <summary>Tools that only appear when their package is installed.</summary>
        /// <remarks>
        /// The reference lists them regardless, because the reader deciding whether to install
        /// the package is exactly who needs to read the row.
        /// </remarks>
        private static readonly string[] ConditionalPrefixes = { "timeline_", "recorder_", "test_" };

        /// <summary>
        /// The reference page, wherever the project keeps it.
        /// </summary>
        /// <remarks>
        /// run-editmode-tests.ps1 links docs/ into the project beside Assets/, so walking up
        /// from dataPath finds it in the headless run as well as in a normal one.
        /// </remarks>
        private static string Reference(string relative)
        {
            var directory = new DirectoryInfo(UnityEngine.Application.dataPath);

            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, "docs", relative);

                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            return null;
        }

        private static IEnumerable<string> RowsIn(string path)
        {
            return Regex.Matches(File.ReadAllText(path), @"^\| `([a-z_0-9]+)` \|", RegexOptions.Multiline)
                .Cast<Match>()
                .Select(m => m.Groups[1].Value);
        }

        [TestCase("tools.md")]
        [TestCase("en/tools.md")]
        public void EveryToolHasARowInTheReference(string reference)
        {
            var path = Reference(reference);

            if (path == null)
            {
                Assert.Ignore($"'{reference}' is not beside this package in the test project.");
            }

            var published = ToolCatalog.Build().Tools.Select(t => t.Name).ToHashSet();
            var documented = RowsIn(path).ToHashSet();
            var missing = published.Except(documented).OrderBy(n => n).ToArray();

            Assert.That(missing, Is.Empty,
                $"{reference} has no row for: {string.Join(", ", missing)}");
        }

        [TestCase("tools.md")]
        [TestCase("en/tools.md")]
        public void TheReferenceDoesNotPromiseAToolThatIsGone(string reference)
        {
            var path = Reference(reference);

            if (path == null)
            {
                Assert.Ignore($"'{reference}' is not beside this package in the test project.");
            }

            var published = ToolCatalog.Build().Tools.Select(t => t.Name).ToHashSet();

            // A row for a tool whose package is absent here is the reference doing its job.
            var stale = RowsIn(path)
                .Where(n => !published.Contains(n))
                .Where(n => !ConditionalPrefixes.Any(n.StartsWith))
                .OrderBy(n => n)
                .ToArray();

            Assert.That(stale, Is.Empty,
                $"{reference} still lists: {string.Join(", ", stale)}");
        }
    }
}
