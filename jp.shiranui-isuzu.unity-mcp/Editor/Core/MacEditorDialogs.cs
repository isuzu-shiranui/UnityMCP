using System;
using System.Runtime.InteropServices;
using System.Text;

using Newtonsoft.Json.Linq;

namespace UnityMCP.Editor.Core
{
    /// <summary>Bounded, in-process Cocoa modal inspection. No Accessibility grant is needed.</summary>
    internal static class MacEditorDialogs
    {
        private const string NativeLibrary = "UnityMcpDialogs";
        private const string HandlePrefix = "mac:";
        private const int MaxResponseBytes = 1024 * 1024;

        [DllImport(NativeLibrary)]
        private static extern IntPtr UnityMcpDialogsList();

        [DllImport(NativeLibrary)]
        private static extern IntPtr UnityMcpDialogsPress(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string handle,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string button);

        [DllImport(NativeLibrary)]
        private static extern void UnityMcpDialogsFree(IntPtr value);

        public static EditorDialogs.DialogInfo[] List(out string error)
        {
            var result = Invoke(UnityMcpDialogsList);
            error = (string)result["error"];
            return result["dialogs"]?.ToObject<EditorDialogs.DialogInfo[]>() ?? Array.Empty<EditorDialogs.DialogInfo>();
        }

        public static bool Press(string handle, string button)
        {
            if (string.IsNullOrEmpty(handle) || !handle.StartsWith(HandlePrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var result = Invoke(() => UnityMcpDialogsPress(handle, button));
            var error = (string)result["error"];
            if (error == "dialog_not_found" || error == "button_not_found")
            {
                return false;
            }

            if (error != null)
            {
                throw new McpToolException(error, Explanation(error), 503);
            }

            return (bool?)result["pressed"] == true;
        }

        internal static string Explanation(string code)
        {
            if (code == "dialog_inspection_busy")
                return "Another native dialog request is pending. No new action was queued; inspect again shortly.";
            if (code == "dialog_main_loop_unavailable")
                return "The macOS event loop did not answer in time. This is not proof that no dialog is open. Any queued press was cancelled; inspect again when the Editor responds.";
            if (code == "dialog_action_pending")
                return "The native operation started but did not finish before the timeout. Do not repeat the press: inspect the dialog and job state first.";
            if (code == "ambiguous_button") return "Several enabled buttons have that title. Answer the dialog manually.";
            if (code == "dialog_native_unavailable") return "The macOS dialog plugin could not load. Check that its universal Editor-only dylib is installed.";
            return "The native dialog could not be inspected safely. Answer it in the Editor.";
        }

        private static JObject Invoke(Func<IntPtr> call)
        {
            try
            {
                var ptr = call();
                if (ptr == IntPtr.Zero) return new JObject { ["error"] = "dialog_native_error" };
                try
                {
                    var length = 0;
                    while (length < MaxResponseBytes && Marshal.ReadByte(ptr, length) != 0)
                    {
                        ++length;
                    }

                    if (length == MaxResponseBytes)
                    {
                        return new JObject { ["error"] = "dialog_native_error" };
                    }

                    var bytes = new byte[length];
                    Marshal.Copy(ptr, bytes, 0, length);
                    return JObject.Parse(Encoding.UTF8.GetString(bytes));
                }
                finally { UnityMcpDialogsFree(ptr); }
            }
            catch (DllNotFoundException) { return new JObject { ["error"] = "dialog_native_unavailable" }; }
            catch (EntryPointNotFoundException) { return new JObject { ["error"] = "dialog_native_unavailable" }; }
            catch (BadImageFormatException) { return new JObject { ["error"] = "dialog_native_unavailable" }; }
        }
    }
}
