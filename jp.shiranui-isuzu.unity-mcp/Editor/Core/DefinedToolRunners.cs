using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

using Newtonsoft.Json.Linq;

using UnityMCP.Editor.Handlers;
using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Core
{
    /// <summary>One read of a <c>probe</c> definition.</summary>
    internal sealed class ProbeRead
    {
        public string Id;

        public string Path;

        public int Depth;

        public int MaxItems;

        public string[] Fields;
    }

    /// <summary>
    /// Runs a <c>probe</c>: a set of reflection reads, optionally reporting only what changed
    /// since the previous call.
    /// </summary>
    internal sealed class ProbeRunner
    {
        // Baselines are per tool name and live in static state so they survive a catalog rebuild
        // and vanish with the domain, which is when the objects they describe vanish too.
        private static readonly object BaselineLock = new();
        private static readonly Dictionary<string, Dictionary<string, JToken>> Baselines = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> DefinitionHashes = new(StringComparer.Ordinal);

        private readonly string toolName;
        private readonly IReadOnlyList<ProbeRead> reads;
        private readonly bool changesOnly;
        private readonly DefinedToolInputs inputs;

        public ProbeRunner(string toolName, IReadOnlyList<ProbeRead> reads, bool changesOnly, DefinedToolInputs inputs)
        {
            this.toolName = toolName;
            this.reads = reads;
            this.changesOnly = changesOnly;
            this.inputs = inputs;
        }

        /// <summary>
        /// Records which definition a tool name currently has. A changed definition drops the
        /// baseline, so the next call reports everything rather than a diff against reads that
        /// may no longer exist.
        /// </summary>
        public static void NoteDefinition(string toolName, string definitionHash)
        {
            lock (BaselineLock)
            {
                if (DefinitionHashes.TryGetValue(toolName, out var previous) && previous != definitionHash)
                {
                    Baselines.Remove(toolName);
                }

                DefinitionHashes[toolName] = definitionHash;
            }
        }

        /// <summary>Forgets every baseline; a test hook.</summary>
        internal static void ResetBaselines()
        {
            lock (BaselineLock)
            {
                Baselines.Clear();
                DefinitionHashes.Clear();
            }
        }

        public JObject Run(JObject arguments)
        {
            Dictionary<string, JToken> baseline;
            bool isBaseline;

            lock (BaselineLock)
            {
                isBaseline = !Baselines.TryGetValue(this.toolName, out baseline);

                if (isBaseline)
                {
                    baseline = new Dictionary<string, JToken>(StringComparer.Ordinal);
                }
            }

            var results = new JObject();
            var changed = new JArray();

            foreach (var read in this.reads)
            {
                var entry = this.Read(read, arguments);
                var differs = isBaseline || !baseline.TryGetValue(read.Id, out var previous) || !JToken.DeepEquals(previous, entry);

                if (differs)
                {
                    changed.Add(read.Id);
                    baseline[read.Id] = entry.DeepClone();
                }

                if (differs || !this.changesOnly)
                {
                    results[read.Id] = entry;
                }
            }

            lock (BaselineLock)
            {
                Baselines[this.toolName] = baseline;
            }

            var result = new JObject
            {
                ["reads"] = results,
                ["mode"] = this.changesOnly ? "changes" : "full",
                ["changed"] = changed,
            };

            if (isBaseline)
            {
                result["baseline"] = true;
            }

            return result;
        }

        private JObject Read(ProbeRead read, JObject arguments)
        {
            var path = this.inputs.Substitute(read.Path, arguments);
            var entry = new JObject { ["path"] = path };

            try
            {
                var value = ReflectTools.ResolvePath(path, out var rootType, out var walked);
                entry["path"] = walked;
                entry["type"] = value?.GetType().FullName ?? rootType?.FullName;
                entry["value"] = read.Fields == null
                    ? ReflectTools.Serialize(value, Math.Max(read.Depth, 0), Math.Max(read.MaxItems, 0))
                    : FieldsOf(path, read);
            }
            catch (McpToolException ex)
            {
                // One read that cannot be answered (nothing selected, an object gone) should not
                // hide the others; the caller sees which one failed and why.
                entry["error"] = ex.Message;
            }

            return entry;
        }

        private static JObject FieldsOf(string path, ProbeRead read)
        {
            var fields = new JObject();

            foreach (var field in read.Fields)
            {
                try
                {
                    var value = ReflectTools.ResolvePath(path + "/" + field, out _, out _);
                    fields[field] = ReflectTools.Serialize(value, Math.Max(read.Depth, 0), Math.Max(read.MaxItems, 0));
                }
                catch (McpToolException ex)
                {
                    fields[field] = new JObject { ["error"] = ex.Message };
                }
            }

            return fields;
        }
    }

    /// <summary>
    /// Runs a <c>script</c>: a C# file read at call time and compiled with the arguments passed
    /// in as <c>JObject args</c>.
    /// </summary>
    internal sealed class ScriptRunner
    {
        private readonly string toolName;
        private readonly string file;

        public ScriptRunner(string toolName, string file)
        {
            this.toolName = toolName;
            this.file = file;
        }

        public JObject Run(JObject arguments)
        {
            string code;

            try
            {
                code = File.ReadAllText(this.file);
            }
            catch (Exception ex)
            {
                throw new McpToolException(
                    "not_found", $"'{this.toolName}' could not read {this.file}: {ex.Message}", 404);
            }

            var result = CodeExecutor.Execute(code, arguments ?? new JObject());

            if (result["error"] is JToken error && error.Type != JTokenType.Null)
            {
                var message = $"{this.toolName} ({this.file}): {error}";

                // A file that does not compile is the caller's to fix, not a server fault: as
                // a 500 it would be retried by clients that retry safe calls, for nothing.
                if (error.ToString().StartsWith(CodeExecutor.CompileErrorPrefix, StringComparison.Ordinal))
                {
                    throw new McpToolException("script_compile_error", message, 400);
                }

                throw new McpToolException("tool_failed", message, 500);
            }

            return result;
        }
    }

    /// <summary>One step of a <c>sequence</c> definition.</summary>
    internal sealed class SequenceStep
    {
        public string Id;

        public string Tool;

        public JObject Arguments;

        public bool ContinueOnError;
    }

    /// <summary>
    /// Runs a <c>sequence</c>: existing tools called in order through <see cref="ToolInvoker"/>,
    /// each step's arguments templated from the inputs and from earlier results.
    /// </summary>
    /// <remarks>
    /// A step that answers with a <see cref="DeferredToolResult"/> has only started its work,
    /// so the sequence waits on the inner item before the next step reads its result. On the
    /// main thread that wait cannot block: the rest of the sequence is handed to
    /// <see cref="FrameSequencer"/> and the sequence itself answers with a
    /// <see cref="DeferredToolResult"/>, which is what lets multi-frame steps nest.
    /// </remarks>
    internal sealed class SequenceRunner
    {
        private static readonly Regex StepReference = new(
            @"^\{\{\s*([A-Za-z_][A-Za-z0-9_\-]*)(?:\.([^}]*?))?\s*\}\}$", RegexOptions.Compiled);

        private readonly string toolName;
        private readonly IReadOnlyList<SequenceStep> steps;
        private readonly Func<string, McpToolDescriptor> resolve;
        private readonly DefinedToolInputs inputs;
        private readonly bool mainThread;

        public SequenceRunner(
            string toolName,
            IReadOnlyList<SequenceStep> steps,
            Func<string, McpToolDescriptor> resolve,
            DefinedToolInputs inputs,
            bool mainThread)
        {
            this.toolName = toolName;
            this.steps = steps;
            this.resolve = resolve;
            this.inputs = inputs;
            this.mainThread = mainThread;
        }

        /// <summary>
        /// Whether a string is exactly a <c>{{stepId.json.path}}</c> reference, and its parts.
        /// </summary>
        public static bool TryParseStepReference(string text, out string stepId, out string path)
        {
            stepId = null;
            path = null;

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var match = StepReference.Match(text);

            if (!match.Success)
            {
                return false;
            }

            stepId = match.Groups[1].Value;
            path = match.Groups[2].Success ? match.Groups[2].Value : null;
            return true;
        }

        public JObject Run(JObject arguments)
        {
            var execution = new Execution(arguments ?? new JObject());
            var run = this.Advance(execution);

            // Steps that answer inline are run here, inside the call; only a step that defers
            // costs the caller a frame sequence.
            while (true)
            {
                if (!run.MoveNext())
                {
                    run.Dispose();
                    return execution.Report();
                }

                if (run.Current.IsDone)
                {
                    run.Dispose();
                    return run.Current.Result;
                }

                if (this.mainThread)
                {
                    return new DeferredToolResult(FrameSequencer.Run(run, this.toolName));
                }

                // Off the main thread nothing else needs this thread, and the inner item is
                // settled by whoever runs the deferred step's frames.
                execution.Pending.Wait(Timeout.Infinite);
            }
        }

        /// <summary>What one run has produced so far; shared between the driver and the iterator.</summary>
        private sealed class Execution
        {
            public Execution(JObject arguments)
            {
                this.Arguments = arguments;
            }

            public JObject Arguments { get; }

            public Dictionary<string, JObject> Results { get; } = new(StringComparer.Ordinal);

            public JArray Steps { get; } = new();

            /// <summary>The inner item the sequence is waiting on, while a step is deferred.</summary>
            public McpMainThreadDispatcher.WorkItem Pending { get; set; }

            public JObject Report() => new JObject { ["steps"] = this.Steps };
        }

        private IEnumerator<FrameStep> Advance(Execution execution)
        {
            foreach (var step in this.steps)
            {
                var entry = new JObject { ["id"] = step.Id, ["tool"] = step.Tool };
                var result = this.Invoke(step, execution, out var error);

                if (error == null && result is DeferredToolResult deferred)
                {
                    execution.Pending = deferred.Item;

                    while (!deferred.Item.IsCompleted)
                    {
                        yield return FrameStep.Wait();
                    }

                    execution.Pending = null;

                    if (deferred.Item.Error != null)
                    {
                        error = AsToolException(step, deferred.Item.Error);
                    }
                    else
                    {
                        result = deferred.Item.Result ?? new JObject();
                    }
                }

                if (error == null)
                {
                    execution.Results[step.Id] = result;
                    entry["ok"] = true;
                    entry["result"] = result;
                }
                else
                {
                    entry["ok"] = false;
                    entry["error"] = new JObject { ["code"] = error.Code, ["message"] = error.Message };
                }

                execution.Steps.Add(entry);

                if (error != null && !step.ContinueOnError)
                {
                    break;
                }
            }

            yield return FrameStep.Done(execution.Report());
        }

        private JObject Invoke(SequenceStep step, Execution execution, out McpToolException error)
        {
            error = null;

            try
            {
                var descriptor = this.resolve(step.Tool);

                if (descriptor == null)
                {
                    throw new McpToolException(
                        "not_found",
                        $"'{this.toolName}' step '{step.Id}' names tool '{step.Tool}', which is not in the catalog.",
                        404);
                }

                var stepArguments = (JObject)step.Arguments.DeepClone();
                this.Substitute(stepArguments, execution.Arguments, execution.Results);

                // The sequence's own confirm is the caller's consent for every destructive
                // step in it; asking once per step would defeat having one name for the chain.
                if (descriptor.Destructive && execution.Arguments["confirm"] != null && stepArguments["confirm"] == null)
                {
                    stepArguments["confirm"] = execution.Arguments["confirm"].DeepClone();
                }

                return ToolInvoker.Invoke(descriptor, stepArguments);
            }
            catch (McpToolException ex)
            {
                error = ex;
                return null;
            }
        }

        private static McpToolException AsToolException(SequenceStep step, Exception error)
        {
            return error as McpToolException
                ?? new McpToolException("tool_failed", $"{step.Tool} threw {error.GetType().Name}: {error.Message}", 500);
        }

        private void Substitute(JToken token, JObject arguments, Dictionary<string, JObject> results)
        {
            switch (token)
            {
                case JObject asObject:
                    foreach (var property in asObject.Properties())
                    {
                        property.Value = this.Resolve(property.Value, arguments, results);
                    }

                    break;
                case JArray asArray:
                    for (var i = 0; i < asArray.Count; i++)
                    {
                        asArray[i] = this.Resolve(asArray[i], arguments, results);
                    }

                    break;
            }
        }

        private JToken Resolve(JToken value, JObject arguments, Dictionary<string, JObject> results)
        {
            if (value.Type != JTokenType.String)
            {
                this.Substitute(value, arguments, results);
                return value;
            }

            var text = value.Value<string>();

            if (TryParseStepReference(text, out var stepId, out var path))
            {
                if (!results.TryGetValue(stepId, out var earlier))
                {
                    throw new McpToolException(
                        "invalid_params",
                        $"'{this.toolName}' refers to step '{stepId}', which has not produced a result.");
                }

                var selected = path == null ? earlier : earlier.SelectToken(path);

                if (selected == null)
                {
                    throw new McpToolException(
                        "invalid_params",
                        $"'{this.toolName}' refers to '{text}', but step '{stepId}' returned nothing at '{path}'.");
                }

                return selected.DeepClone();
            }

            return this.inputs.Substitute(text, arguments);
        }
    }
}
