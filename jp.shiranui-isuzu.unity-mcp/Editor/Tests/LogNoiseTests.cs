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
            "(at C:/Projects/UnityMCP/jp.shiranui-isuzu.unity-mcp/Editor/Tests/ConsoleFilterTests.cs:182)",
            "System.Reflection.MethodBase:Invoke (object,object[])",
            "NUnit.Framework.Internal.Reflect:InvokeMethod (System.Reflection.MethodInfo,object,object[])",
            "NUnit.Framework.Internal.MethodWrapper:Invoke (object,object[])",
            "NUnit.Framework.Internal.Commands.TestMethodCommand:Execute (NUnit.Framework.Internal.ITestExecutionContext)",
            "UnityEditor.EditorApplication:Internal_CallUpdateFunctions ()",
        };

        /// <summary>
        /// A line a snippet logged is the whole entry: the way it reached the console is not.
        /// </summary>
        /// <remarks>
        /// Every frame of this server's own call path names source, so the four kept frames were
        /// always these and never the caller's own. Sixteen characters of message arrived with
        /// three hundred and seventy of delivery behind them, on every entry of every read.
        /// </remarks>
        [Test]
        public void ThePathAToolCallArrivesThroughIsNotKeptAsTheTrace()
        {
            var logged = new[]
            {
                "仕込み完了",
                "UnityMCP.Editor.Handlers.CodeExecutor:Run (System.Reflection.MethodInfo,object[]) "
                + "(at Editor/Handlers/CodeExecutor.cs:326)",
                "UnityMCP.Editor.Handlers.CodeExecutor:Execute (Newtonsoft.Json.Linq.JObject) "
                + "(at Editor/Handlers/CodeExecutor.cs:118)",
                "UnityMCP.Editor.Tools.EditorTools:ExecuteCode (string,string) "
                + "(at Editor/Tools/EditorTools.cs:92)",
                "UnityMCP.Editor.Core.ToolInvoker:Invoke (UnityMCP.Editor.Core.McpToolDescriptor,"
                + "Newtonsoft.Json.Linq.JObject) (at Editor/Core/ToolInvoker.cs:144)",

                // A compiler-generated closure sits between two of these, so the type a frame
                // names is not always the type the pattern is looking for.
                "UnityMCP.Editor.Core.ToolCallRunner/<>c__DisplayClass4_0:<Run>b__0 () "
                + "(at Editor/Core/ToolCallRunner.cs:70)",
                "UnityMCP.Editor.Core.McpMainThreadDispatcher/WorkItem:Run () "
                + "(at Editor/Core/McpMainThreadDispatcher.cs:300)",
                "UnityEditor.EditorApplication:Internal_CallUpdateFunctions ()",
            };

            var kept = LogNoise.TrimStacks(logged);

            Assert.That(kept[0], Is.EqualTo("仕込み完了"), "the message is the whole entry here");
            Assert.That(kept, Has.Count.EqualTo(2), "the message and a count of what was dropped");
            Assert.That(kept[1], Does.Match(@"^\s*\(\+7 frames via "));
        }

        /// <summary>
        /// A frame inside a nested type can arrive without the type that contains it, and the file
        /// it points at is then the only thing saying it is delivery.
        /// </summary>
        [Test]
        public void ADeliveryFrameThatLostItsContainingTypeIsStillCut()
        {
            var thrown = new[]
            {
                "InvalidOperationException: bench failure 19",
                "McpCodeExecution.Runner.Execute () (at <e9da18a3a4784cb7a1e2a1ca59b592fc>:0)",
                "UnityMCP.Editor.Core.<>c__DisplayClass4_0:<Run>b__0() "
                + "(at ./Packages/jp.shiranui-isuzu.unity-mcp/Editor/Core/ToolCallRunner.cs:70)",
                "UnityMCP.Editor.Core.WorkItem:Run() "
                + "(at ./Packages/jp.shiranui-isuzu.unity-mcp/Editor/Core/McpMainThreadDispatcher.cs:310)",
                "UnityEditor.EditorApplication:Internal_CallUpdateFunctions ()",
            };

            var kept = LogNoise.TrimStacks(thrown);

            Assert.That(kept, Has.None.Contains("ToolCallRunner.cs"));
            Assert.That(kept, Has.None.Contains("McpMainThreadDispatcher.cs"));
            Assert.That(kept, Has.Some.Contains("Runner.Execute"), "the snippet's own frame is what the entry is about");
        }

        /// <summary>
        /// Matching delivery by file must not take a frame of the caller's that happens to live in a
        /// folder with the same name.
        /// </summary>
        [Test]
        public void AFrameOutsideThisPackageInAFileOfTheSameNameIsKept()
        {
            var thrown = new[]
            {
                "NullReferenceException",
                "Game.Core.<>c__DisplayClass2_0:<Run>b__0() (at Assets/Game/Editor/Core/ToolInvoker.cs:12)",
                "UnityEditor.EditorApplication:Internal_CallUpdateFunctions ()",
            };

            var kept = LogNoise.TrimStacks(thrown);

            Assert.That(kept, Has.Some.Contains("Game.Core.<>c__DisplayClass2_0:<Run>b__0()"));
        }

        /// <summary>
        /// A failure inside a tool still names the tool, which the delivery path around it does not.
        /// </summary>
        [Test]
        public void AFrameInsideAToolSurvivesTheDeliveryCut()
        {
            var thrown = new[]
            {
                "NullReferenceException while reading the timeline",
                "UnityMCP.Editor.Timeline.TimelineTools:Describe (string) "
                + "(at Editor/Timeline/TimelineTools.cs:88)",
                "UnityMCP.Editor.Core.ToolInvoker:Invoke (UnityMCP.Editor.Core.McpToolDescriptor,"
                + "Newtonsoft.Json.Linq.JObject) (at Editor/Core/ToolInvoker.cs:144)",
            };

            var kept = LogNoise.TrimStacks(thrown);

            Assert.That(kept[1], Does.Contain("TimelineTools:Describe"));
            Assert.That(kept[1], Does.Contain(":88"));
        }

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
            Assert.That(kept[1], Does.Not.Contain("C:/Projects"));
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

        /// <summary>
        /// The message without its stack keeps every line of the message.
        /// </summary>
        /// <remarks>
        /// An exception's own text can run to several lines, and cutting at the first newline
        /// would leave the caller the first sentence of a message that explains itself in three.
        /// </remarks>
        [Test]
        public void TheMessageSurvivesWithoutItsStack()
        {
            var message = string.Join("\n", new[]
            {
                "InvalidOperationException: the first line",
                "  and a second line of the message",
                "UnityMCP.Editor.Tests.Thing:Fail () (at Editor/Tests/Thing.cs:12)",
                "UnityEngine.Debug:LogException (System.Exception)",
            });

            var without = LogNoise.WithoutStack(message);

            Assert.That(without, Does.Contain("the first line"));
            Assert.That(without, Does.Contain("a second line"));
            Assert.That(without, Does.Not.Contain("Thing.cs"));
            Assert.That(without, Does.Not.Contain("Debug:LogException"));
        }

        /// <summary>A trace with nothing the patterns recognise still leaves a message behind.</summary>
        [Test]
        public void AnUnrecognisedTraceLeavesTheMessage()
        {
            Assert.That(LogNoise.WithoutStack("only this\nand this"), Is.EqualTo("only this\nand this"));
            Assert.That(LogNoise.WithoutStack("one line"), Is.EqualTo("one line"));
            Assert.That(LogNoise.WithoutStack(null), Is.Null);
        }

        /// <summary>
        /// A path is cut to its last segments, because the console repeats it on every entry.
        /// </summary>
        [Test]
        public void APathIsCutToWhatNamesTheFile()
        {
            Assert.That(
                LogNoise.ShortenPath(
                    "C:/Projects/UnityMCP/jp.shiranui-isuzu.unity-mcp/Editor/Handlers/CodeExecutor.cs"),
                Is.EqualTo("Editor/Handlers/CodeExecutor.cs"));
            Assert.That(LogNoise.ShortenPath("Assets/Scripts/Thing.cs"), Is.EqualTo("Assets/Scripts/Thing.cs"));
            Assert.That(LogNoise.ShortenPath("Thing.cs"), Is.EqualTo("Thing.cs"));
            Assert.That(LogNoise.ShortenPath(""), Is.EqualTo(""));
        }

        [Test]
        public void ALineOnItsOwnIsLeftAlone()
        {
            Assert.That(LogNoise.TrimStack("just a message"), Is.EqualTo("just a message"));
            Assert.That(LogNoise.TrimStack(null), Is.Null);
        }
    }
}
