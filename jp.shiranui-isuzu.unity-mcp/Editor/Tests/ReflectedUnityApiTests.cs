using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using NUnit.Framework;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Every internal Unity API this package reaches by reflection has to still be there.
    /// </summary>
    /// <remarks>
    /// Reflection is how the console, the Editor loop, the recorder and the input tools reach
    /// APIs Unity does not make public. Nothing in the compiler checks those names, so a rename
    /// between Unity versions turns into a tool that answers wrongly rather than a build that
    /// fails. Running this on each version of the support matrix is what turns that into a
    /// failing test.
    /// <para>
    /// Only <c>readonly</c> fields are checked. A mutable one is a cache the code fills the first
    /// time it needs it, and is legitimately null until then.
    /// </para>
    /// </remarks>
    [TestFixture]
    internal sealed class ReflectedUnityApiTests
    {
        private static IEnumerable<FieldInfo> ResolvedOnce()
        {
            return typeof(UnityMCP.Editor.Core.Attributes.McpToolAttribute).Assembly
                .GetTypes()
                .SelectMany(t => t.GetFields(
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                .Where(f => f.IsInitOnly && typeof(MemberInfo).IsAssignableFrom(f.FieldType));
        }

        [Test]
        public void EveryReflectedUnityMemberStillResolves()
        {
            var missing = new List<string>();
            var checkedCount = 0;

            foreach (var field in ResolvedOnce())
            {
                checkedCount++;

                object value;

                try
                {
                    value = field.GetValue(null);
                }
                catch (Exception e)
                {
                    missing.Add($"{field.DeclaringType?.Name}.{field.Name} threw {e.GetType().Name}");
                    continue;
                }

                if (value == null)
                {
                    missing.Add($"{field.DeclaringType?.Name}.{field.Name} ({field.FieldType.Name})");
                }
            }

            Assert.That(checkedCount, Is.GreaterThan(20),
                "the scan found almost nothing, so it has stopped checking what it was written for");

            Assert.That(missing, Is.Empty,
                "these internal Unity members no longer resolve on "
                + UnityEngine.Application.unityVersion
                + ", so the tools that read through them answer wrongly rather than failing:\n  "
                + string.Join("\n  ", missing));
        }
    }
}
