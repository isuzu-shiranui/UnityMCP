using System.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Handlers;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    internal sealed class FrameProfilerTests
    {
        [Test]
        public void TheCountersThisToolNamesStillExistInThisEditor()
        {
            // Every counter is looked up by a string. A rename in a later Unity would otherwise
            // show up as a reply that quietly stopped carrying a figure.
            using var profiler = new FrameProfiler(4, null);

            Assert.That(profiler.Unavailable(), Is.Empty);
        }

        [Test]
        public void AWindowWithNoFramesInItYetCarriesNoFigures()
        {
            // A recorder collects from the frame it starts, so a read taken before any frame has
            // passed has to come back empty rather than with zeros.
            using var profiler = new FrameProfiler(8, null);

            Assert.That(((JObject)profiler.Read()).Properties(), Is.Empty);
        }

        [Test]
        public void FramesOutsideTheAllowedWindowAreRefusedWithTheRange()
        {
            foreach (var frames in new[] { 0, -1, 301 })
            {
                var thrown = Assert.Throws<McpToolException>(() => RenderTools.ProfileFrame(frames));
                Assert.That(thrown.Code, Is.EqualTo("invalid_params"));
                Assert.That(thrown.Message, Does.Contain("1 to 300"));
            }
        }

        [Test]
        public void MoreMarkersThanOneCallWillCarryAreRefused()
        {
            var markers = Enumerable.Range(0, 21).Select(i => "Marker" + i).ToArray();

            var thrown = Assert.Throws<McpToolException>(() => RenderTools.ProfileFrame(1, markers));
            Assert.That(thrown.Code, Is.EqualTo("invalid_params"));
            Assert.That(thrown.Message, Does.Contain("20"));
        }

        [Test]
        public void AMarkerNameThatMatchesNothingIsReportedRatherThanDroppedSilently()
        {
            using var profiler = new FrameProfiler(4, new[] { "NoSuchMarker.ThatAnyoneWouldDeclare" });

            // A caller who mistypes a marker name would otherwise read a reply that simply does not
            // mention it and conclude the marker never ran.
            Assert.That(profiler.Unavailable(), Does.Contain("NoSuchMarker.ThatAnyoneWouldDeclare"));
        }
    }
}
