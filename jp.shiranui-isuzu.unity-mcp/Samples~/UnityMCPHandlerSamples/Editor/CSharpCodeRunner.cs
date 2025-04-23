using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnityEngine;

namespace UnityMCPHandlerSamples.Editor
{
    /// <summary>
    /// Provides functionality to execute C# code at runtime within Unity.
    /// </summary>
    public sealed class CSharpCodeRunner
    {
        private readonly List<MetadataReference> references;
        private readonly HashSet<string> availableNamespaces;

        // List of excluded namespaces to avoid ambiguous references
        private static readonly HashSet<string> ExcludedNamespaces = new()
        {
            // Namespaces that conflict with Unity types
            "System.Drawing", // Conflicts with UnityEngine.Color
            "System.Numerics", // Conflicts with UnityEngine.Vector3, UnityEngine.Quaternion
            "System.Diagnostics", // Conflicts with UnityEngine.Debug
            "UnityEngine.Experimental.GlobalIllumination", // Conflicts with UnityEngine.LightType

            // Other namespaces to exclude
            "FxResources",
            "Internal",
            "MS.Internal",
            "Mono.Cecil",
            "JetBrains",
            "Microsoft.CodeAnalysis.CSharp.Syntax.InternalSyntax",
            "Microsoft.Cci",
            "Microsoft.Win32",
            "Unity.Android.Gradle",
            "Unity.Android.Types",
            "System.Web",
            "System.Data.SqlClient",
            "System.Data.Sql",
            "System.Runtime.Remoting",
            "System.Runtime.Serialization.Formatters",
            "System.Runtime.InteropServices.ComTypes",
            "System.Security.Cryptography.X509Certificates",
            "System.Security.AccessControl",
            "System.Web.UI.WebControls",
            "System.Web.UI.HtmlControls",
            "Microsoft.SqlServer",
            "Microsoft.VisualBasic",
            "Mono.Net",
            "Mono.Util",
            "Mono.Math",
            "Microsoft.DiaSymReader",
            "Microsoft.CSharp"
        };

        // Priority namespaces (exact match)
        private static readonly HashSet<string> PriorityNamespaces = new()
        {
            "System",
            "System.Collections",
            "System.Collections.Generic",
            "System.Linq",
            "UnityEngine",
            "UnityEngine.UI",
            "UnityEngine.SceneManagement",
            "UnityEngine.Animations",
            "UnityEngine.Audio",
            "UnityEngine.Rendering",
            "UnityEngine.Playables",
            "UnityEditor"
        };

        // Priority namespace prefixes
        private static readonly string[] PriorityPrefixes = {
            "UnityEngine.",
            "UnityEditor.",
            "System.",
            "TMPro.",
            "Unity.Collections.",
            "Unity.Mathematics.",
            "Unity.Jobs."
        };

        /// <summary>
        /// Initializes a new instance of the <see cref="CSharpCodeRunner"/> class.
        /// </summary>
        public CSharpCodeRunner()
        {
            this.references = new List<MetadataReference>();
            this.availableNamespaces = new HashSet<string>(PriorityNamespaces);

            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(x => !x.IsDynamic && !string.IsNullOrEmpty(x.Location));

            foreach (var assembly in assemblies)
            {
                try
                {
                    this.references.Add(MetadataReference.CreateFromFile(assembly.Location));
                    this.CollectNamespacesFromAssembly(assembly);
                }
                catch (Exception)
                {
                    // Skip assemblies that cannot be referenced
                }
            }
        }

