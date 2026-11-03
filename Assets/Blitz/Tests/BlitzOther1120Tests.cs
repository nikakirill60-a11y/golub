using System.Collections.Generic;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Complete 11.20 mixed-nation roster: every source vehicle, full module
    /// research (Battle Pass points converted to XP), the AC tech-tree branch
    /// and the two-sided calibration report.
    /// </summary>
    public sealed class BlitzOther1120Tests
    {
        private static readonly string[] Seasonal =
        {
            "oth36_indien_panzer_s1", "oth37_t26_e4_superpershing_s1", "oth38_50tp_tyszkiewicza_s1",
            "oth39_t20_s2", "oth40_tvp_46_s2", "oth41_centurion_mk3_s2",
            "oth46_charioteer_s3", "oth47_amx_13_90_s3", "oth48_emil_1951e1_s3",
            "oth66_e100ann_s4", "oth67_fv215b_183ann_s4", "oth68_kv2ann_s4",
            "oth72_hazard_i", "oth73_nemini", "oth74_argonaut", "oth76_destiny",
        };

        private static readonly string[] AcBranch =
        {
            "oth61_pz_vm_set", "oth62_pz_vld_set", "oth63_pz_vllf_set", "oth64_pz_vlllk_set",
            "oth81_ac_15_taygete", "oth80_ac_16_celeno", "oth79_ac_17_alcyone",
        };

        private static TanksDatabase Database()
        { return TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData)); }

        private static JsonNode Json(string resource)
        {
            var asset = Resources.Load<TextAsset>("Data/" + resource);
            Assert.IsNotNull(asset, resource);
            JsonNode node;
            Assert.IsTrue(JsonNode.TryParse(asset.text, out node), resource);
            return node;
        }

        [Test]
        public void Roster_HoldsEveryMixedNationSourceVehicle()
        {
            var db = Database();
            Assert.AreEqual(522, db.Count);
            var other = 0;
            foreach (var tank in db.Tanks) { if (tank.Nation == Nation.Other) { other++; } }
            Assert.AreEqual(82, other, "list.xml of vehicles/other has 82 entries");
            var inventory = Json("blitz_other_1120_inventory");
            Assert.AreEqual(72, inventory["records"].Count);
            foreach (var record in inventory["records"].Items)
            {
                var card = db.Get(record["name"].AsString());
                Assert.IsNotNull(card, record["name"].AsString());
                Assert.AreEqual(Nation.Other, card.Nation, card.Name);
                Assert.IsFalse(string.IsNullOrEmpty(card.DisplayName), card.Name);
                Assert.AreNotEqual(record["vehicle_id"].AsString(), card.DisplayName, card.Name + " shows its raw key");
                Assert.IsNotNull(Resources.Load<TextAsset>("Data/" + card.Name.Replace("_", "").Replace("-", "") + "_nodes"),
                    card.Name + " node table");
                var lists = new IReadOnlyList<ModuleInfo>[] { card.Turrets, card.Guns, card.Tracks, card.Engines, card.Radios };
                var groups = new[] { "turrets", "guns", "tracks", "engines", "radios" };
                for (var kind = 0; kind < groups.Length; kind++)
                {
                    var expected = record["modules"][groups[kind]];
                    Assert.AreEqual(expected.Count, lists[kind].Count, card.Name + "/" + groups[kind]);
                    for (var m = 0; m < expected.Count; m++)
                    { Assert.AreEqual(expected[m].AsString(), lists[kind][m].Name, card.Name); }
                }
            }
        }

        [Test]
        public void SeasonalVehicles_ResearchEveryModuleForCombatXp()
        {
            var db = Database();
            foreach (var name in Seasonal)
            {
                var card = db.Get(name);
                Assert.IsNotNull(card, name);
                Assert.IsTrue(card.Premium, name);
                Assert.IsTrue(TankResearchService.HasModuleResearch(card), name);

                var account = new AccountData { FreeXp = 0 };
                account.AddTank(name, true);
                var service = new TankResearchService(account, db);
                Assert.IsFalse(service.IsTankElite(name), name + " starts on stock modules");

                // Without XP nothing opens; with exactly the converted price
                // every module of the vehicle can be researched.
                var total = 0;
                for (var kind = ModuleKind.Gun; kind <= ModuleKind.Radio; kind++)
                {
                    foreach (var module in TankResearchService.UniqueModules(TankResearchService.Modules(card, kind)))
                    { total += module.CostXp; }
                }
                Assert.Greater(total, 50000, name + " module research must cost real XP");
                account.GetTank(name).Exp = total * 2;
                ResearchAll(service, card);
                Assert.IsTrue(service.IsTankElite(name), name + " every module researched");
                Assert.Less(account.GetTank(name).Exp, total * 2, name + " paid in tank XP");
            }
        }

        [Test]
        public void AcBranch_IsAResearchableTreeFromItsTierFourRoot()
        {
            var db = Database();
            var tree = db.TreeTanks(Nation.Other);
            Assert.AreEqual(AcBranch.Length, tree.Count);
            var account = new AccountData { Credits = 100000000, FreeXp = 100000000 };
            var service = new TankResearchService(account, db);
            Assert.IsTrue(service.CanResearchTank(AcBranch[0]), "the tier IV root is open on a fresh account");
            for (var i = 1; i < AcBranch.Length; i++)
            {
                Assert.IsFalse(service.CanResearchTank(AcBranch[i]),
                    AcBranch[i] + " opens only after its predecessor");
            }

            for (var i = 0; i < AcBranch.Length; i++)
            {
                var card = db.Get(AcBranch[i]);
                Assert.IsNotNull(card, AcBranch[i]);
                Assert.IsTrue(card.TreeTank && !card.Premium, card.Name);
                Assert.Greater(card.Cost, 0, card.Name + " is bought for credits");
                var research = service.ResearchTank(card.Name);
                Assert.IsTrue(research.Ok || service.TankResearched(card.Name), card.Name + "/" + research.ErrorKey);
                var bought = service.BuyTank(card.Name);
                Assert.IsTrue(bought.Ok, card.Name + "/" + bought.ErrorKey);
                ResearchAll(service, card);
            }

            // Elite needs the next branch vehicle researched, so check it last.
            for (var i = 0; i < AcBranch.Length; i++)
            {
                Assert.IsTrue(service.IsTankElite(AcBranch[i]), AcBranch[i]);
            }
        }

        [Test]
        public void Calibration_KeepsEveryVehicleNearItsCompetitors()
        {
            var report = Json("blitz_other_1120_balance");
            Assert.AreEqual(72, report["tanks"].Count);
            foreach (var entry in report["tanks"].Items)
            {
                var name = entry["name"].AsString();
                var balanced = entry["balanced_metrics"];
                var peers = entry["peer_medians"];
                Assert.Greater(entry["competitors"].Count, 0, name);
                var hp = balanced["total_health"].AsDouble() / peers["total_health"].AsDouble();
                var dpm = balanced["max_sustained_dpm"].AsDouble() / peers["sustained_dpm"].AsDouble();
                Assert.That(hp, Is.InRange(0.89, 1.11), name + " total HP against the tier");
                // Magazine and auto-reload guns trade sustained output for
                // burst, which is why their floor is lower.
                Assert.That(hp * dpm, Is.InRange(0.65, 1.2), name + " HP x DPM budget against the tier");
            }
        }

        private static void ResearchAll(TankResearchService service, TankInfo card)
        {
            for (var pass = 0; pass < 32; pass++)
            for (var kind = ModuleKind.Gun; kind <= ModuleKind.Radio; kind++)
            {
                var modules = TankResearchService.Modules(card, kind);
                for (var id = 0; id < modules.Count; id++)
                {
                    if (service.ModuleStatus(card.Name, kind, id) == ResearchStatus.Available)
                    {
                        var result = service.ResearchModule(card.Name, kind, id);
                        Assert.IsTrue(result.Ok, card.Name + "/" + modules[id].Name + "/" + result.ErrorKey);
                    }
                }
            }
        }
    }
}
