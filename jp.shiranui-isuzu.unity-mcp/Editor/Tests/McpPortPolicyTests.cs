using System.IO;

using NUnit.Framework;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// A project's port must be the same every time it opens and different from its neighbours'.
    /// </summary>
    [TestFixture]
    internal sealed class McpPortPolicyTests
    {
        [Test]
        public void DerivedPortIsDeterministicAndInsideTheRange()
        {
            var a = McpPortPolicy.Derive(@"C:\Projects\Alpha\Assets");
            var b = McpPortPolicy.Derive(@"C:\Projects\Alpha\Assets");

            Assert.That(a, Is.EqualTo(b));
            Assert.That(a, Is.InRange(McpPortPolicy.RangeStart, McpPortPolicy.RangeEnd));
        }

        [Test]
        public void DifferentProjectsGetDifferentPorts()
        {
            Assert.That(
                McpPortPolicy.Derive(@"C:\Projects\Alpha\Assets"),
                Is.Not.EqualTo(McpPortPolicy.Derive(@"C:\Projects\Beta\Assets")));
        }

        [Test]
        public void SpellingOfTheSamePathDoesNotChangeThePort()
        {
            var root = Path.GetTempPath();
            var forward = root.Replace('\\', '/').TrimEnd('/') + "/proj/Assets/";
            var backward = root.TrimEnd('\\', '/') + @"\proj\Assets";

            Assert.That(McpPortPolicy.Derive(forward), Is.EqualTo(McpPortPolicy.Derive(backward)));

            if (Path.DirectorySeparatorChar == '\\')
            {
                Assert.That(McpPortPolicy.Derive(backward.ToUpperInvariant()), Is.EqualTo(McpPortPolicy.Derive(backward)),
                    "Windows paths are case-insensitive, so case must not change the port.");
            }
        }

        [Test]
        public void ExplicitPortWins()
        {
            // The singleton is the only instance Unity allows; a second one logs an error.
            var settings = Settings.McpSettings.instance;
            var saved = settings.httpPort;
            try
            {
                settings.httpPort = 31000;
                Assert.That(McpPortPolicy.Resolve(settings, @"C:\p\Assets"), Is.EqualTo(31000));

                settings.httpPort = 0;
                Assert.That(McpPortPolicy.Resolve(settings, @"C:\p\Assets"), Is.EqualTo(McpPortPolicy.Derive(@"C:\p\Assets")));
            }
            finally
            {
                settings.httpPort = saved;
            }
        }
    }
}
