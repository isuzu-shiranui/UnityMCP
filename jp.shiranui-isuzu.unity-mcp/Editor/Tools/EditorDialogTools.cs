using System;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Shows and answers the modal dialogs that hold the Editor main thread.
    /// </summary>
    /// <remarks>
    /// Both tools run off the main thread on purpose: while a dialog is up nothing queued for
    /// that thread runs, so a tool that needed it could never report the dialog that blocks it.
    /// </remarks>
    internal static class EditorDialogTools
    {
        [McpTool(
            "editor_dialog_list",
            "List the modal dialogs the Unity Editor is currently showing (title, message, buttons), plus how long " +
            "the main thread has been unresponsive. Use this when a call stays running, when /health reports a stalled " +
            "main thread, or when a job never completes: a dialog such as \"Scene(s) Have Been Modified\" or an import " +
            "prompt blocks every main-thread tool until someone answers it. Works while the Editor is blocked. " +
            "Windows only; elsewhere supported is false and dialogs is empty.",
            Idempotency = McpIdempotency.Safe,
            MainThread = false,
            Group = McpToolGroups.Diagnostics)]
        public static JObject List()
        {
            return new JObject
            {
                ["supported"] = EditorDialogs.IsSupported,
                ["dialogs"] = new JArray(EditorDialogs.List().Select(d => (object)d.ToJson()).ToArray()),
                ["stalledMs"] = StalledMs(),
            };
        }

        [McpTool(
            "editor_dialog_press",
            "Press a button on a modal dialog the Unity Editor is showing, which unblocks the main thread and lets the " +
            "waiting job finish. Read the dialog with editor_dialog_list first and pick the button from its message: " +
            "buttons like \"Don't Save\", \"Discard\" or \"Yes\" can throw away unsaved work, and \"Cancel\" usually " +
            "aborts the operation that opened the dialog. Prefer \"Cancel\" when unsure, then fix the cause (for example " +
            "save the scene with scene_save) and repeat the original call. The button is matched by its visible text, " +
            "case-insensitively. Windows only.",
            MainThread = false,
            Destructive = true,
            Group = McpToolGroups.Diagnostics)]
        public static JObject Press(
            [McpArg("button", "Visible text of the button to press, for example \"Cancel\" or \"Don't Save\".")]
            string button,
            [McpArg("title", "When several dialogs are open, the one whose title contains this text. Otherwise the front-most dialog is used.")]
            string title = null)
        {
            if (string.IsNullOrWhiteSpace(button))
            {
                throw new McpToolException("invalid_params", "'button' is required.");
            }

            if (!EditorDialogs.IsSupported)
            {
                throw new McpToolException(
                    "dialog_detection_unavailable",
                    "Dialogs can only be read and pressed on Windows. Answer the dialog in the Editor.",
                    501);
            }

            var dialogs = EditorDialogs.List();
            if (!string.IsNullOrEmpty(title))
            {
                dialogs = dialogs
                    .Where(d => (d.Title ?? string.Empty).IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToArray();
            }

            if (dialogs.Length == 0)
            {
                throw new McpToolException(
                    "dialog_not_found",
                    string.IsNullOrEmpty(title)
                        ? "The Editor is not showing a dialog. If a call is still running, the main thread is busy with something else; see editor_log_tail."
                        : $"No open dialog has a title containing '{title}'. editor_dialog_list shows the open ones.",
                    404);
            }

            var dialog = dialogs[0];

            if (!EditorDialogs.Press(dialog.Handle, button))
            {
                throw new McpToolException(
                    "button_not_found",
                    $"The dialog \"{dialog.Title}\" has no button '{button}'. Its buttons are: {string.Join(" / ", dialog.Buttons)}.",
                    404);
            }

            var pressed = dialog.Buttons.FirstOrDefault(b => EditorDialogs.ButtonMatches(b, button)) ?? EditorDialogs.DisplayText(button);

            return new JObject
            {
                ["pressed"] = true,
                ["dialog"] = dialog.ToJson(),
                ["button"] = pressed,
            };
        }

        private static long StalledMs()
        {
            return McpServiceManager.Instance.TryGetService<McpHttpServer>(out var server) ? server.MainThreadStalledMs : 0;
        }
    }
}
