using System.IO;

using NUnit.Framework;

using UnityMCP.Editor.Settings;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// The Settings page could not say whether the last setup step had been done, so a finished
    /// setup and an unfinished one looked the same.
    /// </summary>
    /// <remarks>
    /// The project-scope files are what these exercise. The user-scope ones live at fixed paths in
    /// the profile, and a test that wrote there would be editing the machine's real configuration.
    /// </remarks>
    public sealed class McpClientRegistrationTests
    {
        private string root;

        [SetUp]
        public void MakeProject()
        {
            this.root = Path.Combine(Path.GetTempPath(), "McpClientRegistrationTests-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(this.root, ".vscode"));
        }

        [TearDown]
        public void RemoveProject()
        {
            if (Directory.Exists(this.root))
            {
                Directory.Delete(this.root, true);
            }
        }

        [Test]
        public void AConfigurationNamingThisEditorCountsAsRegistered()
        {
            const string url = "http://127.0.0.1:27999/mcp";
            var config = Path.Combine(this.root, ".mcp.json");

            File.WriteAllText(config, "{\"mcpServers\":{\"isuzu-unity\":{\"url\":\"" + url + "\"}}}");

            Assert.That(McpClientRegistration.Registered(url, this.root), Does.Contain(config));
        }

        /// <summary>
        /// The port is what makes the URL this Editor's. An entry left over from a session that
        /// bound a different port cannot reach this one, and a checklist that called that done
        /// would be pointing at the wrong Editor.
        /// </summary>
        [Test]
        public void AConfigurationNamingAnotherPortIsNotThisEditor()
        {
            File.WriteAllText(
                Path.Combine(this.root, ".mcp.json"),
                "{\"mcpServers\":{\"isuzu-unity\":{\"url\":\"http://127.0.0.1:27000/mcp\"}}}");

            Assert.That(McpClientRegistration.Registered("http://127.0.0.1:27999/mcp", this.root), Is.Empty);
        }

        [Test]
        public void AProjectWithNoConfigurationAtAllIsNotRegistered()
        {
            Assert.That(McpClientRegistration.Registered("http://127.0.0.1:27999/mcp", this.root), Is.Empty);
        }

        /// <summary>
        /// A stdio entry counts, and it carries no URL to match on.
        /// </summary>
        /// <remarks>
        /// Claude Desktop cannot open a local HTTP server, so setup gives it the CLI's bridge
        /// instead. Searching for the URL alone reported that as not registered, and offered a
        /// button to write the registration that was already there.
        /// </remarks>
        [Test]
        public void AStdioBridgeEntryForThisProjectCountsAsRegistered()
        {
            var config = Path.Combine(this.root, ".mcp.json");

            File.WriteAllText(
                config,
                "{\"mcpServers\":{\"isuzu-unity\":{\"command\":\"isuzu-unity-cli.exe\"," +
                "\"args\":[\"mcp-stdio\",\"--project\",\"My Game\"]}}}");

            Assert.That(
                McpClientRegistration.Registered("http://127.0.0.1:27999/mcp", this.root, "My Game"),
                Does.Contain(config));

            Assert.That(
                McpClientRegistration.Registered("http://127.0.0.1:27999/mcp", this.root, "Other Game"),
                Is.Empty,
                "a bridge launched for a different project does not reach this Editor");
        }

        /// <summary>
        /// The name has to be the entry's own argument, not merely somewhere in the file.
        /// </summary>
        /// <remarks>
        /// Unity's default product name is "My project", which every unrenamed project carries,
        /// so a file holding one project's bridge and another's name anywhere in it answered for
        /// both. A name that is a prefix of another project's is the same mistake.
        /// </remarks>
        [Test]
        public void AnotherProjectsBridgeIsNotThisProjectsRegistration()
        {
            var config = Path.Combine(this.root, ".mcp.json");

            File.WriteAllText(
                config,
                "{\"mcpServers\":{\"isuzu-unity\":{\"command\":\"isuzu-unity-cli.exe\"," +
                "\"args\":[\"mcp-stdio\",\"--project\",\"DemoScene\"]}," +
                "\"notes\":{\"command\":\"echo\",\"args\":[\"Demo\"]}}}");

            Assert.That(
                McpClientRegistration.Registered("http://127.0.0.1:27999/mcp", this.root, "Demo"),
                Is.Empty,
                "the bridge is for DemoScene, and Demo appears only outside it");
        }

        /// <summary>
        /// Both project-scope files are looked at: setup --scope project writes .mcp.json, and
        /// VS Code keeps its own under .vscode.
        /// </summary>
        [Test]
        public void TheVsCodeConfigurationIsLookedAtToo()
        {
            const string url = "http://127.0.0.1:27999/mcp";
            var config = Path.Combine(this.root, ".vscode", "mcp.json");

            File.WriteAllText(config, "{\"servers\":{\"isuzu-unity\":{\"url\":\"" + url + "\"}}}");

            Assert.That(McpClientRegistration.Registered(url, this.root), Does.Contain(config));
        }

        [Test]
        public void NoUrlMeansNothingToLookFor()
        {
            Assert.That(McpClientRegistration.Registered(null, this.root), Is.Empty);
            Assert.That(McpClientRegistration.Registered(string.Empty, this.root), Is.Empty);
        }
    }
}
