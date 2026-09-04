using System;
using System.Collections.Generic;
using System.Diagnostics;

using Newtonsoft.Json.Linq;

using UnityEditor;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;
using UnityMCP.Editor.Handlers;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Driving an Editor window through its input path, the way a person does.
    /// </summary>
    internal static class InputTools
    {
        private const string ViewDescription =
            "Window to send to: scene (or scene_view_window), game (or game_view_window), inspector, " +
            "hierarchy, project, console, or window:<title substring>.";

        [McpTool(
            "input_pointer",
            "Send mouse input to an Editor window through the same path a real mouse uses: move, " +
            "press, release, click, drag or scroll. Use this to reproduce behaviour that only shows " +
            "up during interaction — a Scene View right-drag is FPS Look, Alt+left-drag is Orbit — " +
            "and that writing the camera's rotation directly never triggers. Coordinates are points " +
            "measured from the top-left of the window's content area, below the tab bar. Fractions " +
            "given with normalized: true, and the bounds a point is checked against, are of the " +
            "whole window rather than of the content, so normalized 0.5 sits slightly past the " +
            "content centre; give points directly when the position has to be exact. " +
            "A drag is split into steps, one Editor frame apart by default, because time-based effects " +
            "react to the frames between events; a drag sent in one frame does not reproduce them. " +
            "Multi-frame drags return a job id when they outlast the sync window.",
            Examples = new[]
            {
                @"{""view"":""scene"",""action"":""drag"",""button"":1,""from"":[200,200],""to"":[400,200],""steps"":30,""frames_per_step"":1}",
                @"{""view"":""scene"",""action"":""drag"",""button"":0,""modifiers"":[""alt""],""from"":[0.5,0.5],""to"":[0.7,0.5],""normalized"":true}",
                @"{""view"":""hierarchy"",""action"":""click"",""from"":[40,60]}",
                @"{""view"":""scene"",""action"":""scroll"",""from"":[300,300],""scroll_delta"":[0,3]}",
            })]
        public static JObject Pointer(
            [McpArg("view", ViewDescription)]
            string view,
            [McpArg("action", "One of move, down, up, click, drag, scroll.")]
            string action = "move",
            [McpArg("button", "Mouse button: 0 left, 1 right, 2 middle.")]
            int button = 0,
            [McpArg("from", "[x, y] where the gesture starts. Required.")]
            double[] from = null,
            [McpArg("to", "[x, y] where a drag ends. Ignored by other actions.")]
            double[] to = null,
            [McpArg("normalized", "Treat from/to as fractions 0..1 of the window's full size, tab " +
                                  "bar included, then use the result as a content-area point.")]
            bool normalized = false,
            [McpArg("steps", "How many MouseDrag events a drag is split into.")]
            int steps = 30,
            [McpArg("frames_per_step", "Editor frames between drag steps. 0 sends the whole drag in one frame.")]
            int framesPerStep = 1,
            [McpArg("modifiers", "Modifier keys held: any of alt, ctrl, shift, cmd.")]
            string[] modifiers = null,
            [McpArg("scroll_delta", "[x, y] wheel delta for scroll. Positive y scrolls down / zooms out in the Scene View.")]
            double[] scrollDelta = null,
            [McpArg("click_count", "Click count for click: 2 for a double-click.")]
            int clickCount = 1,
            [McpArg("restore_focus", "Give focus back to the window that had it before the input was sent.")]
            bool restoreFocus = true)
        {
            var clock = Stopwatch.StartNew();
            var target = ResolveTarget(view);
            var mods = InputEventRecord.ParseModifiers(modifiers);
            var start = ReadPoint(from, "from", true, normalized, target.WindowSize);

            List<InputEventRecord> records;
            switch ((action ?? "move").Trim().ToLowerInvariant())
            {
                case "move":
                    records = new List<InputEventRecord> { Pointer(EventType.MouseMove, button, start, mods) };
                    break;
                case "down":
                    var down = Pointer(EventType.MouseDown, button, start, mods);
                    down.Clicks = Math.Max(1, clickCount);
                    records = new List<InputEventRecord> { down };
                    break;
                case "up":
                    records = new List<InputEventRecord> { Pointer(EventType.MouseUp, button, start, mods) };
                    break;
                case "click":
                    records = new List<InputEventRecord>();
                    for (var i = 1; i <= Math.Max(1, clickCount); i++)
                    {
                        var press = Pointer(EventType.MouseDown, button, start, mods);
                        press.Clicks = i;
                        records.Add(press);
                        records.Add(Pointer(EventType.MouseUp, button, start, mods));
                    }

                    break;
                case "drag":
                    var end = ReadPoint(to, "to", true, normalized, target.WindowSize);
                    records = EditorInput.BuildDrag(start, end, button, mods, steps, framesPerStep);
                    break;
                case "scroll":
                    var wheel = Pointer(EventType.ScrollWheel, 0, start, mods);
                    wheel.Delta = ReadPoint(scrollDelta, "scroll_delta", false, false, Vector2.zero);
                    if (wheel.Delta == Vector2.zero)
                    {
                        throw new McpToolException("invalid_params", "scroll needs a non-zero scroll_delta.");
                    }

                    records = new List<InputEventRecord> { wheel };
                    break;
                default:
                    throw new McpToolException(
                        "invalid_params",
                        $"Unknown action '{action}'. Use move, down, up, click, drag or scroll.");
            }

            var groups = EditorInput.Plan(records);
            return Send(target, groups, 1, restoreFocus, true, null, clock, "input_pointer");
        }

        [McpTool(
            "input_key",
            "Send a key to an Editor window through its input path. press sends KeyDown, a KeyDown " +
            "carrying the character, then KeyUp, which is what typing into a focused field produces; " +
            "down and up send one half for holding a key across other input. Key names are " +
            "UnityEngine.KeyCode members: A, Alpha1, F, Delete, Return, Space, LeftArrow.",
            Examples = new[]
            {
                @"{""view"":""scene"",""key"":""F""}",
                @"{""view"":""inspector"",""key"":""A"",""character"":""a""}",
                @"{""view"":""hierarchy"",""key"":""D"",""modifiers"":[""ctrl""]}",
            })]
        public static JObject Key(
            [McpArg("view", ViewDescription)]
            string view,
            [McpArg("key", "KeyCode name, e.g. F, Alpha1, Return, Delete, LeftArrow.")]
            string key,
            [McpArg("action", "press, down or up.")]
            string action = "press",
            [McpArg("modifiers", "Modifier keys held: any of alt, ctrl, shift, cmd.")]
            string[] modifiers = null,
            [McpArg("character", "Character for the text KeyDown of a press. Derived from the key when omitted.")]
            string character = null,
            [McpArg("restore_focus", "Give focus back to the window that had it before the input was sent.")]
            bool restoreFocus = true)
        {
            var clock = Stopwatch.StartNew();
            var mods = InputEventRecord.ParseModifiers(modifiers);

            if (string.IsNullOrWhiteSpace(key) || !Enum.TryParse(key.Trim(), true, out KeyCode code) || code == KeyCode.None)
            {
                throw new McpToolException("invalid_params", $"Unknown key '{key}'. Use a UnityEngine.KeyCode name such as F, Alpha1 or Return.");
            }

            if (character != null && character.Length > 1)
            {
                throw new McpToolException("invalid_params", $"character must be a single character, got '{character}' ({character.Length} characters).");
            }

            var target = ResolveTarget(view);
            var text = !string.IsNullOrEmpty(character) ? character[0] : CharacterFor(code, mods);
            var records = new List<InputEventRecord>();

            switch ((action ?? "press").Trim().ToLowerInvariant())
            {
                case "press":
                    records.Add(KeyRecord(EventType.KeyDown, code, '\0', mods));
                    if (text != '\0')
                    {
                        records.Add(KeyRecord(EventType.KeyDown, KeyCode.None, text, mods));
                    }

                    records.Add(KeyRecord(EventType.KeyUp, code, '\0', mods));
                    break;
                case "down":
                    records.Add(KeyRecord(EventType.KeyDown, code, text, mods));
                    break;
                case "up":
                    records.Add(KeyRecord(EventType.KeyUp, code, '\0', mods));
                    break;
                default:
                    throw new McpToolException("invalid_params", $"Unknown action '{action}'. Use press, down or up.");
            }

            return Send(target, EditorInput.Plan(records), 1, restoreFocus, true, null, clock, "input_key");
        }

        [McpTool(
            "input_record",
            "Record the input an Editor window receives — from a person at the keyboard or from " +
            "input_pointer — so it can be sent again with input_replay. start needs view and name; " +
            "stop writes a JSON file under the state root, in a directory named after a hash of the " +
            "project path, and returns the full path it used; status reports what is being " +
            "captured. One recording at a time. Mouse moves without a button held are left out " +
            "unless include_moves is true.",
            Examples = new[]
            {
                @"{""action"":""start"",""view"":""scene"",""name"":""look""}",
                @"{""action"":""stop""}",
            })]
        public static JObject Record(
            [McpArg("action", "start, stop or status.")]
            string action = "status",
            [McpArg("view", "Window to record (start only). " + ViewDescription)]
            string view = null,
            [McpArg("name", "Recording name (start only). Anything outside letters, digits, '_' " +
                            "and '-' is replaced with '_' rather than refused, so read the path in " +
                            "the reply to learn the name the file actually got.")]
            string name = null,
            [McpArg("include_moves", "Keep MouseMove events (start only).")]
            bool includeMoves = false)
        {
            switch ((action ?? "status").Trim().ToLowerInvariant())
            {
                case "status":
                    return InputRecorder.Status();
                case "stop":
                    return InputRecorder.Stop();
                case "start":
                    if (string.IsNullOrWhiteSpace(view))
                    {
                        throw new McpToolException("invalid_params", "start needs a view to record.");
                    }

                    var canonical = EditorWindowLocator.NormalizeForInput(view);
                    var window = EditorWindowLocator.Resolve(canonical);
                    var started = InputRecorder.Start(window, EditorWindowLocator.CanonicalView(window), name, includeMoves, Application.dataPath);
                    started["pixelsPerPoint"] = EditorGUIUtility.pixelsPerPoint;
                    started["windowSize"] = new JArray(window.position.width, window.position.height);
                    return started;
                default:
                    throw new McpToolException("invalid_params", $"Unknown action '{action}'. Use start, stop or status.");
            }
        }

        [McpTool(
            "input_replay",
            "Send a recording made by input_record back into a window, one recorded frame per Editor " +
            "frame. Give name (a recording of this project) or path. The window defaults to the one " +
            "recorded and is refused with view_mismatch if that window's type has changed; pass view " +
            "to send elsewhere on purpose. speed scales the gaps between frames, loop_count repeats the " +
            "whole recording, and then_capture takes a capture_screenshot of that view when the last " +
            "event has landed, so replay and picture come back together; with capture_path the picture " +
            "is written there and capture.path names the file, ready for render_compare. Returns a job " +
            "id when the replay outlasts the sync window; fetch the result with job_status.",
            Examples = new[]
            {
                @"{""name"":""look""}",
                @"{""name"":""look"",""speed"":2,""then_capture"":""scene""}",
                @"{""name"":""look"",""then_capture"":""scene"",""capture_path"":""Temp/look_after.png""}",
            })]
        public static JObject Replay(
            [McpArg("name", "Recording name, as given to input_record.")]
            string name = null,
            [McpArg("path", "Path of a recording file; alternative to name.")]
            string path = null,
            [McpArg("view", "Window to send to. Defaults to the recorded view.")]
            string view = null,
            [McpArg("speed", "Playback speed: 2 halves the gaps between frames, 0.5 doubles them. Gaps never drop below one frame.")]
            double speed = 1.0,
            [McpArg("repaint_each_frame", "Request a repaint after each frame's events.")]
            bool repaintEachFrame = true,
            [McpArg("then_capture", "capture_screenshot view to capture when the replay ends, e.g. scene.")]
            string thenCapture = null,
            [McpArg("capture_path", "With then_capture: write the PNG here (capture_screenshot's save_path) so capture.path names it instead of returning the image inline.")]
            string capturePath = null,
            [McpArg("loop_count", "How many times to send the recording.")]
            int loopCount = 1,
            [McpArg("restore_focus", "Give focus back to the window that had it before the replay.")]
            bool restoreFocus = true)
        {
            var clock = Stopwatch.StartNew();

            if (string.IsNullOrWhiteSpace(path))
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new McpToolException("invalid_params", "Give either name or path.");
                }

                path = InputRecordingFile.PathFor(Application.dataPath, name);
            }

            var recording = InputRecordingFile.Read(path);

            if (recording.Events.Count == 0)
            {
                throw new McpToolException("invalid_recording", $"The recording at '{path}' has no events.");
            }

            var explicitView = !string.IsNullOrWhiteSpace(view);
            var target = ResolveTarget(explicitView ? view : recording.View);

            if (!explicitView && !string.Equals(target.Window.GetType().FullName, recording.WindowType, StringComparison.Ordinal))
            {
                throw new McpToolException(
                    "view_mismatch",
                    $"The recording was taken on {recording.WindowType} but view '{recording.View}' now resolves to {target.Window.GetType().FullName}. Pass view to send it there anyway.",
                    409);
            }

            if (loopCount < 1)
            {
                throw new McpToolException("invalid_params", "loop_count must be at least 1.");
            }

            var groups = EditorInput.Plan(recording.Events, speed);
            return Send(
                target, groups, loopCount, restoreFocus, repaintEachFrame, thenCapture, clock, "input_replay",
                forceDeferred: true, recordingPath: path.Replace('\\', '/'), capturePath: capturePath);
        }

        private sealed class Target
        {
            public EditorWindow Window;
            public string View;
            public Vector2 WindowSize;
            public Vector2 ContentOffset;
            public bool ContentOffsetKnown;
            public EditorWindow PreviousFocus;
        }

        private static Target ResolveTarget(string view)
        {
            if (string.IsNullOrWhiteSpace(view))
            {
                throw new McpToolException("invalid_params", "view is required.");
            }

            var window = EditorWindowLocator.Resolve(EditorWindowLocator.NormalizeForInput(view.Trim()));
            var size = window.position.size;

            if (size.x <= 0 || size.y <= 0)
            {
                throw new McpToolException(
                    "window_not_active",
                    $"EditorWindow '{window.titleContent.text}' has no visible area (position={window.position}).");
            }

            return new Target
            {
                Window = window,
                View = EditorWindowLocator.CanonicalView(window),
                WindowSize = size,
                ContentOffset = EditorInput.ContentOffset(window, out var known),
                ContentOffsetKnown = known,
                PreviousFocus = EditorWindow.focusedWindow,
            };
        }

        /// <summary>
        /// Reads an [x, y] argument. Out-of-range content coordinates are refused here because
        /// the IMGUI container drops events outside its bounds without a word, and a drag that
        /// silently did nothing is indistinguishable from the bug being reproduced.
        /// </summary>
        private static Vector2 ReadPoint(double[] values, string argument, bool checkBounds, bool normalized, Vector2 size)
        {
            if (values == null)
            {
                if (!checkBounds)
                {
                    return Vector2.zero;
                }

                throw new McpToolException("invalid_params", $"{argument} is required and must be [x, y].");
            }

            if (values.Length != 2)
            {
                throw new McpToolException("invalid_params", $"{argument} must be [x, y], got {values.Length} values.");
            }

            var point = new Vector2((float)values[0], (float)values[1]);

            if (normalized)
            {
                point = new Vector2(point.x * size.x, point.y * size.y);
            }

            if (checkBounds && (point.x < 0 || point.y < 0 || point.x > size.x || point.y > size.y))
            {
                throw new McpToolException(
                    "invalid_params",
                    $"{argument} [{point.x:0.##}, {point.y:0.##}] is outside the window, which is {size.x:0.##}x{size.y:0.##}. Events outside it are dropped without effect.");
            }

            return point;
        }

        /// <summary>
        /// Sends the planned groups. A single group goes out now and the answer comes back
        /// inline; anything longer runs through <see cref="FrameSequencer"/> and the caller gets
        /// a <see cref="DeferredToolResult"/>.
        /// </summary>
        private static JObject Send(
            Target target,
            List<EditorInput.FrameGroup> groups,
            int loops,
            bool restoreFocus,
            bool repaint,
            string thenCapture,
            Stopwatch clock,
            string label,
            bool forceDeferred = false,
            string recordingPath = null,
            string capturePath = null)
        {
            var before = EditorInput.SceneCameraState(target.Window as SceneView);

            if (!EditorInput.FocusForInput(target.Window, target.ContentOffset))
            {
                throw new McpToolException(
                    "window_not_active",
                    $"EditorWindow '{target.Window.titleContent.text}' could not take focus; it is probably a tab behind another one in the same dock area.",
                    409);
            }

            var singleFrame = groups.Count <= 1 && loops == 1;

            if (singleFrame && !forceDeferred)
            {
                var sent = 0;
                try
                {
                    if (groups.Count == 1)
                    {
                        sent = EditorInput.SendGroup(target.Window, groups[0].Records, target.ContentOffset, repaint);
                    }
                }
                finally
                {
                    RestoreFocus(target, restoreFocus);
                }

                return Describe(target, sent, 1, loops, before, clock, thenCapture, recordingPath, capturePath);
            }

            var item = FrameSequencer.Run(
                Sequence(target, groups, loops, restoreFocus, repaint, thenCapture, before, clock, recordingPath, capturePath),
                label);

            return new DeferredToolResult(item);
        }

        private static IEnumerator<FrameStep> Sequence(
            Target target,
            List<EditorInput.FrameGroup> groups,
            int loops,
            bool restoreFocus,
            bool repaint,
            string thenCapture,
            JObject before,
            Stopwatch clock,
            string recordingPath,
            string capturePath)
        {
            var sent = 0;
            var frames = 0;

            try
            {
                for (var loop = 0; loop < loops; loop++)
                {
                    if (loop > 0)
                    {
                        frames++;
                        yield return FrameStep.Wait();
                    }

                    foreach (var group in groups)
                    {
                        for (var wait = 0; wait < group.WaitTicks; wait++)
                        {
                            frames++;
                            yield return FrameStep.Wait();
                        }

                        if (target.Window == null)
                        {
                            throw new McpToolException("window_not_found", "The window closed during the input sequence.", 409);
                        }

                        sent += EditorInput.SendGroup(target.Window, group.Records, target.ContentOffset, repaint);
                    }
                }

                // One more tick so the last events are processed and drawn before the camera
                // is read and any capture is taken.
                frames++;
                yield return FrameStep.Wait();
            }
            finally
            {
                RestoreFocus(target, restoreFocus);
            }

            yield return FrameStep.Done(Describe(target, sent, frames + 1, loops, before, clock, thenCapture, recordingPath, capturePath));
        }

        private static JObject Describe(
            Target target,
            int sent,
            int frames,
            int loops,
            JObject before,
            Stopwatch clock,
            string thenCapture,
            string recordingPath,
            string capturePath)
        {
            var result = new JObject
            {
                ["sent"] = sent,
                ["frames"] = frames,
                ["loops"] = loops,
                ["window"] = target.Window != null ? target.Window.titleContent.text : null,
                ["view"] = target.View,
                ["durationMs"] = Math.Round(clock.Elapsed.TotalMilliseconds, 1),
                ["contentOffset"] = new JArray(target.ContentOffset.x, target.ContentOffset.y),
                ["contentOffsetKnown"] = target.ContentOffsetKnown,
                ["pixelsPerPoint"] = EditorGUIUtility.pixelsPerPoint,
                ["windowSize"] = new JArray(target.WindowSize.x, target.WindowSize.y),
            };

            if (before != null)
            {
                result["camera"] = new JObject
                {
                    ["before"] = before,
                    ["after"] = EditorInput.SceneCameraState(target.Window as SceneView),
                };
            }

            if (!string.IsNullOrWhiteSpace(thenCapture))
            {
                result["capture"] = CaptureAfter(thenCapture, capturePath);
            }

            if (recordingPath != null)
            {
                result["path"] = recordingPath;
            }

            return result;
        }

        /// <summary>
        /// A failed capture goes inside the result and does not fail the call: the input has
        /// already been sent, and the caller needs to know that even without a picture.
        /// </summary>
        private static JObject CaptureAfter(string view, string savePath)
        {
            try
            {
                var request = new JObject { ["view"] = view };

                if (!string.IsNullOrWhiteSpace(savePath))
                {
                    request["savePath"] = savePath;
                }

                return ScreenshotCapture.Capture(request);
            }
            catch (McpScreenshotException e)
            {
                return new JObject { ["error"] = e.Code, ["message"] = e.Message };
            }
        }

        private static void RestoreFocus(Target target, bool restoreFocus)
        {
            if (!restoreFocus || target.PreviousFocus == null || target.PreviousFocus == target.Window)
            {
                return;
            }

            try
            {
                target.PreviousFocus.Focus();
            }
            catch (Exception)
            {
                // The previous window may have closed meanwhile; the input already landed.
            }
        }

        private static InputEventRecord Pointer(EventType type, int button, Vector2 position, EventModifiers modifiers)
        {
            return new InputEventRecord
            {
                Type = type,
                Button = button,
                Position = position,
                Modifiers = modifiers,
            };
        }

        private static InputEventRecord KeyRecord(EventType type, KeyCode key, char character, EventModifiers modifiers)
        {
            return new InputEventRecord
            {
                Type = type,
                Key = key,
                Character = character,
                Modifiers = modifiers,
            };
        }

        /// <summary>The character a key produces on its own, for the text KeyDown of a press.</summary>
        internal static char CharacterFor(KeyCode key, EventModifiers modifiers)
        {
            if (key >= KeyCode.A && key <= KeyCode.Z)
            {
                var letter = (char)('a' + (key - KeyCode.A));
                return (modifiers & EventModifiers.Shift) != 0 ? char.ToUpperInvariant(letter) : letter;
            }

            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
            {
                return (char)('0' + (key - KeyCode.Alpha0));
            }

            if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9)
            {
                return (char)('0' + (key - KeyCode.Keypad0));
            }

            switch (key)
            {
                case KeyCode.Space: return ' ';
                case KeyCode.Return:
                case KeyCode.KeypadEnter: return '\n';
                case KeyCode.Tab: return '\t';
                case KeyCode.Backspace: return '\b';
                case KeyCode.Period:
                case KeyCode.KeypadPeriod: return '.';
                case KeyCode.Comma: return ',';
                case KeyCode.Minus:
                case KeyCode.KeypadMinus: return '-';
                case KeyCode.Plus:
                case KeyCode.KeypadPlus: return '+';
                case KeyCode.Equals: return '=';
                case KeyCode.Slash:
                case KeyCode.KeypadDivide: return '/';
                case KeyCode.Asterisk:
                case KeyCode.KeypadMultiply: return '*';
                case KeyCode.Underscore: return '_';
                default: return '\0';
            }
        }
    }
}
