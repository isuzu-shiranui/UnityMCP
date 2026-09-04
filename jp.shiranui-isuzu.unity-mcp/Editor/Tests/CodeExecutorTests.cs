using System;
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityMCP.Editor.Handlers;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Covers <see cref="CodeExecutor"/>, the highest-traffic and least constrained tool in
    /// the package. These run in the Editor because compilation needs the live assembly set.
    /// </summary>
    [TestFixture]
    internal sealed class CodeExecutorTests
    {
        private static JObject Run(string code)
        {
            return CodeExecutor.Execute(new JObject { ["code"] = code });
        }

        private static string Base64(string code)
        {
            return Convert.ToBase64String(new UTF8Encoding(false).GetBytes(code));
        }

        [Test]
        public void ReturnsAScalarValue()
        {
            var result = Run("return 1 + 1;");

            Assert.That(result["error"], Is.Null, (string)result["error"]);
            Assert.That(result["returnValue"].Value<int>(), Is.EqualTo(2));
        }

        [Test]
        public void ReturnsNullWithoutAReturnStatement()
        {
            var result = Run("var unused = 1;");

            Assert.That(result["error"], Is.Null);
            Assert.That(result["returnValue"], Is.Null);
        }

        [Test]
        public void SerializesCollectionsStructurally()
        {
            // ToString() on the return value would give
            // "System.Collections.Generic.List`1[System.String]" and tell the caller nothing.
            var result = Run("return new System.Collections.Generic.List<string> { \"a\", \"b\" };");

            Assert.That(result["error"], Is.Null, (string)result["error"]);
            Assert.That(result["returnValue"].Type, Is.EqualTo(JTokenType.Array));
            Assert.That(result["returnValue"].ToObject<string[]>(), Is.EqualTo(new[] { "a", "b" }));
        }

        [Test]
        public void SerializesObjectsWithCamelCaseKeys()
        {
            var result = Run("return new { FirstName = \"x\", ItemCount = 3 };");

            Assert.That(result["error"], Is.Null, (string)result["error"]);
            Assert.That(result["returnValue"]["firstName"].Value<string>(), Is.EqualTo("x"));
            Assert.That(result["returnValue"]["itemCount"].Value<int>(), Is.EqualTo(3));
        }

        [Test]
        public void CapturesDebugLogOutput()
        {
            var result = Run("UnityEngine.Debug.Log(\"hello from snippet\"); return 1;");

            Assert.That(result["output"].Value<string>(), Does.Contain("hello from snippet"));
        }

        [Test]
        public void AcceptsBase64EncodedCode()
        {
            // Base64 exists so a snippet cannot be corrupted by the JSON escaping layers it
            // passes through on the way in.
            var result = CodeExecutor.Execute(new JObject
            {
                ["code_base64"] = Base64("return \"round tripped\";"),
            });

            Assert.That(result["error"], Is.Null, (string)result["error"]);
            Assert.That(result["returnValue"].Value<string>(), Is.EqualTo("round tripped"));
        }

        [Test]
        public void Base64SurvivesEscapeHeavyCode()
        {
            // The snippet declares a literal full of escapes. Compiling it must produce the
            // same string this test would, which is only true if every backslash arrived
            // intact — the thing base64 is here to guarantee.
            var code = "var s = \"line1\\nline2\\ttabbed\\\\backslash\"; return s.Length;";
            var expectedLength = "line1\nline2\ttabbed\\backslash".Length;

            var viaBase64 = CodeExecutor.Execute(new JObject { ["code_base64"] = Base64(code) });

            Assert.That(viaBase64["error"], Is.Null, (string)viaBase64["error"]);
            Assert.That(viaBase64["returnValue"].Value<int>(), Is.EqualTo(expectedLength));
        }

        [Test]
        public void RejectsMalformedBase64()
        {
            var result = CodeExecutor.Execute(new JObject { ["code_base64"] = "not base64!!!" });

            Assert.That(result["error"].Value<string>(), Does.Contain("base64"));
        }

        [Test]
        public void Base64TakesPrecedenceOverPlainCode()
        {
            var result = CodeExecutor.Execute(new JObject
            {
                ["code"] = "return \"plain\";",
                ["code_base64"] = Base64("return \"encoded\";"),
            });

            Assert.That(result["returnValue"].Value<string>(), Is.EqualTo("encoded"));
        }

        [Test]
        public void MissingCodeIsAnError()
        {
            var result = CodeExecutor.Execute(new JObject());

            Assert.That(result["error"], Is.Not.Null);
        }

        [Test]
        public void CompilationErrorsAreReported()
        {
            var result = Run("this is not C#;");

            Assert.That(result["error"].Value<string>(), Does.StartWith("Compilation failed:"));
        }

        [Test]
        public void RuntimeErrorsAreReported()
        {
            var result = Run("throw new System.InvalidOperationException(\"deliberate\");");

            Assert.That(result["error"].Value<string>(), Does.Contain("deliberate"));
            Assert.That(result["error"].Value<string>(), Does.StartWith("Runtime error:"));
        }

        [Test]
        public void RepeatedSnippetsReuseTheCompiledAssembly()
        {
            // Every compilation loads an assembly that .NET cannot unload, so re-running the
            // same snippet must not produce a new one.
            var code = $"return \"cache probe {Guid.NewGuid():N}\";";

            var before = AppDomain.CurrentDomain.GetAssemblies().Length;
            var first = Run(code);
            var afterFirst = AppDomain.CurrentDomain.GetAssemblies().Length;
            var second = Run(code);
            var afterSecond = AppDomain.CurrentDomain.GetAssemblies().Length;

            Assert.That(first["returnValue"].Value<string>(), Is.EqualTo(second["returnValue"].Value<string>()));
            Assert.That(afterFirst, Is.GreaterThan(before), "The first run should compile.");
            Assert.That(afterSecond, Is.EqualTo(afterFirst), "The second run must not compile again.");
        }

        [Test]
        public void IncompleteTaskIsReportedRatherThanAwaited()
        {
            // Blocking on a continuation that may itself need the main thread would deadlock
            // the Editor, so the value is deliberately not read.
            var result = Run("return System.Threading.Tasks.Task.Delay(30000);");

            Assert.That(result["error"], Is.Null, (string)result["error"]);
            Assert.That(result["returnValue"], Is.Null);
            Assert.That(result["note"].Value<string>(), Does.Contain("had not completed"));
        }

        [Test]
        public void CompletedTaskValueIsUnwrapped()
        {
            var result = Run("return System.Threading.Tasks.Task.FromResult(41 + 1);");

            Assert.That(result["error"], Is.Null, (string)result["error"]);
            Assert.That(result["returnValue"].Value<int>(), Is.EqualTo(42));
        }

        [Test]
        public void UnityObjectsAreSummarisedNotExpanded()
        {
            var result = Run(
                "var go = new UnityEngine.GameObject(\"CodeExecutorProbe\"); " +
                "UnityEngine.Object.DestroyImmediate(go); " +
                "return new UnityEngine.Material(UnityEngine.Shader.Find(\"Unlit/Color\"));");

            Assert.That(result["error"], Is.Null, (string)result["error"]);
            Assert.That(result["returnValue"]["type"].Value<string>(), Is.EqualTo("Material"));
        }

        [Test]
        public void SeesAssembliesLoadedAfterTheFirstCompilation()
        {
            // A reference list built once and never rebuilt cannot reach anything loaded
            // later, including the assemblies an earlier snippet created.
            Run("return 1;");

            var result = Run("return typeof(McpCodeExecution.Runner) != null;");

            Assert.That(result["error"], Is.Null, (string)result["error"]);
            Assert.That(result["returnValue"].Value<bool>(), Is.True);
        }
    }
}
