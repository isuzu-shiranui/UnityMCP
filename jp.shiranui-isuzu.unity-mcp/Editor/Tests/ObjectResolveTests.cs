using NUnit.Framework;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Resolving a hierarchy path to the object it names, and back.
    /// </summary>
    /// <remarks>
    /// The two properties worth guarding are the ones <c>GameObject.Find</c> does not have:
    /// inactive objects resolve, and a name that repeats among siblings still identifies exactly
    /// one object. Both matter because every authoring tool addresses its target this way, and a
    /// path that silently lands on the wrong object edits the wrong thing.
    /// </remarks>
    [TestFixture]
    internal sealed class ObjectResolveTests
    {
        private GameObject root;

        [SetUp]
        public void SetUp()
        {
            this.root = new GameObject("ResolveRoot");
            var child = new GameObject("Child");
            child.transform.SetParent(this.root.transform);

            var twinA = new GameObject("Twin");
            twinA.transform.SetParent(this.root.transform);
            var twinB = new GameObject("Twin");
            twinB.transform.SetParent(this.root.transform);

            var deep = new GameObject("Deep");
            deep.transform.SetParent(twinB.transform);
        }

        [TearDown]
        public void TearDown()
        {
            if (this.root != null)
            {
                Object.DestroyImmediate(this.root);
            }
        }

        [Test]
        public void ResolvesARootByPath()
        {
            Assert.That(ObjectResolve.Object("/ResolveRoot", null).name, Is.EqualTo("ResolveRoot"));
        }

        [Test]
        public void ALeadingSlashIsOptional()
        {
            Assert.That(ObjectResolve.Object("ResolveRoot/Child", null).name, Is.EqualTo("Child"));
        }

        [Test]
        public void ResolvesAnInactiveObject()
        {
            var child = ObjectResolve.Object("/ResolveRoot/Child", null);
            child.SetActive(false);

            Assert.That(ObjectResolve.Object("/ResolveRoot/Child", null), Is.SameAs(child),
                "GameObject.Find cannot see this, which is why the walk exists");
        }

        [Test]
        public void ResolvesUnderAnInactiveParent()
        {
            this.root.SetActive(false);

            Assert.That(ObjectResolve.Object("/ResolveRoot/Twin[1]/Deep", null).name, Is.EqualTo("Deep"));
        }

        [Test]
        public void DuplicateSiblingsGetAnIndexedPath()
        {
            var twins = this.root.transform;
            var first = twins.Find("Twin").gameObject;

            Assert.That(ObjectResolve.PathOf(first), Is.EqualTo("/ResolveRoot/Twin[0]"));
        }

        [Test]
        public void AUniqueNameGetsNoIndex()
        {
            var child = ObjectResolve.Object("/ResolveRoot/Child", null);

            Assert.That(ObjectResolve.PathOf(child), Is.EqualTo("/ResolveRoot/Child"));
        }

        [Test]
        public void AnIndexedPathRoundTrips()
        {
            var second = ObjectResolve.Object("/ResolveRoot/Twin[1]", null);
            var path = ObjectResolve.PathOf(second);

            Assert.That(path, Is.EqualTo("/ResolveRoot/Twin[1]"));
            Assert.That(ObjectResolve.Object(path, null), Is.SameAs(second));
        }

        [Test]
        public void AnUnindexedPathTakesTheFirstMatch()
        {
            Assert.That(
                ObjectResolve.Object("/ResolveRoot/Twin", null),
                Is.SameAs(ObjectResolve.Object("/ResolveRoot/Twin[0]", null)));
        }

        [Test]
        public void AnInstanceIdWins()
        {
            var child = ObjectResolve.Object("/ResolveRoot/Child", null);

            Assert.That(ObjectResolve.Object(null, child.GetInstanceID()), Is.SameAs(child));
        }

        [Test]
        public void AMissingSegmentReportsWhatIsThere()
        {
            var ex = Assert.Throws<McpToolException>(
                () => ObjectResolve.Object("/ResolveRoot/Nope", null));

            Assert.That(ex.Code, Is.EqualTo("not_found"));
            Assert.That(ex.Message, Does.Contain("Child"), "the message should list the real children");
            Assert.That(ex.Message, Does.Contain("Twin"));
        }

        [Test]
        public void NeitherPathNorIdIsRefused()
        {
            var ex = Assert.Throws<McpToolException>(() => ObjectResolve.Object(null, null));

            Assert.That(ex.Code, Is.EqualTo("invalid_params"));
        }

        [Test]
        public void AStaleInstanceIdIsExplained()
        {
            var ex = Assert.Throws<McpToolException>(() => ObjectResolve.Object(null, -987654321));

            Assert.That(ex.Code, Is.EqualTo("not_found"));
            Assert.That(ex.Message, Does.Contain("domain reload"));
        }

        // ── repetition ──

        [Test]
        public void CreatingASecondSiblingChangesTheFirstOnesPath()
        {
            var solo = new GameObject("Solo");
            solo.transform.SetParent(this.root.transform);

            Assert.That(ObjectResolve.PathOf(solo), Is.EqualTo("/ResolveRoot/Solo"));

            var twin = new GameObject("Solo");
            twin.transform.SetParent(this.root.transform);

            // Worth pinning down rather than discovering later: an unindexed path held from
            // before the second object existed still resolves, and still resolves to the first.
            Assert.That(ObjectResolve.PathOf(solo), Is.EqualTo("/ResolveRoot/Solo[0]"));
            Assert.That(ObjectResolve.Object("/ResolveRoot/Solo", null), Is.SameAs(solo));
        }

        [Test]
        public void ResolvingRepeatedlyGivesTheSameObject()
        {
            var first = ObjectResolve.Object("/ResolveRoot/Twin[1]", null);

            for (var i = 0; i < 5; i++)
            {
                Assert.That(ObjectResolve.Object("/ResolveRoot/Twin[1]", null), Is.SameAs(first));
            }
        }

        [Test]
        public void APathSurvivesTheObjectBeingDeactivatedAndReactivated()
        {
            var child = ObjectResolve.Object("/ResolveRoot/Child", null);

            for (var i = 0; i < 3; i++)
            {
                child.SetActive(false);
                Assert.That(ObjectResolve.Object("/ResolveRoot/Child", null), Is.SameAs(child));
                child.SetActive(true);
                Assert.That(ObjectResolve.Object("/ResolveRoot/Child", null), Is.SameAs(child));
            }
        }

        [Test]
        public void RenamingInvalidatesTheOldPathAndTheNewOneWorks()
        {
            var child = ObjectResolve.Object("/ResolveRoot/Child", null);
            child.name = "Renamed";

            Assert.Throws<McpToolException>(() => ObjectResolve.Object("/ResolveRoot/Child", null));
            Assert.That(ObjectResolve.Object("/ResolveRoot/Renamed", null), Is.SameAs(child));
        }

        // ── boundaries ──

        [Test]
        public void AnEmptyPathIsRefused()
        {
            Assert.That(Assert.Throws<McpToolException>(() => ObjectResolve.Object("", null)).Code,
                Is.EqualTo("invalid_params"));
            Assert.That(Assert.Throws<McpToolException>(() => ObjectResolve.Object("   ", null)).Code,
                Is.EqualTo("invalid_params"));
        }

        [Test]
        public void RedundantSlashesAreIgnored()
        {
            Assert.That(ObjectResolve.Object("//ResolveRoot///Child/", null).name, Is.EqualTo("Child"));
        }

        [Test]
        public void AnIndexOfZeroIsTheSameAsNoIndex()
        {
            Assert.That(
                ObjectResolve.Object("/ResolveRoot/Child[0]", null),
                Is.SameAs(ObjectResolve.Object("/ResolveRoot/Child", null)));
        }

        [Test]
        public void AnIndexPastTheLastDuplicateDoesNotResolve()
        {
            Assert.That(Assert.Throws<McpToolException>(
                () => ObjectResolve.Object("/ResolveRoot/Twin[2]", null)).Code, Is.EqualTo("not_found"));
        }

        [Test]
        public void DeepNestingRoundTrips()
        {
            var current = this.root.transform;

            for (var i = 0; i < 12; i++)
            {
                var next = new GameObject("L" + i);
                next.transform.SetParent(current);
                current = next.transform;
            }

            var path = ObjectResolve.PathOf(current.gameObject);

            Assert.That(path, Is.EqualTo("/ResolveRoot/L0/L1/L2/L3/L4/L5/L6/L7/L8/L9/L10/L11"));
            Assert.That(ObjectResolve.Object(path, null), Is.SameAs(current.gameObject));
        }

        [Test]
        public void PathOfNullIsNullRatherThanAThrow()
        {
            Assert.That(ObjectResolve.PathOf(null), Is.Null);
        }

        [Test]
        public void FindsAComponentByShortName()
        {
            var child = ObjectResolve.Object("/ResolveRoot/Child", null);

            Assert.That(ObjectResolve.Component(child, "Transform"), Is.SameAs(child.transform));
        }

        [Test]
        public void AMissingComponentListsWhatThereIs()
        {
            var child = ObjectResolve.Object("/ResolveRoot/Child", null);
            var ex = Assert.Throws<McpToolException>(() => ObjectResolve.Component(child, "Rigidbody"));

            Assert.That(ex.Code, Is.EqualTo("not_found"));
            Assert.That(ex.Message, Does.Contain("Transform"));
        }
    }
}
