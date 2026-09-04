using System.Runtime.InteropServices;

using NUnit.Framework;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// The pieces of dialog handling that need no dialog: how a button text is matched, what the
    /// running-call notice says, and when a quiet main thread counts as stalled.
    /// </summary>
    [TestFixture]
    internal sealed class EditorDialogsTests
    {
        private static EditorDialogs.DialogInfo SaveDialog(string message = "Do you want to save the changes you made in the scenes?")
        {
            return new EditorDialogs.DialogInfo
            {
                Handle = "1234",
                Title = "Scene(s) Have Been Modified",
                Message = message,
                Buttons = new[] { "Save", "Don't Save", "Cancel" },
            };
        }

        [Test]
        public void SupportFollowsThePlatform()
        {
            Assert.That(EditorDialogs.IsSupported, Is.EqualTo(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)));
        }

        [Test]
        public void ListingNeverThrowsAndNeedsNoDialog()
        {
            // Nothing modal is up while the tests run, so an empty array is the expected answer.
            Assert.That(EditorDialogs.List(), Is.Empty);
        }

        [Test]
        public void PressingAnUnknownHandleIsFalse()
        {
            Assert.That(EditorDialogs.Press("0", "Cancel"), Is.False);
            Assert.That(EditorDialogs.Press("not-a-number", "Cancel"), Is.False);
            Assert.That(EditorDialogs.Press("1", "Cancel"), Is.False);
        }

        [TestCase("&Cancel", "Cancel")]
        [TestCase("&Cancel", "cancel")]
        [TestCase("Cancel", "&Cancel")]
        [TestCase("&Don't Save", "don't save")]
        [TestCase("&Don't Save", "DON'T SAVE")]
        [TestCase("Yes", " yes ")]
        public void ButtonTextMatchesWithOrWithoutTheAccelerator(string windowText, string requested)
        {
            Assert.That(EditorDialogs.ButtonMatches(windowText, requested), Is.True);
        }

        [TestCase("&Save", "Don't Save")]
        [TestCase("&Cancel", "Cance")]
        [TestCase("&Cancel", "")]
        [TestCase("", "Cancel")]
        [TestCase(null, "Cancel")]
        public void ButtonTextDoesNotMatchAnotherButton(string windowText, string requested)
        {
            Assert.That(EditorDialogs.ButtonMatches(windowText, requested), Is.False);
        }

        [Test]
        public void DisplayTextDropsTheAccelerator()
        {
            Assert.That(EditorDialogs.DisplayText("&Don't Save"), Is.EqualTo("Don't Save"));
            Assert.That(EditorDialogs.DisplayText("Yes"), Is.EqualTo("Yes"));
            Assert.That(EditorDialogs.DisplayText(null), Is.EqualTo(string.Empty));
        }

        [Test]
        public void DialogNoticeNamesTitleMessageAndButtons()
        {
            var notice = MainThreadWatch.RunningNotice(SaveDialog(), 0);

            Assert.That(notice, Is.EqualTo(
                "The Editor is showing a dialog \"Scene(s) Have Been Modified\" " +
                "(Do you want to save the changes you made in the scenes?) with buttons Save / Don't Save / Cancel. " +
                "Nothing proceeds until it is answered; use editor_dialog_press or answer it in the Editor."));
        }

        [Test]
        public void DialogNoticeTrimsALongMessageAndFlattensLines()
        {
            var message = new string('x', 150) + "\r\n" + new string('y', 150);

            var notice = MainThreadWatch.RunningNotice(SaveDialog(message), 0);

            Assert.That(notice, Does.Contain("(" + new string('x', 150) + " " + new string('y', 49) + "...)"));
            Assert.That(notice, Does.Not.Contain("\n"));
        }

        [Test]
        public void DialogNoticeOmitsAnEmptyMessage()
        {
            var notice = MainThreadWatch.RunningNotice(SaveDialog(string.Empty), 0);

            Assert.That(notice, Does.StartWith("The Editor is showing a dialog \"Scene(s) Have Been Modified\" with buttons"));
        }

        [Test]
        public void DialogWinsOverAStall()
        {
            var notice = MainThreadWatch.RunningNotice(SaveDialog(), 30_000);

            Assert.That(notice, Does.StartWith("The Editor is showing a dialog"));
            Assert.That(notice, Does.Not.Contain("has not run"));
        }

        [Test]
        public void StallNoticeAppearsOnlyAboveFiveSeconds()
        {
            Assert.That(MainThreadWatch.RunningNotice(null, 0), Is.Null);
            Assert.That(MainThreadWatch.RunningNotice(null, 5000), Is.Null);
            Assert.That(MainThreadWatch.RunningNotice(null, 7400), Is.EqualTo(
                "The Editor main thread has not run for 7 s; it may be showing a dialog this tool cannot see, importing, or compiling."));
        }

        [Test]
        public void StallIsZeroWhileNothingWaitsOrTheGapIsATick()
        {
            var now = 10_000L * System.TimeSpan.TicksPerMillisecond;
            var watch = new MainThreadWatch(() => now);

            Assert.That(watch.StalledMs(true), Is.Zero, "Never pumped yet.");

            watch.MarkPumped();
            now += 500 * System.TimeSpan.TicksPerMillisecond;

            Assert.That(watch.StalledMs(true), Is.Zero, "Within the Editor's own background tick.");

            now += 600 * System.TimeSpan.TicksPerMillisecond;

            Assert.That(watch.StalledMs(true), Is.EqualTo(1100));
            Assert.That(watch.StalledMs(false), Is.Zero, "An idle main thread is not a stall.");

            watch.MarkPumped();

            Assert.That(watch.StalledMs(true), Is.Zero);
        }

        [Test]
        public void PumpingTheDispatcherMarksTheWatch()
        {
            var now = 10_000L * System.TimeSpan.TicksPerMillisecond;
            var watch = new MainThreadWatch(() => now);
            var dispatcher = new McpMainThreadDispatcher { Pumped = watch.MarkPumped };

            dispatcher.Pump();
            now += 3000 * System.TimeSpan.TicksPerMillisecond;

            Assert.That(watch.StalledMs(true), Is.EqualTo(3000));

            dispatcher.Pump();

            Assert.That(watch.StalledMs(true), Is.Zero);
        }
    }
}
