using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// What a frame cost, as one call. Reaching these numbers meant walking UnityEditor.UnityStats
    /// by reflection, which is how a claim that a scene got cheaper ends up resting on the shape of
    /// the fix instead of on a measurement.
    /// </summary>
    public sealed class RenderStatsTests
    {
        [Test]
        public void EveryFigureAFrameIsJudgedByIsReported()
        {
            var stats = RenderTools.RenderStats();

            foreach (var key in new[]
                     { "drawCalls", "setPassCalls", "triangles", "vertices", "shadowCasters" })
            {
                Assert.That(stats[key], Is.Not.Null, $"'{key}' is missing");
                Assert.That(stats[key].Value<int>(), Is.GreaterThanOrEqualTo(0));
            }

            // srpBatcherDrawCalls is not on every Unity version, so the group is checked by the
            // counter every version has.
            Assert.That(stats["batching"]["dynamicBatches"], Is.Not.Null);
            Assert.That(stats["batching"]["staticBatches"], Is.Not.Null);
            Assert.That(stats["renderTextures"]["count"], Is.Not.Null);
            Assert.That(stats["screen"].ToString(), Is.Not.Empty);

            // Whether the figures can be current at all. Without it a caller comparing before and
            // after has no way to know they compared one stale reading with itself. Open was not
            // enough on its own: a view behind another tab is open and not being drawn.
            Assert.That(stats["gameView"]["open"], Is.Not.Null);
        }

        /// <summary>
        /// Unity marks frameTime and renderTime obsolete, and in the Editor they read as nonsense:
        /// one run reported renderTime as 52491.31. Reporting them would invite a conclusion drawn
        /// from a number that means nothing.
        /// </summary>
        [Test]
        public void TheTimingsUnityCallsObsoleteAreLeftOut()
        {
            var stats = RenderTools.RenderStats();

            Assert.That(stats["frameTime"], Is.Null);
            Assert.That(stats["renderTime"], Is.Null);
        }
    }
}
