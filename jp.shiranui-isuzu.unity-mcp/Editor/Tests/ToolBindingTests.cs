using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Covers the compiled argument binder: that every shipped signature compiles, that each
    /// bind kind produces the same value the boxing path did, and that binding a call allocates
    /// nothing beyond its result.
    /// </summary>
    [TestFixture]
    internal sealed class ToolBindingTests
    {
        private enum Speed
        {
            Slow,
            Fast,
        }

        private static class Tools
        {
            [McpTool("b_values", "Returns the scalars it was given.", Idempotency = McpIdempotency.Safe)]
            public static string Values(
                [McpArg("i", "An int")] int i,
                [McpArg("l", "A long")] long l,
                [McpArg("d", "A double")] double d,
                [McpArg("f", "A float")] float f,
                [McpArg("b", "A bool")] bool b)
            {
                return string.Join(
                    "|",
                    i.ToString(CultureInfo.InvariantCulture),
                    l.ToString(CultureInfo.InvariantCulture),
                    d.ToString(CultureInfo.InvariantCulture),
                    f.ToString(CultureInfo.InvariantCulture),
                    b ? "true" : "false");
            }

            [McpTool("b_nullable", "Reports whether it received a value.", Idempotency = McpIdempotency.Safe)]
            public static string Optional(
                [McpArg("count", "An optional count")] int? count = null,
                [McpArg("weight", "An optional weight")] double? weight = null)
            {
                return (count.HasValue ? count.Value.ToString(CultureInfo.InvariantCulture) : "none") +
                       "/" +
                       (weight.HasValue ? weight.Value.ToString(CultureInfo.InvariantCulture) : "none");
            }

            [McpTool("b_enum", "Returns the enum it was given.", Idempotency = McpIdempotency.Safe)]
            public static string Speed_([McpArg("speed", "Speed")] Speed speed = Speed.Slow)
            {
                return speed.ToString();
            }

            [McpTool("b_array", "Sums the array it was given.", Idempotency = McpIdempotency.Safe)]
            public static int Sum([McpArg("values", "Values")] int[] values)
            {
                return values.Sum();
            }

            [McpTool("b_list", "Joins the list it was given.", Idempotency = McpIdempotency.Safe)]
            public static string Join([McpArg("items", "Items")] List<string> items)
            {
                return string.Join("+", items);
            }

            [McpTool("b_json", "Reads one field of the object it was given.", Idempotency = McpIdempotency.Safe)]
            public static string Json([McpArg("payload", "Payload")] JObject payload)
            {
                return payload["k"]?.Value<string>() ?? "missing";
            }

            [McpTool("b_default", "Returns its argument or the default.", Idempotency = McpIdempotency.Safe)]
            public static string Defaulted([McpArg("text", "Text")] string text = "fallback")
            {
                return text;
            }

            [McpTool("b_alloc_five", "Five value-type arguments.", Idempotency = McpIdempotency.Safe)]
            public static int AllocFive(
                [McpArg("a", "a")] int a,
                [McpArg("b", "b")] long b,
                [McpArg("c", "c")] float c,
                [McpArg("d", "d")] double d,
                [McpArg("e", "e")] bool e)
            {
                return e ? a : (int)b;
            }

            [McpTool("b_alloc_none", "No arguments.", Idempotency = McpIdempotency.Safe)]
            public static int AllocNone()
            {
                return 1;
            }
        }

        private static ToolCatalog catalog;

        [SetUp]
        public void SetUp()
        {
            catalog = ToolCatalog.BuildFromTypes(new[] { typeof(Tools) });
            Assert.That(catalog.Errors, Is.Empty, "Fixture tools must all register cleanly.");
        }

        private static McpToolDescriptor Descriptor(string name)
        {
            Assert.That(catalog.TryGet(name, out var descriptor), Is.True, $"Tool '{name}' was not discovered.");
            return descriptor;
        }

        private static JObject Invoke(string name, JObject args)
        {
            return ToolInvoker.Invoke(Descriptor(name), args);
        }

        [Test]
        public void EveryLiveToolCompiles()
        {
            var live = ToolCatalog.Build().Tools.ToList();

            Assert.That(live, Is.Not.Empty, "The live catalog is what gives this test its coverage.");

            var fallbacks = live
                .Where(descriptor => descriptor.Direct == null && descriptor.UsesReflectionFallback)
                .Select(descriptor => $"{descriptor.Name}: {descriptor.BindPlan.CompileError}")
                .ToList();

            Assert.That(fallbacks, Is.Empty, "Every shipped signature has to compile; these did not.");
        }

        [Test]
        public void BindsEveryValueTypeKind()
        {
            var result = Invoke(
                "b_values",
                new JObject { ["i"] = 3, ["l"] = 4L, ["d"] = 1.5, ["f"] = 2.5, ["b"] = true });

            Assert.That(result["result"].Value<string>(), Is.EqualTo("3|4|1.5|2.5|true"));
        }

        [Test]
        public void CoercesStringsToValueTypes()
        {
            var result = Invoke(
                "b_values",
                new JObject { ["i"] = "3", ["l"] = "4", ["d"] = "1.5", ["f"] = "2.5", ["b"] = "true" });

            Assert.That(result["result"].Value<string>(), Is.EqualTo("3|4|1.5|2.5|true"));
        }

        [Test]
        public void A64BitArgumentKeepsEveryBitThroughTheCompiledBinder()
        {
            // A Unity 6.5 EntityId is around 5.7e17, above the 2^53 a double holds exactly.
            const long Id = (1L << 53) + 1;

            var result = Invoke(
                "b_values",
                new JObject { ["i"] = 0, ["l"] = Id.ToString(CultureInfo.InvariantCulture), ["d"] = 0, ["f"] = 0, ["b"] = false });

            Assert.That(
                result["result"].Value<string>(),
                Does.Contain(Id.ToString(CultureInfo.InvariantCulture)),
                "a numeric string lost precision");
        }

        [Test]
        public void RejectsAFractionalValueForAnIntegerArgument()
        {
            var ex = Assert.Throws<McpToolException>(() => Invoke(
                "b_values",
                new JObject { ["i"] = 1.5, ["l"] = 0, ["d"] = 0, ["f"] = 0, ["b"] = false }));

            Assert.That(ex.Code, Is.EqualTo("invalid_params"));
            Assert.That(ex.Message, Does.Contain("whole number"));
        }

        [Test]
        public void UnreadableValueNamesTheArgumentAndItsType()
        {
            var ex = Assert.Throws<McpToolException>(() => Invoke(
                "b_values",
                new JObject { ["i"] = "nope", ["l"] = 0, ["d"] = 0, ["f"] = 0, ["b"] = false }));

            Assert.That(ex.Code, Is.EqualTo("invalid_params"));
            Assert.That(ex.Message, Does.Contain("nope"), "The error text is the model's cue for correcting the call.");
        }

        [Test]
        public void MissingRequiredArgumentNamesIt()
        {
            var ex = Assert.Throws<McpToolException>(() => Invoke("b_values", new JObject { ["i"] = 1 }));

            Assert.That(ex.Code, Is.EqualTo("invalid_params"));
            Assert.That(ex.Message, Does.Contain("'l'").And.Contain("b_values"));
        }

        [Test]
        public void BindsNullableArgumentsPresentAndAbsent()
        {
            Assert.That(Invoke("b_nullable", new JObject())["result"].Value<string>(), Is.EqualTo("none/none"));

            Assert.That(
                Invoke("b_nullable", new JObject { ["count"] = "7", ["weight"] = 0.5 })["result"].Value<string>(),
                Is.EqualTo("7/0.5"));
        }

        [Test]
        public void AnExplicitNullBindsTheDefault()
        {
            var result = Invoke("b_nullable", new JObject { ["count"] = JValue.CreateNull() });

            Assert.That(result["result"].Value<string>(), Is.EqualTo("none/none"));
        }

        [Test]
        public void BindsAnEnumByNameCaseInsensitively()
        {
            Assert.That(Invoke("b_enum", new JObject { ["speed"] = "fast" })["result"].Value<string>(), Is.EqualTo("Fast"));
        }

        [Test]
        public void AnOmittedEnumBindsItsDeclaredDefault()
        {
            Assert.That(Invoke("b_enum", new JObject())["result"].Value<string>(), Is.EqualTo("Slow"));
        }

        [Test]
        public void UnknownEnumValueListsTheValidOnes()
        {
            var ex = Assert.Throws<McpToolException>(() => Invoke("b_enum", new JObject { ["speed"] = "sideways" }));

            Assert.That(ex.Code, Is.EqualTo("invalid_params"));
            Assert.That(ex.Message, Does.Contain("Slow").And.Contain("Fast"));
        }

        [Test]
        public void BindsAnArrayOfValueTypes()
        {
            Assert.That(
                Invoke("b_array", new JObject { ["values"] = new JArray(1, "2", 3) })["result"].Value<int>(),
                Is.EqualTo(6));
        }

        [Test]
        public void BindsABareValueAsASingleElementArray()
        {
            Assert.That(Invoke("b_array", new JObject { ["values"] = 5 })["result"].Value<int>(), Is.EqualTo(5));
        }

        [Test]
        public void AnUnreadableElementNamesTheArgument()
        {
            var ex = Assert.Throws<McpToolException>(
                () => Invoke("b_array", new JObject { ["values"] = new JArray(1, "nope") }));

            Assert.That(ex.Code, Is.EqualTo("invalid_params"));
            Assert.That(ex.Message, Does.Contain("nope"));
        }

        [Test]
        public void BindsAListOfStrings()
        {
            Assert.That(
                Invoke("b_list", new JObject { ["items"] = new JArray("a", "b") })["result"].Value<string>(),
                Is.EqualTo("a+b"));
        }

        [Test]
        public void PassesAJsonObjectThrough()
        {
            var payload = new JObject { ["k"] = "v" };

            Assert.That(Invoke("b_json", new JObject { ["payload"] = payload })["result"].Value<string>(), Is.EqualTo("v"));
        }

        [Test]
        public void AScalarWhereAnObjectIsExpectedIsInvalidParams()
        {
            var ex = Assert.Throws<McpToolException>(() => Invoke("b_json", new JObject { ["payload"] = 3 }));

            Assert.That(ex.Code, Is.EqualTo("invalid_params"));
            Assert.That(ex.Message, Does.Contain("payload"));
        }

        [Test]
        public void AnOmittedArgumentBindsItsDeclaredDefault()
        {
            Assert.That(Invoke("b_default", new JObject())["result"].Value<string>(), Is.EqualTo("fallback"));
        }

        [Test]
        public void TheCompiledInvokerAllocatesOnlyItsResult()
        {
            var descriptor = Descriptor("b_alloc_five");
            var invoker = ToolInvoker.BuildInvoker(descriptor, descriptor.BindPlan.Parameters);

            var scan = new AllocationScan();
            scan.Visit(invoker);

            Assert.That(scan.Arrays, Is.Zero, "Arguments must not be gathered into an array.");
            Assert.That(
                scan.Boxes,
                Is.EqualTo(1),
                "Only the return value may be boxed; five value-type arguments must reach the method as themselves.");
        }

        /// <summary>
        /// Counts what the generated code would allocate: an array holding the arguments, and
        /// every conversion of a value type to <see cref="object"/>.
        /// </summary>
        private sealed class AllocationScan : ExpressionVisitor
        {
            public int Arrays { get; private set; }

            public int Boxes { get; private set; }

            protected override Expression VisitNewArray(NewArrayExpression node)
            {
                this.Arrays++;
                return base.VisitNewArray(node);
            }

            protected override Expression VisitUnary(UnaryExpression node)
            {
                var converting = node.NodeType == ExpressionType.Convert ||
                                 node.NodeType == ExpressionType.ConvertChecked;

                if (converting && node.Type == typeof(object) && node.Operand.Type.IsValueType)
                {
                    this.Boxes++;
                }

                return base.VisitUnary(node);
            }
        }

        [Test]
        public void BindingArgumentsAddsNoCollectionPressure()
        {
            // Unity's Mono runs the Boehm collector, whose per-thread allocation counter is a
            // stub, so the figure comes from how often a loop provokes a collection, scaled by a
            // loop that allocates a known amount.
            const int Iterations = 100000;
            const int CalibrationBlocks = 100000;
            const int BlockBytes = 96;

            var five = Descriptor("b_alloc_five");
            var none = Descriptor("b_alloc_none");

            var fiveArguments = new JObject
            {
                ["a"] = 1,
                ["b"] = 2L,
                ["c"] = 3.5f,
                ["d"] = 4.5,
                ["e"] = true,
            };

            var noArguments = new JObject();

            // The plan, its compiled delegate and the JIT all cost something the first time.
            for (var i = 0; i < 1000; i++)
            {
                ToolInvoker.Invoke(five, fiveArguments);
                ToolInvoker.Invoke(none, noArguments);
            }

            var calibration = CountCollections(() =>
            {
                for (var i = 0; i < CalibrationBlocks; i++)
                {
                    sink = new byte[BlockBytes - 24];
                }
            });

            var withArguments = CountCollections(() =>
            {
                for (var i = 0; i < Iterations; i++)
                {
                    ToolInvoker.Invoke(five, fiveArguments);
                }
            });

            var withoutArguments = CountCollections(() =>
            {
                for (var i = 0; i < Iterations; i++)
                {
                    ToolInvoker.Invoke(none, noArguments);
                }
            });

            Assert.That(sink, Is.Not.Null, "The calibration loop has to actually allocate.");

            var bytesPerCollection = (double)CalibrationBlocks * BlockBytes / Math.Max(1, calibration);
            var resolution = bytesPerCollection / Iterations;
            var perCallForBinding = (withArguments - withoutArguments) * bytesPerCollection / Iterations;
            var perCall = withArguments * bytesPerCollection / Iterations;

            Debug.Log(
                $"[ToolInvoker] collections: calibration {calibration} for " +
                $"{CalibrationBlocks * (long)BlockBytes} bytes, five-argument {withArguments}, " +
                $"argument-less {withoutArguments} over {Iterations} calls. " +
                $"~{perCall:F0} bytes/call total, ~{perCallForBinding:F0} of it binding, " +
                $"resolution {resolution:F0} bytes/call.");

            // The result is what remains: a JObject holding one JValue, plus what
            // JToken.FromObject spends producing that value. Binding through an object[] with a
            // box per argument cost about 200 bytes a call on top of that.
            Assert.That(
                perCallForBinding,
                Is.LessThan(Math.Max(64d, 3d * resolution)),
                "Binding five value-type arguments must not add allocation.");
        }

        private static byte[] sink;

        private static int CountCollections(Action work)
        {
            var before = GC.CollectionCount(0);
            work();
            return GC.CollectionCount(0) - before;
        }
    }
}
