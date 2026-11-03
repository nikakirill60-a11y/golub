using System;
using System.IO;
using BlitzLegacy.Core.Mods;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class ModCrashGuardTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "mod_crash_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        private string[] Marker()
        {
            var path = Path.Combine(_root, ModCrashGuard.MarkerName);
            return File.Exists(path) ? File.ReadAllText(path).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries) : new string[0];
        }

        [Test]
        public void Recover_AfterACleanExit_FindsNothing()
        {
            var guard = new ModCrashGuard(_root);

            Assert.That(guard.Recover(), Is.Empty);
            Assert.That(ModState.Load(_root).Ids, Is.Empty, "a clean start writes no state");
            Assert.That(new ModCrashGuard(null).Recover(), Is.Empty);
        }

        [Test]
        public void Loading_MarksTheModOnDiskUntilItIsDone()
        {
            var guard = new ModCrashGuard(_root);
            using (guard.Loading("a.tanks"))
            {
                Assert.That(Marker(), Is.EqualTo(new[] { "a.tanks" }));
                Assert.That(guard.Current, Is.EqualTo(new[] { "a.tanks" }));
            }

            Assert.That(File.Exists(guard.MarkerPath), Is.False);
            Assert.That(guard.Current, Is.Empty);
        }

        [Test]
        public void Loading_CountsNestedLoadsOfOneMod()
        {
            var guard = new ModCrashGuard(_root);
            var outer = guard.Loading("a.tanks");
            var inner = guard.Loading("a.tanks");
            guard.Begin("b.maps");
            Assert.That(Marker(), Is.EqualTo(new[] { "a.tanks", "b.maps" }));

            inner.Dispose();
            inner.Dispose();
            Assert.That(Marker(), Is.EqualTo(new[] { "a.tanks", "b.maps" }), "a disposed scope ends only once");
            outer.Dispose();
            Assert.That(Marker(), Is.EqualTo(new[] { "b.maps" }));
            guard.End("b.maps");
            guard.End("b.maps");
            guard.End("never.started");
            Assert.That(File.Exists(guard.MarkerPath), Is.False);
        }

        [Test]
        public void Recover_SwitchesOffWhatWasLoadingWhenTheGameDied()
        {
            ModState.Update(_root, s =>
            {
                s.Append("a.tanks");
                s.Append("b.maps");
                s.Append("c.fine");
            });
            var crashed = new ModCrashGuard(_root);
            crashed.Begin("a.tanks");
            crashed.Begin("b.maps");

            var next = new ModCrashGuard(_root);
            Assert.That(next.Recover(), Is.EqualTo(new[] { "a.tanks", "b.maps" }));
            Assert.That(File.Exists(next.MarkerPath), Is.False);
            var state = ModState.Load(_root);
            Assert.That(state.IsEnabled("a.tanks"), Is.False);
            Assert.That(state.IsEnabled("b.maps"), Is.False);
            Assert.That(state.IsEnabled("c.fine"), Is.True);
            Assert.That(state.Crashed, Is.EqualTo(new[] { "a.tanks", "b.maps" }));
            Assert.That(state.Ids, Is.EqualTo(new[] { "a.tanks", "b.maps", "c.fine" }), "the load order stays");

            Assert.That(new ModCrashGuard(_root).Recover(), Is.Empty, "a crash is reported once");
        }

        [Test]
        public void Recover_ReadsAMarkerWithJunkLines()
        {
            File.WriteAllText(Path.Combine(_root, ModCrashGuard.MarkerName), "\r\n  a.tanks \r\n\r\na.tanks\n" + new string('x', 300) + "\n");

            Assert.That(new ModCrashGuard(_root).Recover(), Is.EqualTo(new[] { "a.tanks" }));
        }

        [Test]
        public void Begin_IgnoresAnEmptyId()
        {
            var guard = new ModCrashGuard(_root);
            guard.Begin(null);
            guard.Begin(string.Empty);
            using (guard.Loading(null))
            {
                Assert.That(guard.Current, Is.Empty);
            }

            Assert.That(File.Exists(guard.MarkerPath), Is.False);
        }

        [Test]
        public void Begin_CreatesTheFolder()
        {
            var nested = Path.Combine(_root, "later");
            var guard = new ModCrashGuard(nested);
            guard.Begin("a.tanks");

            Assert.That(File.Exists(Path.Combine(nested, ModCrashGuard.MarkerName)), Is.True);
            Assert.That(File.Exists(Path.Combine(nested, ModCrashGuard.MarkerName + ".tmp")), Is.False);
        }
    }
}
