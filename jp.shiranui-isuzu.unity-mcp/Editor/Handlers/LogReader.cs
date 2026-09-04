using System;
using System.Collections.Generic;
using System.Reflection;

using Newtonsoft.Json.Linq;
using UnityEngine;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Handlers
{
    internal static class LogReader
    {
        private static readonly Type LogEntriesType;
        private static readonly Type LogEntryType;
        private static readonly MethodInfo StartGettingEntriesMethod;
        private static readonly MethodInfo EndGettingEntriesMethod;
        private static readonly MethodInfo GetCountMethod;
        private static readonly MethodInfo GetCountsByTypeMethod;
        private static readonly MethodInfo GetEntryInternalMethod;
        private static readonly FieldInfo ModeField;
        private static readonly FieldInfo MessageField;
        private static readonly FieldInfo FileField;
        private static readonly FieldInfo LineField;

        static LogReader()
        {
            try
            {
                var asm = typeof(UnityEditor.EditorWindow).Assembly;
                LogEntriesType = asm.GetType("UnityEditor.LogEntries");
                LogEntryType = asm.GetType("UnityEditor.LogEntry");

                if (LogEntriesType == null || LogEntryType == null)
                {
                    Debug.LogError("[SimpleUnityMCP] Failed to find LogEntries/LogEntry types");
                    return;
                }

                var flags = BindingFlags.Public | BindingFlags.Static;
                StartGettingEntriesMethod = LogEntriesType.GetMethod("StartGettingEntries", flags);
                EndGettingEntriesMethod = LogEntriesType.GetMethod("EndGettingEntries", flags);
                GetCountMethod = LogEntriesType.GetMethod("GetCount", flags);
                GetCountsByTypeMethod = LogEntriesType.GetMethod("GetCountsByType", flags);
                GetEntryInternalMethod = LogEntriesType.GetMethod("GetEntryInternal", flags);

                ModeField = LogEntryType.GetField("mode");
                MessageField = LogEntryType.GetField("message");
                FileField = LogEntryType.GetField("file");
                LineField = LogEntryType.GetField("line");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SimpleUnityMCP] LogReader init error: {e.Message}");
            }
        }

        public static JObject ReadLogs(JObject parameters)
        {
            if (LogEntriesType == null || LogEntryType == null)
            {
                return new JObject { ["error"] = "LogEntries reflection not available" };
            }

            try
            {
                // "count" is accepted as an alias for limit.
                var limit = parameters["limit"]?.Value<int>()
                    ?? parameters["count"]?.Value<int>()
                    ?? 50;
                var offset = parameters["offset"]?.Value<int>() ?? 0;
                var typeFilter = parameters["type"]?.ToString() ?? "all";
                var fieldsParam = parameters["fields"]?.ToString();
                var fieldsFilter = ListResponseBuilder.ParseFieldsParam(fieldsParam);

                var totalCount = (int)GetCountMethod.Invoke(null, null);

                // Get counts by type
                var countParams = new object[] { 0, 0, 0 };
                GetCountsByTypeMethod.Invoke(null, countParams);
                var errorCount = (int)countParams[0];
                var warningCount = (int)countParams[1];
                var logCount = (int)countParams[2];

                StartGettingEntriesMethod.Invoke(null, null);
                try
                {
                    // Collect all matching entries (newest-first) into a flat list first
                    var allEntries = new List<JObject>();

                    for (var i = totalCount - 1; i >= 0; i--)
                    {
                        var entry = Activator.CreateInstance(LogEntryType);
                        var success = (bool)GetEntryInternalMethod.Invoke(null, new[] { i, entry });
                        if (!success) continue;

                        var mode = (int)ModeField.GetValue(entry);
                        var typeChar = GetTypeChar(mode);

                        if (typeFilter != "all")
                        {
                            if (typeFilter == "error" && typeChar != "E") continue;
                            if (typeFilter == "warning" && typeChar != "W") continue;
                            if (typeFilter == "log" && typeChar != "L") continue;
                        }

                        var message = (string)MessageField.GetValue(entry) ?? "";
                        var file = (string)FileField.GetValue(entry) ?? "";
                        var line = (int)LineField.GetValue(entry);

                        if (message.Length > 500)
                            message = message.Substring(0, 500) + "...";

                        allEntries.Add(new JObject
                        {
                            ["t"] = typeChar,
                            ["m"] = message,
                            ["f"] = file,
                            ["l"] = line
                        });
                    }

                    var page = ListResponseBuilder.Build(
                        allEntries,
                        offset,
                        limit,
                        item => item,
                        fieldsFilter);

                    return new JObject
                    {
                        ["logs"] = page["items"],
                        ["total"] = totalCount,
                        ["errors"] = errorCount,
                        ["warnings"] = warningCount,
                        ["truncated"] = page["truncated"],
                        ["next"] = page["next"]
                    };
                }
                finally
                {
                    EndGettingEntriesMethod.Invoke(null, null);
                }
            }
            catch (Exception e)
            {
                return new JObject { ["error"] = $"Failed to read logs: {e.Message}" };
            }
        }

        // LogEntry.mode flags from the Editor's LogMessageFlags. A Debug.LogError sets
        // kScriptingError, not kError, and a compiler error sets kScriptCompileError, so testing
        // bit 0 alone classifies almost everything as a plain log while the count API, which
        // knows the full set, still reports errors.
        private const int ErrorFlags =
            1           // kError
            | 2         // kAssert
            | 16        // kFatal
            | 64        // kAssetImportError
            | 256       // kScriptingError
            | 2048      // kScriptCompileError
            | 8192      // kStickyError
            | 131072    // kScriptingException
            | 1048576   // kGraphCompileError
            | 2097152;  // kScriptingAssertion

        private const int WarningFlags =
            128         // kAssetImportWarning
            | 512       // kScriptingWarning
            | 4096;     // kScriptCompileWarning

        internal static string GetTypeChar(int mode)
        {
            if ((mode & ErrorFlags) != 0) return "E";
            if ((mode & WarningFlags) != 0) return "W";
            return "L";
        }
    }
}
