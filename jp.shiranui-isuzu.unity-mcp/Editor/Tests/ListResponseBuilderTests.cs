using System.Collections.Generic;

using NUnit.Framework;
using Newtonsoft.Json.Linq;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Unit tests for <see cref="ListResponseBuilder"/> covering
    /// limit/offset/fields/truncated/next handling per R5.1–R5.2.
    /// </summary>
    [TestFixture]
    internal sealed class ListResponseBuilderTests
    {
        private static IReadOnlyList<JObject> MakeItems(int n)
        {
            var list = new List<JObject>(n);
            for (var i = 0; i < n; i++)
            {
                list.Add(new JObject
                {
                    ["t"] = $"item-{i}",
                    ["m"] = i,
                    ["f"] = i % 2 == 0
                });
            }
            return list;
        }

        [Test]
        public void Build_WithinLimit_ReturnsAllNotTruncated()
        {
            var items = MakeItems(5);

            var result = ListResponseBuilder.Build(items, offset: 0, limit: 10, projector: x => x);

            Assert.AreEqual(5, ((JArray)result["items"]).Count);
            Assert.IsFalse(result["truncated"].Value<bool>());
            Assert.AreEqual(JTokenType.Null, result["next"].Type);
        }

        [Test]
        public void Build_WithLimitSmallerThanTotal_TruncatesAndEmitsNext()
        {
            var items = MakeItems(25);

            var result = ListResponseBuilder.Build(items, offset: 0, limit: 10, projector: x => x);

            Assert.AreEqual(10, ((JArray)result["items"]).Count);
            Assert.IsTrue(result["truncated"].Value<bool>());
            Assert.IsNotNull(result["next"]);
            Assert.AreEqual(10, result["next"]["offset"].Value<int>());
            Assert.AreEqual(10, result["next"]["limit"].Value<int>());
        }

        [Test]
        public void Build_WithOffset_SkipsLeadingItems()
        {
            var items = MakeItems(10);

            var result = ListResponseBuilder.Build(items, offset: 3, limit: 2, projector: x => x);

            var arr = (JArray)result["items"];
            Assert.AreEqual(2, arr.Count);
            Assert.AreEqual("item-3", arr[0]["t"].ToString());
            Assert.AreEqual("item-4", arr[1]["t"].ToString());
            Assert.IsTrue(result["truncated"].Value<bool>());
            Assert.AreEqual(5, result["next"]["offset"].Value<int>());
        }

        [Test]
        public void Build_WithFieldsFilter_KeepsAllowedKeysOnly()
        {
            var items = MakeItems(3);
            var filter = new[] { "t", "f" };

            var result = ListResponseBuilder.Build(items, 0, 10, x => x, filter);

            var first = (JObject)((JArray)result["items"])[0];
            Assert.IsTrue(first.ContainsKey("t"));
            Assert.IsTrue(first.ContainsKey("f"));
            Assert.IsFalse(first.ContainsKey("m"), "m should have been filtered out");
        }

        [Test]
        public void Build_EmptyInput_ReturnsEmptyNotTruncated()
        {
            var items = new List<JObject>();

            var result = ListResponseBuilder.Build(items, 0, 10, x => x);

            Assert.AreEqual(0, ((JArray)result["items"]).Count);
            Assert.IsFalse(result["truncated"].Value<bool>());
            Assert.AreEqual(JTokenType.Null, result["next"].Type);
        }

        [Test]
        public void Build_LimitZero_IsTreatedAsUnlimited()
        {
            var items = MakeItems(4);

            var result = ListResponseBuilder.Build(items, offset: 0, limit: 0, projector: x => x);

            Assert.AreEqual(4, ((JArray)result["items"]).Count);
            Assert.IsFalse(result["truncated"].Value<bool>());
        }

        [Test]
        public void Build_OffsetBeyondTotal_ReturnsEmpty()
        {
            var items = MakeItems(3);

            var result = ListResponseBuilder.Build(items, offset: 99, limit: 10, projector: x => x);

            Assert.AreEqual(0, ((JArray)result["items"]).Count);
            Assert.IsFalse(result["truncated"].Value<bool>());
        }


        // ── the fields allowlist ──

        private static JObject Row(int n) => new JObject
        {
            ["id"] = n,
            ["name"] = "row" + n,
        };

        private static JObject RowWithOptional(int n)
        {
            var row = Row(n);

            // Written on one row only, the way a field omitted at its default value behaves.
            // Assigning null would still create the key, which is not the same thing.
            if (n == 2)
            {
                row["note"] = "seen";
            }

            return row;
        }

        private static readonly IReadOnlyList<int> Three = new[] { 1, 2, 3 };

        /// <summary>
        /// A field the tool keeps on its own travels back, but it is not the caller's name, so it
        /// cannot stand in for their list having matched something.
        /// </summary>
        [Test]
        public void AFieldTheToolKeepsIsReturnedButDoesNotCountAsAMatch()
        {
            var page = ListResponseBuilder.Build(Three, 0, 0, Row, new[] { "name" }, new[] { "id" });
            var first = (JObject)((JArray)page["items"])[0];

            Assert.IsNotNull(first["name"]);
            Assert.IsNotNull(first["id"], "the tool's own field travels whatever was asked for");
        }

        /// <summary>
        /// Answering with empty objects let a caller narrow a reply to nothing and read it as an
        /// empty result.
        /// </summary>
        [Test]
        public void ANameThatMatchesNothingIsRefusedEvenWhenTheToolKeepsItsOwnField()
        {
            var thrown = Assert.Throws<McpToolException>(
                () => ListResponseBuilder.Build(Three, 0, 0, Row, new[] { "nonesuch" }, new[] { "id" }));

            Assert.AreEqual("invalid_params", thrown.Code);
        }

        /// <summary>A row omitting an optional field says nothing about whether the tool has it.</summary>
        [Test]
        public void ANameOnlySomeRowsCarryIsAccepted()
        {
            var page = ListResponseBuilder.Build(Three, 0, 0, RowWithOptional, new[] { "note" });
            var rows = (JArray)page["items"];
            var carrying = 0;

            foreach (JObject row in rows)
            {
                if (row["note"] != null)
                {
                    carrying++;
                }
            }

            Assert.AreEqual(1, carrying);
        }

        [Test]
        public void ParseFieldsParam_HandlesNullAndWhitespaceAsNoFilter()
        {
            Assert.IsNull(ListResponseBuilder.ParseFieldsParam(null));
            Assert.IsNull(ListResponseBuilder.ParseFieldsParam(""));
            Assert.IsNull(ListResponseBuilder.ParseFieldsParam("   "));
        }

        [Test]
        public void ParseFieldsParam_SplitsAndTrims()
        {
            var parsed = ListResponseBuilder.ParseFieldsParam("t, m ,f");
            CollectionAssert.AreEqual(new[] { "t", "m", "f" }, parsed);
        }
    }
}
