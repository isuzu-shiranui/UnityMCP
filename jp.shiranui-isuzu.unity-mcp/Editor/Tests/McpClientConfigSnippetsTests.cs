using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityMCP.Editor.Settings;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Every snippet the Settings window offers must parse and carry both the URL and the token.
    /// </summary>
    [TestFixture]
    internal sealed class McpClientConfigSnippetsTests
    {
        private const string Url = "http://127.0.0.1:27345/mcp";
        private const string Token = "abc123";

        [Test]
        public void JsonSnippetsParseAndNameTheServer()
        {
            var cursor = JObject.Parse(McpClientConfigSnippets.CursorJson(Url, Token));
            Assert.That(cursor["mcpServers"]["isuzu-unity"]["url"].Value<string>(), Is.EqualTo(Url));
            Assert.That(cursor["mcpServers"]["isuzu-unity"]["headers"]["Authorization"].Value<string>(), Is.EqualTo("Bearer " + Token));
            Assert.That(cursor["mcpServers"]["isuzu-unity"]["type"], Is.Null, "Cursor rejects some values of type and infers the transport from url.");

            var gemini = JObject.Parse(McpClientConfigSnippets.GeminiJson(Url, Token));
            Assert.That(gemini["mcpServers"]["isuzu-unity"]["httpUrl"].Value<string>(), Is.EqualTo(Url));

            var vscode = JObject.Parse(McpClientConfigSnippets.VsCodeJson(Url, Token));
            Assert.That(vscode["servers"]["isuzu-unity"]["type"].Value<string>(), Is.EqualTo("http"));

            // .vscode/mcp.json is usually committed, so the snippet must prompt rather than carry
            // a token that lets its reader run code inside the Editor.
            var authorization = vscode["servers"]["isuzu-unity"]["headers"]["Authorization"].Value<string>();
            Assert.That(authorization, Does.Not.Contain(Token));
            Assert.That(authorization, Is.EqualTo("Bearer ${input:isuzu-unity-token}"));
            Assert.That(vscode["inputs"][0]["id"].Value<string>(), Is.EqualTo("isuzu-unity-token"));
            Assert.That(vscode["inputs"][0]["password"].Value<bool>(), Is.True);

            var desktop = JObject.Parse(McpClientConfigSnippets.ClaudeDesktopJson(@"C:\bin\isuzu-unity-cli.exe", "My Game"));
            Assert.That(desktop["mcpServers"]["isuzu-unity"]["args"][2].Value<string>(), Is.EqualTo("My Game"));
        }

        [Test]
        public void CodexTomlQuotesValuesAsStrings()
        {
            var toml = McpClientConfigSnippets.CodexToml(Url, "to\"ken");

            Assert.That(toml, Does.StartWith("[mcp_servers.isuzu-unity]"));
            Assert.That(toml, Does.Contain("url = \"" + Url + "\""));
            Assert.That(toml, Does.Contain("Authorization = \"Bearer to\\\"ken\""));
        }

        [Test]
        public void ClaudeCodeCommandCarriesTheHeader()
        {
            var command = McpClientConfigSnippets.ClaudeCodeCommand(Url, Token);

            Assert.That(command, Does.StartWith("claude mcp add --transport http isuzu-unity " + Url));
            Assert.That(command, Does.Contain("Authorization: Bearer " + Token));
        }
    }
}
