using System;
using System.Collections.Generic;

using Newtonsoft.Json.Linq;

using UnityEngine;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// One Editor input event in the form a recording stores and a replay sends.
    /// </summary>
    /// <remarks>
    /// Positions are window-content points, not the dock-area view space that
    /// <c>EditorWindow.SendEvent</c> expects: the tab bar height differs between a docked
    /// and a floating window, so a recording that stored view-space coordinates would land in
    /// the wrong place as soon as the window was re-docked. <see cref="EditorInput"/> adds the
    /// offset on the way out and removes it on the way in.
    /// </remarks>
    internal sealed class InputEventRecord
    {
        /// <summary>Frame index relative to the first event of the recording.</summary>
        public int Frame { get; set; }

        /// <summary>Milliseconds since the recording started.</summary>
        public double Time { get; set; }

        public EventType Type { get; set; }

        public int Button { get; set; }

        public Vector2 Position { get; set; }

        public Vector2 Delta { get; set; }

        public EventModifiers Modifiers { get; set; }

        public KeyCode Key { get; set; } = KeyCode.None;

        /// <summary>The character of a text-producing KeyDown; <c>'\0'</c> when there is none.</summary>
        public char Character { get; set; }

        /// <summary>Click count of a MouseDown; zero when the event carries none.</summary>
        public int Clicks { get; set; }

        /// <summary>
        /// True for the event types a recording keeps: mouse, wheel and key events. Layout,
        /// Repaint, and the command events a window sends itself while handling input (the Scene
        /// View posts one to take the hot control) are not input and would be sent twice on
        /// replay if they were kept.
        /// </summary>
        public bool IsInput => IsPointerType(this.Type) || IsKeyType(this.Type);

        public bool IsMove => this.Type == EventType.MouseMove;

        public JObject ToJson()
        {
            var json = new JObject
            {
                ["f"] = this.Frame,
                ["t"] = Math.Round(this.Time, 3),
                ["type"] = this.Type.ToString(),
            };

            if (IsPointerType(this.Type))
            {
                json["button"] = this.Button;
                json["pos"] = new JArray(Round(this.Position.x), Round(this.Position.y));

                if (this.Delta != Vector2.zero)
                {
                    json["delta"] = new JArray(Round(this.Delta.x), Round(this.Delta.y));
                }
            }

            var mods = ModifiersToString(this.Modifiers);
            if (mods.Length > 0)
            {
                json["mods"] = mods;
            }

            if (this.Key != KeyCode.None)
            {
                json["key"] = this.Key.ToString();
            }

            if (this.Character != '\0')
            {
                json["char"] = this.Character.ToString();
            }

            if (this.Clicks > 0)
            {
                json["clicks"] = this.Clicks;
            }

            return json;
        }

        public static InputEventRecord FromJson(JObject json)
        {
            if (json == null)
            {
                throw new McpToolException("invalid_recording", "An event entry is not an object.");
            }

            var typeName = json["type"]?.ToString();
            if (!Enum.TryParse(typeName, true, out EventType type))
            {
                throw new McpToolException("invalid_recording", $"Unknown event type '{typeName}'.");
            }

            var record = new InputEventRecord
            {
                Frame = ReadInt(json, "f"),
                Time = ReadNumber(json, "t"),
                Type = type,
                Button = ReadInt(json, "button"),
                Position = ReadVector(json["pos"], "pos"),
                Delta = ReadVector(json["delta"], "delta"),
                Modifiers = ParseModifiers(json["mods"]?.ToString()),
                Clicks = ReadInt(json, "clicks"),
            };

            var keyName = json["key"]?.ToString();
            if (!string.IsNullOrEmpty(keyName))
            {
                if (!Enum.TryParse(keyName, true, out KeyCode key))
                {
                    throw new McpToolException("invalid_recording", $"Unknown key code '{keyName}'.");
                }

                record.Key = key;
            }

            var character = json["char"]?.ToString();
            if (!string.IsNullOrEmpty(character))
            {
                record.Character = character[0];
            }

            return record;
        }

        public static bool IsPointerType(EventType type)
        {
            switch (type)
            {
                case EventType.MouseDown:
                case EventType.MouseUp:
                case EventType.MouseMove:
                case EventType.MouseDrag:
                case EventType.ScrollWheel:
                case EventType.ContextClick:
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsKeyType(EventType type)
        {
            return type == EventType.KeyDown || type == EventType.KeyUp;
        }

        /// <summary>Serialises modifiers as <c>alt|ctrl|shift|cmd</c>, in that order.</summary>
        public static string ModifiersToString(EventModifiers modifiers)
        {
            var parts = new List<string>(4);

            if ((modifiers & EventModifiers.Alt) != 0) parts.Add("alt");
            if ((modifiers & EventModifiers.Control) != 0) parts.Add("ctrl");
            if ((modifiers & EventModifiers.Shift) != 0) parts.Add("shift");
            if ((modifiers & EventModifiers.Command) != 0) parts.Add("cmd");

            return string.Join("|", parts);
        }

        /// <summary>
        /// Parses <c>alt|ctrl|shift|cmd</c>. Accepts the spellings a caller is likely to type
        /// (<c>control</c>, <c>command</c>, <c>meta</c>, <c>option</c>); anything else is an error,
        /// because a drag without Alt is a different gesture, not a slightly wrong one.
        /// </summary>
        public static EventModifiers ParseModifiers(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return EventModifiers.None;
            }

            return ParseModifiers(text.Split(new[] { '|', ',', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public static EventModifiers ParseModifiers(IEnumerable<string> names)
        {
            var result = EventModifiers.None;

            if (names == null)
            {
                return result;
            }

            foreach (var raw in names)
            {
                switch ((raw ?? string.Empty).Trim().ToLowerInvariant())
                {
                    case "alt":
                    case "option":
                        result |= EventModifiers.Alt;
                        break;
                    case "ctrl":
                    case "control":
                        result |= EventModifiers.Control;
                        break;
                    case "shift":
                        result |= EventModifiers.Shift;
                        break;
                    case "cmd":
                    case "command":
                    case "meta":
                        result |= EventModifiers.Command;
                        break;
                    case "":
                        break;
                    default:
                        throw new McpToolException(
                            "invalid_params",
                            $"Unknown modifier '{raw}'. Use alt, ctrl, shift or cmd.");
                }
            }

            return result;
        }

        /// <exception cref="McpToolException"><c>invalid_recording</c> when present but not an [x, y] of numbers.</exception>
        internal static Vector2 ReadVector(JToken token, string key)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return Vector2.zero;
            }

            if (token is JArray array && array.Count >= 2 && IsNumber(array[0]) && IsNumber(array[1]))
            {
                return new Vector2(array[0].Value<float>(), array[1].Value<float>());
            }

            throw Malformed(key, token, "[x, y] with numeric x and y");
        }

        private static int ReadInt(JObject json, string key)
        {
            var token = json[key];

            if (token == null || token.Type == JTokenType.Null)
            {
                return 0;
            }

            if (token.Type == JTokenType.Integer)
            {
                try
                {
                    return token.Value<int>();
                }
                catch (OverflowException)
                {
                    // Reported below; an out-of-range literal still parses as an Integer token.
                }
            }

            throw Malformed(key, token, "a 32-bit integer");
        }

        private static double ReadNumber(JObject json, string key)
        {
            var token = json[key];

            if (token == null || token.Type == JTokenType.Null)
            {
                return 0;
            }

            if (IsNumber(token))
            {
                return token.Value<double>();
            }

            throw Malformed(key, token, "a number");
        }

        private static bool IsNumber(JToken token) => token.Type == JTokenType.Integer || token.Type == JTokenType.Float;

        private static McpToolException Malformed(string key, JToken token, string expected)
        {
            return new McpToolException(
                "invalid_recording",
                $"Event field '{key}' is {token.ToString(Newtonsoft.Json.Formatting.None)}; expected {expected}.");
        }

        private static double Round(float value) => Math.Round(value, 3);
    }
}
