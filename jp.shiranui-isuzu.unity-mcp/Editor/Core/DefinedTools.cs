using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Newtonsoft.Json.Linq;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// One definition file that produced a tool, as <c>definitions_list</c> reports it.
    /// </summary>
    internal sealed class DefinedToolEntry
    {
        public DefinedToolEntry(string name, string kind, string file)
        {
            this.Name = name;
            this.Kind = kind;
            this.File = file;
        }

        public string Name { get; }

        public string Kind { get; }

        public string File { get; }
    }

    /// <summary>
    /// The outcome of one <see cref="DefinedTools.Load"/>: the descriptors to register, the files
    /// they came from, and every refusal.
    /// </summary>
    internal sealed class DefinedToolSet
    {
        public static readonly DefinedToolSet Empty = new(
            Array.Empty<McpToolDescriptor>(), Array.Empty<DefinedToolEntry>(), Array.Empty<string>());

        public DefinedToolSet(
            IReadOnlyList<McpToolDescriptor> descriptors,
            IReadOnlyList<DefinedToolEntry> entries,
            IReadOnlyList<string> errors)
        {
            this.Descriptors = descriptors;
            this.Entries = entries;
            this.Errors = errors;
        }

        public IReadOnlyList<McpToolDescriptor> Descriptors { get; }

        public IReadOnlyList<DefinedToolEntry> Entries { get; }

        /// <summary>Refusals, each starting with the full path of the file it concerns.</summary>
        public IReadOnlyList<string> Errors { get; }

        /// <summary>The same set with further errors appended.</summary>
        public DefinedToolSet WithErrors(IEnumerable<string> more)
        {
            return new DefinedToolSet(this.Descriptors, this.Entries, this.Errors.Concat(more).ToList());
        }
    }

    /// <summary>
    /// The declared inputs of a defined tool, and the <c>{name}</c> substitution they drive.
    /// </summary>
    internal sealed class DefinedToolInputs
    {
        /// <summary>
        /// <c>{name}</c>, but not the inner braces of a <c>{{step.path}}</c> reference, which the
        /// sequence runner resolves as a whole.
        /// </summary>
        private static readonly Regex Placeholder = new(
            @"(?<!\{)\{([A-Za-z_][A-Za-z0-9_]*)\}(?!\})", RegexOptions.Compiled);

        private readonly string toolName;
        private readonly Dictionary<string, InputSpec> specs;

        public DefinedToolInputs(string toolName, Dictionary<string, InputSpec> specs)
        {
            this.toolName = toolName;
            this.specs = specs;
        }

        public bool Declares(string name) => this.specs.ContainsKey(name);

        public IReadOnlyDictionary<string, InputSpec> Specs => this.specs;

        /// <summary>The input names a template refers to, in order of appearance.</summary>
        public static IEnumerable<string> PlaceholdersIn(string template)
        {
            if (string.IsNullOrEmpty(template))
            {
                yield break;
            }

            foreach (Match match in Placeholder.Matches(template))
            {
                yield return match.Groups[1].Value;
            }
        }

        /// <summary>Replaces every <c>{name}</c> with the argument's text.</summary>
        public string Substitute(string template, JObject arguments)
        {
            if (string.IsNullOrEmpty(template) || template.IndexOf('{') < 0)
            {
                return template;
            }

            return Placeholder.Replace(template, match => this.ValueOf(match.Groups[1].Value, arguments));
        }

        private string ValueOf(string name, JObject arguments)
        {
            var token = arguments?[name];

            if (token == null || token.Type == JTokenType.Null)
            {
                if (this.specs.TryGetValue(name, out var spec) && spec.Default != null)
                {
                    token = spec.Default;
                }
                else
                {
                    throw new McpToolException("invalid_params", $"'{this.toolName}' requires input '{name}'.");
                }
            }

            return token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Refuses arguments that do not match the declared inputs, so a wrong type fails on
        /// the defined tool rather than deep inside whatever the value was substituted into.
        /// </summary>
        /// <exception cref="McpToolException"><c>invalid_params</c>, naming the input.</exception>
        public void Validate(JObject arguments)
        {
            foreach (var pair in this.specs)
            {
                var token = arguments?[pair.Key];

                if (token == null || token.Type == JTokenType.Null)
                {
                    if (pair.Value.Required && pair.Value.Default == null)
                    {
                        throw new McpToolException("invalid_params", $"'{this.toolName}' requires input '{pair.Key}'.");
                    }

                    continue;
                }

                if (!Matches(pair.Value.Type, token.Type))
                {
                    throw new McpToolException(
                        "invalid_params",
                        $"'{this.toolName}' input '{pair.Key}' must be {AnArticle(pair.Value.Type)}, got {Describe(token)}.");
                }

                if (pair.Value.Enum != null && !pair.Value.Enum.Any(allowed => JToken.DeepEquals(allowed, token)))
                {
                    throw new McpToolException(
                        "invalid_params",
                        $"'{this.toolName}' input '{pair.Key}' must be one of {pair.Value.Enum.ToString(Newtonsoft.Json.Formatting.None)}, got {Describe(token)}.");
                }
            }
        }

        private static bool Matches(string declared, JTokenType actual)
        {
            switch (declared)
            {
                case "string": return actual == JTokenType.String;
                case "integer": return actual == JTokenType.Integer;
                case "number": return actual == JTokenType.Integer || actual == JTokenType.Float;
                case "boolean": return actual == JTokenType.Boolean;
                case "object": return actual == JTokenType.Object;
                case "array": return actual == JTokenType.Array;
                default: return true;
            }
        }

        private static string AnArticle(string type) => type == "integer" || type == "object" || type == "array" ? $"an {type}" : $"a {type}";

        private static string Describe(JToken token)
        {
            return token.Type == JTokenType.String || token.Type == JTokenType.Object || token.Type == JTokenType.Array
                ? token.Type.ToString().ToLowerInvariant() + " " + token.ToString(Newtonsoft.Json.Formatting.None)
                : token.ToString(Newtonsoft.Json.Formatting.None);
        }

        internal sealed class InputSpec
        {
            public string Type;

            public JToken Default;

            public bool Required;

            public JArray Enum;
        }
    }

    /// <summary>
    /// Loads tools defined in JSON files: a <c>probe</c> (a set of reflection reads), a
    /// <c>script</c> (one C# file) or a <c>sequence</c> (a chain of existing tools).
    /// </summary>
    /// <remarks>
    /// Loading touches no Unity API so it can run on the HTTP worker thread that rebuilds the
    /// catalog; the project directory is computed on the main thread and passed in.
    /// </remarks>
    internal static class DefinedTools
    {
        private static readonly HashSet<string> CommonKeys = new(StringComparer.Ordinal)
        {
            "name", "description", "kind", "group", "idempotency", "mainThread", "destructive",
            "undoGroup", "alwaysLoad", "maxResultSizeChars", "inputs", "examples",
        };

        private static readonly Dictionary<string, string[]> KindKeys = new(StringComparer.Ordinal)
        {
            ["probe"] = new[] { "reads", "mode" },
            ["script"] = new[] { "file" },
            ["sequence"] = new[] { "steps" },
        };

        private static readonly HashSet<string> InputKeys = new(StringComparer.Ordinal)
        {
            "type", "description", "required", "default", "enum",
        };

        private static readonly HashSet<string> InputTypes = new(StringComparer.Ordinal)
        {
            "string", "integer", "number", "boolean", "object", "array",
        };

        private static readonly HashSet<string> ReadKeys = new(StringComparer.Ordinal)
        {
            "id", "path", "depth", "max_items", "fields",
        };

        private static readonly HashSet<string> StepKeys = new(StringComparer.Ordinal)
        {
            "id", "tool", "arguments", "continue_on_error",
        };

        /// <summary>Where this project's own definitions live.</summary>
        public static string ProjectDirectory(string projectDataPath)
        {
            return Path.Combine(
                McpInstanceDescriptor.StateRoot, "tools", McpInstanceDescriptor.HashProjectPath(projectDataPath));
        }

        /// <summary>Where definitions shared by every project live.</summary>
        public static string SharedDirectory => Path.Combine(McpInstanceDescriptor.StateRoot, "tools", "shared");

        /// <summary>
        /// Reads every <c>*.json</c> in <paramref name="directories"/>, earlier directories
        /// shadowing later ones by tool name. File names carry no meaning.
        /// </summary>
        /// <remarks>
        /// Never throws: anything a file does wrong becomes an error entry for that file, so one
        /// broken definition cannot take the whole catalog down with it.
        /// </remarks>
        /// <param name="errors">Receives one entry per refused file, starting with its full path.</param>
        /// <param name="attributeCatalog">
        /// The attribute tools, which win a name collision and which a sequence step may name.
        /// </param>
        public static DefinedToolSet Load(
            IReadOnlyList<string> directories, List<string> errors, ToolCatalog attributeCatalog)
        {
            errors ??= new List<string>();
            var firstError = errors.Count;

            var descriptors = new List<McpToolDescriptor>();
            var entries = new List<DefinedToolEntry>();
            var loaded = new Dictionary<string, Definition>(StringComparer.Ordinal);
            var pending = new List<Definition>();

            foreach (var directory in directories ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    continue;
                }

                string[] files;

                try
                {
                    files = Directory.GetFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
                }
                catch (Exception ex)
                {
                    errors.Add($"{directory}: cannot be listed: {ex.Message}");
                    continue;
                }

                foreach (var file in files)
                {
                    Definition definition;

                    try
                    {
                        definition = Parse(file, errors);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{file}: {ex.GetType().Name}: {ex.Message}");
                        continue;
                    }

                    if (definition == null)
                    {
                        continue;
                    }

                    if (loaded.TryGetValue(definition.Name, out var earlier))
                    {
                        if (string.Equals(
                                Path.GetDirectoryName(earlier.File), Path.GetDirectoryName(file), StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add($"{file}: tool '{definition.Name}' is already defined by {earlier.File}.");
                        }

                        continue;
                    }

                    if (attributeCatalog != null && attributeCatalog.TryGet(definition.Name, out var attributeTool))
                    {
                        errors.Add(
                            $"{file}: tool '{definition.Name}' collides with {attributeTool.Origin}; the attribute tool wins.");
                        continue;
                    }

                    loaded[definition.Name] = definition;
                    pending.Add(definition);
                }
            }

            McpToolDescriptor Resolve(string name)
            {
                if (attributeCatalog != null && attributeCatalog.TryGet(name, out var attributeTool))
                {
                    return attributeTool;
                }

                return loaded.TryGetValue(name, out var definition) ? definition.Descriptor : null;
            }

            RejectCycles(loaded, pending, errors);

            // Sequences are validated after every file is read so a step may name a tool defined
            // by a sibling file, whichever order the directory listing put them in.
            foreach (var definition in pending)
            {
                bool finished;

                try
                {
                    finished = Finish(definition, loaded, Resolve, errors);
                }
                catch (Exception ex)
                {
                    errors.Add($"{definition.File}: {ex.GetType().Name}: {ex.Message}");
                    finished = false;
                }

                if (!finished)
                {
                    loaded.Remove(definition.Name);
                    continue;
                }

                descriptors.Add(definition.Descriptor);
                entries.Add(new DefinedToolEntry(definition.Name, definition.Kind, definition.File));
            }

            return new DefinedToolSet(descriptors, entries, errors.Skip(firstError).ToList());
        }

        /// <summary>
        /// Drops every sequence that is part of a reference cycle, naming the cycle. A cycle
        /// would recurse without bound at call time, and a stack overflow cannot be caught.
        /// </summary>
        private static void RejectCycles(Dictionary<string, Definition> loaded, List<Definition> pending, List<string> errors)
        {
            const int OnPath = 1;
            const int Done = 2;

            var state = new Dictionary<string, int>(StringComparer.Ordinal);
            var path = new List<string>();
            var cyclic = new HashSet<string>(StringComparer.Ordinal);

            void Visit(string name)
            {
                state[name] = OnPath;
                path.Add(name);

                foreach (var next in StepToolNames(loaded[name]))
                {
                    // A step naming its own sequence is reported by BuildSequence.
                    if (next == name || !loaded.TryGetValue(next, out var sibling) || sibling.Kind != "sequence")
                    {
                        continue;
                    }

                    if (!state.TryGetValue(next, out var seen))
                    {
                        Visit(next);
                    }
                    else if (seen == OnPath)
                    {
                        var start = path.IndexOf(next);
                        var cycle = string.Join(" -> ", path.Skip(start).Concat(new[] { next }));

                        foreach (var member in path.Skip(start))
                        {
                            if (cyclic.Add(member))
                            {
                                errors.Add($"{loaded[member].File}: sequence '{member}' is part of a cycle: {cycle}.");
                            }
                        }
                    }
                }

                path.RemoveAt(path.Count - 1);
                state[name] = Done;
            }

            foreach (var definition in pending)
            {
                if (definition.Kind == "sequence" && !state.ContainsKey(definition.Name))
                {
                    Visit(definition.Name);
                }
            }

            foreach (var name in cyclic)
            {
                loaded.Remove(name);
            }

            pending.RemoveAll(d => cyclic.Contains(d.Name));
        }

        /// <summary>The tools a sequence's steps name, read leniently; BuildSequence reports the malformed ones.</summary>
        private static IEnumerable<string> StepToolNames(Definition definition)
        {
            if (definition.Body["steps"] is not JArray steps)
            {
                yield break;
            }

            foreach (var step in steps)
            {
                if (step is JObject asObject && asObject["tool"]?.Type == JTokenType.String)
                {
                    yield return asObject["tool"].Value<string>();
                }
            }
        }

        /// <summary>
        /// The JSON Schema for a definition's <c>inputs</c>, in the same shape the catalog derives
        /// from a method signature.
        /// </summary>
        public static JObject BuildInputSchema(
            IReadOnlyDictionary<string, DefinedToolInputs.InputSpec> inputs,
            IReadOnlyDictionary<string, string> descriptions,
            IReadOnlyDictionary<string, JArray> enums,
            bool destructive,
            JArray examples)
        {
            var properties = new JObject();
            var required = new JArray();

            foreach (var pair in inputs)
            {
                var schema = new JObject { ["type"] = pair.Value.Type };

                if (descriptions != null && descriptions.TryGetValue(pair.Key, out var description))
                {
                    schema["description"] = description;
                }

                if (pair.Value.Default != null)
                {
                    schema["default"] = pair.Value.Default.DeepClone();
                }

                if (enums != null && enums.TryGetValue(pair.Key, out var values))
                {
                    schema["enum"] = (JArray)values.DeepClone();
                }

                properties[pair.Key] = schema;

                if (pair.Value.Required)
                {
                    required.Add(pair.Key);
                }
            }

            if (destructive)
            {
                ToolCatalog.AppendConfirmationProperties(properties);
            }

            var inputSchema = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
            };

            if (required.Count > 0)
            {
                inputSchema["required"] = required;
            }

            if (examples != null && examples.Count > 0)
            {
                inputSchema["examples"] = examples;
            }

            return inputSchema;
        }

        // ── one file ──

        private sealed class Definition
        {
            public string File;

            public string Hash;

            public string Name;

            public string Description;

            public string Kind;

            public string Group;

            public McpIdempotency Idempotency;

            public bool MainThread;

            public bool? MainThreadExplicit;

            public bool Destructive;

            public bool? DestructiveExplicit;

            public string UndoGroup;

            public bool AlwaysLoad;

            public int MaxResultSizeChars;

            public DefinedToolInputs Inputs;

            public JObject InputSchema;

            public JObject Body;

            public McpToolDescriptor Descriptor;
        }

        private sealed class FileErrors
        {
            private readonly string file;
            private readonly List<string> sink;

            public FileErrors(string file, List<string> sink)
            {
                this.file = file;
                this.sink = sink;
            }

            public bool Any { get; private set; }

            public void Add(string message)
            {
                this.Any = true;
                this.sink.Add($"{this.file}: {message}");
            }
        }

        private static Definition Parse(string file, List<string> sink)
        {
            var errors = new FileErrors(file, sink);
            string text;
            JObject body;

            try
            {
                text = File.ReadAllText(file);
                body = JObject.Parse(text);
            }
            catch (Exception ex)
            {
                errors.Add($"not valid JSON: {ex.Message}");
                return null;
            }

            var kind = ReadString(body, "kind", errors, required: true);

            if (kind == null)
            {
                return null;
            }

            if (!KindKeys.TryGetValue(kind, out var kindKeys))
            {
                errors.Add($"'kind' is '{kind}'; expected probe, script or sequence.");
                return null;
            }

            var unknown = body.Properties()
                .Select(p => p.Name)
                .Where(k => !CommonKeys.Contains(k) && Array.IndexOf(kindKeys, k) < 0)
                .ToArray();

            if (unknown.Length > 0)
            {
                var allowed = string.Join(", ", CommonKeys.Concat(kindKeys).OrderBy(k => k, StringComparer.Ordinal));
                errors.Add($"unknown key(s) {string.Join(", ", unknown.Select(k => $"'{k}'"))} for kind '{kind}'. Allowed: {allowed}.");
            }

            var definition = new Definition
            {
                File = file,
                Hash = Hash(text),
                Kind = kind,
                Body = body,
                Name = ReadString(body, "name", errors, required: true),
                Description = ReadString(body, "description", errors, required: true),
                Group = ReadString(body, "group", errors, required: false),
                UndoGroup = ReadString(body, "undoGroup", errors, required: false),
                DestructiveExplicit = ReadBool(body, "destructive", errors),
                AlwaysLoad = ReadBool(body, "alwaysLoad", errors) ?? false,
                MaxResultSizeChars = ReadInt(body, "maxResultSizeChars", errors) ?? 0,
                MainThreadExplicit = ReadBool(body, "mainThread", errors),
            };

            definition.MainThread = definition.MainThreadExplicit ?? true;
            definition.Destructive = definition.DestructiveExplicit ?? false;

            if (definition.Name != null && !ToolCatalog.IsValidName(definition.Name))
            {
                errors.Add(
                    $"'name' is '{definition.Name}'. Names must match ^[a-z][a-z0-9_]{{0,63}}$ " +
                    "(dots are not valid in MCP tool names).");
            }

            if (definition.Description != null && string.IsNullOrWhiteSpace(definition.Description))
            {
                errors.Add("'description' is empty.");
            }

            var idempotency = ReadString(body, "idempotency", errors, required: false);

            switch (idempotency)
            {
                case null:
                    definition.Idempotency = kind == "probe" ? McpIdempotency.Safe : McpIdempotency.Unsafe;
                    break;
                case "safe":
                    definition.Idempotency = McpIdempotency.Safe;
                    break;
                case "unsafe":
                    definition.Idempotency = McpIdempotency.Unsafe;
                    break;
                default:
                    errors.Add($"'idempotency' is '{idempotency}'; expected safe or unsafe.");
                    break;
            }

            if (definition.Group == null)
            {
                definition.Group = definition.Name == null ? McpToolGroups.Code : McpToolGroups.Derive(definition.Name);
            }
            else if (!McpToolGroups.IsKnown(definition.Group))
            {
                errors.Add($"'group' is '{definition.Group}'. Known: {string.Join(", ", McpToolGroups.Known)}.");
            }

            if (definition.UndoGroup != null && kind != "sequence")
            {
                errors.Add("'undoGroup' is only valid for kind 'sequence'.");
            }

            if (definition.UndoGroup != null && definition.MainThreadExplicit == false)
            {
                errors.Add("'undoGroup' requires mainThread: true; Undo is main-thread only.");
            }

            if (kind == "probe" && definition.MainThreadExplicit == false)
            {
                errors.Add("'mainThread' is false but a probe reads Unity objects, which is main-thread only.");
            }

            var inputs = ParseInputs(body, definition.Name ?? "?", errors, out var descriptions, out var enums);
            var examples = ParseExamples(body, errors);

            if (errors.Any)
            {
                return null;
            }

            definition.Inputs = inputs;
            definition.InputSchema = BuildInputSchema(
                inputs.Specs, descriptions, enums, definition.Destructive, examples);

            return definition;
        }

        private static DefinedToolInputs ParseInputs(
            JObject body,
            string toolName,
            FileErrors errors,
            out Dictionary<string, string> descriptions,
            out Dictionary<string, JArray> enums)
        {
            var specs = new Dictionary<string, DefinedToolInputs.InputSpec>(StringComparer.Ordinal);
            descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
            enums = new Dictionary<string, JArray>(StringComparer.Ordinal);

            var token = body["inputs"];

            if (token == null || token.Type == JTokenType.Null)
            {
                return new DefinedToolInputs(toolName, specs);
            }

            if (token is not JObject inputs)
            {
                errors.Add("'inputs' must be an object mapping input names to their specifications.");
                return new DefinedToolInputs(toolName, specs);
            }

            foreach (var property in inputs.Properties())
            {
                var name = property.Name;

                if (ToolCatalog.IsReservedParameterName(name))
                {
                    errors.Add($"input '{name}' is reserved; confirm and dry_run are added by the invoker to destructive tools, and target is the client's routing key.");
                    continue;
                }

                if (property.Value is not JObject spec)
                {
                    errors.Add($"input '{name}' must be an object with at least 'type'.");
                    continue;
                }

                var unknown = spec.Properties().Select(p => p.Name).Where(k => !InputKeys.Contains(k)).ToArray();

                if (unknown.Length > 0)
                {
                    errors.Add(
                        $"input '{name}' has unknown key(s) {string.Join(", ", unknown.Select(k => $"'{k}'"))}. " +
                        "Allowed: type, description, required, default, enum.");
                }

                var type = spec["type"]?.Type == JTokenType.String ? spec["type"].Value<string>() : null;

                if (type == null || !InputTypes.Contains(type))
                {
                    errors.Add(
                        $"input '{name}' has type '{spec["type"]}'; expected string, integer, number, boolean, object or array.");
                    continue;
                }

                var required = spec["required"];

                if (required != null && required.Type != JTokenType.Boolean)
                {
                    errors.Add($"input '{name}' has a non-boolean 'required'.");
                }

                var description = spec["description"];

                if (description != null && description.Type != JTokenType.String)
                {
                    errors.Add($"input '{name}' has a non-string 'description'.");
                }
                else if (description != null)
                {
                    descriptions[name] = description.Value<string>();
                }

                var enumValues = spec["enum"];

                if (enumValues != null && enumValues is not JArray)
                {
                    errors.Add($"input '{name}' has an 'enum' that is not an array.");
                }
                else if (enumValues is JArray values)
                {
                    enums[name] = values;
                }

                var defaultValue = spec["default"];

                specs[name] = new DefinedToolInputs.InputSpec
                {
                    Type = type,
                    Default = defaultValue == null || defaultValue.Type == JTokenType.Null ? null : defaultValue,
                    Required = required?.Type == JTokenType.Boolean && required.Value<bool>(),
                    Enum = enumValues as JArray,
                };
            }

            return new DefinedToolInputs(toolName, specs);
        }

        private static JArray ParseExamples(JObject body, FileErrors errors)
        {
            var token = body["examples"];

            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token is not JArray items)
            {
                errors.Add("'examples' must be an array of argument objects, each as a JSON string.");
                return null;
            }

            var parsed = new JArray();

            foreach (var item in items)
            {
                switch (item.Type)
                {
                    case JTokenType.String:
                        try
                        {
                            parsed.Add(JObject.Parse(item.Value<string>()));
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"an example is not a JSON object: {ex.Message}");
                        }

                        break;
                    case JTokenType.Object:
                        parsed.Add(item.DeepClone());
                        break;
                    default:
                        errors.Add($"an example is a {item.Type}; expected a JSON object or a string holding one.");
                        break;
                }
            }

            return parsed;
        }

        /// <summary>
        /// Builds the runner and descriptor for a parsed definition. Split from <see cref="Parse"/>
        /// because a sequence step may name a tool from another file.
        /// </summary>
        private static bool Finish(
            Definition definition,
            Dictionary<string, Definition> siblings,
            Func<string, McpToolDescriptor> resolve,
            List<string> sink)
        {
            var errors = new FileErrors(definition.File, sink);
            Func<JObject, JObject> direct;

            switch (definition.Kind)
            {
                case "probe":
                    direct = BuildProbe(definition, errors);
                    break;
                case "script":
                    direct = BuildScript(definition, errors);
                    break;
                default:
                    direct = BuildSequence(definition, siblings, resolve, errors);
                    break;
            }

            if (errors.Any || direct == null)
            {
                return false;
            }

            var inputs = definition.Inputs;
            var body = direct;
            direct = arguments =>
            {
                inputs.Validate(arguments);
                return body(arguments);
            };

            definition.Descriptor = new McpToolDescriptor(
                definition.Name,
                definition.Description,
                definition.InputSchema,
                definition.Idempotency,
                definition.MainThread,
                definition.Destructive,
                definition.UndoGroup,
                definition.Group,
                definition.AlwaysLoad,
                definition.MaxResultSizeChars,
                definition.File,
                direct);

            return true;
        }

        private static Func<JObject, JObject> BuildProbe(Definition definition, FileErrors errors)
        {
            var body = definition.Body;

            if (body["reads"] is not JArray readsArray || readsArray.Count == 0)
            {
                errors.Add("'reads' must be a non-empty array of { id, path, depth?, max_items?, fields? }.");
                return null;
            }

            var mode = ReadString(body, "mode", errors, required: false) ?? "full";

            if (mode != "full" && mode != "changes")
            {
                errors.Add($"'mode' is '{mode}'; expected full or changes.");
            }

            var reads = new List<ProbeRead>();
            var ids = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < readsArray.Count; i++)
            {
                if (readsArray[i] is not JObject read)
                {
                    errors.Add($"reads[{i}] is not an object.");
                    continue;
                }

                var unknown = read.Properties().Select(p => p.Name).Where(k => !ReadKeys.Contains(k)).ToArray();

                if (unknown.Length > 0)
                {
                    errors.Add(
                        $"reads[{i}] has unknown key(s) {string.Join(", ", unknown.Select(k => $"'{k}'"))}. " +
                        "Allowed: id, path, depth, max_items, fields.");
                }

                var id = ReadString(read, "id", errors, required: true, where: $"reads[{i}]");
                var path = ReadString(read, "path", errors, required: true, where: $"reads[{i}]");

                if (id != null && !ids.Add(id))
                {
                    errors.Add($"reads[{i}] repeats id '{id}'.");
                }

                if (path != null)
                {
                    foreach (var placeholder in DefinedToolInputs.PlaceholdersIn(path))
                    {
                        if (!definition.Inputs.Declares(placeholder))
                        {
                            errors.Add($"reads[{i}].path refers to input '{placeholder}', which 'inputs' does not declare.");
                        }
                    }
                }

                string[] fields = null;

                if (read["fields"] is JArray fieldArray)
                {
                    fields = fieldArray.Select(f => f.ToString()).ToArray();
                }
                else if (read["fields"] != null)
                {
                    errors.Add($"reads[{i}].fields must be an array of member names.");
                }

                reads.Add(new ProbeRead
                {
                    Id = id,
                    Path = path,
                    Depth = ReadInt(read, "depth", errors, where: $"reads[{i}]") ?? 2,
                    MaxItems = ReadInt(read, "max_items", errors, where: $"reads[{i}]") ?? 20,
                    Fields = fields,
                });
            }

            if (errors.Any)
            {
                return null;
            }

            ProbeRunner.NoteDefinition(definition.Name, definition.Hash);

            var runner = new ProbeRunner(definition.Name, reads, mode == "changes", definition.Inputs);
            return runner.Run;
        }

        private static Func<JObject, JObject> BuildScript(Definition definition, FileErrors errors)
        {
            var file = ReadString(definition.Body, "file", errors, required: true);

            if (file == null)
            {
                return null;
            }

            var resolved = Path.IsPathRooted(file)
                ? file
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(definition.File) ?? string.Empty, file));

            if (!File.Exists(resolved))
            {
                errors.Add($"'file' names {resolved}, which does not exist.");
                return null;
            }

            var runner = new ScriptRunner(definition.Name, resolved);
            return runner.Run;
        }

        private static Func<JObject, JObject> BuildSequence(
            Definition definition,
            Dictionary<string, Definition> siblings,
            Func<string, McpToolDescriptor> resolve,
            FileErrors errors)
        {
            if (definition.Body["steps"] is not JArray stepsArray || stepsArray.Count == 0)
            {
                errors.Add("'steps' must be a non-empty array of { id, tool, arguments?, continue_on_error? }.");
                return null;
            }

            var steps = new List<SequenceStep>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var needsMainThread = false;
            string destructiveStep = null;

            for (var i = 0; i < stepsArray.Count; i++)
            {
                if (stepsArray[i] is not JObject step)
                {
                    errors.Add($"steps[{i}] is not an object.");
                    continue;
                }

                var unknown = step.Properties().Select(p => p.Name).Where(k => !StepKeys.Contains(k)).ToArray();

                if (unknown.Length > 0)
                {
                    errors.Add(
                        $"steps[{i}] has unknown key(s) {string.Join(", ", unknown.Select(k => $"'{k}'"))}. " +
                        "Allowed: id, tool, arguments, continue_on_error.");
                }

                var id = ReadString(step, "id", errors, required: true, where: $"steps[{i}]");
                var tool = ReadString(step, "tool", errors, required: true, where: $"steps[{i}]");
                var continueOnError = ReadBool(step, "continue_on_error", errors, where: $"steps[{i}]") ?? false;

                JObject arguments;

                switch (step["arguments"])
                {
                    case null:
                        arguments = new JObject();
                        break;
                    case JObject asObject:
                        arguments = asObject;
                        break;
                    default:
                        errors.Add($"steps[{i}].arguments must be an object.");
                        arguments = new JObject();
                        break;
                }

                if (tool != null)
                {
                    if (tool == definition.Name)
                    {
                        errors.Add($"steps[{i}] names the sequence itself.");
                    }
                    else if (siblings.TryGetValue(tool, out var sibling))
                    {
                        // A sibling has no descriptor yet; its own mainThread is what it will publish.
                        needsMainThread |= sibling.MainThread;
                        destructiveStep ??= IsDestructive(sibling, siblings, resolve) ? tool : null;
                    }
                    else if (resolve(tool) is McpToolDescriptor descriptor)
                    {
                        needsMainThread |= descriptor.MainThread;
                        destructiveStep ??= descriptor.Destructive ? tool : null;
                    }
                    else
                    {
                        errors.Add($"steps[{i}] names tool '{tool}', which is not in the catalog.");
                    }
                }

                foreach (var text in StringValues(arguments))
                {
                    if (SequenceRunner.TryParseStepReference(text, out var stepId, out _))
                    {
                        if (!ids.Contains(stepId))
                        {
                            errors.Add(
                                $"steps[{i}] refers to '{{{{{stepId}...}}}}', which is not an earlier step. " +
                                "A step can only use the result of a step before it.");
                        }

                        continue;
                    }

                    foreach (var placeholder in DefinedToolInputs.PlaceholdersIn(text))
                    {
                        if (!definition.Inputs.Declares(placeholder))
                        {
                            errors.Add($"steps[{i}] refers to input '{placeholder}', which 'inputs' does not declare.");
                        }
                    }
                }

                if (id != null && !ids.Add(id))
                {
                    errors.Add($"steps[{i}] repeats id '{id}'.");
                }

                steps.Add(new SequenceStep
                {
                    Id = id,
                    Tool = tool,
                    Arguments = arguments,
                    ContinueOnError = continueOnError,
                });
            }

            if (definition.MainThreadExplicit == false && needsMainThread)
            {
                errors.Add("'mainThread' is false but a step needs the main thread; the sequence has to run there too.");
            }

            // A destructive step can only be confirmed through the sequence's own confirm, which
            // exists only when the sequence is destructive itself.
            if (definition.DestructiveExplicit == false && destructiveStep != null)
            {
                errors.Add(
                    $"'destructive' is false but step tool '{destructiveStep}' is destructive; confirm cannot be " +
                    "forwarded to it. Remove 'destructive: false' to let the sequence ask for confirmation.");
            }

            if (errors.Any)
            {
                return null;
            }

            definition.MainThread = needsMainThread || (definition.MainThreadExplicit ?? false);

            if (destructiveStep != null && !definition.Destructive)
            {
                definition.Destructive = true;
                ToolCatalog.AppendConfirmationProperties((JObject)definition.InputSchema["properties"]);
            }

            if (definition.UndoGroup != null && !definition.MainThread)
            {
                errors.Add("'undoGroup' requires mainThread: true; Undo is main-thread only.");
                return null;
            }

            var runner = new SequenceRunner(definition.Name, steps, resolve, definition.Inputs, definition.MainThread);
            return runner.Run;
        }

        /// <summary>
        /// Whether a sibling will publish as destructive: what its file says, or for a sequence,
        /// whether any of its steps is. Cycles are gone by the time this runs, so the recursion ends.
        /// </summary>
        private static bool IsDestructive(
            Definition definition, Dictionary<string, Definition> siblings, Func<string, McpToolDescriptor> resolve)
        {
            if (definition.DestructiveExplicit.HasValue || definition.Kind != "sequence")
            {
                return definition.Destructive;
            }

            foreach (var tool in StepToolNames(definition))
            {
                if (tool == definition.Name)
                {
                    continue;
                }

                if (siblings.TryGetValue(tool, out var sibling)
                    ? IsDestructive(sibling, siblings, resolve)
                    : resolve(tool)?.Destructive == true)
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> StringValues(JToken token)
        {
            switch (token)
            {
                case JValue value when value.Type == JTokenType.String:
                    yield return value.Value<string>();
                    break;
                case JContainer container:
                    foreach (var child in container.Children())
                    {
                        foreach (var text in StringValues(child is JProperty property ? property.Value : child))
                        {
                            yield return text;
                        }
                    }

                    break;
            }
        }

        // ── typed field readers ──

        private static string ReadString(JObject body, string key, FileErrors errors, bool required, string where = null)
        {
            var token = body[key];
            var label = where == null ? $"'{key}'" : $"{where}.{key}";

            if (token == null || token.Type == JTokenType.Null)
            {
                if (required)
                {
                    errors.Add($"{label} is required.");
                }

                return null;
            }

            if (token.Type != JTokenType.String)
            {
                errors.Add($"{label} must be a string.");
                return null;
            }

            return token.Value<string>();
        }

        private static bool? ReadBool(JObject body, string key, FileErrors errors, string where = null)
        {
            var token = body[key];

            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type != JTokenType.Boolean)
            {
                errors.Add($"{(where == null ? $"'{key}'" : $"{where}.{key}")} must be true or false.");
                return null;
            }

            return token.Value<bool>();
        }

        private static int? ReadInt(JObject body, string key, FileErrors errors, string where = null)
        {
            var token = body[key];

            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type == JTokenType.Integer)
            {
                try
                {
                    return token.Value<int>();
                }
                catch (OverflowException)
                {
                    // Reported below; an out-of-range literal still parses as an Integer token.
                }
            }

            errors.Add($"{(where == null ? $"'{key}'" : $"{where}.{key}")} must be a 32-bit integer.");
            return null;
        }

        private static string Hash(string text)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            var builder = new StringBuilder(bytes.Length * 2);

            foreach (var b in bytes)
            {
                builder.Append(b.ToString("x2"));
            }

            return builder.ToString();
        }
    }
}
