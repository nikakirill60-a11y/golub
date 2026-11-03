using System.Collections.Generic;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>Completeness gates for the generated all-nation 4.8.0 card table.</summary>
    public sealed class Blitz480DataTests
    {
        private TanksDatabase _database;

        [SetUp]
        public void SetUp()
        {
            var text = Resources.Load<TextAsset>("Data/tanks");
            Assert.IsNotNull(text, "generated Data/tanks.json is missing");
            _database = TanksDatabase.Parse(text.text);
        }

        [Test]
        public void GeneratedTable_ParsesEveryResearchableVehicleAndModule()
        {
            Assert.AreEqual(522, _database.Count);
            var turrets = 0;
            var guns = 0;
            var tracks = 0;
            var engines = 0;
            var radios = 0;
            var resources = new HashSet<string>();

            foreach (var tank in _database.Tanks)
            {
                Assert.That(tank.Level, Is.InRange(1, 10), tank.Name);
                Assert.Greater(tank.Health, 0, tank.Name);
                // Researchable cards are tree cards; premium, event and
                // collector vehicles are tree cards only when the client sells
                // them for gold inside the tree.
                Assert.AreEqual(!tank.Premium || tank.Acquisition == "tree_premium",
                    tank.TreeTank, tank.Name);
                Assert.IsNotEmpty(tank.DisplayName, tank.Name);
                Assert.IsTrue(resources.Add(TankModelAssets.ResourceId(tank.Name)),
                    "resource id collision: " + tank.Name);
                Assert.IsNotEmpty(tank.Turrets, tank.Name + " turrets");
                Assert.IsNotEmpty(tank.Guns, tank.Name + " guns");
                Assert.IsNotEmpty(tank.Tracks, tank.Name + " tracks");
                Assert.IsNotEmpty(tank.Engines, tank.Name + " engines");
                Assert.IsNotEmpty(tank.Radios, tank.Name + " radios");

                turrets += tank.Turrets.Count;
                guns += tank.Guns.Count;
                tracks += tank.Tracks.Count;
                engines += tank.Engines.Count;
                radios += tank.Radios.Count;

                foreach (var turret in tank.Turrets)
                {
                    Assert.IsNotEmpty(turret.Mesh, tank.Name + "/" + turret.Name);
                    Assert.IsNotEmpty(turret.Guns, tank.Name + "/" + turret.Name);
                    foreach (var slot in turret.Guns)
                    {
                        Assert.IsNotEmpty(slot.Mesh,
                            tank.Name + "/" + turret.Name + "/" + slot.Name);
                        Assert.IsTrue(turret.CanMount(slot.Name));
                    }
                }
            }

            Assert.AreEqual(719, turrets);
            Assert.AreEqual(1039, guns);
            Assert.AreEqual(760, tracks);
            Assert.AreEqual(859, engines);
            Assert.AreEqual(522, radios);
        }

        [Test]
        public void GunReports_FollowTheClientGunEffectsNotTheCalibre()
        {
            // IS-3 Defender's autoloading D-25TA names shot_large; reading the
            // magazine as an automatic cannon made it fire like a T-26.
            Assert.AreEqual((int)ShootSound.ShotLarge, _database.Get("r115_is-3_auto").Guns[0].ShotSound);
            // A gun-launcher keeps its gun report; the guided round alone
            // switches to the launcher report when it is fired.
            foreach (var name in new[] { "g14_kpfpz_70r", "a116_xm551_exp" })
            {
                var gun = _database.Get(name).Guns[0];
                Assert.AreEqual((int)ShootSound.ShotHuge, gun.ShotSound, name);
                var guided = 0;
                foreach (var shell in gun.Shells)
                {
                    if (shell.Atgm) { guided++; }
                }
                Assert.AreEqual(1, guided, name);
                Assert.Greater(gun.Shells.Count, guided, name);
            }
        }

        [Test]
        public void RocketT49_CarriesTheGuidedLauncherShells()
        {
            var t49 = _database.Get("t49_rocket");
            Assert.IsNotNull(t49);

            var launcher = t49.Guns[t49.Guns.Count - 1];
            Assert.AreEqual(2, launcher.Shells.Count);
            // gun_effects.xml fires the launcher with shot_atgm, not with the
            // calibre-sized report a 152 mm gun would take.
            Assert.AreEqual((int)ShootSound.ShotAtgm, launcher.ShotSound);

            var hollow = launcher.Shells[0];
            Assert.AreEqual(ShellType.Heat, hollow.Type);
            Assert.IsTrue(hollow.Atgm);
            Assert.AreEqual(0.6f, hollow.TurnSpeed, 0.0001f);
            Assert.AreEqual(0.01f, hollow.GravityMultiplier, 0.0001f);
            Assert.AreEqual(85f, hollow.RicochetAngle, 0.0001f);
            Assert.AreEqual(720f, hollow.MaxDistance, 0.0001f);
            Assert.AreEqual(85f, hollow.Speed, 0.0001f);
            Assert.AreEqual(560f, hollow.Damage, 0.0001f);
            Assert.AreEqual(185f, hollow.Penetration, 0.0001f);

            var explosive = launcher.Shells[1];
            Assert.AreEqual(ShellType.He, explosive.Type);
            Assert.IsTrue(explosive.Atgm);
            Assert.IsTrue(explosive.Premium);
            Assert.AreEqual(0.5f, explosive.TurnSpeed, 0.0001f);
            Assert.AreEqual(75f, explosive.Speed, 0.0001f);
            Assert.AreEqual(750f, explosive.Damage, 0.0001f);
            Assert.AreEqual(85f, explosive.Penetration, 0.0001f);
            Assert.AreEqual(0f, explosive.RicochetAngle, 0.0001f);
            Assert.AreEqual(5.11f, explosive.ExplosionRadius, 0.0001f);
            Assert.AreEqual(
                5.11f,
                ArmorMath.ExplosionRadiusFor(
                    explosive.ExplosionRadius, explosive.Caliber),
                0.0001f);
            // The shaped charge carries no blast at all, so the calibre rule
            // is what would answer for it.
            Assert.AreEqual(0f, hollow.ExplosionRadius, 0.0001f);
        }

        [Test]
        public void PremiumCards_CarryTheClientAcquisitionClass()
        {
            var treePremiums = 0;
            var premiums = 0;
            var collectibles = 0;

            foreach (var tank in _database.Tanks)
            {
                if (!tank.Premium)
                {
                    Assert.IsEmpty(tank.Acquisition, tank.Name);
                    continue;
                }

                // Nothing outside the research graph costs XP or unlocks a
                // successor: the client hands these out whole.
                Assert.AreEqual(0, tank.CostXp, tank.Name);
                Assert.IsEmpty(tank.UnlockTanks, tank.Name);
                Assert.AreEqual(0, tank.Cost, tank.Name);

                switch (tank.Acquisition)
                {
                    case "tree_premium":
                        Assert.IsTrue(tank.TreeTank, tank.Name);
                        Assert.IsTrue(tank.ShopTank, tank.Name);
                        Assert.IsFalse(tank.Collector, tank.Name);
                        Assert.Greater(tank.CostGold, 0, tank.Name);
                        treePremiums++;
                        break;
                    case "premium":
                        Assert.IsFalse(tank.TreeTank, tank.Name);
                        Assert.IsFalse(tank.ShopTank, tank.Name);
                        Assert.IsFalse(tank.Collector, tank.Name);
                        premiums++;
                        break;
                    case "collectible":
                        Assert.IsFalse(tank.TreeTank, tank.Name);
                        Assert.IsTrue(tank.Collector, tank.Name);
                        collectibles++;
                        break;
                    default:
                        Assert.Fail(tank.Name + " unknown acquisition: " + tank.Acquisition);
                        break;
                }
            }

            Assert.AreEqual(24, treePremiums);
            Assert.AreEqual(85, premiums);
            // The 11.20 Mark I Male adds one source-authored collectible.
            Assert.AreEqual(143, collectibles);

            // The tutorial bots stay out of the port; the 11.20 mixed-nation
            // overlay brings WarDuck in as an event premium.
            Assert.IsNull(_database.Get("oth01_tutorial_bot"));
            Assert.AreEqual("premium", _database.Get("oth10_warduck").Acquisition);

            var superPershing = _database.Get("t26_e4_superpershing");
            Assert.IsNotNull(superPershing);
            Assert.AreEqual("tree_premium", superPershing.Acquisition);
            Assert.AreEqual(8, superPershing.Level);

            // The rocket T49 ships with the client's secret tag, so it lands
            // in the collection instead of the event premiums.
            var t49 = _database.Get("t49_rocket");
            Assert.IsNotNull(t49);
            Assert.AreEqual("collectible", t49.Acquisition);
            Assert.IsTrue(t49.Collector);
        }

        [Test]
        public void ClientTechTreePositions_PreserveEveryBranchWithoutOverlap()
        {
            var occupied = new HashSet<string>();
            foreach (var tank in _database.Tanks)
            {
                // Only the research graph owns slots. Event premiums and
                // collectibles live outside it and all share branch zero,
                // and the yellow tree premiums share the premium row above
                // the graph, several of them per level.
                if (!tank.TreeTank)
                {
                    continue;
                }

                if (tank.Premium)
                {
                    Assert.GreaterOrEqual(tank.TreeBranch, 6, tank.Name);
                    continue;
                }

                Assert.That(tank.TreeBranch, Is.InRange(-5, 5), tank.Name);
                var slot = tank.Nation + "/" + tank.Level + "/" + tank.TreeBranch;
                Assert.IsTrue(occupied.Add(slot), "duplicate tech-tree slot: " + slot);
            }

            Assert.AreEqual(0, _database.Get("t82").TreeBranch);
            Assert.AreEqual(-2, _database.Get("jagdpz_e100").TreeBranch);
            Assert.AreEqual(4, _database.Get("is-4").TreeBranch);
            Assert.AreEqual(-2, _database.Get("amx_50b").TreeBranch);
        }

        [Test]
        public void CasemateTraverse_ComesFromTheClientTurretLimits()
        {
            var t82 = _database.Get("t82");
            var jagd = _database.Get("jagdpz_e100");
            Assert.AreEqual(12f, t82.Guns[0].DeviationLeft, 0.001f);
            Assert.AreEqual(12f, t82.Guns[0].DeviationRight, 0.001f);
            Assert.AreEqual(8f, jagd.Guns[0].DeviationLeft, 0.001f);
            Assert.AreEqual(8f, jagd.Guns[0].DeviationRight, 0.001f);
        }

        [Test]
        public void ExportManifest_ShipsEveryRecordedResearchableModulePayload()
        {
            var manifestText = Resources.Load<TextAsset>("Data/tank_models_source_manifest");
            Assert.IsNotNull(manifestText);
            var manifest = JsonNode.Parse(manifestText.text)["tanks"];
            var checkedTanks = 0;
            foreach (var entry in manifest.Fields)
            {
                var tank = _database.Get(entry.Key);
                Assert.IsNotNull(tank, entry.Key + " staging card");
                checkedTanks++;
                var assets = TankModelAssets.For(tank);
                var model = Resources.Load<GameObject>(assets.ModelResource);
                var nodesText = Resources.Load<TextAsset>("Data/" + assets.NodeDataResource);
                var armorText = Resources.Load<TextAsset>("Data/" + assets.ArmorDataResource);
                Assert.IsNotNull(model, assets.ModelResource);
                Assert.IsNotNull(Resources.Load<Texture2D>(assets.BodyTextureResource));
                Assert.IsNotNull(Resources.Load<Texture2D>(assets.TrackTextureResource));
                Assert.IsNotNull(nodesText, assets.NodeDataResource);
                Assert.IsNotNull(armorText, assets.ArmorDataResource);

                var nodes = TankNodeTable.Parse(nodesText.text);
                var armor = TankArmorTable.Parse(armorText.text);
                foreach (var turret in tank.Turrets)
                {
                    Assert.IsTrue(nodes.Has(turret.Mesh), tank.Name + "/" + turret.Mesh);
                    var turretArmor = armor.OnMount(turret.Mesh);
                    if (turretArmor.Count == 0)
                    {
                        // Casemate TDs author the fixed mount as a turret module
                        // for compatibility, but its armour is part of the hull.
                        Assert.Less(turret.YawLeft + turret.YawRight, 360f,
                            tank.Name + "/" + turret.Mesh + " unarmoured full turret");
                    }
                    foreach (var gun in turret.Guns)
                    {
                        Assert.IsTrue(nodes.Has(gun.Mesh), tank.Name + "/" + gun.Mesh);
                        Assert.IsTrue(nodes.Has(nodes.MuzzleOf(turret.Mesh, gun.Mesh)),
                            tank.Name + "/" + gun.Mesh + " muzzle");
                    }
                }
            }

            Assert.AreEqual(manifest.Count, checkedTanks);
            Assert.GreaterOrEqual(checkedTanks, 33);
        }

        [Test]
        public void KnownClientValues_AreNotInheritedFromTheOldTenCards()
        {
            var ms1 = _database.Get("ms-1");
            Assert.IsNotNull(ms1);
            Assert.AreEqual(160, ms1.Health);
            Assert.AreEqual(2, ms1.Turrets.Count);
            Assert.AreEqual(5, ms1.Guns.Count);
            Assert.AreEqual(500f, ms1.Radios[0].Distance, 0.001f);

            var t34 = _database.Get("t-34");
            Assert.IsNotNull(t34);
            Assert.AreEqual(512, t34.Health);
            Assert.AreEqual(356700, t34.Cost);
            Assert.AreEqual(12990, _database.ResearchCost(t34.Name));

            // USA T28 and USSR T-28 collapse to the same punctuation-free id,
            // so the generated table namespaces only the colliding American card.
            Assert.IsNotNull(_database.Get("usa-t28"));
            Assert.IsNotNull(_database.Get("t-28"));
            Assert.AreNotEqual(
                TankModelAssets.ResourceId("usa-t28"),
                TankModelAssets.ResourceId("t-28"));
        }
    }
}
