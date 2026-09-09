using System.Collections.Generic;

using NUnit.Framework;

using UnityMCP.Editor.Tools;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// depth bounds how deep a reflection walk goes and max_items how wide each level is. They
    /// multiply, so a value either one allows on its own can still be the product of the two, and
    /// a reply built from it stays in the conversation for the rest of the session.
    /// </summary>
    public sealed class ReflectSerializeTests
    {
        private sealed class Node
        {
            public Node A;
            public Node B;
            public Node C;
            public int Leaf;
        }

        private static Node Chain(int deep)
        {
            var node = new Node { Leaf = deep };

            if (deep > 0)
            {
                node.A = Chain(deep - 1);
                node.B = Chain(deep - 1);
                node.C = Chain(deep - 1);
            }

            return node;
        }

        /// <summary>A back-reference re-expands its subtree at every level that is left.</summary>
        private sealed class Loop
        {
            public Loop Self;
            public int Value;
        }

        /// <summary>A wide level costs as much as a deep one, and only the budget counts both.</summary>
        [Test]
        public void AWideWalkStopsAtTheNodeBudget()
        {
            var wide = new List<List<int>>();

            for (var i = 0; i < 300; i++)
            {
                var inner = new List<int>();
                for (var k = 0; k < 300; k++)
                {
                    inner.Add(k);
                }

                wide.Add(inner);
            }

            var text = ReflectTools.Serialize(wide, 99, 99).ToString(Newtonsoft.Json.Formatting.None);

            Assert.That(text, Does.Contain("\"truncated\":\"budget\""),
                "depth says nothing about how wide a level is");
            Assert.That(text.Length, Is.LessThan(400_000),
                "the budget has to hold the reply to something a reader can carry");
        }

        /// <summary>A caller asking for more than the ceiling gets the ceiling, not the ask.</summary>
        [Test]
        public void DepthAndItemCountAreClampedNotObeyed()
        {
            var wild = ReflectTools.Serialize(Chain(9), 99, 99).ToString(Newtonsoft.Json.Formatting.None);
            var sane = ReflectTools.Serialize(Chain(9), 4, 200).ToString(Newtonsoft.Json.Formatting.None);

            Assert.That(wild.Length, Is.EqualTo(sane.Length));
        }

        [Test]
        public void ACycleTerminates()
        {
            var loop = new Loop { Value = 1 };
            loop.Self = loop;

            Assert.That(
                () => ReflectTools.Serialize(loop, 99, 99),
                Throws.Nothing,
                "depth and the budget both have to hold, or a back-reference runs until the stack gives out");
        }

        [Test]
        public void ALongStringIsCutAndSaysHowLongItWas()
        {
            var serialized = ReflectTools.Serialize(new string('x', 12_000), 2, 20).ToString();

            Assert.That(serialized.Length, Is.LessThan(5_000));
            Assert.That(serialized, Does.Contain("12000 characters"));
        }

        [Test]
        public void AShortStringIsLeftAlone()
        {
            Assert.That(ReflectTools.Serialize("hello", 2, 20).ToString(), Is.EqualTo("hello"));
        }

        [Test]
        public void ACollectionStillReportsThatItWasCut()
        {
            var many = new List<int>();
            for (var i = 0; i < 500; i++)
            {
                many.Add(i);
            }

            var text = ReflectTools.Serialize(many, 2, 5).ToString(Newtonsoft.Json.Formatting.None);

            Assert.That(text, Does.Contain("more than 5"));
        }
    }
}
