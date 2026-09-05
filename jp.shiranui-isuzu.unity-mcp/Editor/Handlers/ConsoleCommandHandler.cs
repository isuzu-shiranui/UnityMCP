using System;
using System.Collections.Generic;
using System.Reflection;

using Newtonsoft.Json.Linq;
using UnityEngine;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Handlers
{
    /// <summary>
    /// Command handler for accessing and managing Unity Console logs.
    /// </summary>
    internal sealed class ConsoleCommandHandler
    {
        // Cached reflection methods for LogEntries
        private static readonly Type LogEntriesType;
        private static readonly MethodInfo StartGettingEntriesMethod;
        private static readonly MethodInfo EndGettingEntriesMethod;
        private static readonly MethodInfo GetCountMethod;
        private static readonly MethodInfo GetCountsByTypeMethod;
        private static readonly MethodInfo ClearMethod;
        private static readonly MethodInfo SetFilteringTextMethod;
        private static readonly MethodInfo GetFilteringTextMethod;
        private static readonly MethodInfo GetEntryInternalMethod;
        private static readonly Type LogEntryType;

        static ConsoleCommandHandler()
        {
            try
            {
                // Get the LogEntries type from UnityEditor assembly
                LogEntriesType = typeof(UnityEditor.EditorWindow).Assembly.GetType("UnityEditor.LogEntries");
                if (LogEntriesType == null)
                {
                    Debug.LogError("[ConsoleCommandHandler] Failed to find LogEntries type via reflection");
                    return;
                }

                // Cache method info for all methods we need
                StartGettingEntriesMethod = LogEntriesType.GetMethod("StartGettingEntries", BindingFlags.Public | BindingFlags.Static);
                EndGettingEntriesMethod = LogEntriesType.GetMethod("EndGettingEntries", BindingFlags.Public | BindingFlags.Static);
                GetCountMethod = LogEntriesType.GetMethod("GetCount", BindingFlags.Public | BindingFlags.Static);
                GetCountsByTypeMethod = LogEntriesType.GetMethod("GetCountsByType", BindingFlags.Public | BindingFlags.Static);
                ClearMethod = LogEntriesType.GetMethod("Clear", BindingFlags.Public | BindingFlags.Static);
                SetFilteringTextMethod = LogEntriesType.GetMethod("SetFilteringText", BindingFlags.Public | BindingFlags.Static);
                GetFilteringTextMethod = LogEntriesType.GetMethod("GetFilteringText", BindingFlags.Public | BindingFlags.Static);
                GetEntryInternalMethod = LogEntriesType.GetMethod("GetEntryInternal", BindingFlags.Public | BindingFlags.Static);

                // Get LogEntry type
                LogEntryType = typeof(UnityEditor.EditorWindow).Assembly.GetType("UnityEditor.LogEntry");
                if (LogEntryType == null)
                {
                    Debug.LogError("[ConsoleCommandHandler] Failed to find LogEntry type via reflection");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ConsoleCommandHandler] Error initializing LogEntries reflection: {ex.Message}");
            }
        }

        /// <summary>
        /// Executes the command with the given parameters.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        /// <param name="parameters">The parameters for the command.</param>
        /// <returns>A JSON object containing the execution result.</returns>
        public JObject Execute(string action, JObject parameters)
        {
            // Check if reflection init succeeded
            if (LogEntriesType == null || LogEntryType == null)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = "LogEntries reflection initialization failed"
                };
            }

            return action.ToLower() switch
            {
                "getlogs" => this.GetLogs(parameters),
                "getcount" => this.GetLogCount(),
                "clear" => this.ClearLogs(),
                "setfilter" => this.SetFilter(parameters),
                _ => new JObject { ["success"] = false, ["error"] = $"Unknown action: {action}. Supported actions: getLogs, getCount, clear, setFilter" }
            };
        }

        /// <summary>
        /// Gets logs from the Unity Console using reflection.
        /// </summary>
        /// <param name="parameters">Optional parameters for filtering logs.</param>
        /// <returns>A JSON object containing the logs.</returns>
        private JObject GetLogs(JObject parameters)
        {
            try
            {
                var startRow = parameters["startRow"]?.Value<int>() ?? 0;
                var count = parameters["count"]?.Value<int>() ?? 100;

                // Ensure we're not exceeding the available logs
                var totalCount = (int)GetCountMethod.Invoke(null, null);
                count = Math.Min(count, totalCount - startRow);

                if (count <= 0 || startRow >= totalCount)
                {
                    var noneVisible = new JObject
                    {
                        ["success"] = true,
                        ["logs"] = new JArray(),
                        ["totalCount"] = totalCount
                    };

                    var withheld = HiddenByTheConsoleWindow(totalCount);
                    if (withheld > 0)
                    {
                        noneVisible["hiddenByConsoleFilter"] = withheld;
                        noneVisible["note"] = FilteringNotice(withheld);
                    }

                    return noneVisible;
                }

                // Begin getting entries
                StartGettingEntriesMethod.Invoke(null, null);

                try
                {
                    var logs = new JArray();

                    for (var i = 0; i < count; i++)
                    {
                        var currentRow = startRow + i;
                        if (currentRow >= totalCount)
                            break;

                        // Create a LogEntry instance via reflection
                        var logEntry = Activator.CreateInstance(LogEntryType);

                        // Invoke GetEntryInternal
                        var success = (bool)GetEntryInternalMethod.Invoke(null, new object[] { currentRow, logEntry });

                        if (!success) continue;
                        // Extract properties via reflection
                        var mode = (int)LogEntryType.GetField("mode").GetValue(logEntry);
                        var message = (string)LogEntryType.GetField("message").GetValue(logEntry);
                        var file = (string)LogEntryType.GetField("file").GetValue(logEntry);
                        var line = (int)LogEntryType.GetField("line").GetValue(logEntry);
                        var column = (int)LogEntryType.GetField("column").GetValue(logEntry);
                        var instanceID = (int)LogEntryType.GetField("instanceID").GetValue(logEntry);

                        logs.Add(new JObject
                        {
                            ["mode"] = mode,
                            ["message"] = message,
                            ["file"] = file,
                            ["line"] = line,
                            ["column"] = column,
                            ["instanceID"] = instanceID
                        });
                    }

                    // Get filter text
                    var filterText = (string)GetFilteringTextMethod.Invoke(null, null);

                    return new JObject
                    {
                        ["success"] = true,
                        ["logs"] = logs,
                        ["totalCount"] = totalCount,
                        ["filter"] = filterText
                    };
                }
                finally
                {
                    // Always end getting entries to clean up
                    EndGettingEntriesMethod.Invoke(null, null);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ConsoleCommandHandler] Error getting logs: {ex.Message}");
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = ex.Message
                };
            }
        }

        /// <summary>
        /// Gets the count of logs by type using reflection.
        /// </summary>
        /// <returns>A JSON object containing the counts.</returns>
        private JObject GetLogCount()
        {
            try
            {
                var errorCount = 0;
                var warningCount = 0;
                var logCount = 0;

                // Using ref parameters via reflection
                var parameters = new object[] { errorCount, warningCount, logCount };
                GetCountsByTypeMethod.Invoke(null, parameters);

                // Extract the output values
                errorCount = (int)parameters[0];
                warningCount = (int)parameters[1];
                logCount = (int)parameters[2];

                var visible = (int)GetCountMethod.Invoke(null, null);

                var result = new JObject
                {
                    ["success"] = true,
                    ["totalCount"] = visible,
                    ["errorCount"] = errorCount,
                    ["warningCount"] = warningCount,
                    ["logCount"] = logCount
                };

                var hidden = HiddenByTheConsoleWindow(visible);
                if (hidden > 0)
                {
                    result["hiddenByConsoleFilter"] = hidden;
                    result["note"] = FilteringNotice(hidden);
                }

                return result;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ConsoleCommandHandler] Error getting log counts: {ex.Message}");
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = ex.Message
                };
            }
        }

        /// <summary>
        /// Clears all logs from the console using reflection.
        /// </summary>
        /// <returns>A JSON object indicating success or failure.</returns>
        private JObject ClearLogs()
        {
            try
            {
                ClearMethod.Invoke(null, null);
                return new JObject
                {
                    ["success"] = true
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ConsoleCommandHandler] Error clearing logs: {ex.Message}");
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = ex.Message
                };
            }
        }

