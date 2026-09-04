using System;
using System.Collections.Generic;
using System.Reflection;

using Newtonsoft.Json.Linq;

using UnityEditor;

using UnityEngine;

using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Sends input to an <see cref="EditorWindow"/> through the path its own GUI code sees, and
    /// converts between <see cref="Event"/> and the record form a recording stores.
    /// </summary>
    /// <remarks>
    /// <c>EditorWindow.SendEvent</c> delivers an event as if the operating system had, minus the
    /// check that input is enabled for the view. Its coordinates are dock-area view space: the
    /// origin is the top-left of the tab bar, not of the window's content. Records hold content
    /// space, so every send adds <see cref="ContentOffset"/> and every capture removes it.
    /// </remarks>
    internal static class EditorInput
    {
        /// <summary>
        /// How far the first drag of a multi-step gesture travels before the real steps start.
        /// Scene View navigation is a clutch shortcut: two shortcuts match a right mouse down,
        /// and the one that wins is decided by the first MouseDrag to travel more than 6 px.
        /// That deciding event is consumed by the decision and moves nothing, so a drag whose
        /// every step is under the threshold never starts.
        /// </summary>
        public const float ClutchDistance = 7f;

        private static readonly FieldInfo ParentField =
            typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);

        private static PropertyInfo borderSizeProperty;

        /// <summary>
        /// Brings the window forward and, for a Scene View, tells it the pointer is over its
        /// viewport. Returns false when the window still does not have focus afterwards, which
        /// is what happens to a tab hidden behind another in the same dock area.
        /// </summary>
        /// <remarks>
        /// Scene View navigation refuses to start unless <c>viewportsUnderMouse</c> is set. The
        /// flag is driven by MouseEnter callbacks on the panel, so a MouseMove is sent first to
        /// raise it; Scene View also raises it itself on any MouseMove or MouseDown that reaches
        /// its OnGUI.
        /// </remarks>
        public static bool FocusForInput(EditorWindow window, Vector2 contentOffset)
        {
            window.Focus();

            if (!window.hasFocus)
            {
                return false;
            }

            if (window is SceneView)
            {
                var wake = new Event
                {
                    type = EventType.MouseMove,
                    mousePosition = contentOffset + new Vector2(1f, 1f),
                };
                window.SendEvent(wake);
            }

            return true;
        }

        /// <summary>
        /// Offset from the dock-area view origin to the window's content origin: the tab bar and
        /// borders. Unknown when the window's host cannot be read, in which case the caller
        /// reports <c>contentOffsetKnown: false</c> and sends coordinates unshifted.
        /// </summary>
        public static Vector2 ContentOffset(EditorWindow window, out bool known)
        {
            known = false;

            try
            {
                var host = ParentField?.GetValue(window);
                if (host == null)
                {
                    return Vector2.zero;
                }

                var property = borderSizeProperty;
                if (property == null || !property.DeclaringType.IsInstanceOfType(host))
                {
                    property = host.GetType().GetProperty(
                        "borderSize",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    borderSizeProperty = property;
                }

                if (property == null)
                {
                    return Vector2.zero;
                }

                if (property.GetValue(host) is RectOffset border)
                {
                    known = true;
                    return new Vector2(border.left, border.top);
                }
            }
            catch (Exception)
            {
                // Reflection into the host is best effort; an unknown offset is reported, not thrown.
            }

            return Vector2.zero;
        }

        /// <summary>Builds the <see cref="Event"/> a record describes, shifted into view space.</summary>
        public static Event ToEvent(InputEventRecord record, Vector2 contentOffset)
        {
            var e = new Event
            {
                type = record.Type,
                button = record.Button,
                mousePosition = record.Position + contentOffset,
                delta = record.Delta,
                modifiers = record.Modifiers,
                keyCode = record.Key,
                character = record.Character,
            };

            if (record.Clicks > 0)
            {
                e.clickCount = record.Clicks;
            }

            return e;
        }

        /// <summary>Captures an <see cref="Event"/> as a record in content space.</summary>
        public static InputEventRecord FromEvent(Event e, Vector2 contentOffset, int frame, double timeMs)
        {
            return new InputEventRecord
            {
                Frame = frame,
                Time = timeMs,
                Type = e.type,
                Button = e.button,
                Position = e.mousePosition - contentOffset,
                Delta = e.delta,
                Modifiers = e.modifiers,
                // IMGUI stamps mouse events with KeyCode.Mouse0..6; the button field already
                // says which, and a key on a pointer record would replay as a key press.
                Key = InputEventRecord.IsPointerType(e.type) ? KeyCode.None : e.keyCode,
                Character = e.type == EventType.KeyDown ? e.character : '\0',
                Clicks = e.type == EventType.MouseDown ? e.clickCount : 0,
            };
        }

        /// <summary>
        /// Sends one frame's worth of records, then asks for a single repaint.
        /// </summary>
        /// <remarks>
        /// <c>EventType.Repaint</c> is never sent: SendEvent treats it as an immediate draw
        /// outside the normal frame, and a recording that captured one would otherwise force a
        /// draw at a point the window did not choose. <c>Repaint()</c> queues the draw for the
        /// Editor's own next pass, which is when a real user's input would be drawn too.
        /// </remarks>
        /// <returns>How many events were sent.</returns>
        public static int SendGroup(EditorWindow window, IEnumerable<InputEventRecord> records, Vector2 contentOffset, bool repaint)
        {
            var sent = 0;

            foreach (var record in records)
            {
                if (!record.IsInput)
                {
                    continue;
                }

                window.SendEvent(ToEvent(record, contentOffset));
                sent++;
            }

            if (repaint)
            {
                window.Repaint();
            }

            return sent;
        }

        /// <summary>
        /// The records of a drag from <paramref name="from"/> to <paramref name="to"/>: MouseDown,
        /// then MouseDrag steps whose deltas sum to the full displacement, then MouseUp at the
        /// destination. Frame numbers advance by <paramref name="framesPerStep"/> per step, so
        /// zero puts the whole gesture in one frame.
        /// </summary>
        /// <remarks>
        /// With more than one step the first drag travels <see cref="ClutchDistance"/> along the
        /// path so the clutch decides on it, and the remaining distance is split evenly across
        /// the requested steps. A displacement shorter than the clutch distance gets no such
        /// step; nothing that short would start a navigation anyway.
        /// </remarks>
        public static List<InputEventRecord> BuildDrag(
            Vector2 from, Vector2 to, int button, EventModifiers modifiers, int steps, int framesPerStep)
        {
            if (steps < 1)
            {
                steps = 1;
            }

            if (framesPerStep < 0)
            {
                framesPerStep = 0;
            }

            var records = new List<InputEventRecord>(steps + 3);
            var frame = 0;

            records.Add(new InputEventRecord
            {
                Frame = frame,
                Type = EventType.MouseDown,
                Button = button,
                Position = from,
                Modifiers = modifiers,
                Clicks = 1,
            });

            var previous = from;
            var displacement = to - from;

            if (steps > 1 && displacement.magnitude > ClutchDistance)
            {
                frame += framesPerStep;
                var prep = from + displacement.normalized * ClutchDistance;
                records.Add(Drag(frame, button, modifiers, prep, previous));
                previous = prep;
            }

            var start = previous;
            var remaining = to - start;

            for (var i = 1; i <= steps; i++)
            {
                frame += framesPerStep;
                var position = i == steps ? to : start + remaining * ((float)i / steps);
                records.Add(Drag(frame, button, modifiers, position, previous));
                previous = position;
            }

            frame += framesPerStep;
            records.Add(new InputEventRecord
            {
                Frame = frame,
                Type = EventType.MouseUp,
                Button = button,
                Position = to,
                Modifiers = modifiers,
            });

            return records;
        }

        /// <summary>Consecutive records that share a frame, with the tick gap that precedes each group.</summary>
        public sealed class FrameGroup
        {
            public FrameGroup(int frame, int waitTicks)
            {
                this.Frame = frame;
                this.WaitTicks = waitTicks;
            }

            /// <summary>The recorded frame index the group came from.</summary>
            public int Frame { get; }

            /// <summary>Editor ticks to let pass before sending this group. Zero for the first.</summary>
            public int WaitTicks { get; }

            public List<InputEventRecord> Records { get; } = new();
        }

        /// <summary>
        /// Splits records by frame and scales the gaps between frames by <c>1/speed</c>. A gap
        /// never rounds below one tick: two groups sent on the same tick would collapse into the
        /// single-frame case the caller split them to avoid.
        /// </summary>
        public static List<FrameGroup> Plan(IEnumerable<InputEventRecord> records, double speed = 1.0)
        {
            if (speed <= 0 || double.IsNaN(speed) || double.IsInfinity(speed))
            {
                throw new McpToolException("invalid_params", "speed must be a positive number.");
            }

            var groups = new List<FrameGroup>();
            FrameGroup current = null;

            foreach (var record in records)
            {
                if (current == null)
                {
                    current = new FrameGroup(record.Frame, 0);
                    groups.Add(current);
                }
                else if (record.Frame != current.Frame)
                {
                    var gap = record.Frame - current.Frame;
                    var wait = (int)Math.Max(1, Math.Round(gap / speed));
                    current = new FrameGroup(record.Frame, wait);
                    groups.Add(current);
                }

                current.Records.Add(record);
            }

            return groups;
        }

        /// <summary>The three numbers that describe where a Scene View camera is looking.</summary>
        public static JObject SceneCameraState(SceneView view)
        {
            if (view == null)
            {
                return null;
            }

            var euler = view.rotation.eulerAngles;
            var pivot = view.pivot;

            return new JObject
            {
                ["rotation"] = new JObject { ["x"] = Round(euler.x), ["y"] = Round(euler.y), ["z"] = Round(euler.z) },
                ["pivot"] = new JObject { ["x"] = Round(pivot.x), ["y"] = Round(pivot.y), ["z"] = Round(pivot.z) },
                ["size"] = Round(view.size),
            };
        }

        private static InputEventRecord Drag(int frame, int button, EventModifiers modifiers, Vector2 position, Vector2 previous)
        {
            return new InputEventRecord
            {
                Frame = frame,
                Type = EventType.MouseDrag,
                Button = button,
                Position = position,
                Delta = position - previous,
                Modifiers = modifiers,
            };
        }

        private static double Round(float value) => Math.Round(value, 4);
    }
}
