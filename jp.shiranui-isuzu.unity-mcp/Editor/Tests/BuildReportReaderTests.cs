using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Handlers;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    internal sealed class BuildReportReaderTests
    {
        private static List<BuildReportReader.Packed> Packed(params (string Path, string Type, long Bytes)[] rows)
        {
            var packed = new List<BuildReportReader.Packed>();

            foreach (var (path, type, bytes) in rows)
            {
                packed.Add(new BuildReportReader.Packed { Path = path, Type = type, Bytes = bytes });
            }

            return packed;
        }

        [Test]
        public void AMissingReportIsRefusedWithWhereOneComesFrom()
        {
            var thrown = Assert.Throws<McpToolException>(
                () => BuildReportReader.Load("Library/NoSuchFile.buildreport"));

            Assert.That(thrown.Code, Is.EqualTo("not_found"));
            Assert.That(thrown.Message, Does.Contain("build_player"));
        }

        [Test]
        public void TypeTotalsAddUpAndComeBackHeaviestFirst()
        {
            var rows = BuildReportReader.ByType(Packed(
                ("a.png", "Texture2D", 100),
                ("b.png", "Texture2D", 50),
                ("c.asset", "TextAsset", 400)));

            Assert.That(rows.Select(r => (string)r["type"]), Is.EqualTo(new[] { "TextAsset", "Texture2D" }));
            Assert.That((long)rows[1]["bytes"], Is.EqualTo(150));
            Assert.That((int)rows[1]["count"], Is.EqualTo(2));
        }

        [Test]
        public void AnAssetPackedTwiceIsCountedTwice()
        {
            // Counting it once would average away exactly the duplication a size question is
            // usually asked to find.
            var rows = BuildReportReader.ByType(Packed(
                ("shared.png", "Texture2D", 100),
                ("shared.png", "Texture2D", 100)));

            Assert.That((long)rows[0]["bytes"], Is.EqualTo(200));
            Assert.That((int)rows[0]["count"], Is.EqualTo(2));
        }

        [Test]
        public void ComparingTwoBuildsSeparatesWhatAppearedFromWhatGrew()
        {
            var before = Packed(("kept.png", "Texture2D", 100), ("gone.png", "Texture2D", 30));
            var after = Packed(("kept.png", "Texture2D", 180), ("new.png", "Texture2D", 70));

            var changes = BuildReportReader.Compare(before, after, 10);

            Assert.That((int)changes["addedCount"], Is.EqualTo(1));
            Assert.That((int)changes["removedCount"], Is.EqualTo(1));
            Assert.That((int)changes["changedCount"], Is.EqualTo(1));
            Assert.That((string)changes["added"][0]["path"], Is.EqualTo("new.png"));
            Assert.That((string)changes["removed"][0]["path"], Is.EqualTo("gone.png"));
            Assert.That((long)changes["changed"][0]["delta"], Is.EqualTo(80));
        }

        [Test]
        public void AnAssetThatShrankIsReportedAsWellAsOneThatGrew()
        {
            // Sorting by the size of the change rather than its sign is what keeps a large
            // reduction from being pushed off the end of the list by a small increase.
            var changes = BuildReportReader.Compare(
                Packed(("big.png", "Texture2D", 900), ("small.png", "Texture2D", 10)),
                Packed(("big.png", "Texture2D", 100), ("small.png", "Texture2D", 20)),
                10);

            Assert.That((string)changes["changed"][0]["path"], Is.EqualTo("big.png"));
            Assert.That((long)changes["changed"][0]["delta"], Is.EqualTo(-800));
        }

        [Test]
        public void AnAssetPackedSeveralTimesIsOneRowInAComparison()
        {
            var changes = BuildReportReader.Compare(
                Packed(("shared.png", "Texture2D", 100), ("shared.png", "Texture2D", 100)),
                Packed(("shared.png", "Texture2D", 100)),
                10);

            Assert.That((int)changes["changedCount"], Is.EqualTo(1));
            Assert.That((long)changes["changed"][0]["delta"], Is.EqualTo(-100));
        }

        [Test]
        public void MoreNamedAssetsThanOneReplyWillCarryIsRefused()
        {
            var thrown = Assert.Throws<McpToolException>(() => BuildTools.Report(top: 201));

            Assert.That(thrown.Code, Is.EqualTo("invalid_params"));
            Assert.That(thrown.Message, Does.Contain("200"));
        }
    }
}
