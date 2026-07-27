using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

using UnityEngine;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Handlers
{
    /// <summary>
    /// Compiles and runs a C# snippet inside the Editor.
    /// </summary>
    internal static class CodeExecutor
    {
        private static List<MetadataReference> cachedReferences;

        /// <summary>
        /// Assembly count when <see cref="cachedReferences"/> was built.
        /// </summary>
        /// <remarks>
        /// v2 built the reference list once and never rebuilt it, so a snippet could not see
        /// any assembly loaded after the first execution — including the ones this very method
        /// creates. Rebuilding when the count moves costs about a tenth of a second on a new
        /// snippet and makes "type exists but the compiler cannot see it" go away.
        /// </remarks>
        private static int cachedReferenceAssemblyCount = -1;

        /// <summary>
        /// Compiled snippets keyed by a hash of their source.
        /// </summary>
        /// <remarks>
        /// <c>Assembly.Load(byte[])</c> loads into the current domain and cannot be unloaded,
        /// so v2 leaked one assembly per call — iterating on a snippet a dozen times left a
        /// dozen behind. Caching does not make loading unloadable, but it stops the common
        /// case (running the same code repeatedly) from growing the domain at all.
        /// </remarks>
        private static readonly Dictionary<string, MethodInfo> CompiledCache = new(StringComparer.Ordinal);

        /// <summary>Distinct snippets after which the accumulated assemblies are worth mentioning.</summary>
        private const int DistinctSnippetWarningThreshold = 100;

        private static bool warnedAboutAssemblyGrowth;

        private static readonly string[] DefaultUsings =
        {
            "System",
            "System.Collections",
            "System.Collections.Generic",
            "System.Linq",
            "System.Threading.Tasks",
            "UnityEngine",
            "UnityEditor"
        };

        /// <summary>Serializer for return values; matches the camelCase used everywhere else.</summary>
        private static readonly JsonSerializer ValueSerializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore,
            MaxDepth = 12,
        });

        public static JObject Execute(JObject parameters)
        {
            string code;

            try
            {
                code = ReadCode(parameters);
            }
            catch (McpToolException e)
            {
                return new JObject { ["error"] = e.Message };
            }

            if (string.IsNullOrEmpty(code))
            {
                return new JObject { ["error"] = "Code parameter is required" };
            }

            try
            {
                var wrappedCode = Wrap(code);
                var hash = Hash(wrappedCode);

                if (!CompiledCache.TryGetValue(hash, out var method))
                {
                    var compiled = Compile(wrappedCode, out var errors);

                    if (compiled == null)
                    {
                        return new JObject { ["error"] = "Compilation failed:\n" + string.Join("\n", errors) };
                    }

                    method = compiled;
                    CompiledCache[hash] = method;
                    WarnIfDomainGrowing();
                }

                return Run(method);
            }
            catch (Exception e)
            {
                return new JObject { ["error"] = $"Error: {e.Message}" };
            }
        }

        /// <summary>
        /// Reads the snippet from either <c>code</c> or <c>code_base64</c>.
        /// </summary>
        /// <remarks>
        /// A snippet built by a real JSON encoder arrives intact through <c>code</c>. Base64 is
        /// for the case where the request is assembled by hand — a shell heredoc, string
        /// concatenation — and some layer reinterprets the backslashes in a C# string literal.
        /// The failure then surfaces as a compile error inside generated source the caller
        /// never sees ("Unrecognized escape sequence", "Newline in constant"), which is close
        /// to undiagnosable from the outside. Base64 has no escapes left to mangle.
        /// </remarks>
        private static string ReadCode(JObject parameters)
        {
            var encoded = parameters["code_base64"]?.ToString();

            if (!string.IsNullOrEmpty(encoded))
            {
                try
                {
                    return new UTF8Encoding(false).GetString(Convert.FromBase64String(encoded));
                }
                catch (FormatException e)
                {
                    throw new McpToolException("invalid_params", $"code_base64 is not valid base64: {e.Message}");
                }
            }

            return parameters["code"]?.ToString();
        }

        private static string Wrap(string code)
        {
            var usings = string.Join("\n", DefaultUsings.Select(u => $"using {u};"));

            return $@"
{usings}

namespace McpCodeExecution
{{
    public static class Runner
    {{
        public static object Execute()
        {{
            {code}
            return null;
        }}
    }}
}}";
        }

        private static MethodInfo Compile(string wrappedCode, out string[] errors)
        {
            EnsureReferences();

            var syntaxTree = CSharpSyntaxTree.ParseText(
                wrappedCode,
                new CSharpParseOptions(LanguageVersion.Latest));

            var compilation = CSharpCompilation.Create(
                "McpDynamic_" + Guid.NewGuid().ToString("N"),
                new[] { syntaxTree },
                cachedReferences,
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release,
                    allowUnsafe: true));

            using var stream = new MemoryStream();
            var emitResult = compilation.Emit(stream);

            if (!emitResult.Success)
            {
                errors = emitResult.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.GetMessage())
                    .ToArray();

                return null;
            }

            errors = Array.Empty<string>();

            var assembly = Assembly.Load(stream.ToArray());
            return assembly.GetType("McpCodeExecution.Runner")?.GetMethod("Execute");
        }

        /// <summary>
        /// Rebuilds the metadata reference list when the set of loaded assemblies has changed.
        /// </summary>
        private static void EnsureReferences()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            if (cachedReferences != null && assemblies.Length == cachedReferenceAssemblyCount)
            {
                return;
            }

            var references = new List<MetadataReference>(assemblies.Length);

            foreach (var assembly in assemblies)
            {
                if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                {
                    continue;
                }

                try
                {
                    if (File.Exists(assembly.Location))
                    {
                        references.Add(MetadataReference.CreateFromFile(assembly.Location));
                    }
                }
                catch
                {
                    // An assembly whose file cannot be read simply is not referenceable.
                }
            }

            cachedReferences = references;
            cachedReferenceAssemblyCount = assemblies.Length;
        }

        private static JObject Run(MethodInfo method)
        {
            var capturedOutput = new StringBuilder();
            var executingThreadId = Thread.CurrentThread.ManagedThreadId;

            // Only lines logged by this thread are captured. The event fires for every source
            // in the Editor, so without the check a snippet's output would be interleaved with
            // whatever background work happened to log at the same moment.
            void LogHandler(string message, string stackTrace, LogType logType)
            {
                if (Thread.CurrentThread.ManagedThreadId == executingThreadId)
                {
                    capturedOutput.AppendLine($"[{logType}] {message}");
                }
            }

            Application.logMessageReceived += LogHandler;

            object returnValue;
            try
            {
                returnValue = method.Invoke(null, null);
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                return new JObject { ["error"] = $"Runtime error: {inner.Message}" };
            }
            finally
            {
                Application.logMessageReceived -= LogHandler;
            }

            var result = new JObject
            {
                ["output"] = capturedOutput.ToString().TrimEnd(),
            };

            var (token, note) = DescribeReturnValue(returnValue);

            if (token != null)
            {
                result["returnValue"] = token;
            }

            if (note != null)
            {
                result["note"] = note;
            }

            return result;
        }

        /// <summary>
        /// Serializes the snippet's return value.
        /// </summary>
        /// <remarks>
        /// v2 called <c>ToString()</c>, so returning a list produced the string
        /// "System.Collections.Generic.List`1[UnityEngine.GameObject]" — the caller learned the
        /// type and nothing else. Structured serialization means a returned collection arrives
        /// as an actual array.
        /// </remarks>
        private static (JToken Token, string Note) DescribeReturnValue(object returnValue)
        {
            switch (returnValue)
            {
                case null:
                    return (null, null);

                case Task task:
                    // Waiting here would block the Editor main thread on a continuation that
                    // may itself need that thread, which deadlocks the Editor outright.
                    if (!task.IsCompleted)
                    {
                        return (null,
                            "The snippet returned a Task that had not completed. Awaiting it would block the " +
                            "Editor main thread, so its value was not read. Drive the work to completion inside " +
                            "the snippet, or poll for the result from a later call.");
                    }

                    if (task.IsFaulted)
                    {
                        var inner = task.Exception?.GetBaseException();
                        return (null, $"The returned Task faulted: {inner?.Message}");
                    }

                    var taskType = task.GetType();
                    if (taskType.IsGenericType)
                    {
                        var value = taskType.GetProperty("Result")?.GetValue(task);
                        return (Serialize(value), null);
                    }

                    return (null, "The snippet returned a completed Task with no value.");

                default:
                    return (Serialize(returnValue), null);
            }
        }

        private static JToken Serialize(object value)
        {
            if (value == null)
            {
                return JValue.CreateNull();
            }

            if (value is JToken token)
            {
                return token;
            }

            if (value is UnityEngine.Object unityObject)
            {
                // A live UnityEngine.Object serializes into an enormous, mostly useless graph.
                return new JObject
                {
                    ["name"] = unityObject.name,
                    ["type"] = unityObject.GetType().Name,
                };
            }

            try
            {
                return JToken.FromObject(value, ValueSerializer);
            }
            catch (Exception e)
            {
                // Falling back to ToString is still better than failing the whole call.
                return new JValue($"{value} (not serializable: {e.Message})");
            }
        }

        private static void WarnIfDomainGrowing()
        {
            if (warnedAboutAssemblyGrowth || CompiledCache.Count < DistinctSnippetWarningThreshold)
            {
                return;
            }

            warnedAboutAssemblyGrowth = true;
            Debug.LogWarning(
                $"[CodeExecutor] {CompiledCache.Count} distinct snippets have been compiled this session. " +
                "Each loads an assembly that .NET cannot unload; a domain reload (recompile or entering " +
                "play mode) reclaims them.");
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
