using System.Collections.Generic;

using NUnit.Framework;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// A stack trace is mostly the machinery that reached the failure, and an Editor log is mostly
    /// the asset pipeline narrating itself. Both are re-sent to a model on every later turn, so
    /// what survives the cut decides whether the reader can act on the reply at all.
    /// </summary>
    public sealed class LogNoiseTests
    {
        private static readonly string[] NUnitTrace =
        {
            "ConsoleFilterTests: a warning that survives the Log toggle.",
            "UnityEngine.Debug:LogWarning (object)",
            "UnityMCP.Editor.Tests.ConsoleFilterTests:TheCountsAgree () " +
            "(at H:/PublicGithub/UnityMCP/jp.shiranui-isuzu.unity-mcp/Editor/Tests/ConsoleFilterTests.cs:182)",
            "System.Reflection.MethodBase:Invoke (object,object[])",
            "NUnit.Framework.Internal.Reflect:InvokeMethod (System.Reflection.MethodInfo,object,object[])",
            "NUnit.Framework.Internal.MethodWrapper:Invoke (object,object[])",
            "NUnit.Framework.Internal.Commands.TestMethodCommand:Execute (NUnit.Framework.Internal.ITestExecutionContext)",
            "UnityEditor.EditorApplication:Internal_CallUpdateFunctions ()",
        };

        [Test]
        public void TheFrameNamingSourceSurvivesAndTheMachineryIsCounted()
        {
            var kept = LogNoise.TrimStacks(NUnitTrace);

            Assert.That(kept[0], Is.EqualTo(NUnitTrace[0]), "the message is the point of the entry");
            Assert.That(kept[1], Does.Contain("ConsoleFilterTests:TheCountsAgree"));
            Assert.That(kept[1], Does.Contain(":182"), "a frame without its line cannot be opened");
            Assert.That(kept[kept.Count - 1], Does.Match(@"^\s*\(\+6 frames via "));
            Assert.That(kept, Has.Count.EqualTo(3));
        }

        /// <summary>
        /// The path is the same machine and package root on every frame of every entry, and three
        /// segments still name the file.
        /// </summary>
        [Test]
        public void ALocationIsCutBackToItsLastSegments()
        {
            var kept = LogNoise.TrimStacks(NUnitTrace);

            Assert.That(kept[1], Does.Contain("(at Editor/Tests/ConsoleFilterTests.cs:182)"));
            Assert.That(kept[1], Does.Not.Contain("H:/PublicGithub"));
        }

        /// <summary>
        /// Mono prints generated thunks between real frames. A line the walk does not recognise
        /// ends the run, and each half then keeps its own quota, so the trace comes back twice
        /// the size it should.
        /// </summary>
        [Test]
        public void AGeneratedThunkDoesNotSplitTheTrace()
        {
            var trace = new List<string>
            {
                "boom",
                "A.B:C () (at Assets/Scripts/A.cs:1)",
                "(wrapper dynamic-method) object:lambda_method (System.Runtime.CompilerServices.Closure)",
                "D.E:F () (at Assets/Scripts/D.cs:2)",
                "NUnit.Framework.Internal.MethodWrapper:Invoke (object,object[])",
            };

            var kept = LogNoise.TrimStacks(trace);

            Assert.That(kept, Has.Count.EqualTo(4), "one message, two located frames, one note");
            Assert.That(kept[3], Does.Contain("(+2 frames"), "the thunk names no source either");
        }

        /// <summary>
        /// An engine-side failure names no source. Dropping every frame would leave the reader
        /// with nothing at all, so the top of the stack stands in.
        /// </summary>
        [Test]
        public void ATraceNamingNoSourceKeepsItsTopFrames()
        {
            var trace = new List<string>
            {
                "crashed",
                "UnityEngine.Rendering.CommandBuffer:DrawMesh (UnityEngine.Mesh,UnityEngine.Matrix4x4)",
                "UnityEngine.Rendering.ScriptableRenderContext:Submit ()",
                "UnityEngine.GUIUtility:ProcessEvent (int,intptr,bool&)",
                "UnityEditor.DockArea:OnGUI ()",
            };

            var kept = LogNoise.TrimStacks(trace);

            Assert.That(kept[1], Does.Contain("CommandBuffer:DrawMesh"));
            Assert.That(kept, Has.Count.EqualTo(5), "three frames stand in, plus the message and the note");
        }

        [Test]
        public void AlikeLinesFoldToTheFirstOneWithACount()
        {
            var lines = new List<string>
            {
                "Asset Pipeline Refresh (id=aabbccddeeff0011): Total: 0.011 seconds",
                "Asset Pipeline Refresh (id=1122334455667788): Total: 0.014 seconds",
                "Asset Pipeline Refresh (id=99aabbccddeeff00): Total: 0.009 seconds",
                "something else",
            };

            var folded = LogNoise.FoldRepeats(lines, out var hidden);

            Assert.That(hidden, Is.EqualTo(2));
            Assert.That(folded, Has.Count.EqualTo(2));
            Assert.That(folded[0], Does.StartWith(lines[0]), "the first is kept whole, not turned into a pattern");
            Assert.That(folded[0], Does.Contain("[and 2 more like it]"));
        }

        /// <summary>Two failures differing only by a number are two failures, not one repeated.</summary>
        [Test]
        public void LinesReportingAProblemAreNeverFolded()
        {
            var lines = new List<string>
            {
                "error CS0103: The name 'a1' does not exist",
                "error CS0103: The name 'a2' does not exist",
                "error CS0103: The name 'a3' does not exist",
            };

            var folded = LogNoise.FoldRepeats(lines, out var hidden);

            Assert.That(hidden, Is.Zero);
            Assert.That(folded, Is.EqualTo(lines));
        }

        [Test]
        public void ALineOnItsOwnIsLeftAlone()
        {
            Assert.That(LogNoise.TrimStack("just a message"), Is.EqualTo("just a message"));
            Assert.That(LogNoise.TrimStack(null), Is.Null);
        }
    }
}
