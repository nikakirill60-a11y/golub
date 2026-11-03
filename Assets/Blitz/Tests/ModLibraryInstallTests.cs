using System.IO;
using BlitzLegacy.Core.Mods;
using BlitzLegacy.Core.Tanks;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class ModLibraryInstallTests
    {
        private const string SourceMods = "/home/smh01/projects/ALL_MODS";

        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "blitz_mods_" + System.Guid.NewGuid().ToString("N"));
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

        [Test]
        public void Install_CompressedGodotRar_FindsCardsAsModClass()
        {
            if (!Directory.Exists(SourceMods) || !ModUnpacker.Available)
            {
                Assert.Ignore("No local Godot mod archives or unpacker on this machine.");
            }

            var archives = Directory.GetFiles(SourceMods, "*.rar");
            Assert.That(archives.Length, Is.GreaterThan(0));
            var library = new ModLibrary(_root);
            for (var i = 0; i < archives.Length; i++)
            {
                var result = library.Install(archives[i]);
                Assert.That(result.Ok, Is.True, archives[i] + ": " + result.ErrorKey);
                Assert.That(result.Mod.TankCount, Is.GreaterThan(0), archives[i]);
            }

            var cards = library.Cards(null);
            Assert.That(cards.Count, Is.GreaterThanOrEqualTo(archives.Length));
            for (var i = 0; i < cards.Count; i++)
            {
                Assert.That(cards[i].Mod, Is.True, cards[i].Name);
                Assert.That(cards[i].Nation, Is.EqualTo(Nation.Other), cards[i].Name);
                Assert.That(cards[i].Collector, Is.False, cards[i].Name);
            }
        }

        [Test]
        public void Scan_ModelWithoutCard_StillBecomesAModTank()
        {
            var tanks = Path.Combine(_root, "bare", "assets", "tanks");
            Directory.CreateDirectory(tanks);
            File.WriteAllBytes(Path.Combine(tanks, "mystery.glb"), new byte[] { 1, 2, 3 });

            var library = new ModLibrary(_root);
            library.Refresh();
            Assert.That(library.Mods.Count, Is.EqualTo(1));
            Assert.That(library.Mods[0].Tanks[0].HasCard, Is.False);

            var cards = library.Cards(null);
            Assert.That(cards.Count, Is.EqualTo(1));
            Assert.That(cards[0].Name, Is.EqualTo("mystery"));
            Assert.That(cards[0].Mod, Is.True);
            Assert.That(cards[0].Nation, Is.EqualTo(Nation.Other));
        }
    }
}