/// <summary>
        /// How many entries the Console window is hiding right now, or zero.
        /// </summary>
        /// <remarks>
        /// GetCount answers with the window's own filter applied: its Error, Warning and Log
        /// toggles and the text in its search box. GetCountsByType ignores both. A reader that
        /// only calls GetCount is told a smaller number with nothing to say why, and a search
        /// box left with text in it reports zero entries while the Console holds hundreds.
        /// </remarks>
        private static int HiddenByTheConsoleWindow(int visible)
        {
            try
            {
                var counts = new object[] { 0, 0, 0 };
                GetCountsByTypeMethod.Invoke(null, counts);
                var everything = (int)counts[0] + (int)counts[1] + (int)counts[2];

                return everything > visible ? everything - visible : 0;
            }
            catch (Exception)
            {
                // Reporting no filtering is the same answer this gave before the check existed.
                return 0;
            }
        }

        /// <summary>The sentence a caller needs to see when rows are being withheld.</summary>
        private static string FilteringNotice(int hidden)
        {
            return $"The Console window is filtering: {hidden} more entries exist than are " +
                   "reported here. Its Error, Warning and Log toggles and its search box apply " +
                   "to this call. Turn the toggles on and clear the search box to see them all.";
        }

        /// <summary>
        /// Sets a filter on the console logs using reflection.
        /// </summary>
        /// <param name="parameters">Parameters containing the filter text.</param>
        /// <returns>A JSON object indicating success or failure.</returns>
        private JObject SetFilter(JObject parameters)
        {
            try
            {
                var filterText = parameters["filter"]?.ToString() ?? string.Empty;
                SetFilteringTextMethod.Invoke(null, new object[] { filterText });

                return new JObject
                {
                    ["success"] = true,
                    ["filter"] = filterText
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ConsoleCommandHandler] Error setting filter: {ex.Message}");
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = ex.Message
                };
            }
        }
    }
}
