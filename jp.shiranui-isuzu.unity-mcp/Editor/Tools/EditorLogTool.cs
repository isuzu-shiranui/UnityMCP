using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

using UnityEditor;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Reads Unity's <c>Editor.log</c> straight from disk.
    /// </summary>
    /// <remarks>
    /// This is the first tool declared with <see cref="McpToolAttribute"/> and it is
    /// deliberately <c>MainThread = false</c>. The console-backed <c>/read_logs</c> endpoint
    /// has to marshal onto the main thread, so it reports nothing at exactly the moment it is
    /// most needed — while the Editor is wedged importing, compiling, or stuck behind a modal
    /// "Hold on" dialog. Reading the file needs no Unity API at all, so this keeps answering.
    /// <para>
    /// It also sees entries the in-memory console has dropped or not yet received, which is
    /// why the log file is the authoritative source when the two disagree.
    /// </para>
    /// </remarks>
    internal static class EditorLogTool
    {
        /// <summary>Largest tail slice read from the end of the file.</summary>
        private const int MaxTailBytes = 1024 * 1024;

        /// <summary>Upper bound on returned lines, regardless of what the caller asks for.</summary>
        private const int MaxLines = 2000;

        /// <summary>Upper bound on returned characters, so one enormous line cannot flood the context.</summary>
        private const int MaxCharacters = 200_000;

        /// <summary>
        /// Captured on the main thread at load because <c>Application.consoleLogPath</c> is a
        /// Unity API and the tool itself runs off-thread by design.
        /// </summary>
        private static string logPath;

        [InitializeOnLoadMethod]
        private static void CaptureLogPath()
        {
            logPath = Application.consoleLogPath;
        }

        [McpTool(
            "editor_log_tail",
            "Read the tail of Unity's Editor.log directly from disk, optionally filtered by a regex. " +
            "Works while the Editor is busy importing, compiling, or showing a modal dialog, when the " +
            "in-memory console cannot be queried. Prefer this over console log tools when you suspect " +
            "the Editor is stuck, or when the console reports zero entries but you expect output.",
            Idempotency = McpIdempotency.Safe,
            MainThread = false)]
        public static LogTailResult Tail(
            [McpArg("lines", "Maximum number of lines to return, newest last. Capped at 2000.")]
            int lines = 200,
            [McpArg("pattern", "Optional .NET regex; only matching lines are returned.")]
            string pattern = null,
            [McpArg("ignore_case", "Match the pattern case-insensitively.")]
            bool ignoreCase = true)
        {
            var path = logPath;

            if (string.IsNullOrEmpty(path))
            {
                throw new McpToolException(
                    "log_path_unknown",
                    "The Editor log path has not been captured yet. Reload the domain and retry.",
                    503);
            }

            if (!File.Exists(path))
            {
                throw new McpToolException("log_not_found", $"Editor log not found at '{path}'.", 404);
            }

            Regex regex = null;
            if (!string.IsNullOrEmpty(pattern))
            {
                try
                {
                    regex = new Regex(pattern, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
                }
                catch (ArgumentException e)
                {
                    throw new McpToolException("invalid_params", $"'{pattern}' is not a valid regex: {e.Message}");
                }
            }

            var requested = Math.Max(1, Math.Min(lines, MaxLines));
            var text = ReadTail(path, out var totalBytes, out var readBytes);

            var all = text.Split('\n');
            var matched = new List<string>();

            // The first element is usually a partial line left over from the byte-offset read.
            var start = readBytes < totalBytes ? 1 : 0;

            for (var i = start; i < all.Length; i++)
            {
                var line = all[i].TrimEnd('\r');
                if (line.Length == 0)
                {
                    continue;
                }

                if (regex == null || regex.IsMatch(line))
                {
                    matched.Add(line);
                }
            }

            var truncated = matched.Count > requested;
            var selected = truncated ? matched.GetRange(matched.Count - requested, requested) : matched;

            var characters = 0;
            var kept = new List<string>(selected.Count);

            // Walk backwards so the newest lines survive the character budget.
            for (var i = selected.Count - 1; i >= 0; i--)
            {
                characters += selected[i].Length + 1;
                if (characters > MaxCharacters)
                {
                    truncated = true;
                    break;
                }

                kept.Add(selected[i]);
            }

            kept.Reverse();

            return new LogTailResult
            {
                Path = path,
                FileBytes = totalBytes,
                ScannedBytes = readBytes,
                Matched = matched.Count,
                Returned = kept.Count,
                Truncated = truncated,
                Lines = kept,
            };
        }

        /// <summary>
        /// Reads the last <see cref="MaxTailBytes"/> of the file.
        /// </summary>
        /// <remarks>
        /// <c>FileShare.ReadWrite</c> is required: Unity holds the log open for writing, and
        /// anything stricter fails with a sharing violation.
        /// </remarks>
        private static string ReadTail(string path, out long totalBytes, out int readBytes)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            totalBytes = stream.Length;
            readBytes = (int)Math.Min(totalBytes, MaxTailBytes);

            stream.Seek(-readBytes, SeekOrigin.End);

            var buffer = new byte[readBytes];
            var offset = 0;
            while (offset < readBytes)
            {
                var read = stream.Read(buffer, offset, readBytes - offset);
                if (read <= 0)
                {
                    break;
                }

                offset += read;
            }

            return new UTF8Encoding(false).GetString(buffer, 0, offset);
        }

        /// <summary>Result of <see cref="Tail"/>; serialized to JSON by the invoker.</summary>
        internal sealed class LogTailResult
        {
            public string Path { get; set; }

            public long FileBytes { get; set; }

            public int ScannedBytes { get; set; }

            /// <summary>How many lines matched before the line and character caps were applied.</summary>
            public int Matched { get; set; }

            public int Returned { get; set; }

            /// <summary>True when lines were dropped, so the caller knows not to treat this as complete.</summary>
            public bool Truncated { get; set; }

            public List<string> Lines { get; set; }
        }
    }
}
