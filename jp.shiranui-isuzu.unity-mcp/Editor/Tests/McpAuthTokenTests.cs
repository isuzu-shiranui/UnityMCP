using System.IO;

using NUnit.Framework;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// The token has to survive Editor restarts and change only when asked.
    /// </summary>
    [TestFixture]
    internal sealed class McpAuthTokenTests
    {
        private string projectPath;

        [SetUp]
        public void SetUp()
        {
            this.projectPath = Path.Combine(Path.GetTempPath(), "unity-mcp-token-test-" + Path.GetRandomFileName(), "Assets");
        }

        [TearDown]
        public void TearDown()
        {
            var path = McpAuthToken.PathFor(this.projectPath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        [Test]
        public void FirstLoadMintsAndPersistsAToken()
        {
            var token = McpAuthToken.Load(this.projectPath);

            Assert.That(token, Has.Length.EqualTo(64));
            Assert.That(File.ReadAllText(McpAuthToken.PathFor(this.projectPath)).Trim(), Is.EqualTo(token));
        }

        [Test]
        public void SecondLoadReturnsTheSameToken()
        {
            var first = McpAuthToken.Load(this.projectPath);
            var second = McpAuthToken.Load(this.projectPath);

            Assert.That(second, Is.EqualTo(first));
        }

        [Test]
        public void RegenerateReplacesTheToken()
        {
            var first = McpAuthToken.Load(this.projectPath);
            var rotated = McpAuthToken.Regenerate(this.projectPath);

            Assert.That(rotated, Is.Not.EqualTo(first));
            Assert.That(McpAuthToken.Load(this.projectPath), Is.EqualTo(rotated));
        }

        [Test]
        public void ACorruptFileIsReplacedRatherThanTrusted()
        {
            var path = McpAuthToken.PathFor(this.projectPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "not-a-token");

            var token = McpAuthToken.Load(this.projectPath);

            Assert.That(token, Has.Length.EqualTo(64));
            Assert.That(File.ReadAllText(path).Trim(), Is.EqualTo(token));
        }
    }
}