        /// <summary>
        /// Collects namespaces from the specified assembly.
        /// </summary>
        /// <param name="assembly">The assembly to collect namespaces from.</param>
        private void CollectNamespacesFromAssembly(Assembly assembly)
        {
            try
            {
                // Get types from the assembly and collect their namespaces
                var types = assembly.GetTypes();
                foreach (var type in types)
                {
                    if (!string.IsNullOrEmpty(type.Namespace) &&
                        !ExcludedNamespaces.Contains(type.Namespace) &&
                        !ShouldExcludeByPrefix(type.Namespace))
                    {
                        this.availableNamespaces.Add(type.Namespace);
                    }
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Even if some types can't be loaded, collect namespaces from the ones that can
                foreach (var type in ex.Types)
                {
                    if (type != null &&
                        !string.IsNullOrEmpty(type.Namespace) &&
                        !ExcludedNamespaces.Contains(type.Namespace) &&
                        !ShouldExcludeByPrefix(type.Namespace))
                    {
                        this.availableNamespaces.Add(type.Namespace);
                    }
                }
            }
            catch (Exception)
            {
                // Ignore other exceptions
            }
        }

        /// <summary>
        /// Determines whether a namespace should be excluded based on its prefix.
        /// </summary>
        /// <param name="ns">The namespace to check.</param>
        /// <returns>True if the namespace should be excluded, false otherwise.</returns>
        private bool ShouldExcludeByPrefix(string ns)
        {
            // Don't exclude namespaces that start with priority prefixes
            foreach (var prefix in PriorityPrefixes)
            {
                if (ns.StartsWith(prefix))
                {
                    return false;
                }
            }

            // Check for prefixes that should be excluded
            var excludePrefixes = new[]
            {
                "Microsoft.CodeAnalysis.",
                "Microsoft.Cci.",
                "Microsoft.Win32.",
                "Mono.Cecil.",
                "JetBrains.",
                "System.Web.",
                "System.Runtime.Remoting.",
                "System.Security.Cryptography.",
                "System.Data.Sql",
                "MS.Internal.",
                "Internal.",
                "FxResources."
            };

            return excludePrefixes.Any(prefix => ns.StartsWith(prefix));
        }

        /// <summary>
        /// Runs the specified C# code asynchronously.
        /// </summary>
        /// <param name="command">The C# code to execute.</param>
        public void RunAsync(string command)
        {
            try
            {
                var result = this.CompileAndExecute(command);
                if (!result.Success)
                {
                    Debug.LogError($"Error executing code: {result.ErrorMessage}");
                    Debug.LogError("Write C# code that follows these format guidelines to ensure proper execution in Unity:\n\n1. Do not include 'using' statements - they are already included\n2. Do not wrap your code in a class or method - it will be automatically wrapped\n3. Write straight executable code that would be valid inside a method body\n4. For returning values, use a 'return' statement at the end of your code\n5. You can use Unity and .NET APIs directly (UnityEngine, UnityEditor, System, etc.)\n6. Available namespaces include: System, System.Collections, System.Collections.Generic, System.Linq, UnityEngine, UnityEditor\n\nExample valid code:\n```csharp\nvar activeObjects = UnityEngine.Object.FindObjectsByType<UnityEngine.GameObject>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None);\n    .Where(go => go.activeInHierarchy)\n    .ToList();\n    \nDebug.Log($\"Found {activeObjects.Count} active GameObjects\");\nforeach (var obj in activeObjects.Take(5)) {\n    Debug.Log($\"Object: {obj.name}\");\n}\nreturn activeObjects.Count;\n```\n\nThis code will be executed in the context of:\n\nusing System;\nusing System.Collections;\nusing System.Collections.Generic;\nusing System.Linq;\nusing UnityEngine;\nusing UnityEditor;\n\nnamespace CodeExecutionContainer\n{\n    public static class CodeExecutor\n    {\n        public static object Execute()\n        {\n            {code}\n            return null;\n        }\n    }\n}");
                    return;
                }

                if (result.ReturnValue != null)
                {
                    Debug.Log($"Result: {result.ReturnValue}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error executing code: {ex.Message}");
            }
        }

        /// <summary>
        /// Wraps the specified code in a class with a static method.
        /// </summary>
        /// <param name="code">The code to wrap.</param>
        /// <returns>The wrapped code.</returns>
        private string WrapCodeInClass(string code)
        {
            // Explicitly list priority namespaces first
            var explicitImports = string.Join("\n", PriorityNamespaces
                .OrderBy(ns => ns)
                .Select(ns => $"using {ns};"));

            // Then include other namespaces
            var otherImports = string.Join("\n", this.availableNamespaces
                .Except(PriorityNamespaces)
                .OrderBy(ns => ns)
                .Select(ns => $"using {ns};"));

            return $@"
// Priority namespaces
{explicitImports}

// Other namespaces
{otherImports}

namespace CodeExecutionContainer
{{
    public static class CodeExecutor
    {{
        public static object Execute()
        {{
            {code}
            return null;
        }}
    }}
}}";
        }

        /// <summary>
        /// Compiles and executes the specified C# code.
        /// </summary>
        /// <param name="code">The C# code to compile and execute.</param>
        /// <returns>The result of the compilation and execution.</returns>
        private EvaluationResult CompileAndExecute(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return new EvaluationResult
                {
                    Success = false,
                    ErrorMessage = "Code cannot be null or empty"
                };
            }

            // Wrap the code in a class with a static method that returns the result
            var wrappedCode = this.WrapCodeInClass(code);

            // Compile the code
            var result = this.CompileCode(wrappedCode);

            if (!result.Success)
            {
                return result;
            }

            // Execute the compiled code
            try
            {
                var assembly = result.CompiledAssembly;
                if (assembly == null)
                {
                    return new EvaluationResult
                    {
                        Success = false,
                        ErrorMessage = "Failed to compile the code"
                    };
                }

                var type = assembly.GetType("CodeExecutionContainer.CodeExecutor");
                if (type == null)
                {
                    return new EvaluationResult
                    {
                        Success = false,
                        ErrorMessage = "Failed to find the CodeExecutor type"
                    };
                }

                var method = type.GetMethod("Execute");
                if (method == null)
                {
                    return new EvaluationResult
                    {
                        Success = false,
                        ErrorMessage = "Failed to find the Execute method"
                    };
                }

                var returnValue = method.Invoke(null, null);

                return new EvaluationResult
                {
                    Success = true,
                    ReturnValue = returnValue
                };
            }
            catch (Exception ex)
            {
                return new EvaluationResult
                {
                    Success = false,
                    ErrorMessage = $"Runtime error: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Compiles the specified C# code.
        /// </summary>
        /// <param name="code">The C# code to compile.</param>
        /// <returns>The result of the compilation.</returns>
        private EvaluationResult CompileCode(string code)
        {
            var options = new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                allowUnsafe: true);

            var syntaxTree = CSharpSyntaxTree.ParseText(code);
            var compilation = CSharpCompilation.Create(
                "DynamicAssembly_" + Guid.NewGuid().ToString("N"),
                new[] { syntaxTree },
                this.references,
                options);

            using (var ms = new MemoryStream())
            {
                var emitResult = compilation.Emit(ms);

                if (!emitResult.Success)
                {
                    var errors = emitResult.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => d.GetMessage())
                        .ToArray();

                    return new EvaluationResult
                    {
                        Success = false,
                        ErrorMessage = string.Join(Environment.NewLine, errors)
                    };
                }

                ms.Seek(0, SeekOrigin.Begin);
                var assembly = Assembly.Load(ms.ToArray());

                return new EvaluationResult
                {
                    Success = true,
                    CompiledAssembly = assembly
                };
            }
        }
    }

    /// <summary>
    /// Represents the result of a code evaluation.
    /// </summary>
    internal sealed class EvaluationResult
    {
        /// <summary>
        /// Gets or sets a value indicating whether the evaluation was successful.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Gets or sets the return value from the executed code.
        /// </summary>
        public object ReturnValue { get; set; }

        /// <summary>
        /// Gets or sets the error message if evaluation failed.
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Gets or sets the compiled assembly.
        /// </summary>
        public Assembly CompiledAssembly { get; set; }
    }
}
