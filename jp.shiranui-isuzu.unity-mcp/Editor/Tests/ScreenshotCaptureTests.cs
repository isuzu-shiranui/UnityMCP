using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using NUnit.Framework;

using UnityMCP.Editor.Handlers;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Validates the <see cref="ScreenshotCapture.ViewToTypeName"/> mapping that
    /// drives EditorWindow discovery for the /capture_screenshot endpoint.
    /// Full P/Invoke capture paths require a live Editor window and are not
    /// unit-testable here; they are exercised via integration curl tests.
    /// </summary>
    [TestFixture]
    internal sealed class ScreenshotCaptureTests
    {
        private static readonly string[] ExpectedKeys =
        {
            "inspector",
            "hierarchy",
            "project",
            "console",
            "game_view_window",
            "scene_view_window",
        };

        [Test]
        public void ViewToTypeName_HasAllExpectedKeys()
        {
            var map = ScreenshotCapture.ViewToTypeName;

            foreach (var key in ExpectedKeys)
            {
                Assert.IsTrue(map.ContainsKey(key), $"Expected key '{key}' missing from ViewToTypeName.");
            }

            Assert.AreEqual(ExpectedKeys.Length, map.Count, "ViewToTypeName has unexpected extra keys.");
        }

        [Test]
        public void ViewToTypeName_NoDuplicateTypeNames()
        {
            var map = ScreenshotCapture.ViewToTypeName;
            var seen = new HashSet<string>();

            foreach (var kv in map)
            {
                Assert.IsTrue(seen.Add(kv.Value), $"Duplicate type name mapped: '{kv.Value}' (key '{kv.Key}').");
            }
        }

        [Test]
        public void ViewToTypeName_NoEmptyKeysOrValues()
        {
            var map = ScreenshotCapture.ViewToTypeName;

            foreach (var kv in map)
            {
                Assert.IsFalse(string.IsNullOrEmpty(kv.Key), "Found empty key in ViewToTypeName.");
                Assert.IsFalse(string.IsNullOrEmpty(kv.Value), $"Found empty value for key '{kv.Key}'.");
            }
        }

        [Test]
        public void ViewToTypeName_AllValuesStartWithUnityEditor()
        {
            var map = ScreenshotCapture.ViewToTypeName;

            foreach (var kv in map)
            {
                Assert.IsTrue(
                    kv.Value.StartsWith("UnityEditor."),
                    $"Mapped type '{kv.Value}' for key '{kv.Key}' should be in the UnityEditor namespace.");
            }
        }

        /// <summary>
        /// A render can start compiling variants nothing had used, and the picture it produced then
        /// holds cyan placeholders, so the capture has to be taken again once that compile ends.
        /// </summary>
        [Test]
        public void ACaptureThatStartsACompileIsTakenAgainOnceTheCompileEnds()
        {
            var takes = 0;
            var checks = 0;

            // Nothing compiling before the first capture; that capture starts a compile seen by the
            // next three checks, and the second capture starts nothing.
            bool Compiling() => takes == 1 && checks++ < 3;

            var waits = ScreenshotCapture.TakeWhenCompiled(
                () => takes++, Compiling, Stopwatch.StartNew(), TimeSpan.FromMinutes(1)).Count();

            Assert.That(takes, Is.EqualTo(2));
            Assert.That(waits, Is.EqualTo(2));
        }

        [Test]
        public void ACompileThatNeverEndsStillGetsOneCaptureAtTheLimit()
        {
            var takes = 0;

            var waits = ScreenshotCapture.TakeWhenCompiled(
                () => takes++, () => true, Stopwatch.StartNew(), TimeSpan.Zero).Count();

            Assert.That(takes, Is.EqualTo(1));
            Assert.That(waits, Is.EqualTo(0));
        }
    }
}
