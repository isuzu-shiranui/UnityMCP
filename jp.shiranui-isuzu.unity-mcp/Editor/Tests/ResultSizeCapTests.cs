using NUnit.Framework;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// The size a tool declares is a limit rather than a note about one.
    /// </summary>
    /// <remarks>
    /// It rode along in <c>_meta</c> and nothing checked it, so a large scene could answer
    /// scene_browse_hierarchy with several times the 200,000 characters it declares, under a
    /// <c>truncated</c> of false: most of a context window, spent on a reply that says nothing
    /// was left out. A small test scene stays under the limit, so only a large one shows it.
    /// </remarks>
    [TestFixture]
    internal sealed class ResultSizeCapTests
    {
        private static McpToolDescriptor Browse()
        {
            var catalog = ToolCatalog.Build();

            foreach (var tool in catalog.Tools)
            {
                if (tool.Name == "scene_browse_hierarchy")
                {
                    return tool;
                }
            }

            return null;
        }

        [Test]
        public void AReplyPastTheDeclaredSizeIsNotSent()
        {
            var descriptor = Browse();

            Assert.That(descriptor.MaxResultSizeChars, Is.GreaterThan(0),
                "the tool has to declare a size for there to be one to hold it to");

            var oversized = new string('x', descriptor.MaxResultSizeChars + 1);
            var said = McpStreamableHttpEndpoint.TooLarge(oversized, descriptor);

            Assert.That(said, Is.Not.Null);
            Assert.That(said, Does.Contain("scene_browse_hierarchy"));
            Assert.That(said, Does.Contain("limit"), "and say how to ask for less");
        }

        [Test]
        public void AReplyWithinTheDeclaredSizeGoesThrough()
        {
            var descriptor = Browse();
            var fits = new string('x', descriptor.MaxResultSizeChars);

            Assert.That(McpStreamableHttpEndpoint.TooLarge(fits, descriptor), Is.Null);
        }

        [Test]
        public void AToolThatDeclaresNoSizeIsNotHeldToOne()
        {
            var catalog = ToolCatalog.Build();

            foreach (var tool in catalog.Tools)
            {
                if (tool.MaxResultSizeChars == 0)
                {
                    Assert.That(
                        McpStreamableHttpEndpoint.TooLarge(new string('x', 5_000_000), tool),
                        Is.Null,
                        "a tool with no declared size has nothing to exceed");
                    return;
                }
            }

            Assert.Ignore("every tool declares a size, so there is nothing to check here");
        }
    }
}
