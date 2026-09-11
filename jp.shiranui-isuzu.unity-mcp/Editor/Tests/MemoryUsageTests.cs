using System.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Handlers;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    internal sealed class MemoryUsageTests
    {
        [Test]
        public void EveryTypeAskedAboutComesBackAsARow()
        {
            var answer = MemoryTools.Usage(types: new[] { "Texture", "Mesh" }, top: 0);
            var types = ((JArray)answer["byType"]).Select(row => (string)row["type"]).ToList();

            Assert.That(types, Is.EquivalentTo(new[] { "Texture", "Mesh" }));
        }

        [Test]
        public void ATypeNothingIsWeighedForIsRefusedWithTheOnesThatAre()
        {
            var thrown = Assert.Throws<McpToolException>(
                () => MemoryTools.Usage(types: new[] { "Prefab" }));

            Assert.That(thrown.Code, Is.EqualTo("invalid_params"));
            Assert.That(thrown.Message, Does.Contain("Texture").And.Contain("Mesh"));
        }

        [Test]
        public void TypeNamesAreMatchedWithoutRegardToCase()
        {
            Assert.DoesNotThrow(() => MemoryTools.Usage(types: new[] { "texture" }, top: 0));
        }

        [Test]
        public void AScopeItDoesNotKnowIsRefusedWithTheThreeItDoes()
        {
            var thrown = Assert.Throws<McpToolException>(() => MemoryTools.Usage(scope: "everything"));

            Assert.That(thrown.Code, Is.EqualTo("invalid_params"));
            Assert.That(thrown.Message, Does.Contain("'project'").And.Contain("'all'"));
        }

        [Test]
        public void TheProjectTotalIsNeverMoreThanTheWholeTotal()
        {
            // An Editor holds objects the project does not own, so the two are only equal in a
            // project with nothing of its own; the project figure being larger would mean the split
            // is counting something twice.
            var answer = MemoryTools.Usage(top: 0);

            Assert.That((long)answer["projectBytes"], Is.LessThanOrEqualTo((long)answer["totalBytes"]));
        }

        [Test]
        public void EachTypeRowKeepsItsProjectShareInsideItsTotal()
        {
            var answer = MemoryTools.Usage(top: 0);

            foreach (var row in (JArray)answer["byType"])
            {
                Assert.That(
                    (long)row["projectBytes"], Is.LessThanOrEqualTo((long)row["bytes"]),
                    (string)row["type"]);
                Assert.That(
                    (int)row["projectCount"], Is.LessThanOrEqualTo((int)row["count"]),
                    (string)row["type"]);
            }
        }

        [Test]
        public void TheHeaviestObjectsComeBackHeaviestFirst()
        {
            var answer = MemoryTools.Usage(scope: "all", top: 20);
            var sizes = ((JArray)answer["items"]).Select(item => (long)item["bytes"]).ToList();

            Assert.That(sizes, Is.Ordered.Descending);
        }

        [Test]
        public void MoreNamedObjectsThanOneReplyWillCarryIsRefused()
        {
            var thrown = Assert.Throws<McpToolException>(() => MemoryTools.Usage(top: 201));

            Assert.That(thrown.Code, Is.EqualTo("invalid_params"));
            Assert.That(thrown.Message, Does.Contain("200"));
        }
    }
}
