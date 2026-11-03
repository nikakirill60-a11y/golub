using BlitzLegacy.Core.Tanks;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Guards the exported node pivots the turret, gun and muzzle hang off.
    /// </summary>
    public sealed class TankNodeTableTests
    {
        private const float Tolerance = 1e-4f;

        private const string Sample =
            "{\"albedos\":{\"turret_02\":\"E100_2\"}," +
            "\"nodes\":{\"turret01\":{\"local\":[0.0,1.31885,-0.029574]," +
            "\"parent\":\"ussr1-ms-1\",\"world\":[0.0,1.31885,-0.029574]}}}";

        [Test]
        public void Parse_MirrorsThePivotAcrossZ()
        {
            var table = TankNodeTable.Parse(Sample);
            var pivot = table.World(TankNodeTable.Turret);

            Assert.That(table.Count, Is.EqualTo(1));
            Assert.That(pivot.X, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(pivot.Y, Is.EqualTo(1.31885f).Within(Tolerance));
            // Raw Godot Z is negative; battle space flips it.
            Assert.That(pivot.Z, Is.EqualTo(0.029574f).Within(Tolerance));
        }

        [Test]
        public void SourceWorld_KeepsTheExporterSpaceTheHangarDrawsIn()
        {
            var table = TankNodeTable.Parse(Sample);
            var pivot = table.SourceWorld(TankNodeTable.Turret);

            // The hangar shows the imported model untouched, so the armour
            // plates it mounts must not take the battle mirror.
            Assert.That(pivot.X, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(pivot.Y, Is.EqualTo(1.31885f).Within(Tolerance));
            Assert.That(pivot.Z, Is.EqualTo(-0.029574f).Within(Tolerance));
            Assert.That(pivot.Z, Is.EqualTo(-table.World(TankNodeTable.Turret).Z).Within(Tolerance));
        }

        [Test]
        public void OffCentrePivot_CancelsTheImportersXFlipInBattle()
        {
            // Chi-Ni's turret sits a quarter metre left of the hull axis.
            var table = TankNodeTable.Parse(
                "{\"nodes\":{\"turret_01\":{\"local\":[-0.25,1.42,-0.11]," +
                "\"parent\":\"\",\"world\":[-0.25,1.42,-0.11]}}}");

            var battle = table.World("turret_01");
            Assert.That(battle.X, Is.EqualTo(-0.25f).Within(Tolerance));
            Assert.That(battle.Z, Is.EqualTo(0.11f).Within(Tolerance));
            var local = table.Local("turret_01");
            Assert.That(local.X, Is.EqualTo(-0.25f).Within(Tolerance));

            var model = table.ModelWorld("turret_01");
            Assert.That(model.X, Is.EqualTo(0.25f).Within(Tolerance));
            Assert.That(model.Z, Is.EqualTo(-0.11f).Within(Tolerance));
        }

        [Test]
        public void SourceWorld_IsZeroForAnUnknownNode()
        {
            var table = TankNodeTable.Parse(Sample);
            var pivot = table.SourceWorld("missing");

            Assert.That(pivot.X, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(pivot.Y, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(pivot.Z, Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void Parse_KeepsTheAuthoredParent()
        {
            var table = TankNodeTable.Parse(Sample);

            Assert.That(table.ParentOf(TankNodeTable.Turret), Is.EqualTo("ussr1-ms-1"));
            Assert.That(table.Has(TankNodeTable.Turret), Is.True);
        }

        [Test]
        public void Parse_KeepsPerModuleAlbedoBindings()
        {
            var table = TankNodeTable.Parse(Sample);

            Assert.That(table.AlbedoOf("turret_02"), Is.EqualTo("E100_2"));
            Assert.That(table.AlbedoOf("turret01"), Is.Null);
        }

        [Test]
        public void Parse_SurvivesMissingNodesAndEmptyInput()
        {
            var table = TankNodeTable.Parse(Sample);
            var missing = table.Local("nope");

            Assert.That(table.Has("nope"), Is.False);
            Assert.That(table.ParentOf("nope"), Is.Null);
            Assert.That(missing.X, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(missing.Y, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(missing.Z, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(TankNodeTable.Parse(null).Count, Is.EqualTo(0));
        }

        [Test]
        public void ShippedTable_CarriesTheTurretGunAndMuzzle()
        {
            var asset = Resources.Load<TextAsset>("Data/" + TankNodeTable.Resource);
            Assert.That(asset, Is.Not.Null, "the porter must export ms1_nodes.json");

            var table = TankNodeTable.Parse(asset.text);
            // Two turrets, six guns and one muzzle marker per gun.
            Assert.That(table.Count, Is.EqualTo(14));

            // The rig is the stock configuration of the card, named after the
            // client slots the research graph installs.
            Assert.That(table.TurretNode, Is.EqualTo("turret_01"));
            Assert.That(table.GunNode, Is.EqualTo("gun_02"));
            Assert.That(table.MuzzleNode, Is.EqualTo("gun_02_shoot_pos"));

            var turret = table.World(table.TurretNode);
            Assert.That(turret.Y, Is.EqualTo(1.316402f).Within(Tolerance));
            Assert.That(turret.Z, Is.EqualTo(0.033774f).Within(Tolerance));

            // The gun elevates relative to the turret it hangs off, around the
            // trunnion the client authors in the vehicle XML.
            Assert.That(table.ParentOf(table.GunNode), Is.EqualTo(table.TurretNode));
            var gun = table.Local(table.GunNode);
            Assert.That(gun.Y, Is.EqualTo(0.234668f).Within(Tolerance));
            Assert.That(gun.Z, Is.EqualTo(0.410043f).Within(Tolerance));

            Assert.That(table.ParentOf(table.MuzzleNode), Is.EqualTo(table.GunNode));
            var muzzle = table.Local(table.MuzzleNode);
            Assert.That(muzzle.Z, Is.EqualTo(0.497716f).Within(Tolerance));
        }

        [Test]
        public void ShippedTable_ResolvesEveryResearchableModule()
        {
            var asset = Resources.Load<TextAsset>("Data/" + TankNodeTable.Resource);
            var table = TankNodeTable.Parse(asset.text);

            Assert.That(table.TurretNodes, Is.EquivalentTo(new[] { "turret_01", "turret_02" }));
            // The client keeps the gun list inside the turret that mounts it.
            Assert.That(table.GunsOf("turret_01"), Is.EquivalentTo(new[] { "gun_02" }));
            Assert.That(table.GunsOf("turret_02").Count, Is.EqualTo(5));
            Assert.That(table.IsModuleNode("gun_09"), Is.True);
            Assert.That(table.IsModuleNode("hull_vis"), Is.False);

            Assert.That(table.ResolveTurret("turret_02"), Is.EqualTo("turret_02"));
            // A module the export does not carry falls back on the stock rig.
            Assert.That(table.ResolveTurret("turret_09"), Is.EqualTo(table.TurretNode));
            Assert.That(table.ResolveGun("turret_02", "gun_09"), Is.EqualTo("gun_09"));
            // A gun the turret cannot mount falls back on that turret's stock.
            Assert.That(table.ResolveGun("turret_02", "gun_02"), Is.EqualTo("gun_06"));
            Assert.That(
                table.MuzzleOf("turret_02", "gun_09"), Is.EqualTo("gun_09_shoot_pos"));
        }
    }
}
