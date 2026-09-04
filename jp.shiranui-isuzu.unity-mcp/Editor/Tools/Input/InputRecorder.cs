using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

using Newtonsoft.Json.Linq;

using UnityEditor;

using UnityEngine;
using UnityEngine.UIElements;

using UnityMCP.Editor.Core;

using Debug = UnityEngine.Debug;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Captures the input one <see cref="EditorWindow"/> receives, from a person or from
    /// <see cref="EditorInput"/>, until asked to stop. One session per Editor.
    /// </summary>
    /// <remarks>
    /// A Scene View is recorded from <c>SceneView.duringSceneGui</c>, which sees every event its
    /// OnGUI handles including a drag after the mouse is captured. Trickle-down callbacks on the
    /// window's root visual element stop seeing pointer events once the IMGUI container takes
    /// the capture, so they would miss all but the first frame of a drag; and
    /// <c>EditorApplication.globalEventHandler</c> only sees events no window used. Other
    /// windows have no OnGUI hook, so they are recorded from the root element and get the
    /// events the panel delivers there.
    /// </remarks>
    internal static class InputRecorder
    {
        private static Session current;

        public static bool IsRecording => current != null;

        public static JObject Status()
        {
            var session = current;

            if (session == null)
            {
                return new JObject { ["recording"] = false };
            }

            return new JObject
            {
                ["recording"] = true,
                ["view"] = session.View,
                ["window"] = session.WindowTitle,
                ["name"] = session.Name,
                ["events"] = session.Events.Count,
                ["frames"] = session.Frame,
                ["durationMs"] = Math.Round(session.Clock.Elapsed.TotalMilliseconds, 1),
                ["includeMoves"] = session.IncludeMoves,
            };
        }

        /// <exception cref="McpToolException"><c>already_recording</c> when a session is open.</exception>
        public static JObject Start(EditorWindow window, string view, string name, bool includeMoves, string projectPath)
        {
            if (current != null)
            {
                throw new McpToolException(
                    "already_recording",
                    $"A recording named '{current.Name}' is already in progress on '{current.View}'. Stop it first.",
                    409);
            }

            var safeName = InputRecordingFile.NormalizeName(name);
            var session = new Session(window, view, safeName, includeMoves, InputRecordingFile.PathFor(projectPath, safeName));
            session.Attach();
            current = session;

            return new JObject
            {
                ["recording"] = true,
                ["view"] = view,
                ["window"] = session.WindowTitle,
                ["name"] = safeName,
                ["path"] = session.Path.Replace('\\', '/'),
                ["contentOffset"] = new JArray(session.ContentOffset.x, session.ContentOffset.y),
                ["contentOffsetKnown"] = session.ContentOffsetKnown,
            };
        }

        /// <exception cref="McpToolException"><c>not_recording</c> when nothing is being recorded.</exception>
        public static JObject Stop()
        {
            var session = current;

            if (session == null)
            {
                throw new McpToolException("not_recording", "No recording is in progress.", 409);
            }

            current = null;
            session.Detach();

            var recording = session.ToRecording();
            var path = InputRecordingFile.Write(session.Path, recording);

            return new JObject
            {
                ["path"] = path.Replace('\\', '/'),
                ["events"] = recording.Events.Count,
                ["frames"] = recording.FrameCount,
                ["durationMs"] = Math.Round(recording.DurationMs, 1),
                ["view"] = recording.View,
                ["window"] = session.WindowTitle,
            };
        }

        private sealed class Session
        {
            private readonly EditorWindow window;
            private readonly VisualElement root;

            private EventCallback<PointerDownEvent> onPointerDown;
            private EventCallback<PointerUpEvent> onPointerUp;
            private EventCallback<PointerMoveEvent> onPointerMove;
            private EventCallback<WheelEvent> onWheel;
            private EventCallback<KeyDownEvent> onKeyDown;
            private EventCallback<KeyUpEvent> onKeyUp;
            private Action<SceneView> onSceneGui;
            private bool attached;

            public Session(EditorWindow window, string view, string name, bool includeMoves, string path)
            {
                this.window = window;
                this.root = window.rootVisualElement;
                this.View = view;
                this.Name = name;
                this.IncludeMoves = includeMoves;
                this.Path = path;
                this.WindowTitle = window.titleContent?.text;
                this.WindowType = window.GetType().FullName;
                this.WindowSize = window.position.size;
                this.ContentOffset = EditorInput.ContentOffset(window, out var known);
                this.ContentOffsetKnown = known;
                this.Clock = Stopwatch.StartNew();
            }

            public string View { get; }

            public string Name { get; }

            public bool IncludeMoves { get; }

            public string Path { get; }

            public string WindowTitle { get; }

            public string WindowType { get; }

            public Vector2 WindowSize { get; }

            public Vector2 ContentOffset { get; }

            public bool ContentOffsetKnown { get; }

            public Stopwatch Clock { get; }

            public int Frame { get; private set; }

            public List<InputEventRecord> Events { get; } = new();

            public void Attach()
            {
                if (this.window is SceneView)
                {
                    this.onSceneGui = this.OnSceneGui;
                    SceneView.duringSceneGui += this.onSceneGui;
                }
                else
                {
                    this.onPointerDown = e => this.Events.Add(this.FromPointer(e, EventType.MouseDown, e.button));
                    this.onPointerUp = e => this.Events.Add(this.FromPointer(e, EventType.MouseUp, e.button));
                    this.onPointerMove = e => this.Events.Add(e.pressedButtons != 0
                        ? this.FromPointer(e, EventType.MouseDrag, LowestButton(e.pressedButtons))
                        : this.FromPointer(e, EventType.MouseMove, 0));
                    this.onWheel = e => this.Events.Add(new InputEventRecord
                    {
                        Frame = this.Frame,
                        Time = this.Clock.Elapsed.TotalMilliseconds,
                        Type = EventType.ScrollWheel,
                        Position = (Vector2)e.mousePosition - this.ContentOffset,
                        Delta = e.delta,
                        Modifiers = e.modifiers,
                    });
                    this.onKeyDown = e => this.Events.Add(this.FromKey(e.keyCode, e.character, e.modifiers, EventType.KeyDown));
                    this.onKeyUp = e => this.Events.Add(this.FromKey(e.keyCode, '\0', e.modifiers, EventType.KeyUp));

                    this.root.RegisterCallback(this.onPointerDown, TrickleDown.TrickleDown);
                    this.root.RegisterCallback(this.onPointerUp, TrickleDown.TrickleDown);
                    this.root.RegisterCallback(this.onPointerMove, TrickleDown.TrickleDown);
                    this.root.RegisterCallback(this.onWheel, TrickleDown.TrickleDown);
                    this.root.RegisterCallback(this.onKeyDown, TrickleDown.TrickleDown);
                    this.root.RegisterCallback(this.onKeyUp, TrickleDown.TrickleDown);
                }

                EditorApplication.update += this.Tick;
                AssemblyReloadEvents.beforeAssemblyReload += this.OnBeforeReload;
                this.attached = true;
            }

            public void Detach()
            {
                if (!this.attached)
                {
                    return;
                }

                this.attached = false;
                EditorApplication.update -= this.Tick;
                AssemblyReloadEvents.beforeAssemblyReload -= this.OnBeforeReload;

                if (this.onSceneGui != null)
                {
                    SceneView.duringSceneGui -= this.onSceneGui;
                    return;
                }

                // The window may have been closed while recording; its root is gone with it.
                if (this.window != null && this.root != null)
                {
                    this.root.UnregisterCallback(this.onPointerDown, TrickleDown.TrickleDown);
                    this.root.UnregisterCallback(this.onPointerUp, TrickleDown.TrickleDown);
                    this.root.UnregisterCallback(this.onPointerMove, TrickleDown.TrickleDown);
                    this.root.UnregisterCallback(this.onWheel, TrickleDown.TrickleDown);
                    this.root.UnregisterCallback(this.onKeyDown, TrickleDown.TrickleDown);
                    this.root.UnregisterCallback(this.onKeyUp, TrickleDown.TrickleDown);
                }
            }

            /// <summary>
            /// The kept events with frame and time rebased to the first of them, so a replay's
            /// first group goes out at once no matter how long the person took to begin.
            /// </summary>
            public InputRecording ToRecording()
            {
                var kept = InputRecordingFile.Filter(this.Events, this.IncludeMoves);

                if (kept.Count > 0)
                {
                    var firstFrame = kept[0].Frame;
                    var firstTime = kept[0].Time;

                    foreach (var e in kept)
                    {
                        e.Frame -= firstFrame;
                        e.Time -= firstTime;
                    }
                }

                var recording = new InputRecording
                {
                    Unity = Application.unityVersion,
                    View = this.View,
                    WindowType = this.WindowType,
                    WindowSize = this.WindowSize,
                    ContentOffset = this.ContentOffset,
                    CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                };
                recording.Events.AddRange(kept);

                return recording;
            }

            private void Tick()
            {
                this.Frame++;
            }

            private void OnBeforeReload()
            {
                if (current != this)
                {
                    return;
                }

                // Static state does not survive the reload, so the session ends here and what has
                // been captured is written first.
                var result = Stop();
                Debug.Log($"[InputRecorder] Domain reload ended the recording; written to {result["path"]}.");
            }

            private void OnSceneGui(SceneView view)
            {
                if (view != this.window)
                {
                    return;
                }

                var e = Event.current;
                if (e == null)
                {
                    return;
                }

                // An event a handler earlier in OnGUI used still reports its original type here.
                var type = e.type == EventType.Used ? e.rawType : e.type;
                if (!InputEventRecord.IsPointerType(type) && !InputEventRecord.IsKeyType(type))
                {
                    return;
                }

                // Inside OnGUI the mouse position is relative to the IMGUI container, which sits
                // below the Scene View toolbar. The window's position is its host view's origin
                // on screen (the size is the content size, the origin is not), so the container's
                // offset within the content is its screen origin minus that, minus the border.
                var containerOrigin = GUIUtility.GUIToScreenPoint(Vector2.zero) - this.window.position.position - this.ContentOffset;

                this.Events.Add(new InputEventRecord
                {
                    Frame = this.Frame,
                    Time = this.Clock.Elapsed.TotalMilliseconds,
                    Type = type,
                    Button = e.button,
                    Position = e.mousePosition + containerOrigin,
                    Delta = e.delta,
                    Modifiers = e.modifiers,
                    Key = InputEventRecord.IsPointerType(type) ? KeyCode.None : e.keyCode,
                    Character = type == EventType.KeyDown ? e.character : '\0',
                    Clicks = type == EventType.MouseDown ? e.clickCount : 0,
                });
            }

            private InputEventRecord FromPointer<T>(PointerEventBase<T> e, EventType type, int button) where T : PointerEventBase<T>, new()
            {
                return new InputEventRecord
                {
                    Frame = this.Frame,
                    Time = this.Clock.Elapsed.TotalMilliseconds,
                    Type = type,
                    Button = button < 0 ? 0 : button,
                    Position = (Vector2)e.position - this.ContentOffset,
                    Delta = e.deltaPosition,
                    Modifiers = e.modifiers,
                    Clicks = type == EventType.MouseDown ? e.clickCount : 0,
                };
            }

            private InputEventRecord FromKey(KeyCode key, char character, EventModifiers modifiers, EventType type)
            {
                return new InputEventRecord
                {
                    Frame = this.Frame,
                    Time = this.Clock.Elapsed.TotalMilliseconds,
                    Type = type,
                    Key = key,
                    Character = character,
                    Modifiers = modifiers,
                };
            }

            /// <summary>
            /// A pointer move carries no button, only the mask of buttons held (bit 0 left,
            /// bit 1 right, bit 2 middle), which is the same numbering IMGUI's button uses.
            /// </summary>
            private static int LowestButton(int pressedButtons)
            {
                for (var bit = 0; bit < 8; bit++)
                {
                    if ((pressedButtons & (1 << bit)) != 0)
                    {
                        return bit;
                    }
                }

                return 0;
            }
        }
    }
}
