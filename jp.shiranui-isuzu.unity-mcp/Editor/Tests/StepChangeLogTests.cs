using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityMCP.Editor.Core;
using UnityMCP.Editor.Handlers;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    internal sealed class StepChangeLogTests
    {
        [Test]
        public void TruncationKeepsFirst199AndLatestAndDoesNotMutateInputs()
        {
            var initial = new JObject { ["x"] = 0 };
            var log = StepChangeLog.Append(null, initial, 0, 0);
            initial["x"] = -1;
            for (var i = 1; i <= 205; i++) log = StepChangeLog.Append(log, new JObject { ["x"] = i }, i, i * .1);
            Assert.That((int)log["start"]["x"], Is.Zero);
            Assert.That(((JArray)log["changes"]).Count, Is.EqualTo(200));
            Assert.That((int)log["changes"][198][0], Is.EqualTo(199));
            Assert.That((int)log["changes"][199][0], Is.EqualTo(205));
            Assert.That((bool)log["truncated"], Is.True);
        }
        [Test]
        public void EqualValuesAndStickyErrorsDoNotFabricateChanges()
        {
            var log = StepChangeLog.Append(null, new JValue(3), 0, 0);
            log = StepChangeLog.Append(log, new JValue(3), 1, .1);
            Assert.That((JArray)log["changes"], Is.Empty);
            log = StepChangeLog.Append(log, null, 2, .2);
            Assert.That(log["end"].Type, Is.EqualTo(JTokenType.Null));
            log = StepChangeLog.Append(log, null, 3, .3, "first failure");
            log = StepChangeLog.Append(log, new JValue(5), 4, .4, "later failure");
            Assert.That((string)log["error"], Is.EqualTo("first failure"));
            Assert.That(log.Count, Is.EqualTo(1));
        }
        [Test]
        public void SecondsCannotSilentlyOverrideExplicitCount()
        {
            Assert.That(Assert.Throws<McpToolException>(() => PlayModeTools.Step(count: 1, seconds: 1)).Code, Is.EqualTo("invalid_params"));
            Assert.Throws<McpToolException>(() => PlayModeTools.Step(changes: true));
        }
    }
}
