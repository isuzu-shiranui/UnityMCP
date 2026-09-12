using System.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// The one tool that has to answer when nothing else can.
    /// </summary>
    /// <remarks>
    /// Every other tool runs on the Editor's main thread, so an import or a modal dialog holding
    /// that thread silences the ones that would report it. Twenty-five calls queued behind a
    /// project import and none came back, which from the caller's side reads as tools that hang.
    /// The declaration below is what keeps this one off that queue, so it is the thing worth
    /// guarding: a later edit that drops it would leave the tool present and useless.
    /// </remarks>
    [TestFixture]
    internal sealed class EditorStateToolsTests
    {
        private static McpToolDescriptor Descriptor()
        {
            return ToolCatalog.Build().Tools.FirstOrDefault(t => t.Name == "editor_state");
        }

        [Test]
        public void ItIsAnsweredWithoutTheMainThread()
        {
            var descriptor = Descriptor();

            Assert.That(descriptor, Is.Not.Null, "the tool has to be in the catalog");
            Assert.That(descriptor.MainThread, Is.False,
                "queued behind the main thread it would be silent exactly when it is needed");
        }

        [Test]
        public void ItIsSafeToCallAndCostsNothingToRetry()
        {
            Assert.That(Descriptor().Idempotency, Is.EqualTo(McpIdempotency.Safe));
        }

        /// <summary>
        /// Batch mode runs no server, so the tool has both answers to give depending on where it
        /// is called. Either is correct; a generic failure is not.
        /// </summary>
        [Test]
        public void ItEitherReportsTheQueueOrSaysThereIsNoServer()
        {
            JObject state;

            try
            {
                state = Tools.EditorStateTools.State();
            }
            catch (McpToolException refused)
            {
                Assert.That(refused.Code, Is.EqualTo("not_found"));
                Assert.That(refused.Message, Does.Contain("not running"),
                    "the caller has to be able to tell this from the Editor being wedged");
                return;
            }

            Assert.That(state["queueDepth"], Is.Not.Null,
                "a climbing queue against a still reqCount is how a wedged main thread shows");
            Assert.That(state["reqCount"], Is.Not.Null);
            Assert.That(state["mainThread"], Is.Not.Null);
        }
    }
}
