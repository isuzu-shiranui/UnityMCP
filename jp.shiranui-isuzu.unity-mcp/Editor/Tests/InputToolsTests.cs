using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityEditor;

using UnityEngine;
using UnityEngine.TestTools;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// The input tools promise that what goes into a window is what a person would have sent,
    /// and that a recording sends the same thing back. These check the pieces that promise rests
    /// on without a window: the shape of a synthesized drag, how events are split across frames,
    /// the record and file formats, and the offset between content and view space.
    /// </summary>
    [TestFixture]
    internal sealed class InputToolsTests
    {
        private static readonly Vector2 From = new(100f, 200f);
        private static readonly Vector2 To = new(300f, 260f);

        [Test]
        public void BuildDragDeltasSumToTheDisplacementAndTheEndpointsMatch()
        {
            var records = EditorInput.BuildDrag(From, To, 1, EventModifiers.None, 30, 1);

            Assert.That(records[0].Type, Is.EqualTo(EventType.MouseDown));
            Assert.That(records[0].Position, Is.EqualTo(From));
            Assert.That(records[0].Button, Is.EqualTo(1));
            Assert.That(records[0].Clicks, Is.EqualTo(1));

            var last = records[records.Count - 1];
            Assert.That(last.Type, Is.EqualTo(EventType.MouseUp));
            Assert.That(last.Position, Is.EqualTo(To));

            var drags = records.Where(r => r.Type == EventType.MouseDrag).ToList();
            Assert.That(drags.Count, Is.EqualTo(31), "30 steps plus the drag that resolves the clutch shortcut.");

            var total = Vector2.zero;
            foreach (var d in drags)
            {
                total += d.Delta;
            }

            Assert.That(total.x, Is.EqualTo((To - From).x).Within(0.001f));
            Assert.That(total.y, Is.EqualTo((To - From).y).Within(0.001f));
            Assert.That(drags[0].Delta.magnitude, Is.EqualTo(EditorInput.ClutchDistance).Within(0.001f),
                "The first drag must clear the 6 px clutch threshold on its own.");
            Assert.That(drags[drags.Count - 1].Position, Is.EqualTo(To));

            // Each delta is the difference of consecutive positions, which is what a real drag
            // reports and what Scene View navigation integrates.
            var previous = From;
            foreach (var d in drags)
            {
                Assert.That((d.Position - previous - d.Delta).magnitude, Is.LessThan(0.001f));
                previous = d.Position;
            }
        }

        [Test]
        public void BuildDragWithOneStepHasNoPreparatoryDrag()
        {
            var records = EditorInput.BuildDrag(From, To, 0, EventModifiers.Alt, 1, 1);

            Assert.That(records.Select(r => r.Type), Is.EqualTo(new[] { EventType.MouseDown, EventType.MouseDrag, EventType.MouseUp }));
            Assert.That(records[1].Delta, Is.EqualTo(To - From));
            Assert.That(records.All(r => r.Modifiers == EventModifiers.Alt), Is.True);
        }

        [Test]
        public void BuildDragShorterThanTheClutchDistanceHasNoPreparatoryDrag()
        {
            var records = EditorInput.BuildDrag(From, From + new Vector2(4f, 0f), 1, EventModifiers.None, 4, 1);

            Assert.That(records.Count(r => r.Type == EventType.MouseDrag), Is.EqualTo(4));
        }

        [Test]
        public void ZeroFramesPerStepPutsTheWholeDragInOneGroup()
        {
            var records = EditorInput.BuildDrag(From, To, 1, EventModifiers.None, 5, 0);
            var groups = EditorInput.Plan(records);

            Assert.That(groups.Count, Is.EqualTo(1));
            Assert.That(groups[0].WaitTicks, Is.Zero);
            Assert.That(groups[0].Records.Count, Is.EqualTo(records.Count));
        }

        [Test]
        public void TwoFramesPerStepSeparatesEveryEventByTwoTicks()
        {
            var records = EditorInput.BuildDrag(From, To, 1, EventModifiers.None, 5, 2);
            var groups = EditorInput.Plan(records);

            // MouseDown, the clutch drag, five steps, MouseUp.
            Assert.That(groups.Count, Is.EqualTo(8));
            Assert.That(groups[0].WaitTicks, Is.Zero);
            Assert.That(groups.Skip(1).Select(g => g.WaitTicks), Is.All.EqualTo(2));
            Assert.That(groups.Select(g => g.Records.Count), Is.All.EqualTo(1));
        }

        [Test]
        public void PlanScalesGapsBySpeedAndNeverBelowOneTick()
        {
            var records = new List<InputEventRecord>
            {
                new() { Frame = 0, Type = EventType.MouseDown },
                new() { Frame = 0, Type = EventType.MouseDrag },
                new() { Frame = 10, Type = EventType.MouseDrag },
                new() { Frame = 20, Type = EventType.MouseUp },
            };

            var normal = EditorInput.Plan(records);
            Assert.That(normal.Select(g => g.WaitTicks), Is.EqualTo(new[] { 0, 10, 10 }));
            Assert.That(normal[0].Records.Count, Is.EqualTo(2), "Events on the same frame share a group.");

            var doubled = EditorInput.Plan(records, 2.0);
            Assert.That(doubled.Select(g => g.WaitTicks), Is.EqualTo(new[] { 0, 5, 5 }));

            var slowed = EditorInput.Plan(records, 0.5);
            Assert.That(slowed.Select(g => g.WaitTicks), Is.EqualTo(new[] { 0, 20, 20 }));

            var fast = EditorInput.Plan(records, 1000.0);
            Assert.That(fast.Select(g => g.WaitTicks), Is.EqualTo(new[] { 0, 1, 1 }),
                "Two groups on one tick would collapse back into the single-frame case.");

            var error = Assert.Throws<McpToolException>(() => EditorInput.Plan(records, 0));
            Assert.That(error.Code, Is.EqualTo("invalid_params"));
        }

        [Test]
        public void RecordJsonRoundTripKeepsEveryField()
        {
            var original = new InputEventRecord
            {
                Frame = 7,
                Time = 123.456,
                Type = EventType.MouseDown,
                Button = 1,
                Position = new Vector2(12.5f, 40f),
                Delta = new Vector2(-3f, 2f),
                Modifiers = EventModifiers.Alt | EventModifiers.Shift,
                Key = KeyCode.F,
                Character = 'f',
                Clicks = 2,
            };

            var json = original.ToJson();
            Assert.That(json["mods"].ToString(), Is.EqualTo("alt|shift"));
            Assert.That(json["pos"].ToObject<float[]>(), Is.EqualTo(new[] { 12.5f, 40f }));

            var copy = InputEventRecord.FromJson(json);
            Assert.That(copy.Frame, Is.EqualTo(7));
            Assert.That(copy.Time, Is.EqualTo(123.456).Within(0.001));
            Assert.That(copy.Type, Is.EqualTo(EventType.MouseDown));
            Assert.That(copy.Button, Is.EqualTo(1));
            Assert.That(copy.Position, Is.EqualTo(original.Position));
            Assert.That(copy.Delta, Is.EqualTo(original.Delta));
            Assert.That(copy.Modifiers, Is.EqualTo(original.Modifiers));
            Assert.That(copy.Key, Is.EqualTo(KeyCode.F));
            Assert.That(copy.Character, Is.EqualTo('f'));
            Assert.That(copy.Clicks, Is.EqualTo(2));
        }

        [Test]
        public void RecordJsonOmitsWhatAnEventDoesNotCarry()
        {
            var key = new InputEventRecord { Frame = 1, Type = EventType.KeyDown, Key = KeyCode.Return }.ToJson();

            Assert.That(key["pos"], Is.Null, "A key event has no position.");
            Assert.That(key["button"], Is.Null);
            Assert.That(key["mods"], Is.Null);
            Assert.That(key["char"], Is.Null);
            Assert.That(key["clicks"], Is.Null);
            Assert.That(key["key"].ToString(), Is.EqualTo("Return"));
        }

        [Test]
        public void ModifierParsingAcceptsCommonSpellingsAndRefusesTheRest()
        {
            Assert.That(InputEventRecord.ParseModifiers(new[] { "Alt", "control", "SHIFT", "meta" }),
                Is.EqualTo(EventModifiers.Alt | EventModifiers.Control | EventModifiers.Shift | EventModifiers.Command));
            Assert.That(InputEventRecord.ParseModifiers("ctrl|cmd"), Is.EqualTo(EventModifiers.Control | EventModifiers.Command));
            Assert.That(InputEventRecord.ParseModifiers((string)null), Is.EqualTo(EventModifiers.None));

            var error = Assert.Throws<McpToolException>(() => InputEventRecord.ParseModifiers(new[] { "hyper" }));
            Assert.That(error.Code, Is.EqualTo("invalid_params"));
        }

        [Test]
        public void RecordingFileRoundTripsAndDropsRepaintLayoutAndMoves()
        {
            var directory = Path.Combine(Path.GetTempPath(), "UnityMCP.InputToolsTests." + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "look.json");

            try
            {
                var captured = new List<InputEventRecord>
                {
                    new() { Frame = 0, Type = EventType.Layout },
                    new() { Frame = 0, Type = EventType.ExecuteCommand },
                    new() { Frame = 0, Type = EventType.ValidateCommand },
                    new() { Frame = 0, Type = EventType.Used },
                    new() { Frame = 0, Type = EventType.MouseMove, Position = new Vector2(1f, 1f) },
                    new() { Frame = 0, Type = EventType.MouseDown, Button = 1, Position = From, Clicks = 1 },
                    new() { Frame = 0, Type = EventType.Repaint },
                    new() { Frame = 1, Type = EventType.MouseDrag, Button = 1, Position = From + Vector2.right * 7f, Delta = Vector2.right * 7f },
                    new() { Frame = 2, Type = EventType.MouseUp, Button = 1, Position = From + Vector2.right * 7f },
                    new() { Frame = 3, Type = EventType.KeyDown, Key = KeyCode.F, Character = 'f' },
                };

                var withoutMoves = InputRecordingFile.Filter(captured, includeMoves: false);
                Assert.That(withoutMoves.Select(r => r.Type),
                    Is.EqualTo(new[] { EventType.MouseDown, EventType.MouseDrag, EventType.MouseUp, EventType.KeyDown }));

                var withMoves = InputRecordingFile.Filter(captured, includeMoves: true);
                Assert.That(withMoves.Count, Is.EqualTo(5));
                Assert.That(withMoves.Any(r => !r.IsInput), Is.False, "Layout, Repaint and command events are not input.");

                var recording = new InputRecording
                {
                    Unity = "6000.0.0f1",
                    View = "scene_view_window",
                    WindowType = "UnityEditor.SceneView",
                    WindowSize = new Vector2(800f, 600f),
                    ContentOffset = new Vector2(0f, 21f),
                    CreatedUtc = "2026-09-04T00:00:00.0000000Z",
                };
                recording.Events.AddRange(withoutMoves);

                var written = InputRecordingFile.Write(path, recording);
                Assert.That(File.Exists(written), Is.True);

                var read = InputRecordingFile.Read(written);
                Assert.That(read.Version, Is.EqualTo(InputRecording.CurrentVersion));
                Assert.That(read.Unity, Is.EqualTo("6000.0.0f1"));
                Assert.That(read.View, Is.EqualTo("scene_view_window"));
                Assert.That(read.WindowType, Is.EqualTo("UnityEditor.SceneView"));
                Assert.That(read.WindowSize, Is.EqualTo(new Vector2(800f, 600f)));
                Assert.That(read.ContentOffset, Is.EqualTo(new Vector2(0f, 21f)));
                Assert.That(read.CreatedUtc, Is.EqualTo("2026-09-04T00:00:00.0000000Z"));
                Assert.That(read.Events.Count, Is.EqualTo(4));
                Assert.That(read.Events[1].Delta, Is.EqualTo(Vector2.right * 7f));
                Assert.That(read.Events[3].Character, Is.EqualTo('f'));
                Assert.That(read.FrameCount, Is.EqualTo(4));
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }

        [Test]
        public void UnknownRecordingVersionIsRejectedBeforeAnyEventIsRead()
        {
            var future = new JObject
            {
                ["version"] = 2,
                ["view"] = "scene_view_window",
                ["events"] = new JArray(new JObject { ["type"] = "NotAnEventType" }),
            };

            var error = Assert.Throws<McpToolException>(() => InputRecording.FromJson(future));
            Assert.That(error.Code, Is.EqualTo("invalid_recording"));
            Assert.That(error.Message, Does.Contain("version 2"));

            var missing = Assert.Throws<McpToolException>(() => InputRecording.FromJson(new JObject { ["events"] = new JArray() }));
            Assert.That(missing.Code, Is.EqualTo("invalid_recording"));
        }

        [Test]
        public void MissingRecordingIsReportedAsNotFound()
        {
            var error = Assert.Throws<McpToolException>(
                () => InputRecordingFile.Read(Path.Combine(Path.GetTempPath(), "UnityMCP.no-such-recording.json")));

            Assert.That(error.Code, Is.EqualTo("recording_not_found"));
            Assert.That(error.HttpStatus, Is.EqualTo(404));
        }

        [Test]
        public void RecordingNamesAreReducedToSafeCharacters()
        {
            Assert.That(InputRecordingFile.NormalizeName("look"), Is.EqualTo("look"));
            Assert.That(InputRecordingFile.NormalizeName(" right drag/../x "), Is.EqualTo("right_drag_x"));
            Assert.That(InputRecordingFile.NormalizeName("a-b_C9"), Is.EqualTo("a-b_C9"));

            var path = InputRecordingFile.PathFor("C:/Some/Project/Assets", "../../evil");
            Assert.That(Path.GetFileName(path), Is.EqualTo("evil.json"));
            Assert.That(path, Does.Contain(Path.Combine("recordings", McpInstanceDescriptor.HashProjectPath("C:/Some/Project/Assets"))));

            var error = Assert.Throws<McpToolException>(() => InputRecordingFile.NormalizeName("///"));
            Assert.That(error.Code, Is.EqualTo("invalid_params"));
        }

        [Test]
        public void WindowsDeviceNamesCannotNameARecording()
        {
            foreach (var reserved in new[] { "CON", "con", "PRN", "AUX", "NUL", "COM1", "com9", "LPT1", "lpt9" })
            {
                var error = Assert.Throws<McpToolException>(() => InputRecordingFile.NormalizeName(reserved), reserved);
                Assert.That(error.Code, Is.EqualTo("invalid_params"), reserved);
                Assert.That(error.Message, Does.Contain("reserved"), reserved);
            }

            Assert.That(InputRecordingFile.NormalizeName("COM0"), Is.EqualTo("COM0"), "Only COM1-9 are devices.");
            Assert.That(InputRecordingFile.NormalizeName("console"), Is.EqualTo("console"));
            Assert.That(InputRecordingFile.NormalizeName("con_look"), Is.EqualTo("con_look"));
        }

        [Test]
        public void MalformedEventNumbersAreInvalidRecording()
        {
            var cases = new (string Field, JToken Value)[]
            {
                ("f", "abc"),
                ("f", 99999999999L),
                ("f", 1.5),
                ("t", "later"),
                ("pos", new JArray("x", 1)),
                ("pos", 3),
                ("button", true),
            };

            foreach (var (field, value) in cases)
            {
                var json = new JObject { ["type"] = "MouseDown", [field] = value };

                var error = Assert.Throws<McpToolException>(() => InputEventRecord.FromJson(json), $"{field}={value}");
                Assert.That(error.Code, Is.EqualTo("invalid_recording"), $"{field}={value}");
                Assert.That(error.Message, Does.Contain($"'{field}'"), $"{field}={value}");
            }

            var record = InputEventRecord.FromJson(new JObject { ["type"] = "MouseDown", ["f"] = 3, ["t"] = 12, ["pos"] = new JArray(1, 2.5) });
            Assert.That(record.Frame, Is.EqualTo(3));
            Assert.That(record.Time, Is.EqualTo(12d), "An integer is a valid time.");
            Assert.That(record.Position, Is.EqualTo(new Vector2(1f, 2.5f)));
        }

        [Test]
        public void EmptyWindowNeedleIsRefused()
        {
            foreach (var view in new[] { "window:", "window: " })
            {
                var error = Assert.Throws<McpToolException>(() => EditorWindowLocator.Resolve(view), view);
                Assert.That(error.Code, Is.EqualTo("invalid_params"), view);
            }
        }

        [Test]
        public void MultiCharacterTextIsRefusedBeforeAnyWindowIsTouched()
        {
            var error = Assert.Throws<McpToolException>(() => InputTools.Key("scene", "A", character: "ab"));

            Assert.That(error.Code, Is.EqualTo("invalid_params"));
            Assert.That(error.Message, Does.Contain("character"));
        }

        [Test]
        public void InputToolsLandInTheInputGroupWithParsableExamples()
        {
            var catalog = ToolCatalog.BuildFromTypes(new[] { typeof(InputTools) });

            Assert.That(catalog.Errors, Is.Empty);

            foreach (var name in new[] { "input_pointer", "input_key", "input_record", "input_replay" })
            {
                Assert.That(catalog.TryGet(name, out var descriptor), Is.True, name);
                Assert.That(descriptor.ToCatalogEntry()["group"].ToString(), Is.EqualTo(McpToolGroups.Input), name);
                Assert.That(descriptor.ToCatalogEntry()["destructive"].Value<bool>(), Is.False, name);
            }

            Assert.That(McpToolGroups.Derive("input_anything"), Is.EqualTo(McpToolGroups.Input));
        }

        [Test]
        public void KeyCharactersFollowTheKeyAndShift()
        {
            Assert.That(InputTools.CharacterFor(KeyCode.A, EventModifiers.None), Is.EqualTo('a'));
            Assert.That(InputTools.CharacterFor(KeyCode.A, EventModifiers.Shift), Is.EqualTo('A'));
            Assert.That(InputTools.CharacterFor(KeyCode.Alpha7, EventModifiers.None), Is.EqualTo('7'));
            Assert.That(InputTools.CharacterFor(KeyCode.Return, EventModifiers.None), Is.EqualTo('\n'));
            Assert.That(InputTools.CharacterFor(KeyCode.F1, EventModifiers.None), Is.EqualTo('\0'));
        }
    }

    /// <summary>
    /// The parts that need a live <see cref="Event"/> or a window, so they run only inside the
    /// Editor: the content offset applied on the way out and removed on the way in, and the
    /// promise the whole feature makes, that a recorded Scene View drag replays to the same yaw.
    /// </summary>
    [TestFixture]
    internal sealed class InputToolsEditorTests
    {
        private const int FrameBudget = 900;

        [TearDown]
        public void TearDown()
        {
            if (InputRecorder.IsRecording)
            {
                InputRecorder.Stop();
            }

            FrameSequencer.CancelAll("Test teardown.");
        }

        [Test]
        public void ContentOffsetIsAppliedOnTheWayOutAndRemovedOnTheWayIn()
        {
            var offset = new Vector2(0f, 21f);
            var record = new InputEventRecord
            {
                Type = EventType.MouseDrag,
                Button = 1,
                Position = new Vector2(50f, 60f),
                Delta = new Vector2(7f, 0f),
                Modifiers = EventModifiers.Alt,
                Clicks = 0,
            };

            var e = EditorInput.ToEvent(record, offset);
            Assert.That(e.type, Is.EqualTo(EventType.MouseDrag));
            Assert.That(e.mousePosition, Is.EqualTo(new Vector2(50f, 81f)), "View space is content space plus the tab bar.");
            Assert.That(e.delta, Is.EqualTo(record.Delta), "A delta is a difference and has no origin to shift.");
            Assert.That(e.button, Is.EqualTo(1));
            Assert.That(e.modifiers, Is.EqualTo(EventModifiers.Alt));

            var back = EditorInput.FromEvent(e, offset, 3, 12.5);
            Assert.That(back.Position, Is.EqualTo(record.Position));
            Assert.That(back.Delta, Is.EqualTo(record.Delta));
            Assert.That(back.Type, Is.EqualTo(record.Type));
            Assert.That(back.Button, Is.EqualTo(1));
            Assert.That(back.Modifiers, Is.EqualTo(EventModifiers.Alt));
            Assert.That(back.Frame, Is.EqualTo(3));
            Assert.That(back.Time, Is.EqualTo(12.5));

            var key = EditorInput.ToEvent(new InputEventRecord { Type = EventType.KeyDown, Key = KeyCode.F, Character = 'f' }, offset);
            Assert.That(key.keyCode, Is.EqualTo(KeyCode.F));
            Assert.That(key.character, Is.EqualTo('f'));
        }

        [UnityTest]
        public IEnumerator SceneView_RightDrag_Record_Then_Replay_ReproducesYaw()
        {
            // Assume.That would mark the test inconclusive, which the batch-mode runner reports as
            // a non-zero exit code; Ignore counts as skipped.
            if (Application.isBatchMode)
            {
                Assert.Ignore("Scene View navigation needs a GUI Editor.");
            }

            var view = EditorWindow.GetWindow<SceneView>();
            view.Show();
            view.Focus();
            view.in2DMode = false;
            view.orthographic = false;
            view.rotation = Quaternion.Euler(20f, 30f, 0f);
            view.pivot = Vector3.zero;
            view.size = 10f;
            view.Repaint();
            yield return null;
            yield return null;

            if (view.position.width <= 320f)
            {
                Assert.Ignore("The Scene View is too narrow for a 200 px drag.");
            }

            var start = view.rotation;
            var name = "test_rightdrag_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string path = null;

            try
            {
                var started = InputTools.Record("start", "scene", name);
                Assert.That(started["recording"].Value<bool>(), Is.True);

                var y = Mathf.Round(view.position.height * 0.5f);
                var drag = InputTools.Pointer(
                    "scene", "drag", 1,
                    new double[] { 100, y }, new double[] { 300, y },
                    normalized: false, steps: 30, framesPerStep: 1, restoreFocus: false);

                yield return Settle(drag);
                var dragResult = Result(drag);
                Assert.That(dragResult["sent"].Value<int>(), Is.EqualTo(33), "MouseDown, clutch drag, 30 steps, MouseUp.");

                var stopped = InputTools.Record("stop");
                path = stopped["path"].ToString();
                Assert.That(stopped["events"].Value<int>(), Is.EqualTo(33),
                    "The recorder must see every event the synthesizer sent, once each.");

                var recorded = InputRecordingFile.Read(path);
                Assert.That(recorded.Events.Select(r => r.Type).Distinct(),
                    Is.EquivalentTo(new[] { EventType.MouseDown, EventType.MouseDrag, EventType.MouseUp }));
                Assert.That(recorded.Events.Select(r => r.Button), Is.All.EqualTo(1), "Every event of a right-drag carries button 1.");
                Assert.That(recorded.Events[0].Position.x, Is.EqualTo(100f).Within(1f));
                Assert.That(recorded.Events[0].Position.y, Is.EqualTo(y).Within(1f),
                    "Recorded positions must be in the content space input_pointer takes, or a replay lands elsewhere.");

                var recordedYaw = view.rotation.eulerAngles.y;
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(recordedYaw, start.eulerAngles.y)), Is.GreaterThan(1f),
                    "A 200 px right-drag must turn the camera; if it did not, FPS Look never engaged.");
                Assert.That(GUIUtility.hotControl, Is.Zero, "MouseUp must release the navigation hot control.");

                view.rotation = start;
                view.Repaint();
                yield return null;
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(view.rotation.eulerAngles.y, start.eulerAngles.y)), Is.LessThan(0.01f));

                var replay = InputTools.Replay(name, restoreFocus: false);
                yield return Settle(replay);
                var replayResult = Result(replay);
                Assert.That(replayResult["sent"].Value<int>(), Is.GreaterThanOrEqualTo(33));

                var replayedYaw = view.rotation.eulerAngles.y;
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(replayedYaw, recordedYaw)), Is.LessThan(0.5f),
                    $"Recorded yaw {recordedYaw:0.###}, replayed yaw {replayedYaw:0.###}.");
                Assert.That(GUIUtility.hotControl, Is.Zero);
            }
            finally
            {
                if (path != null && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        private static IEnumerator Settle(JObject result)
        {
            if (result is not DeferredToolResult deferred)
            {
                yield break;
            }

            for (var frame = 0; frame < FrameBudget && !deferred.Item.IsCompleted; frame++)
            {
                yield return null;
            }

            Assert.That(deferred.Item.IsCompleted, Is.True, "The input sequence never finished.");
        }

        private static JObject Result(JObject result)
        {
            if (result is not DeferredToolResult deferred)
            {
                return result;
            }

            if (deferred.Item.Error != null)
            {
                throw deferred.Item.Error;
            }

            return deferred.Item.Result;
        }
    }
}
