using System;
using System.IO;
using BlitzLegacy.Core.Mods;
using BlitzLegacy.Core.Mods.Rempack;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>The legacy mods keep their switches in mods.json next to the Remaster Packages.</summary>
    public sealed class ModLibraryStateTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "mod_library_state_" + Guid.NewGuid().ToString("N"));
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

        /// <summary>An unpacked Godot mod: one model is enough to count as a mod.</summary>
        private void LegacyMod(string name)
        {
            var tanks = Path.Combine(_root, name, "assets", "tanks");
            Directory.CreateDirectory(tanks);
            File.WriteAllBytes(Path.Combine(tanks, "mystery.glb"), new byte[] { 1, 2, 3 });
        }

        private ModLibrary Legacy()
        {
            var library = new ModLibrary(_root);
            library.Refresh();
            return library;
        }

        [Test]
        public void Refresh_HonoursTheOldSwitchListAndMovesItOver()
        {
            LegacyMod("tiger_pack");
            LegacyMod("panther_pack");
            var legacyList = Path.Combine(_root, ModState.LegacyFileName);
            File.WriteAllText(legacyList, "tiger_pack\n");

            var library = Legacy();
            Assert.That(library.Find("tiger_pack").Enabled, Is.False);
            Assert.That(library.Find("panther_pack").Enabled, Is.True);

            library.SetEnabled("panther_pack", false);
            Assert.That(File.Exists(legacyList), Is.False);
            var state = ModState.Load(_root);
            Assert.That(state.IsEnabled("tiger_pack"), Is.False, "the imported switch survives the first save");
            Assert.That(state.IsEnabled("panther_pack"), Is.False);
            Assert.That(Legacy().Find("panther_pack").Enabled, Is.False);
        }

        [Test]
        public void Switches_AreSharedWithThePackageLibrary()
        {
            LegacyMod("tiger_pack");
            RempackFixture.Tank().Write(_root);
            Legacy().SetEnabled("tiger_pack", false);
            var packages = new RempackLibrary(_root, RempackFixture.Options());
            packages.Refresh();
            packages.SetEnabled("author.test_mod", false);

            Assert.That(Legacy().Find("tiger_pack").Enabled, Is.False, "a package change keeps the legacy switch");
            var again = new RempackLibrary(_root, RempackFixture.Options());
            again.Refresh();
            Assert.That(again.Find("author.test_mod").Enabled, Is.False);
            Assert.That(again.Mods.Count, Is.EqualTo(1), "legacy folders are not packages");

            Legacy().Remove("tiger_pack");
            var state = ModState.Load(_root);
            Assert.That(state.IsKnown("tiger_pack"), Is.False);
            Assert.That(state.IsEnabled("author.test_mod"), Is.False);
        }
    }
}
