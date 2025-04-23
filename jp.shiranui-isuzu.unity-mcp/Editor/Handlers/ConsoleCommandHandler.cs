using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Handlers
{
    /// <summary>
    /// Command handler for accessing and managing Unity Console logs.
    /// </summary>
    internal sealed class ConsoleCommandHandler : IMcpCommandHandler
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
                    Debug.LogError("Failed to find LogEntries type via reflection");
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
                    Debug.LogError("Failed to find LogEntry type via reflection");
                }

                Debug.Log("Successfully initialized reflection cache for LogEntries");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error initializing LogEntries reflection: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the command prefix for this handler.
        /// </summary>
        public string CommandPrefix => "console";

        /// <summary>
        /// Gets the description of this command handler.
        /// </summary>
        public string Description => "Access and manage Unity Console logs";

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
                    return new JObject
                    {
                        ["success"] = true,
                        ["logs"] = new JArray(),
                        ["totalCount"] = totalCount
                    };
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
                Debug.LogError($"Error getting logs: {ex.Message}");
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

                return new JObject
                {
                    ["success"] = true,
                    ["totalCount"] = (int)GetCountMethod.Invoke(null, null),
                    ["errorCount"] = errorCount,
                    ["warningCount"] = warningCount,
                    ["logCount"] = logCount
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error getting log counts: {ex.Message}");
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
                Debug.LogError($"Error clearing logs: {ex.Message}");
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = ex.Message
                };
            }
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
                Debug.LogError($"Error setting filter: {ex.Message}");
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = ex.Message
                };
            }
        }
    }
}
