using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using UnityEngine;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// A recording of Editor input: a header describing the window it was taken from and the
    /// events, in the order they arrived.
    /// </summary>
    /// <remarks>
    /// The file is a single JSON object so that the header can be checked before any event is
    /// sent: a replay into the wrong window type, or of a file written by a later format, is
    /// refused before the first click lands.
    /// </remarks>
    internal sealed class InputRecording
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;

        public string Unity { get; set; }

        /// <summary>The view name the recording was taken from, in <c>EditorWindowLocator</c>'s vocabulary.</summary>
        public string View { get; set; }

        public string WindowType { get; set; }

        public Vector2 WindowSize { get; set; }

        public Vector2 ContentOffset { get; set; }

        public string CreatedUtc { get; set; }

        public List<InputEventRecord> Events { get; } = new();

        public int FrameCount
        {
            get
            {
                var max = -1;
                foreach (var e in this.Events)
                {
                    if (e.Frame > max) max = e.Frame;
                }

                return max + 1;
            }
        }

        public double DurationMs
        {
            get
            {
                var max = 0d;
                foreach (var e in this.Events)
                {
                    if (e.Time > max) max = e.Time;
                }

                return max;
            }
        }

        public JObject ToJson()
        {
            var events = new JArray();
            foreach (var e in this.Events)
            {
                events.Add(e.ToJson());
            }

            return new JObject
            {
                ["version"] = this.Version,
                ["unity"] = this.Unity,
                ["view"] = this.View,
                ["windowType"] = this.WindowType,
                ["windowSize"] = new JArray(this.WindowSize.x, this.WindowSize.y),
                ["contentOffset"] = new JArray(this.ContentOffset.x, this.ContentOffset.y),
                ["createdUtc"] = this.CreatedUtc,
                ["events"] = events,
            };
        }

        /// <exception cref="McpToolException"><c>invalid_recording</c> for a version this build cannot read or a malformed body.</exception>
        public static InputRecording FromJson(JObject json)
        {
            if (json == null)
            {
                throw new McpToolException("invalid_recording", "The recording is not a JSON object.");
            }

            var version = json["version"]?.Type == JTokenType.Integer ? json["version"].Value<int>() : -1;
            if (version != CurrentVersion)
            {
                throw new McpToolException(
                    "invalid_recording",
                    $"Recording version {json["version"]?.ToString() ?? "(missing)"} is not supported; this build reads version {CurrentVersion}.");
            }

            var recording = new InputRecording
            {
                Version = version,
                Unity = json["unity"]?.ToString(),
                View = json["view"]?.ToString(),
                WindowType = json["windowType"]?.ToString(),
                WindowSize = ReadVector(json["windowSize"]),
                ContentOffset = ReadVector(json["contentOffset"]),
                CreatedUtc = ReadTimestamp(json["createdUtc"]),
            };

            if (json["events"] is not JArray events)
            {
                throw new McpToolException("invalid_recording", "The recording has no 'events' array.");
            }

            foreach (var token in events)
            {
                recording.Events.Add(InputEventRecord.FromJson(token as JObject));
            }

            return recording;
        }

        private static Vector2 ReadVector(JToken token) => InputEventRecord.ReadVector(token, token?.Path ?? "vector");

        /// <summary>
        /// Newtonsoft parses an ISO 8601 string into a Date token whose <c>ToString()</c> follows
        /// the machine's culture, so the round trip is pinned back to the invariant form.
        /// </summary>
        private static string ReadTimestamp(JToken token)
        {
            switch (token)
            {
                case null:
                    return null;
                case JValue { Value: DateTime dateTime }:
                    return dateTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
                case JValue { Value: DateTimeOffset offset }:
                    return offset.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
                default:
                    return token.ToString();
            }
        }
    }

    /// <summary>Where recordings live and how they get to and from disk.</summary>
    internal static class InputRecordingFile
    {
        private static readonly Regex Unsafe = new("[^A-Za-z0-9_-]+", RegexOptions.Compiled);

        /// <summary>
        /// Windows device names, which stay reserved with any extension appended: a write to
        /// <c>CON.json</c> goes to the console and one to <c>COM1.json</c> can block.
        /// </summary>
        private static readonly Regex ReservedDeviceName = new(
            "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Directory for the given project's recordings, next to its tokens and tool definitions.</summary>
        public static string DirectoryFor(string projectPath)
        {
            return Path.Combine(
                McpInstanceDescriptor.StateRoot,
                "recordings",
                McpInstanceDescriptor.HashProjectPath(projectPath));
        }

        public static string PathFor(string projectPath, string name)
        {
            return Path.Combine(DirectoryFor(projectPath), NormalizeName(name) + ".json");
        }

        /// <summary>
        /// Reduces a caller-supplied name to <c>[A-Za-z0-9_-]</c>. The name becomes a file name
        /// under the state root, so a path separator or a parent reference in it would write
        /// outside the recordings directory.
        /// </summary>
        public static string NormalizeName(string name)
        {
            var trimmed = (name ?? string.Empty).Trim();
            var safe = Unsafe.Replace(trimmed, "_").Trim('_');

            if (safe.Length == 0)
            {
                throw new McpToolException("invalid_params", "A recording needs a name made of letters, digits, '_' or '-'.");
            }

            if (ReservedDeviceName.IsMatch(safe))
            {
                throw new McpToolException("invalid_params", $"'{safe}' is a reserved device name on Windows and cannot name a recording.");
            }

            return safe;
        }

        /// <summary>
        /// The events worth keeping: Layout and Repaint carry no input, and MouseMove is dropped
        /// unless asked for, because a hover trail is most of a recording's bytes and almost
        /// never what a replay is meant to reproduce.
        /// </summary>
        public static List<InputEventRecord> Filter(IEnumerable<InputEventRecord> events, bool includeMoves)
        {
            var kept = new List<InputEventRecord>();

            foreach (var e in events)
            {
                if (!e.IsInput)
                {
                    continue;
                }

                if (e.IsMove && !includeMoves)
                {
                    continue;
                }

                kept.Add(e);
            }

            return kept;
        }

        public static string Write(string path, InputRecording recording)
        {
            var full = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(full);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(full, recording.ToJson().ToString(Formatting.Indented), new UTF8Encoding(false));
            return full;
        }

        /// <exception cref="McpToolException"><c>recording_not_found</c> when the file is missing, <c>invalid_recording</c> when it does not parse.</exception>
        public static InputRecording Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new McpToolException("recording_not_found", $"No recording at '{path}'.", 404);
            }

            JObject json;
            try
            {
                json = JObject.Parse(File.ReadAllText(path));
            }
            catch (JsonException e)
            {
                throw new McpToolException("invalid_recording", $"{path}: {e.Message}");
            }

            return InputRecording.FromJson(json);
        }
    }
}
