using System;
using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;
using UnityEditor.Compilation;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Script compilation: triggering it, and finding out whether it worked.
    /// </summary>
    /// <remarks>
    /// Both halves exist because the obvious approaches are wrong in ways that are hard to
    /// notice. <c>AssetDatabase.Refresh()</c> does not necessarily recompile, so a caller that
    /// edits a script and refreshes can sit waiting for a compilation that was never
    /// requested. And after a failed compile the Editor keeps running the last good assembly
    /// and sets <c>isCompiling</c> back to false, so "not compiling" reads exactly like
    /// "compiled fine" unless the errors are checked explicitly.
    /// </remarks>
    internal static class CompileTools
    {
        /// <summary>
        /// Errors survive here because compiling reloads the domain, which wipes statics —
        /// the results would otherwise be gone by the time anyone could ask for them.
        /// </summary>
        private const string SessionKeyMessages = "UnityMCP.LastCompileMessages";

        private const string SessionKeyCompletedAt = "UnityMCP.LastCompileCompletedAt";

        [InitializeOnLoadMethod]
        private static void Subscribe()
        {
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        private static void OnCompilationStarted(object context)
        {
            SessionState.SetString(SessionKeyMessages, "[]");
            SessionState.EraseString(SessionKeyCompletedAt);
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null || messages.Length == 0)
            {
                return;
            }

            var collected = LoadMessages();

            foreach (var message in messages)
            {
                if (message.type != CompilerMessageType.Error && message.type != CompilerMessageType.Warning)
                {
                    continue;
                }

                collected.Add(new JObject
                {
                    ["assembly"] = System.IO.Path.GetFileName(assemblyPath),
                    ["type"] = message.type.ToString().ToLowerInvariant(),
                    ["message"] = message.message,
                    ["file"] = message.file,
                    ["line"] = message.line,
                    ["column"] = message.column,
                });
            }

            SessionState.SetString(SessionKeyMessages, collected.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static void OnCompilationFinished(object context)
        {
            SessionState.SetString(SessionKeyCompletedAt, DateTime.UtcNow.ToString("o"));
        }

        private static JArray LoadMessages()
        {
            var raw = SessionState.GetString(SessionKeyMessages, "[]");

            try
            {
                return JArray.Parse(raw);
            }
            catch
            {
                return new JArray();
            }
        }

        [McpTool(
            "compile_status",
            "Report whether scripts are compiling and whether the last compilation produced errors. " +
            "Always check this after editing scripts: a failed compile leaves the Editor running the " +
            "previous assembly with isCompiling back to false, so silence does not mean success.",
            Idempotency = McpIdempotency.Safe,
            // Every script edit has to be followed by this check, and a caller who cannot find it
            // is left believing an edit took effect when it did not.
            AlwaysLoad = true)]
        public static JObject Status(
            [McpArg("include_warnings", "Include warnings alongside errors.")]
            bool includeWarnings = false,
            [McpArg("limit", "Maximum messages to return, newest last.")]
            int limit = 50)
        {
            var messages = LoadMessages()
                .OfType<JObject>()
                .Where(m => includeWarnings || (string)m["type"] == "error")
                .ToList();

            var errorCount = LoadMessages().OfType<JObject>().Count(m => (string)m["type"] == "error");
            var truncated = messages.Count > limit;

            if (truncated)
            {
                messages = messages.Skip(messages.Count - limit).ToList();
            }

            var completedAt = SessionState.GetString(SessionKeyCompletedAt, string.Empty);

            return new JObject
            {
                ["isCompiling"] = EditorApplication.isCompiling,
                ["isUpdating"] = EditorApplication.isUpdating,
                // Null rather than false while a compilation is in flight: "no errors yet" is
                // not the same claim as "it succeeded".
                ["succeeded"] = EditorApplication.isCompiling || completedAt.Length == 0
                    ? null
                    : (JToken)(errorCount == 0),
                ["errorCount"] = errorCount,
                ["completedAt"] = completedAt.Length == 0 ? null : (JToken)completedAt,
                ["truncated"] = truncated,
                ["messages"] = new JArray(messages.Cast<object>().ToArray()),
            };
        }

        [McpTool(
            "compile_request",
            "Ask the Editor to recompile scripts. Needed after editing a script file: a refresh " +
            "alone does not reliably trigger a compile, so this runs a full asset refresh and then " +
            "requests the compile. The refresh imports every asset changed on disk and runs every " +
            "asset postprocessor, which on a large project takes minutes and can itself open a " +
            "modal dialog. Returns immediately; poll compile_status to find out whether it " +
            "succeeded.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Request()
        {
            if (EditorApplication.isCompiling)
            {
                return new JObject
                {
                    ["requested"] = false,
                    ["message"] = "A compilation is already in progress; poll compile_status instead.",
                };
            }

            AssetDatabase.Refresh();
            CompilationPipeline.RequestScriptCompilation();

            return new JObject
            {
                ["requested"] = true,
                ["message"] =
                    "Compilation requested. It triggers a domain reload, so the MCP connection will " +
                    "drop briefly. Poll compile_status until isCompiling is false, then check succeeded.",
            };
        }
    }
}
