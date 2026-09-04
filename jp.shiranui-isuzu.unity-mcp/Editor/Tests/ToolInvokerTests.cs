using System;
using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Covers argument binding, type coercion, the destructive-call gate and result
    /// serialization in <see cref="ToolInvoker"/>.
    /// </summary>
    [TestFixture]
    internal sealed class ToolInvokerTests
    {
        private enum Speed
        {
            Slow,
            Fast,
        }

        private sealed class Point
        {
            public int X { get; set; }

            public int Y { get; set; }
        }

        private static class Tools
        {
            public static int DeleteCallCount;

            [McpTool("t_echo", "Echoes its arguments.", Idempotency = McpIdempotency.Safe)]
            public static string Echo(
                [McpArg("text", "Text to echo")] string text,
                [McpArg("times", "Repeat count")] int times = 1)
            {
                return string.Concat(Enumerable.Repeat(text, times));
            }

            [McpTool("t_scalars", "Returns the scalars it was given.", Idempotency = McpIdempotency.Safe)]
            public static Point Scalars(
                [McpArg("x", "X")] int x,
                [McpArg("y", "Y")] int y)
            {
                return new Point { X = x, Y = y };
            }

            [McpTool("t_wide", "Returns the 64-bit value it was given.", Idempotency = McpIdempotency.Safe)]
            public static long Wide([McpArg("id", "A 64-bit id")] long id)
            {
                return id;
            }

            [McpTool("t_flag", "Returns the flag it was given.", Idempotency = McpIdempotency.Safe)]
            public static bool Flag([McpArg("enabled", "Whether enabled")] bool enabled)
            {
                return enabled;
            }

            [McpTool("t_speed", "Returns the enum it was given.", Idempotency = McpIdempotency.Safe)]
            public static string Speed_([McpArg("speed", "Speed")] Speed speed)
            {
                return speed.ToString();
            }

            [McpTool("t_list", "Counts the items it was given.", Idempotency = McpIdempotency.Safe)]
            public static int Count([McpArg("items", "Items")] List<string> items)
            {
                return items?.Count ?? -1;
            }

            [McpTool("t_void", "Returns nothing.", Idempotency = McpIdempotency.Safe)]
            public static void Nothing()
            {
            }

            [McpTool("t_throws", "Always fails.")]
            public static void Throws()
            {
                throw new InvalidOperationException("boom");
            }

            [McpTool("t_delete", "Destructive operation.", Destructive = true)]
            public static string Delete([McpArg("path", "Path")] string path)
            {
                DeleteCallCount++;
                return path;
            }
        }

        private static ToolCatalog catalog;

        [SetUp]
        public void SetUp()
        {
            catalog = ToolCatalog.BuildFromTypes(new[] { typeof(Tools) });
            Tools.DeleteCallCount = 0;
            Assert.That(catalog.Errors, Is.Empty, "Fixture tools must all register cleanly.");
        }

        private static JObject Invoke(string name, JObject args)
        {
            Assert.That(catalog.TryGet(name, out var descriptor), Is.True, $"Tool '{name}' was not discovered.");
            return ToolInvoker.Invoke(descriptor, args);
        }

        /// <summary>
        /// A tool whose body is a delegate rather than a discovered method.
        /// </summary>
        private static McpToolDescriptor Direct(string name, Func<JObject, JObject> body, bool destructive = false)
        {
            return new McpToolDescriptor(
                name,
                "A tool with no backing method.",
                new JObject { ["type"] = "object", ["properties"] = new JObject() },
                McpIdempotency.Unsafe,
                true,
                destructive,
                null,
                null,
                false,
                0,
                "Assets/Defined/" + name + ".json",
                body);
        }

        [Test]
        public void BindsRequiredAndDefaultedArguments()
        {
            var result = Invoke("t_echo", new JObject { ["text"] = "ab" });

            Assert.That(result["result"].Value<string>(), Is.EqualTo("ab"), "times must fall back to its default of 1.");
        }

        [Test]
        public void MissingRequiredArgumentIsInvalidParams()
        {
            var ex = Assert.Throws<McpToolException>(() => Invoke("t_echo", new JObject()));

            Assert.That(ex.Code, Is.EqualTo("invalid_params"));
            Assert.That(ex.Message, Does.Contain("text"), "The error must name the missing argument.");
        }

        [Test]
        public void CoercesNumericStringToInteger()
        {
            // MCP clients routinely send scalars as strings; rejecting that produces a
            // failure the model cannot diagnose from the error text.
            var result = Invoke("t_scalars", new JObject { ["x"] = "3", ["y"] = 4 });

            Assert.That(result["x"].Value<int>(), Is.EqualTo(3));
            Assert.That(result["y"].Value<int>(), Is.EqualTo(4));
        }

        [Test]
        public void A64BitIntegerKeepsEveryBit()
        {
            // A Unity 6.5 EntityId is around 5.7e17, above the 2^53 a double holds exactly. Coercing
            // through a double rounds the low bits off, and the id then names a different object.
            const long id = (1L << 53) + 1;

            Assert.That(Invoke("t_wide", new JObject { ["id"] = id })["result"].Value<long>(), Is.EqualTo(id),
                        "a JSON integer lost precision");
            Assert.That(Invoke("t_wide", new JObject { ["id"] = id.ToString() })["result"].Value<long>(), Is.EqualTo(id),
                        "a numeric string lost precision");
        }

        [Test]
        public void RejectsFractionalValueForIntegerParameter()
        {
            var ex = Assert.Throws<McpToolException>(() => Invoke("t_scalars", new JObject { ["x"] = 1.5, ["y"] = 0 }));

            Assert.That(ex.Code, Is.EqualTo("invalid_params"));
        }

        [Test]
        public void CoercesBooleanStrings()
        {
            Assert.That(Invoke("t_flag", new JObject { ["enabled"] = "true" })["result"].Value<bool>(), Is.True);
            Assert.That(Invoke("t_flag", new JObject { ["enabled"] = "false" })["result"].Value<bool>(), Is.False);
            Assert.That(Invoke("t_flag", new JObject { ["enabled"] = 1 })["result"].Value<bool>(), Is.True);
        }

        [Test]
        public void CoercesEnumByNameCaseInsensitively()
        {
            var result = Invoke("t_speed", new JObject { ["speed"] = "fast" });

            Assert.That(result["result"].Value<string>(), Is.EqualTo("Fast"));
        }

        [Test]
        public void UnknownEnumValueListsTheValidOnes()
        {
            var ex = Assert.Throws<McpToolException>(() => Invoke("t_speed", new JObject { ["speed"] = "sideways" }));

            Assert.That(ex.Message, Does.Contain("Slow").And.Contain("Fast"),
                "The error text is the model's only cue for correcting the call.");
        }

        [Test]
        public void BindsJsonArrayToList()
        {
            var result = Invoke("t_list", new JObject { ["items"] = new JArray("a", "b", "c") });

            Assert.That(result["result"].Value<int>(), Is.EqualTo(3));
        }

        [Test]
        public void WrapsBareValueAsSingleElementList()
        {
            var result = Invoke("t_list", new JObject { ["items"] = "only" });

            Assert.That(result["result"].Value<int>(), Is.EqualTo(1));
        }

        [Test]
        public void VoidReturnBecomesOkObject()
        {
            var result = Invoke("t_void", new JObject());

            Assert.That(result["ok"].Value<bool>(), Is.True);
        }

        [Test]
        public void ObjectReturnIsNotWrapped()
        {
            var result = Invoke("t_scalars", new JObject { ["x"] = 1, ["y"] = 2 });

            Assert.That(result["result"], Is.Null, "Object results keep their own field names.");
            Assert.That(result["x"].Value<int>(), Is.EqualTo(1));
        }

        [Test]
        public void ObjectReturnUsesCamelCaseKeys()
        {
            // The rest of the API is camelCase (jobId, queueDepth, inputSchema); a tool
            // returning a plain C# object must not leak PascalCase identifiers onto the wire.
            var result = Invoke("t_scalars", new JObject { ["x"] = 1, ["y"] = 2 });

            Assert.That(result["X"], Is.Null, "PascalCase property names must not reach the wire.");
            Assert.That(result["x"].Value<int>(), Is.EqualTo(1));
            Assert.That(result["y"].Value<int>(), Is.EqualTo(2));
        }

        [Test]
        public void ToolExceptionCarriesToolNameAndCode()
        {
            var ex = Assert.Throws<McpToolException>(() => Invoke("t_throws", new JObject()));

            Assert.That(ex.Code, Is.EqualTo("tool_failed"));
            Assert.That(ex.HttpStatus, Is.EqualTo(500));
            Assert.That(ex.Message, Does.Contain("boom"));
        }

        [Test]
        public void DestructiveToolRefusesWithoutConfirm()
        {
            var ex = Assert.Throws<McpToolException>(() => Invoke("t_delete", new JObject { ["path"] = "Assets/x" }));

            Assert.That(ex.Code, Is.EqualTo("confirmation_required"));
            Assert.That(ex.HttpStatus, Is.EqualTo(409));
            Assert.That(Tools.DeleteCallCount, Is.Zero, "The tool body must not run without confirmation.");
        }

        [Test]
        public void DestructiveToolRunsWithConfirm()
        {
            var result = Invoke("t_delete", new JObject { ["path"] = "Assets/x", ["confirm"] = true });

            Assert.That(result["result"].Value<string>(), Is.EqualTo("Assets/x"));
            Assert.That(Tools.DeleteCallCount, Is.EqualTo(1));
        }

        [Test]
        public void DryRunReportsWithoutExecuting()
        {
            var result = Invoke("t_delete", new JObject { ["path"] = "Assets/x", ["dry_run"] = true });

            Assert.That(result["dry_run"].Value<bool>(), Is.True);
            Assert.That(result["arguments"]["path"].Value<string>(), Is.EqualTo("Assets/x"));
            Assert.That(Tools.DeleteCallCount, Is.Zero, "A dry run must not execute the tool body.");
        }

        [Test]
        public void DryRunStillValidatesArguments()
        {
            var ex = Assert.Throws<McpToolException>(() => Invoke("t_delete", new JObject { ["dry_run"] = true }));

            Assert.That(ex.Code, Is.EqualTo("invalid_params"),
                "A preview whose arguments do not bind is not a useful preview.");
        }

        [Test]
        public void DirectToolReturnsItsObject()
        {
            var descriptor = Direct("t_direct", args => new JObject { ["seen"] = args["text"] });

            var result = ToolInvoker.Invoke(descriptor, new JObject { ["text"] = "hello" });

            Assert.That(result["seen"].Value<string>(), Is.EqualTo("hello"),
                "A direct tool's object is the result, unwrapped, exactly as a method tool's is.");
        }

        [Test]
        public void DirectToolNullBecomesOk()
        {
            var descriptor = Direct("t_direct_null", _ => null);

            var result = ToolInvoker.Invoke(descriptor, new JObject());

            Assert.That(result["ok"].Value<bool>(), Is.True);
        }

        [Test]
        public void DirectToolExceptionIsToolFailed()
        {
            var descriptor = Direct("t_direct_throws", _ => throw new InvalidOperationException("boom"));

            var failure = Assert.Throws<McpToolException>(() => ToolInvoker.Invoke(descriptor, new JObject()));

            Assert.That(failure.Code, Is.EqualTo("tool_failed"));
            Assert.That(failure.HttpStatus, Is.EqualTo(500));
            Assert.That(failure.Message, Does.Contain("t_direct_throws").And.Contain("boom"));

            var refusing = Direct("t_direct_refuses", _ => throw new McpToolException("invalid_params", "no path", 400));

            var refusal = Assert.Throws<McpToolException>(() => ToolInvoker.Invoke(refusing, new JObject()));

            Assert.That(refusal.Code, Is.EqualTo("invalid_params"),
                "A tool's own refusal already carries the code the caller acts on.");
        }

        [Test]
        public void DirectDestructiveToolNeedsConfirmAndSupportsDryRun()
        {
            var calls = 0;
            var descriptor = Direct(
                "t_direct_delete",
                args =>
                {
                    calls++;
                    return new JObject { ["path"] = args["path"] };
                },
                destructive: true);

            var refusal = Assert.Throws<McpToolException>(
                () => ToolInvoker.Invoke(descriptor, new JObject { ["path"] = "Assets/x" }));

            Assert.That(refusal.Code, Is.EqualTo("confirmation_required"));
            Assert.That(calls, Is.Zero, "The tool body must not run without confirmation.");

            var preview = ToolInvoker.Invoke(descriptor, new JObject { ["path"] = "Assets/x", ["dry_run"] = true });

            Assert.That(preview["dry_run"].Value<bool>(), Is.True);
            Assert.That(preview["tool"].Value<string>(), Is.EqualTo("t_direct_delete"));
            Assert.That(preview["would_execute"].Value<bool>(), Is.True);
            Assert.That(preview["arguments"]["path"].Value<string>(), Is.EqualTo("Assets/x"));
            Assert.That(preview["arguments"]["dry_run"], Is.Null, "The invoker's own flags are not arguments.");
            Assert.That(calls, Is.Zero, "A dry run must not execute the tool body.");

            var executed = ToolInvoker.Invoke(descriptor, new JObject { ["path"] = "Assets/x", ["confirm"] = true });

            Assert.That(executed["path"].Value<string>(), Is.EqualTo("Assets/x"));
            Assert.That(calls, Is.EqualTo(1));
        }
    }
}
