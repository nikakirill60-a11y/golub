using System.Collections.Generic;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Two complete 11.20 tech-tree branches: UK tank destroyers Challenger ->
    /// FV4005 (VII-X) after the Sherman Firefly and Italian mediums P.43 bis ->
    /// Progetto 65 (VI-X) after the Strv m/42, with every source module, the
    /// authored research XP and the Italian auto-reloaders.
    /// </summary>
    public sealed class BlitzBranches1120Tests
    {
        private static readonly string[] Uk = {
            "gb41_challenger", "gb80_charioteer", "gb81_fv4004", "gb83_4005" };
        private static readonly string[] Italy = {
            "it10_p43_bis", "it09_p43_ter", "it14_p44_pantera", "it12_prototipo_standard_b",
            "it08_progetto_m40_mod65" };

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

        private static IEnumerable<string> All()
        {
            foreach (var name in Uk) { yield return name; }
            foreach (var name in Italy) { yield return name; }
        }

        [Test]
        public void UkBranch_ChallengerToFv4005_ContinuesTheFireflyRow()
        {
            var db = Database();
            Assert.AreEqual(523, db.Count);
            var names = new[] { "Challenger", "Charioteer", "FV4004 Conway", "FV4005" };
            var xp = new[] { 65680, 106250, 160000, 280000 };
            var firefly = db.Get("gb19_sherman_firefly");
            for (var i = 0; i < Uk.Length; i++)
            {
                var card = db.Get(Uk[i]);
                Assert.IsNotNull(card, Uk[i]);
                Assert.AreEqual(names[i], card.DisplayName);
                Assert.AreEqual(Nation.Gb, card.Nation);
                Assert.AreEqual(7 + i, card.Level);
                Assert.AreEqual(TankClass.TankDestroyer, card.Class);
                Assert.IsTrue(card.TreeTank && !card.Premium && !card.Collector);
                Assert.AreEqual(firefly.TreeBranch, card.TreeBranch, "straight on from the Firefly");
                Assert.AreEqual(xp[i], card.CostXp, "authored 11.20 research XP");
                if (i + 1 < Uk.Length) { Assert.AreEqual(xp[i + 1], card.UnlockTanks[Uk[i + 1]]); }
                else { Assert.IsEmpty(card.UnlockTanks); }
            }
            Assert.IsTrue(firefly.AdditionalResearchLinks.ContainsKey("gb41_challenger"));
            Assert.IsTrue(firefly.UnlockTanks.ContainsKey("gb22_comet"), "the Comet line stays");
        }

        [Test]
        public void ItalianBranch_P43BisToProgetto_StartsAfterTheStrvM42()
        {
            var db = Database();
            var names = new[] { "P.43 bis", "P.43 ter", "P.44 Pantera", "Prototipo Standard B", "Progetto M40 mod. 65" };
            var xp = new[] { 24500, 53370, 87200, 162350, 250200 };
            var strv = db.Get("s02_strvm42");
            for (var i = 0; i < Italy.Length; i++)
            {
                var card = db.Get(Italy[i]);
                Assert.IsNotNull(card, Italy[i]);
                Assert.AreEqual(names[i], card.DisplayName);
                Assert.AreEqual(strv.Nation, card.Nation);
                Assert.AreEqual(6 + i, card.Level);
                Assert.AreEqual(TankClass.Medium, card.Class);
                Assert.IsTrue(card.TreeTank && !card.Premium && !card.Collector);
                Assert.AreEqual(strv.TreeBranch + 1, card.TreeBranch, "next to the Kranvagn line");
                Assert.AreEqual(xp[i], card.CostXp, "authored 11.20 research XP");
                if (i + 1 < Italy.Length) { Assert.AreEqual(xp[i + 1], card.UnlockTanks[Italy[i + 1]]); }
                else { Assert.IsEmpty(card.UnlockTanks); }
            }
            Assert.AreEqual("Progetto 65", db.Get("it08_progetto_m40_mod65").DisplayNameShort);
            Assert.IsTrue(strv.AdditionalResearchLinks.ContainsKey("it10_p43_bis"));
            Assert.IsTrue(strv.UnlockTanks.ContainsKey("s07_strv_74"), "the Kranvagn line stays");
        }

        [Test]
        public void TreeSlots_DoNotOverlap()
        {
            var db = Database();
            foreach (var nation in new[] { Nation.Gb, db.Get("s02_strvm42").Nation })
            {
                var occupied = new HashSet<string>();
                foreach (var card in db.TreeTanks(nation))
                {
                    if (card.Premium) { continue; }
                    Assert.IsTrue(occupied.Add(card.Level + "/" + card.TreeBranch), card.Name + ": overlapping tree slot");
                }
            }
        }

        [TestCase("gb19_sherman_firefly", "gb41_challenger")]
        [TestCase("s02_strvm42", "it10_p43_bis")]
        public void AdditionalLink_RequiresTheAuthoredParentModule_AndChargesTankXp(string parentName, string targetName)
        {
            var db = Database(); var account = new AccountData { FreeXp = 0, Credits = 100000000 };
            account.AddTank(parentName, true); var service = new TankResearchService(account, db);
            var parent = db.Get(parentName);
            Assert.IsTrue(parent.AdditionalResearchLinks.ContainsKey(targetName));
            Assert.IsFalse(service.CanResearchTank(targetName));
            account.FreeXp = 100000000; ResearchAll(service, parent);
            Assert.IsTrue(service.CanResearchTank(targetName));
            account.FreeXp = 0; var payer = account.GetTank(parentName);
            var cost = db.ResearchCost(targetName); payer.Exp = cost + 13;
            var researched = service.ResearchTank(targetName);
            Assert.IsTrue(researched.Ok, researched.ErrorKey); Assert.AreEqual(cost, researched.Paid);
            Assert.AreEqual(13, payer.Exp);
            Assert.IsTrue(service.BuyTank(targetName).Ok);
            Assert.IsTrue(account.HasTank(targetName));
        }

        [Test]
        public void EveryModule_IsResearchedAndInstalledThroughTheNormalRuntime()
        {
            var db = Database(); var checkedModules = 0;
            foreach (var name in All())
            {
                var card = db.Get(name); var account = new AccountData { FreeXp = 100000000, Credits = 100000000 };
                account.AddTank(name, true); var service = new TankResearchService(account, db);
                ResearchAll(service, card);
                for (var kind = ModuleKind.Gun; kind <= ModuleKind.Radio; kind++)
                {
                    var modules = TankResearchService.Modules(card, kind);
                    for (var id = 0; id < modules.Count; id++)
                    {
                        var status = service.ModuleStatus(name, kind, id);
                        Assert.That(status, Is.EqualTo(ResearchStatus.Researched).Or.EqualTo(ResearchStatus.InstalledBought), name + "/" + modules[id].Name);
                        if (kind != ModuleKind.Gun)
                        { Assert.IsTrue(service.InstallModule(name, kind, id).Ok, name + "/" + modules[id].Name); }
                        checkedModules++;
                    }
                }
                for (var t = 0; t < card.Turrets.Count; t++)
                {
                    Assert.IsTrue(service.InstallModule(name, ModuleKind.Turret, t).Ok);
                    foreach (var slot in card.Turrets[t].Guns)
                    {
                        var gun = card.Guns.FindIndex(g => g.Name == slot.Name);
                        Assert.IsTrue(service.InstallModule(name, ModuleKind.Gun, gun).Ok, name + "/" + slot.Name);
                        Assert.AreEqual(gun, account.GetTank(name).GunId);
                    }
                }
            }
            Assert.AreEqual(74, checkedModules, "14 turrets, 16 guns, 16 chassis, 19 engines, 9 radios");
        }

        [Test]
        public void Branches_CarryEverySourceModule()
        {
            var db = Database();
            var inventory = Json("blitz_branches_1120_inventory");
            Assert.AreEqual(9, inventory["records"].Count);
            var groups = new[] { "turrets", "guns", "tracks", "engines", "radios" };
            foreach (var record in inventory["records"].Items)
            {
                var card = db.Get(record["name"].AsString());
                var lists = new IReadOnlyList<ModuleInfo>[] { card.Turrets, card.Guns, card.Tracks, card.Engines, card.Radios };
                for (var kind = 0; kind < groups.Length; kind++)
                {
                    var expected = record["modules"][groups[kind]];
                    Assert.AreEqual(expected.Count, lists[kind].Count, card.Name + "/" + groups[kind]);
                    for (var m = 0; m < expected.Count; m++)
                    { Assert.AreEqual(expected[m].AsString(), lists[kind][m].Name, groups[kind]); }
                }
                var configurations = 0;
                foreach (var turret in card.Turrets) { configurations += turret.Guns.Count; }
                Assert.AreEqual(record["configurations"].Count, configurations, card.Name);
                var id = TankModelAssets.ResourceId(card.Name).ToLowerInvariant();
                Assert.IsNotNull(Resources.Load<TextAsset>("Data/" + id + "_nodes"), card.Name + " node table");
                Assert.IsNotNull(Resources.Load<TextAsset>("Data/" + id + "_armor"), card.Name + " armour table");
            }
        }

        [TestCase("it14_p44_pantera", "_90mm_Cannone_da_74", 7.2f, 3.8f, 3.3f)]
        [TestCase("it12_prototipo_standard_b", "_105mm_Cannone_da_Rh_V1", 9.5f, 8.5f, 6.5f)]
        [TestCase("it08_progetto_m40_mod65", "Cannone_da_105_51_M68", 9f, 7f, 5f)]
        public void ItalianTopGuns_KeepTheirShellByShellAutoReload(string name, string gunName, float a, float b, float c)
        {
            var card = Database().Get(name);
            var gun = card.Guns.Find(g => g.Name == gunName);
            Assert.IsNotNull(gun, gunName);
            Assert.IsTrue(gun.AutoReload, "pumpGunMode of the 11.20 gun");
            Assert.AreEqual(3, gun.AutoReloadTimes.Count);
            // Calibration scales every timer by one factor; the cadence stays.
            var scale = gun.AutoReloadTimes[0] / a;
            Assert.That(scale, Is.InRange(0.85f, 1.18f));
            Assert.That(gun.AutoReloadTimes[1], Is.EqualTo(b * scale).Within(0.01f));
            Assert.That(gun.AutoReloadTimes[2], Is.EqualTo(c * scale).Within(0.01f));
        }

        [Test]
        public void Branches_ImportTheSourceModelsLosslessly()
        {
            foreach (var name in All())
            {
                var id = TankModelAssets.ResourceId(name);
                var folder = "Assets/Blitz/Resources/Models/Tanks/" + id;
                foreach (var suffix in new[] { "", "_armor", "_phys" })
                {
                    var importer = AssetImporter.GetAtPath(folder + "/" + id + suffix + ".obj") as ModelImporter;
                    Assert.IsNotNull(importer, id + suffix);
                    Assert.AreEqual("blitz-source-11.20", importer.userData);
                    Assert.AreEqual(ModelImporterMeshCompression.Off, importer.meshCompression);
                    Assert.IsTrue(importer.isReadable);
                }
                var textures = AssetDatabase.FindAssets("t:Texture2D", new[] { folder + "/Textures" });
                Assert.IsNotEmpty(textures, id);
                foreach (var guid in textures)
                {
                    var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guid));
                    Assert.AreEqual(TextureImporterCompression.Uncompressed, importer.textureCompression);
                }
            }
        }

        [Test]
        public void Calibration_KeepsThe1120StandingWithinOneBoundedStep()
        {
            var db = Database();
            var tanks = Json("blitz_branches_1120_balance")["tanks"];
            Assert.AreEqual(9, tanks.Count);
            foreach (var entry in tanks.Items)
            {
                var name = entry["name"].AsString();
                Assert.Greater(entry["competitors"].Count, 5, name);
                Assert.Greater(entry["competitors_1120"].Count, 5, name);
                foreach (var key in new[] { "health", "output" })
                {
                    Assert.That(entry["factors"][key].AsDouble(), Is.InRange(0.85, 1.15),
                        name + "/" + key + ": at most 15 % either way");
                }
                var standing = entry["standing_1120"];
                var balanced = entry["standing_balanced"];
                foreach (var key in new[] { "total_health", "sustained_dpm" })
                {
                    Assert.That(balanced[key].AsDouble(), Is.EqualTo(standing[key].AsDouble()).Within(0.02),
                        name + "/" + key + " keeps its 11.20 standing");
                }
                Assert.LessOrEqual(balanced["total_health"].AsDouble() * balanced["sustained_dpm"].AsDouble(), 1.15 + 1e-6,
                    name + ": HP x DPM budget");
                Assert.AreEqual(entry["balanced_metrics"]["total_health"].AsInt(), db.Get(name).TotalHealth, name);

                // Alpha, penetration and shell speed are the gun's identity.
                var original = entry["original"]["guns"];
                var calibrated = entry["balanced"]["guns"];
                Assert.AreEqual(original.Count, calibrated.Count);
                for (var g = 0; g < original.Count; g++)
                {
                    for (var s = 0; s < original[g]["shells"].Count; s++)
                    {
                        foreach (var field in new[] { "dmg", "pen", "speed" })
                        {
                            Assert.AreEqual(original[g]["shells"][s][field].AsDouble(),
                                calibrated[g]["shells"][s][field].AsDouble(), name + "/" + field);
                        }
                    }
                }
            }
        }

        [Test]
        public void HullsUseTheVehicleSheetNotAPaintSkin()
        {
            // GB83_4005.sc2 binds the `GB83_4005_skin2` paint skin next to the
            // vehicle sheet `GB83_FV4005`. The skin has a UV layout of its own,
            // so as the hull sheet it smeared black patches over the FV4005
            // hull while the turret, bound per node, looked right.
            var manifest = Json("tank_models_source_manifest");
            foreach (var name in All())
            {
                var entry = manifest["tanks"][name];
                var body = entry["id"].AsString() + ".png";
                var bodies = 0;
                foreach (var texture in entry["textures"].Items)
                {
                    StringAssert.DoesNotContain("_skin", texture["source"].AsString().ToLowerInvariant(), name);
                    if (texture["destination"].AsString() == body) { bodies++; }
                }

                Assert.AreEqual(1, bodies, name + " body sheet");
            }

            foreach (var texture in manifest["tanks"]["gb83_4005"]["textures"].Items)
            {
                if (texture["destination"].AsString() == "GB834005.png")
                {
                    Assert.AreEqual("3d/Tanks/GB/images/GB83_FV4005.mali.pvr", texture["source"].AsString());
                }
            }

            var parts = 0;
            foreach (var field in Json("gb834005_nodes")["albedos"].Fields) { parts++; }
            Assert.AreEqual(0, parts, "turret and gun share the hull sheet");
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
                    { var result = service.ResearchModule(card.Name, kind, id); Assert.IsTrue(result.Ok, card.Name + "/" + modules[id].Name + "/" + result.ErrorKey); }
                }
            }
        }
    }
}
