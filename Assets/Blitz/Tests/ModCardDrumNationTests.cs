using BlitzLegacy.Core.Mods;
using BlitzLegacy.Core.Tanks;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The two mod authoring freedoms this file pins: a mod gun may declare
    /// a drum (clip) magazine, and a mod tank flies the nation flag its
    /// card names instead of always landing on the mixed one.
    /// </summary>
    public sealed class ModCardDrumNationTests
    {
        [Test]
        public void MarkAsMod_KeepsTheDeclaredNation()
        {
            var card = new TankInfo { Name = "mod_france", Nation = Nation.France };
            ModLibrary.MarkAsMod(card);
            Assert.That(card.Mod, Is.True);
            Assert.That(card.Nation, Is.EqualTo(Nation.France));
            Assert.That(card.TreeTank, Is.False);
        }

        [Test]
        public void MarkAsMod_FallsBackToOtherWithoutADeclaredNation()
        {
            var unnamed = new TankInfo { Name = "mod_none", Nation = Nation.None };
            ModLibrary.MarkAsMod(unnamed);
            Assert.That(unnamed.Nation, Is.EqualTo(Nation.Other));

            var invalid = new TankInfo { Name = "mod_bad", Nation = (Nation)42 };
            ModLibrary.MarkAsMod(invalid);
            Assert.That(invalid.Nation, Is.EqualTo(Nation.Other));
        }

        [Test]
        public void GodotCard_ClipSizeExpandsToAOneShellBurstDrum()
        {
            var file = GodotResourceFile.Parse(
                "[ext_resource id=\"1\" path=\"res://module_gun.gd\"]\n"
                + "[sub_resource id=\"2\"]\n"
                + "script = ExtResource(\"1\")\n"
                + "name = \"Drum gun\"\n"
                + "reload = 20.0\n"
                + "fire_mode = 1\n"
                + "clip_size = 3\n"
                + "intraclip = 2.5\n"
                + "[resource]\n"
                + "tank_name = \"DrumTank\"\n"
                + "nation = 1\n"
                + "health = 1000\n"
                + "gun = SubResource(\"2\")\n");

            var card = GodotModCardReader.Read(file, "drumtank");
            Assert.That(card, Is.Not.Null);
            Assert.That(card.Nation, Is.EqualTo(Nation.Ussr));
            Assert.That(card.Guns.Count, Is.EqualTo(1));

            var gun = card.Guns[0];
            Assert.That(gun.FireMode, Is.EqualTo(1));
            Assert.That(gun.Burst.Bursts, Is.EqualTo(new[] { 1, 1, 1 }));
            Assert.That(gun.Burst.IntraClip, Is.EqualTo(2.5f));
            Assert.That(gun.Reload, Is.EqualTo(20f));
        }

        [Test]
        public void GodotCard_ExplicitBurstsAndAutoloadTimersAreRead()
        {
            var file = GodotResourceFile.Parse(
                "[ext_resource id=\"1\" path=\"res://module_gun.gd\"]\n"
                + "[sub_resource id=\"2\"]\n"
                + "script = ExtResource(\"1\")\n"
                + "name = \"Auto gun\"\n"
                + "reload = 9.0\n"
                + "fire_mode = 2\n"
                + "bursts = [1, 1, 1, 1]\n"
                + "intraclip = 3.0\n"
                + "autoreload_times = [9.0, 6.0, 4.0, 3.0]\n"
                + "[resource]\n"
                + "tank_name = \"AutoTank\"\n"
                + "nation = 2\n"
                + "health = 1200\n"
                + "gun = SubResource(\"2\")\n");

            var card = GodotModCardReader.Read(file, "autotank");
            Assert.That(card.Guns.Count, Is.EqualTo(1));

            var gun = card.Guns[0];
            Assert.That(gun.FireMode, Is.EqualTo(2));
            Assert.That(gun.AutoReload, Is.True);
            Assert.That(gun.Burst.Bursts, Is.EqualTo(new[] { 1, 1, 1, 1 }));
            Assert.That(gun.AutoReloadTimes.Count, Is.EqualTo(4));
            Assert.That(gun.AutoReloadTimes[0], Is.EqualTo(9f));
        }
    }
}
