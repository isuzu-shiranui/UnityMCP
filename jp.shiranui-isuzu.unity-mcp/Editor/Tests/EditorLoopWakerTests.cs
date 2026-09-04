using System.Threading;

using NUnit.Framework;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// The pump runs only while work is queued (plus a short grace), or for the whole session
    /// when asked to, and does nothing on an Editor without the wake-up call.
    /// </summary>
    [TestFixture]
    internal sealed class EditorLoopWakerTests
    {
        [Test]
        public void ThisEditorExposesSignalTick()
        {
            // The whole point of the pump. If a Unity release removes the internal method, this
            // says so instead of every unfocused call quietly going back to 100 ms.
            Assert.That(EditorLoopWaker.Available, Is.True);
        }

        [Test]
        public void DemandStartsThePumpAndItStopsAfterTheQueueDrains()
        {
            var pending = true;
            var signals = 0;
            using var pump = new EditorLoopWaker(() => pending, () => Interlocked.Increment(ref signals));

            pump.Demand();
            Thread.Sleep(120);

            Assert.That(pump.IsRunning, Is.True);
            Assert.That(Volatile.Read(ref signals), Is.GreaterThan(3), "Several ticks in 120 ms.");

            pending = false;
            Thread.Sleep(600);

            Assert.That(pump.IsRunning, Is.False, "Stops once nothing is queued and the grace period has passed.");
        }

        [Test]
        public void AlwaysOnKeepsRunningWithoutWork()
        {
            var signals = 0;
            using var pump = new EditorLoopWaker(() => false, () => Interlocked.Increment(ref signals)) { AlwaysOn = true };

            Thread.Sleep(400);

            Assert.That(pump.IsRunning, Is.True);
            Assert.That(Volatile.Read(ref signals), Is.GreaterThan(10));

            pump.AlwaysOn = false;
            Thread.Sleep(600);
            Assert.That(pump.IsRunning, Is.False);
        }

        [Test]
        public void UnavailableSignalMeansNoThread()
        {
            using var pump = new EditorLoopWaker(() => true, null);

            pump.Demand();

            Assert.That(pump.IsAvailable, Is.False);
            Assert.That(pump.IsRunning, Is.False);
        }

        [Test]
        public void ASignalThatThrowsDoesNotKillThePump()
        {
            var pending = true;
            var calls = 0;
            using var pump = new EditorLoopWaker(() => pending, () =>
            {
                Interlocked.Increment(ref calls);
                throw new System.InvalidOperationException("editor going away");
            });

            pump.Demand();
            Thread.Sleep(100);

            Assert.That(Volatile.Read(ref calls), Is.GreaterThan(2));
            Assert.That(pump.IsRunning, Is.True);
        }
    }
}
