using System;
using System.IO;
using System.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Difference statistics, their boundaries, and what repeating a call costs.
    /// </summary>
    /// <remarks>
    /// The numbers here are chosen by the test and written into the images, so a wrong answer
    /// cannot agree with a wrong expectation. The repetition cases exist because the defects
    /// this file was written alongside were all of that shape: correct once, wrong or leaking
    /// the second time.
    /// </remarks>
    [TestFixture]
    internal sealed class RenderCompareTests
    {
        private string directory;

        [SetUp]
        public void SetUp()
        {
            this.directory = Path.Combine(Path.GetTempPath(), "unity-mcp-compare-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(this.directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(this.directory))
            {
                Directory.Delete(this.directory, true);
            }
        }

        /// <summary>Writes a solid image, optionally with one differently coloured rectangle.</summary>
        private string Png(string name, int width, int height, Color32 fill, RectInt? patch = null, Color32 patchColour = default)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);

            try
            {
                var pixels = Enumerable.Repeat(fill, width * height).ToArray();

                if (patch.HasValue)
                {
                    var r = patch.Value;

                    for (var y = r.yMin; y < r.yMax; y++)
                    {
                        for (var x = r.xMin; x < r.xMax; x++)
                        {
                            pixels[y * width + x] = patchColour;
                        }
                    }
                }

                texture.SetPixels32(pixels);
                texture.Apply();

                var path = Path.Combine(this.directory, name);
                File.WriteAllBytes(path, texture.EncodeToPNG());
                return path;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static int LiveTextureCount()
        {
            return UnityEngine.Resources.FindObjectsOfTypeAll<Texture2D>().Length;
        }

        [Test]
        public void IdenticalImagesReportNoChange()
        {
            var a = this.Png("a.png", 16, 8, new Color32(10, 20, 30, 255));
            var b = this.Png("b.png", 16, 8, new Color32(10, 20, 30, 255));

            var result = RenderTools.Compare(a, b);

            Assert.That(result["identical"].Value<bool>(), Is.True);
            Assert.That(result["changedPixels"].Value<long>(), Is.EqualTo(0));
            Assert.That(result["maxDelta"].Value<int>(), Is.EqualTo(0));
            Assert.That(result["boundingBox"], Is.Null, "there is no box when nothing moved");
        }

        [Test]
        public void CountsAndLocatesAKnownRectangle()
        {
            var a = this.Png("a.png", 20, 10, new Color32(10, 20, 30, 255));
            var b = this.Png("b.png", 20, 10, new Color32(10, 20, 30, 255),
                new RectInt(4, 2, 5, 3), new Color32(10, 60, 30, 255));

            var result = RenderTools.Compare(a, b);

            Assert.That(result["changedPixels"].Value<long>(), Is.EqualTo(5 * 3));
            Assert.That(result["meanDelta"].Value<double>(), Is.EqualTo(40d));
            Assert.That(result["maxDelta"].Value<int>(), Is.EqualTo(40));
            Assert.That(result["boundingBox"]["x"].Value<int>(), Is.EqualTo(4));
            Assert.That(result["boundingBox"]["y"].Value<int>(), Is.EqualTo(2));
            Assert.That(result["boundingBox"]["width"].Value<int>(), Is.EqualTo(5));
            Assert.That(result["boundingBox"]["height"].Value<int>(), Is.EqualTo(3));
        }

        [Test]
        public void TheDeltaIsThePerChannelMaximum()
        {
            var a = this.Png("a.png", 4, 4, new Color32(10, 10, 10, 255));
            var b = this.Png("b.png", 4, 4, new Color32(20, 15, 12, 255));

            var result = RenderTools.Compare(a, b);

            Assert.That(result["maxDelta"].Value<int>(), Is.EqualTo(10), "not the sum of the channels");
        }

        // ── boundaries ──

        [Test]
        public void AThresholdIsInclusive()
        {
            var a = this.Png("a.png", 4, 4, new Color32(10, 10, 10, 255));
            var b = this.Png("b.png", 4, 4, new Color32(13, 10, 10, 255));

            Assert.That(RenderTools.Compare(a, b, threshold: 3)["changedPixels"].Value<long>(), Is.EqualTo(0),
                "a delta equal to the threshold is not a change");
            Assert.That(RenderTools.Compare(a, b, threshold: 2)["changedPixels"].Value<long>(), Is.EqualTo(16));
        }

        [Test]
        public void AThresholdAboveEveryPossibleDeltaFindsNothing()
        {
            var a = this.Png("a.png", 4, 4, new Color32(0, 0, 0, 255));
            var b = this.Png("b.png", 4, 4, new Color32(255, 255, 255, 255));

            Assert.That(RenderTools.Compare(a, b, threshold: 255)["identical"].Value<bool>(), Is.True);
            Assert.That(RenderTools.Compare(a, b, threshold: 254)["changedPixels"].Value<long>(), Is.EqualTo(16));
        }

        [Test]
        public void ASinglePixelImageWorks()
        {
            var a = this.Png("a.png", 1, 1, new Color32(0, 0, 0, 255));
            var b = this.Png("b.png", 1, 1, new Color32(9, 0, 0, 255));

            var result = RenderTools.Compare(a, b);

            Assert.That(result["totalPixels"].Value<long>(), Is.EqualTo(1));
            Assert.That(result["changedPixels"].Value<long>(), Is.EqualTo(1));
            Assert.That(result["boundingBox"]["width"].Value<int>(), Is.EqualTo(1));
        }

        [Test]
        public void TheGridIsClampedRatherThanTrusted()
        {
            var a = this.Png("a.png", 8, 8, new Color32(0, 0, 0, 255));
            var b = this.Png("b.png", 8, 8, new Color32(9, 0, 0, 255));

            Assert.That(RenderTools.Compare(a, b, grid: 0)["gridChangedRatio"].Count(), Is.EqualTo(1),
                "a grid below one collapses to a single cell rather than dividing by zero");
            Assert.That(RenderTools.Compare(a, b, grid: 1000)["gridChangedRatio"].Count(), Is.EqualTo(32),
                "and an absurd grid is capped");
        }

        [Test]
        public void EveryCellIsFullWhenEverythingChanged()
        {
            var a = this.Png("a.png", 8, 8, new Color32(0, 0, 0, 255));
            var b = this.Png("b.png", 8, 8, new Color32(90, 0, 0, 255));

            var grid = RenderTools.Compare(a, b, grid: 2)["gridChangedRatio"];

            Assert.That(grid.SelectMany(row => row).Select(v => v.Value<double>()),
                Is.All.EqualTo(1d));
        }

        [Test]
        public void MismatchedSizesAreRefusedWithTheSizes()
        {
            var a = this.Png("a.png", 8, 8, new Color32(0, 0, 0, 255));
            var b = this.Png("b.png", 4, 8, new Color32(0, 0, 0, 255));

            var ex = Assert.Throws<McpToolException>(() => RenderTools.Compare(a, b));

            Assert.That(ex.Code, Is.EqualTo("invalid_params"));
            Assert.That(ex.Message, Does.Contain("8x8"), "the refusal has to name the sizes it saw");
            Assert.That(ex.Message, Does.Contain("4x8"));
        }

        [Test]
        public void AMissingFileIsReported()
        {
            var a = this.Png("a.png", 4, 4, new Color32(0, 0, 0, 255));
            var ex = Assert.Throws<McpToolException>(
                () => RenderTools.Compare(a, Path.Combine(this.directory, "absent.png")));

            Assert.That(ex.Code, Is.EqualTo("not_found"));
        }

        // ── repetition ──

        [Test]
        public void RepeatedCallsGiveTheSameAnswer()
        {
            var a = this.Png("a.png", 12, 6, new Color32(10, 20, 30, 255));
            var b = this.Png("b.png", 12, 6, new Color32(10, 20, 30, 255),
                new RectInt(1, 1, 3, 2), new Color32(10, 70, 30, 255));

            var first = RenderTools.Compare(a, b).ToString();

            for (var i = 0; i < 4; i++)
            {
                Assert.That(RenderTools.Compare(a, b).ToString(), Is.EqualTo(first));
            }
        }

        [Test]
        public void RepeatedCallsDoNotAccumulateTextures()
        {
            var a = this.Png("a.png", 12, 6, new Color32(10, 20, 30, 255));
            var b = this.Png("b.png", 12, 6, new Color32(11, 20, 30, 255));

            RenderTools.Compare(a, b);
            var baseline = LiveTextureCount();

            for (var i = 0; i < 10; i++)
            {
                RenderTools.Compare(a, b);
            }

            Assert.That(LiveTextureCount(), Is.EqualTo(baseline),
                "each call loads two textures and must destroy both");
        }

        [Test]
        public void RepeatedFailuresDoNotAccumulateTextures()
        {
            // The first image loads, the second does not. Everything already loaded still has to
            // be released on the way out — this leaked one texture per attempt.
            var a = this.Png("a.png", 12, 6, new Color32(10, 20, 30, 255));
            var absent = Path.Combine(this.directory, "absent.png");

            Assert.Throws<McpToolException>(() => RenderTools.Compare(a, absent));
            var baseline = LiveTextureCount();

            for (var i = 0; i < 10; i++)
            {
                Assert.Throws<McpToolException>(() => RenderTools.Compare(a, absent));
            }

            Assert.That(LiveTextureCount(), Is.EqualTo(baseline));
        }

        [Test]
        public void RepeatedSizeMismatchesDoNotAccumulateTextures()
        {
            var a = this.Png("a.png", 8, 8, new Color32(0, 0, 0, 255));
            var b = this.Png("b.png", 4, 4, new Color32(0, 0, 0, 255));

            Assert.Throws<McpToolException>(() => RenderTools.Compare(a, b));
            var baseline = LiveTextureCount();

            for (var i = 0; i < 10; i++)
            {
                Assert.Throws<McpToolException>(() => RenderTools.Compare(a, b));
            }

            Assert.That(LiveTextureCount(), Is.EqualTo(baseline));
        }
    }
}
