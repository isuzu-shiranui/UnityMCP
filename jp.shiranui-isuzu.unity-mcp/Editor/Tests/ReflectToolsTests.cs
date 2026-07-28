using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Path parsing, member walking and the limits on what comes back.
    /// </summary>
    /// <remarks>
    /// The limits are the part worth guarding. A reflection reader that follows every reference
    /// as far as it goes will happily serialise an entire scene graph into one response, and a
    /// truncation that is not announced is indistinguishable from an empty collection.
    /// </remarks>
    [TestFixture]
    internal sealed class ReflectToolsTests
    {
        internal sealed class Inner
        {
            public int Depth2 = 42;
        }

        internal sealed class Outer
        {
            public Inner Nested = new Inner();

            public int Number = 7;
        }

        internal static class Probe
        {
            public static int Value = 5;

            public static string[] Names = { "a", "b", "c" };

            public static Dictionary<string, int> Map = new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 };

            public static Outer Tree = new Outer();

            public static Vector3 Position = new Vector3(1f, 2f, 3f);

            public static object Nothing;
        }

        private static JToken Read(string path, int depth = 2, int maxItems = 20)
        {
            var value = ReflectTools.ResolvePath(path, out _, out _);
            return ReflectTools.Serialize(value, depth, maxItems);
        }

        [Test]
        public void SplitsSegmentsAndIndexers()
        {
            var segments = ReflectTools.SplitPath("A.B/c/d[3]/e[\"key\"]");

            Assert.That(segments.Select(s => s.Name), Is.EqualTo(new[] { "A.B", "c", "d", "e" }));
            Assert.That(segments[2].Index, Is.EqualTo("3"));
            Assert.That(segments[3].Index, Is.EqualTo("key"), "quotes should be stripped");
            Assert.That(segments[1].Index, Is.Null);
        }

        [Test]
        public void ReadsAStaticField()
        {
            Assert.That(Read($"{typeof(Probe).FullName}/Value").Value<int>(), Is.EqualTo(5));
        }

        [Test]
        public void IndexesAnArray()
        {
            Assert.That(Read($"{typeof(Probe).FullName}/Names[1]").Value<string>(), Is.EqualTo("b"));
        }

        [Test]
        public void IndexesADictionaryByKey()
        {
            Assert.That(Read($"{typeof(Probe).FullName}/Map[\"y\"]").Value<int>(), Is.EqualTo(2));
        }

        [Test]
        public void AMissingDictionaryKeyListsTheKeys()
        {
            var ex = Assert.Throws<McpToolException>(
                () => ReflectTools.ResolvePath($"{typeof(Probe).FullName}/Map[\"z\"]", out _, out _));

            Assert.That(ex.Code, Is.EqualTo("not_found"));
            Assert.That(ex.Message, Does.Contain("x"));
        }

        [Test]
        public void AnIndexPastTheEndIsRefused()
        {
            var ex = Assert.Throws<McpToolException>(
                () => ReflectTools.ResolvePath($"{typeof(Probe).FullName}/Names[9]", out _, out _));

            Assert.That(ex.Code, Is.EqualTo("invalid_params"));
        }

        [Test]
        public void AnUnknownMemberListsWhatIsAvailable()
        {
            var ex = Assert.Throws<McpToolException>(
                () => ReflectTools.ResolvePath($"{typeof(Probe).FullName}/NotThere", out _, out _));

            Assert.That(ex.Code, Is.EqualTo("not_found"));
            Assert.That(ex.Message, Does.Contain("Value"));
        }

        [Test]
        public void AnUnknownTypeIsReported()
        {
            var ex = Assert.Throws<McpToolException>(
                () => ReflectTools.ResolvePath("No.Such.Type/x", out _, out _));

            Assert.That(ex.Code, Is.EqualTo("not_found"));
        }

        [Test]
        public void WalkingThroughNullExplainsWhere()
        {
            var ex = Assert.Throws<McpToolException>(
                () => ReflectTools.ResolvePath($"{typeof(Probe).FullName}/Nothing/anything", out _, out _));

            Assert.That(ex.Code, Is.EqualTo("not_found"));
            Assert.That(ex.Message, Does.Contain("Nothing"));
        }

        [Test]
        public void VectorsSerialiseAsComponents()
        {
            var v = Read($"{typeof(Probe).FullName}/Position");

            Assert.That(v["x"].Value<float>(), Is.EqualTo(1f));
            Assert.That(v["z"].Value<float>(), Is.EqualTo(3f));
        }

        [Test]
        public void DepthIsAnnouncedWhenItRunsOut()
        {
            var shallow = Read($"{typeof(Probe).FullName}/Tree", depth: 1);

            Assert.That(shallow["Number"].Value<int>(), Is.EqualTo(7));
            Assert.That(shallow["Nested"]["truncated"].Value<string>(), Is.EqualTo("depth"),
                "a value cut off by the depth limit must say so rather than read as empty");
        }

        [Test]
        public void DepthReachesNestedValuesWhenAllowed()
        {
            var deep = Read($"{typeof(Probe).FullName}/Tree", depth: 3);

            Assert.That(deep["Nested"]["Depth2"].Value<int>(), Is.EqualTo(42));
        }

        [Test]
        public void CollectionTruncationIsAnnounced()
        {
            var limited = Read($"{typeof(Probe).FullName}/Names", maxItems: 2);

            Assert.That(limited["items"].Count(), Is.EqualTo(2));
            Assert.That(limited["truncated"].Value<string>(), Does.Contain("2"));
        }

        [Test]
        public void AnUntruncatedCollectionIsAPlainArray()
        {
            var full = Read($"{typeof(Probe).FullName}/Names", maxItems: 10);

            Assert.That(full, Is.TypeOf<JArray>());
            Assert.That(full.Count(), Is.EqualTo(3));
        }

        // ── repetition ──

        [Test]
        public void RepeatedReadsAreStable()
        {
            var first = Read($"{typeof(Probe).FullName}/Tree", depth: 3).ToString();

            for (var i = 0; i < 5; i++)
            {
                Assert.That(Read($"{typeof(Probe).FullName}/Tree", depth: 3).ToString(), Is.EqualTo(first));
            }
        }

        [Test]
        public void ReadingDoesNotChangeWhatIsBeingRead()
        {
            var before = Probe.Value;
            var names = Probe.Names.ToArray();

            for (var i = 0; i < 3; i++)
            {
                Read($"{typeof(Probe).FullName}/Value");
                Read($"{typeof(Probe).FullName}/Names");
                Read($"{typeof(Probe).FullName}/Map");
            }

            Assert.That(Probe.Value, Is.EqualTo(before));
            Assert.That(Probe.Names, Is.EqualTo(names));
        }

        [Test]
        public void RepeatedFailuresKeepFailingTheSameWay()
        {
            for (var i = 0; i < 5; i++)
            {
                var ex = Assert.Throws<McpToolException>(
                    () => ReflectTools.ResolvePath($"{typeof(Probe).FullName}/NotThere", out _, out _));

                Assert.That(ex.Code, Is.EqualTo("not_found"));
            }
        }

        // ── boundaries ──

        [Test]
        public void DepthZeroStillNamesTheType()
        {
            var v = Read($"{typeof(Probe).FullName}/Tree", depth: 0);

            Assert.That(v["truncated"].Value<string>(), Is.EqualTo("depth"));
            Assert.That(v["type"].Value<string>(), Does.Contain("Outer"));
        }

        [Test]
        public void MaxItemsZeroReturnsNoneAndSaysSo()
        {
            var v = Read($"{typeof(Probe).FullName}/Names", maxItems: 0);

            Assert.That(v["items"].Count(), Is.EqualTo(0));
            Assert.That(v["truncated"], Is.Not.Null);
        }

        [Test]
        public void MaxItemsExactlyAtTheCountIsNotTruncated()
        {
            var v = Read($"{typeof(Probe).FullName}/Names", maxItems: 3);

            Assert.That(v, Is.TypeOf<JArray>(), "three of three is the whole thing");
        }

        [Test]
        public void ATrailingSlashIsIgnored()
        {
            Assert.That(Read($"{typeof(Probe).FullName}/Value/").Value<int>(), Is.EqualTo(5));
        }

        [Test]
        public void AnUnclosedIndexerIsTreatedAsPartOfTheName()
        {
            // Better to fail on the member name than to guess at what was meant.
            var ex = Assert.Throws<McpToolException>(
                () => ReflectTools.ResolvePath($"{typeof(Probe).FullName}/Names[1", out _, out _));

            Assert.That(ex.Code, Is.EqualTo("not_found"));
        }

        [Test]
        public void AnEmptyPathIsRefused()
        {
            Assert.That(Assert.Throws<McpToolException>(
                () => ReflectTools.SplitPath("///")).Code, Is.EqualTo("invalid_params"));
        }

        [Test]
        public void TheTypeAloneIsAValidPath()
        {
            var value = ReflectTools.ResolvePath(typeof(Probe).FullName, out var type, out _);

            Assert.That(value, Is.Null, "no member was named, so there is no value");
            Assert.That(type, Is.EqualTo(typeof(Probe)));
        }

        [Test]
        public void NullSerialisesAsNullRatherThanBeingDropped()
        {
            Assert.That(Read($"{typeof(Probe).FullName}/Nothing").Type, Is.EqualTo(JTokenType.Null));
        }

        [Test]
        public void MemberNamesIncludeNonPublicOnes()
        {
            var names = ReflectTools.MemberNames(typeof(Probe)).ToArray();

            Assert.That(names, Does.Contain("Value"));
            Assert.That(names, Does.Contain("Map"));
        }
    }
}
