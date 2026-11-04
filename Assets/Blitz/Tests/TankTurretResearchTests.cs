using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The turret half of the branch rebuilt from the Blitz client 4.8.0:
    /// every card carries researchable turrets, a gun list per turret, the
    /// research graph of the client and a reload that depends on the turret.
    /// </summary>
    public sealed class TankTurretResearchTests
    {
        private AccountData _account;
        private TanksDatabase _database;
        private TankResearchService _service;

        private static readonly string[] Branch =
        {
            "ms-1", "bt-2", "bt-7", "a-20", "t-34",
            "t-34-85", "t-43", "t-44", "t-54", "t62a",
        };

        [SetUp]
        public void SetUp()
        {
            var text = Resources.Load<TextAsset>("Data/tanks");
            Assert.IsNotNull(text, "Data/tanks.json is missing from Resources");
            _database = TanksDatabase.Parse(text.text);
            _account = new AccountData();
            _service = new TankResearchService(_account, _database);
            _account.AddTank("ms-1", true);
        }

        [Test]
        public void EveryCardOfTheBranchCarriesTheClientTurrets()
        {
            Assert.AreEqual(523, _database.Count);
            foreach (var name in Branch)
            {
                var info = _database.Get(name);
                Assert.IsNotNull(info, name + " is missing");
                Assert.Greater(info.Turrets.Count, 0, name + " has no turret");

                // Every turret lists the guns it can mount, and each of those
                // guns is a card of the same tank.
                for (var t = 0; t < info.Turrets.Count; t++)
                {
                    var turret = info.Turrets[t];
                    Assert.Greater(turret.Guns.Count, 0,
                        name + " turret " + turret.Name + " has no gun");
                    for (var g = 0; g < turret.Guns.Count; g++)
                    {
                        var slot = turret.Guns[g];
                        Assert.Greater(slot.Reload, 0f, name + " " + slot.Name);
                        Assert.Greater(slot.MaxAmmo, 0, name + " " + slot.Name);
                        Assert.IsNotEmpty(slot.Mesh, name + " " + slot.Name);
                        Assert.AreNotEqual(-1, info.GunIdFor(t, IndexOf(info, slot.Name)),
                            name + " cannot resolve " + slot.Name);
                    }
                }
            }
        }

        /// <summary>Client prices of the MS-1 and the BT-2, straight out of 4.8.0.</summary>
        [Test]
        public void ResearchGraphKeepsTheClientPrices()
        {
            var ms1 = _database.Get("ms-1");
            Assert.AreEqual("T-18_Standart", ms1.Turrets[0].Name);
            Assert.AreEqual(0, ms1.Turrets[0].CostXp);
            Assert.AreEqual("T-18_mod", ms1.Turrets[1].Name);
            Assert.AreEqual(170, ms1.Turrets[1].CostXp);
            Assert.AreEqual(330, ms1.Guns[3].CostXp);
            Assert.AreEqual(150, ms1.Guns[4].CostXp);
            Assert.AreEqual(550, _database.ResearchCost("bt-2"));

            var bt2 = _database.Get("bt-2");
            Assert.AreEqual(150, bt2.Turrets[1].CostXp);
            Assert.AreEqual(230, bt2.Guns[3].CostXp);
            Assert.AreEqual(2700, _database.ResearchCost("bt-7"));
        }

        [Test]
        public void StockTurretDoesNotHoldTheTopGun()
        {
            var ms1 = _database.Get("ms-1");
            var stock = ms1.Turrets[0];
            var top = ms1.Turrets[1];

            Assert.IsTrue(stock.CanMount("_37mm_Gochkins"));
            Assert.IsFalse(stock.CanMount("_45mm_mod_1932"));
            Assert.IsTrue(top.CanMount("_45mm_mod_1932"));

            // Installing it on the stock turret is refused by the service.
            var save = _account.GetTank("ms-1");
            _service.MarkModuleResearched("_45mm_mod_1932");
            var refused = _service.InstallModule("ms-1", ModuleKind.Gun, 4);
            Assert.IsFalse(refused.Ok);
            Assert.AreEqual(TankResearchService.ErrorGunNeedsTurret, refused.ErrorKey);
            Assert.AreEqual(0, save.GunId);
        }

        [Test]
        public void SwappingTheTurretDownReplacesTheGunAndEmptiesTheRack()
        {
            var save = _account.GetTank("ms-1");
            var info = _database.Get("ms-1");

            save.Exp = info.Turrets[1].CostXp + info.Guns[4].CostXp;
            Assert.IsTrue(_service.ResearchModule("ms-1", ModuleKind.Turret, 1).Ok);
            Assert.IsTrue(_service.ResearchModule("ms-1", ModuleKind.Gun, 4).Ok);
            Assert.AreEqual(4, save.GunId);

            for (var i = 0; i < save.ShellsAmounts.Count; i++)
            {
                save.ShellsAmounts[i] = 10;
            }

            // Back to the stock turret: the 45 mm does not fit it, so Blitz
            // mounts the only gun that turret carries and clears the rack.
            Assert.IsTrue(_service.InstallModule("ms-1", ModuleKind.Turret, 0).Ok);
            Assert.AreEqual(0, save.TurretId);
            Assert.AreEqual(0, save.GunId);
            for (var i = 0; i < save.ShellsAmounts.Count; i++)
            {
                Assert.AreEqual(0, save.ShellsAmounts[i]);
            }
        }

        [Test]
        public void TheSameGunReloadsDifferentlyInEachTurret()
        {
            var info = _database.Get("ms-1");
            var stock = info.GunFor(0, 0);
            var top = info.GunFor(1, 0);

            Assert.AreEqual(stock.Name, top.Name);
            Assert.AreEqual(96, stock.MaxAmmo);
            Assert.AreEqual(104, top.MaxAmmo);

            var bt2 = _database.Get("bt-2");
            Assert.AreEqual(4.0f, bt2.GunFor(0, 0).Reload, 0.001f);
            Assert.AreEqual(3.934f, bt2.GunFor(1, 0).Reload, 0.001f);
        }

        /// <summary>
        /// The turret also owns the traverse speed and the view range, so the
        /// two MS-1 turrets must differ there as well.
        /// </summary>
        [Test]
        public void TurretCarriesTraverseAndViewRange()
        {
            var info = _database.Get("ms-1");
            // cleanup() stores the traverse in radians per second, because
            // rotate_turret spends it per delta without converting.
            Assert.AreEqual(0.680678f, info.Turrets[0].RotationSpeed, 0.001f);
            Assert.AreEqual(0.610865f, info.Turrets[1].RotationSpeed, 0.001f);
            Assert.AreEqual(200f, info.Turrets[0].SpotRadius, 0.001f);
            Assert.AreEqual(210f, info.Turrets[1].SpotRadius, 0.001f);
            Assert.Greater(info.Turrets[1].Health, info.Turrets[0].Health);
        }

        private static int IndexOf(TankInfo info, string gun)
        {
            for (var i = 0; i < info.Guns.Count; i++)
            {
                if (info.Guns[i].Name == gun)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
