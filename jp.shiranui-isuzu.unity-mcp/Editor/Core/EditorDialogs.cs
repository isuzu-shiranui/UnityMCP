using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

using Newtonsoft.Json.Linq;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Finds the modal dialogs the Editor is showing and presses their buttons, from a worker
    /// thread and without any Unity API.
    /// </summary>
    /// <remarks>
    /// A modal dialog runs its own message loop on the main thread, so nothing queued for that
    /// thread runs until it is answered. The loop does pump window messages, which is what lets a
    /// click sent from another thread close it. Windows only: the dialogs are top-level windows
    /// of class <c>#32770</c> belonging to this process, their body is a <c>Static</c> or a
    /// read-only <c>Edit</c> child (Unity's own dialogs use the latter, with an empty
    /// <c>Static</c> for the icon), and their buttons are <c>Button</c> children whose text
    /// may keep the accelerator ampersand (<c>&amp;Save</c>).
    /// <para>
    /// Text is read with <c>SendMessageTimeout</c> rather than <c>GetWindowText</c>. For a window
    /// of the calling process the latter sends <c>WM_GETTEXT</c> and waits for the owning thread
    /// without limit, so a main thread that is busy but not in a dialog would take the worker
    /// down with it, and with it the health endpoint this exists to keep answering.
    /// </para>
    /// </remarks>
    internal static class EditorDialogs
    {
        private const string DialogClass = "#32770";
        private static readonly string[] BodyClasses = { "Static", "Edit" };
        private const uint WmGetText = 0x000D;
        private const uint WmGetTextLength = 0x000E;
        private const uint BmClick = 0x00F5;
        private const uint SmtoAbortIfHung = 0x0002;
        private const uint ReadTimeoutMs = 300;
        private const uint ClickTimeoutMs = 3000;

        /// <summary>True on Windows, where the dialogs can be enumerated.</summary>
        public static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>
        /// Every visible dialog of this process, front-most first. Empty when unsupported.
        /// </summary>
        public static DialogInfo[] List()
        {
            if (!IsSupported)
            {
                return Array.Empty<DialogInfo>();
            }

            var handles = new List<IntPtr>();
            var pid = (uint)Process.GetCurrentProcess().Id;

            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd))
                {
                    return true;
                }

                GetWindowThreadProcessId(hwnd, out var owner);
                if (owner == pid && ClassNameOf(hwnd) == DialogClass)
                {
                    handles.Add(hwnd);
                }

                return true;
            }, IntPtr.Zero);

            var dialogs = new List<DialogInfo>(handles.Count);
            foreach (var hwnd in handles)
            {
                dialogs.Add(Describe(hwnd));
            }

            return dialogs.ToArray();
        }

        /// <summary>
        /// Clicks the button whose text matches <paramref name="buttonText"/> on the dialog
        /// <paramref name="handle"/> names. False when the dialog is gone or has no such button.
        /// </summary>
        public static bool Press(string handle, string buttonText)
        {
            if (!IsSupported || !TryParseHandle(handle, out var hwnd) || !IsWindow(hwnd))
            {
                return false;
            }

            foreach (var button in Children(hwnd, "Button"))
            {
                if (ButtonMatches(ReadText(button), buttonText))
                {
                    SendMessageTimeout(button, BmClick, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, ClickTimeoutMs, out _);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a button's window text names the button the caller asked for. The accelerator
        /// ampersand is ignored on both sides, as is case.
        /// </summary>
        public static bool ButtonMatches(string windowText, string requested)
        {
            if (string.IsNullOrEmpty(windowText) || string.IsNullOrEmpty(requested))
            {
                return false;
            }

            return string.Equals(DisplayText(windowText), DisplayText(requested), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Button text as the user sees it: the accelerator ampersand removed.</summary>
        public static string DisplayText(string windowText)
        {
            return (windowText ?? string.Empty).Replace("&", string.Empty).Trim();
        }

        private static DialogInfo Describe(IntPtr hwnd)
        {
            var message = new StringBuilder();
            foreach (var className in BodyClasses)
            {
                foreach (var label in Children(hwnd, className))
                {
                    var text = ReadText(label).Trim();
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    if (message.Length > 0)
                    {
                        message.Append(' ');
                    }

                    message.Append(text);
                }
            }

            var buttons = new List<string>();
            foreach (var button in Children(hwnd, "Button"))
            {
                var text = DisplayText(ReadText(button));
                if (text.Length > 0)
                {
                    buttons.Add(text);
                }
            }

            return new DialogInfo
            {
                Handle = hwnd.ToInt64().ToString(),
                Title = ReadText(hwnd),
                Message = message.ToString(),
                Buttons = buttons.ToArray(),
            };
        }

        private static IEnumerable<IntPtr> Children(IntPtr parent, string className)
        {
            var child = IntPtr.Zero;
            while ((child = FindWindowEx(parent, child, className, null)) != IntPtr.Zero)
            {
                yield return child;
            }
        }

        private static string ReadText(IntPtr hwnd)
        {
            if (SendMessageTimeout(hwnd, WmGetTextLength, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, ReadTimeoutMs, out var length) == IntPtr.Zero)
            {
                return string.Empty;
            }

            var capacity = (int)length.ToInt64();
            if (capacity <= 0)
            {
                return string.Empty;
            }

            var buffer = new StringBuilder(capacity + 1);
            if (SendMessageTimeout(hwnd, WmGetText, (IntPtr)(capacity + 1), buffer, SmtoAbortIfHung, ReadTimeoutMs, out _) == IntPtr.Zero)
            {
                return string.Empty;
            }

            return buffer.ToString();
        }

        private static string ClassNameOf(IntPtr hwnd)
        {
            var buffer = new StringBuilder(64);
            var length = GetClassName(hwnd, buffer, buffer.Capacity);
            return length > 0 ? buffer.ToString(0, length) : string.Empty;
        }

        private static bool TryParseHandle(string handle, out IntPtr hwnd)
        {
            hwnd = IntPtr.Zero;
            if (!long.TryParse(handle, out var value) || value == 0)
            {
                return false;
            }

            hwnd = new IntPtr(value);
            return true;
        }

        /// <summary>One visible dialog as reported to callers.</summary>
        internal sealed class DialogInfo
        {
            /// <summary>The window handle as a decimal string; the argument <see cref="Press"/> takes.</summary>
            public string Handle { get; set; }

            public string Title { get; set; }

            /// <summary>The body text, empty when the dialog has none that can be read.</summary>
            public string Message { get; set; }

            /// <summary>Button texts as displayed, without the accelerator ampersand.</summary>
            public string[] Buttons { get; set; }

            public JObject ToJson()
            {
                return new JObject
                {
                    ["handle"] = this.Handle,
                    ["title"] = this.Title,
                    ["message"] = this.Message,
                    ["buttons"] = new JArray(this.Buttons ?? Array.Empty<string>()),
                };
            }
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int capacity);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowText);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, StringBuilder lParam, uint flags, uint timeoutMs, out IntPtr result);
    }
}
