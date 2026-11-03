using System.IO;
using BlitzLegacy.Core.Mods;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// A mod ships the same .glb the port tools read offline, so the runtime
    /// reader has to recover the very same rig and armour table.
    /// </summary>
    public sealed class ModTankRigTests
    {
        private const string SourceMods = "/home/smh01/projects/ALL_MODS";

        private static string AnyModel()
        {
            if (!Directory.Exists(SourceMods)) { return null; }

            var files = Directory.GetFiles(SourceMods, "*.glb", SearchOption.AllDirectories);
            return files.Length > 0 ? files[0] : null;
        }

        [Test]
        public void Thickness_ComesOffTheLastNameToken()
        {
            Assert.That(ModTankRig.Thickness("279_armor_hull_40"), Is.EqualTo(40));
            Assert.That(ModTankRig.Thickness("mauses_gun_mask_250"), Is.EqualTo(250));
            Assert.That(ModTankRig.Thickness("hull_vis"), Is.EqualTo(0));
        }

        [Test]
        public void Visibility_KeepsTheDrawnPartsOnly()
        {
            Assert.That(ModTankRig.IsVisible("hull_vis"), Is.True);
            Assert.That(ModTankRig.IsVisible("trackL"), Is.True);
            Assert.That(ModTankRig.IsVisible("turret01"), Is.True);
            Assert.That(ModTankRig.IsVisible("wheelL3"), Is.True);

            Assert.That(ModTankRig.IsVisible("279_armor_hull_40"), Is.False);
            Assert.That(ModTankRig.IsVisible("279_screen_turret_30"), Is.False);
            Assert.That(ModTankRig.IsVisible("279_hull_phys"), Is.False);
            Assert.That(ModTankRig.IsVisible("gun01_shoot_pos"), Is.False);
        }

        [Test]
        public void Parse_RealModGlb_RecoversTheRigAndThePlates()
        {
            var model = AnyModel();
            if (model == null)
            {
                Assert.Ignore("No local Godot mod models on this machine.");
            }

            var file = GlbFile.Load(model);
            Assert.That(file.Nodes.Count, Is.GreaterThan(0), model);

            var hull = file.Find("hull_vis");
            Assert.That(hull, Is.Not.Null, "hull_vis");
            Assert.That(hull.HasGeometry, Is.True);
            Assert.That(hull.Primitives[0].Positions.Length, Is.GreaterThan(0));

            var nodes = TankNodeTable.Parse(ModTankRig.NodesJson(file));
            Assert.That(nodes.Count, Is.GreaterThan(0));
            Assert.That(nodes.TurretNode, Is.EqualTo("turret01"));
            Assert.That(nodes.Has(nodes.TurretNode), Is.True);
            Assert.That(nodes.GunsOf(nodes.TurretNode).Count, Is.EqualTo(1));

            var armor = TankArmorTable.Parse(ModTankRig.ArmorJson(file));
            Assert.That(armor.Count, Is.GreaterThan(0));
            Assert.That(armor.MaxArmor, Is.GreaterThan(0));
            for (var i = 0; i < armor.Plates.Count; i++)
            {
                var plate = armor.Plates[i];
                Assert.That(plate.Submesh, Is.EqualTo(i));
                Assert.That(plate.Triangles, Is.GreaterThan(0), plate.Name);
                Assert.That(
                    plate.Mount == ModTankRig.HullMount
                    || plate.Mount == nodes.TurretNode
                    || plate.Mount == nodes.GunNode,
                    Is.True,
                    plate.Name + " rides " + plate.Mount);
            }
        }

        [Test]
        public void Grant_InstalledMod_BecomesABoughtVehicle()
        {
            var database = new TanksDatabase();
            var card = new TankInfo { Name = "mod_tank", Level = 1, Health = 1 };
            ModLibrary.MarkAsMod(card);
            database.Add(card);

            var account = new AccountData();
            Assert.That(ModOwnership.Grant(account, database), Is.EqualTo(1));
            Assert.That(account.Tanks.Count, Is.EqualTo(1));
            Assert.That(account.Tanks[0].Name, Is.EqualTo("mod_tank"));
            Assert.That(account.Tanks[0].Bought, Is.True);

            Assert.That(ModOwnership.Grant(account, database), Is.EqualTo(0));
        }
    }
}

