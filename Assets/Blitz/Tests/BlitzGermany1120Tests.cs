using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Hangar;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>Complete playable source roster, not only the five researchable cards.</summary>
    public sealed class BlitzGermany1120Tests
    {
        private GameObject _root;
        private static readonly string[] NewNames = {
            "vk7201", "g136_tiger_131", "g114_rheinmetall_scorpion", "g115_vk10001p",
            "g110_mauschen", "g119_pz58_mutz", "g138_vk16801p", "g_e-75ts", "g_vk9001p",
            "vk2801_bp", "g112_jagdpanzer_105", "pzvi_ausfbp", "g147_mpanzer_64",
            "g144_kpz_50t", "pz_iv_ausfbt", "g109_steyr_wt", "vk3002db_bp", "g142_m48rpz",
            "g_e-75rt", "jagdpziv_ausfbp", "g_vk3502_bp", "waffentrager_iv_cl", "g140_hwk_30",
            "g148_elephant", "nashorn_bp", "g154_kpz_07_rh", "g164_kpz_pr_68_p",
            "g85_auf_panther", "g162_project_kpz_07p_e", "g119_panzer58_sh", "g165_erich_koncept",
            "g166_lkpz_70_k", "g126_hwk_12", "g125_spz_57_rh", "g98_waffentrager_e100",
            "g167_tiger_maus", "g190_vk_1602_quby", "g168_kjpz_t_iii", "g14_kpfpz_70r" };

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
        private static TanksDatabase Overlay()
        { return TanksDatabase.Parse(Resources.Load<TextAsset>("Data/blitz_germany_1120_cards").text); }

        [TearDown]
        public void TearDown()
        {
            BattleGun.Clock = null;
            if (_root != null) { Object.DestroyImmediate(_root); }
        }

        [Test]
        public void Roster_AccountsForEveryPlayableGermanSourceRecord()
        {
            var db = Database(); var overlay = Overlay();
            Assert.AreEqual(522, db.Count); Assert.AreEqual(39, overlay.Count);
            var german = 0; var researchable = 0; var premium = 0; var collectible = 0;
            foreach (var tank in db.Tanks) { if (tank.Nation == Nation.Germany) { german++; } }
            Assert.AreEqual(116, german);
            foreach (var name in NewNames)
            {
                var card = db.Get(name); Assert.IsNotNull(card, name);
                Assert.IsNotNull(overlay.Get(name), name);
                Assert.AreEqual(Nation.Germany, card.Nation, name);
                Assert.IsFalse(card.Hidden, name);
                if (card.TreeTank) { researchable++; Assert.IsFalse(card.Premium, name); }
                if (card.Collector) { collectible++; Assert.IsTrue(card.Premium, name); }
                if (card.Acquisition == "premium") { premium++; Assert.IsTrue(card.Premium, name); }
            }
            Assert.AreEqual(5, researchable); Assert.AreEqual(2, premium); Assert.AreEqual(32, collectible);
            Assert.IsFalse(db.Has("pzvi_ausftu"), "Removed duplicate collectible Tiger I must stay absent");
            Assert.IsFalse(db.Has("pzvi_ausftur"), "Removed duplicate collectible Tiger I variant must stay absent");
            foreach (var excluded in new[] { "pzii_tutorial_bot", "pzii_tutorial_bot2", "ltraktor_tutorial_bot", "ltraktor_tutorial_bot2" })
            { Assert.IsFalse(db.Has(excluded), excluded); }
        }

        [Test]
        public void LiveManifests_ExcludeRemovedTigerDuplicatesAndUnknownVehicles()
        {
            var db = Database();
            var models = Json("tank_models_source_manifest")["tanks"];
            var crash = Json("legacy1120_crash_manifest");
            var atlas = Json("atlas_regions");
            foreach (var entry in models.Fields)
            { Assert.IsTrue(db.Has(entry.Key), entry.Key + ": stale model manifest"); }
            foreach (var entry in crash["vehicles"].Fields)
            { Assert.IsTrue(db.Has(entry.Key), entry.Key + ": stale crash manifest"); }
            foreach (var name in new[] { "pzvi_ausftu", "pzvi_ausftur" })
            {
                Assert.IsFalse(db.Has(name));
                var id = TankModelAssets.ResourceId(name).ToLowerInvariant();
                Assert.AreEqual(JsonKind.Null, atlas["regions"]["tank_icon_" + id].Kind);
                Assert.AreEqual(JsonKind.Null, atlas["regions"]["flow_tank_" + id + "_small"].Kind);
                Assert.AreEqual(JsonKind.Null, atlas["textures"]["TankIcons/" + id].Kind);
                Assert.AreEqual(JsonKind.Null, atlas["textures"]["SmallTankIcons/" + id].Kind);
                foreach (var source in crash["provenance"].Fields)
                { Assert.Less(source.Key.IndexOf("/" + name + ".", StringComparison.OrdinalIgnoreCase), 0); }
            }
            Assert.IsTrue(db.Has("pzvi"), "The researchable Tiger I must remain");
        }

        [Test]
        public void EverySourceModuleAndTurretGunCompatibility_IsPresent()
        {
            var inventory = Json("blitz_germany_1120_inventory"); var db = Database();
            var totals = new int[5]; var configurations = 0;
            var groups = new[] { "turrets", "guns", "tracks", "engines", "radios" };
            foreach (var record in inventory["records"].Items)
            {
                var card = db.Get(record["name"].AsString()); Assert.IsNotNull(card);
                var lists = new IReadOnlyList<ModuleInfo>[] { card.Turrets, card.Guns, card.Tracks, card.Engines, card.Radios };
                for (var kind = 0; kind < groups.Length; kind++)
                {
                    var expected = record["modules"][groups[kind]];
                    Assert.AreEqual(expected.Count, lists[kind].Count, card.Name + "/" + groups[kind]);
                    for (var m = 0; m < expected.Count; m++)
                    { Assert.AreEqual(expected[m].AsString(), lists[kind][m].Name, card.Name); totals[kind]++; }
                }
                foreach (var configuration in record["configurations"].Items)
                {
                    var turret = card.Turrets.Find(t => t.Name == configuration["turret"].AsString());
                    Assert.IsNotNull(turret, card.Name); var slot = turret.Slot(configuration["gun"].AsString());
                    Assert.IsNotNull(slot, card.Name); Assert.AreEqual(configuration["turret_mesh"].AsString(), turret.Mesh);
                    Assert.AreEqual(configuration["gun_mesh"].AsString(), slot.Mesh); configurations++;
                }
                Assert.IsNotNull(card.AmmoBay, card.Name); Assert.IsNotNull(card.FuelTank, card.Name);
                Assert.IsNotEmpty(card.Crew, card.Name);
            }
            CollectionAssert.AreEqual(new[] { 40, 45, 41, 42, 39 }, totals);
            Assert.AreEqual(46, configurations);
        }

        [Test]
        public void OldCards_KeepEveryPropertyModuleAndResearchEdgeExactly()
        {
            var protectedCards = Json("blitz_germany_1120_preservation")["cards"];
            var checkedCards = 0;
            foreach (var fragment in CardFragments(Resources.Load<TextAsset>("Data/tanks").text))
            {
                JsonNode card; Assert.IsTrue(JsonNode.TryParse(fragment, out card));
                var name = card["name"].AsString(); var expected = protectedCards[name].AsString();
                if (string.IsNullOrEmpty(expected)) { continue; }
                Assert.AreEqual(expected, Hash(WithoutTreeRow(Minify(fragment))), name + ": previous card changed");
                checkedCards++;
            }
            Assert.AreEqual(357, checkedCards);
        }

        [Test]
        public void ResearchBranches_ReachAllFiveTanksWithoutReplacingOldRoutes()
        {
            var db = Database();
            AssertEdge(db, "pzvi_tiger_p", "g115_vk10001p", 78500);
            AssertEdge(db, "g115_vk10001p", "g110_mauschen", 160500);
            AssertEdge(db, "g110_mauschen", "maus", 245590);
            AssertEdge(db, "vk4502p", "vk7201", 245000);
            AssertEdge(db, "g103_ru_251", "g126_hwk_12", 202500);
            AssertEdge(db, "g126_hwk_12", "g125_spz_57_rh", 330700);
            Assert.IsTrue(db.Get("vk4502p").UnlockTanks.ContainsKey("maus"), "Original Maus path must survive");
            Assert.IsTrue(db.Get("g103_ru_251").UnlockTanks.ContainsKey("pro_ag_a"), "Original Leopard path must survive");
            Assert.IsTrue(db.Get("pzvi_tiger_p").UnlockTanks.ContainsKey("ferdinand"));
            Assert.IsTrue(db.Get("pzvi_tiger_p").UnlockTanks.ContainsKey("vk4502a"));
            var occupied = new HashSet<string>();
            foreach (var card in db.TreeTanks(Nation.Germany))
            { Assert.IsTrue(occupied.Add(card.Level + "/" + card.TreeBranch), card.Name + ": overlapping tree slot"); }
        }

        [TestCase("pzvi_tiger_p", "g115_vk10001p", ModuleKind.Turret)]
        [TestCase("vk4502p", "vk7201", ModuleKind.Gun)]
        [TestCase("g103_ru_251", "g126_hwk_12", ModuleKind.Tracks)]
        public void AdditionalLink_RequiresTheAuthoredOldModule_AndChargesTankXp(
            string parentName, string targetName, ModuleKind kind)
        {
            var db = Database(); var account = new AccountData { FreeXp = 0, Credits = 100000000 };
            account.AddTank(parentName, true); var service = new TankResearchService(account, db);
            var parent = db.Get(parentName); var link = parent.AdditionalResearchLinks[targetName];
            Assert.AreEqual(kind, link.RequiredKind);
            Assert.IsFalse(service.CanResearchTank(targetName));
            account.FreeXp = 100000000; ResearchAll(service, parent);
            Assert.IsTrue(service.CanResearchTank(targetName));
            account.FreeXp = 0; var payer = account.GetTank(parentName);
            var cost = db.ResearchCost(targetName); payer.Exp = cost + 13;
            var researched = service.ResearchTank(targetName);
            Assert.IsTrue(researched.Ok, researched.ErrorKey); Assert.AreEqual(cost, researched.Paid);
            Assert.AreEqual(0, researched.PaidFreeXp); Assert.AreEqual(13, payer.Exp);
            Assert.IsTrue(service.BuyTank(targetName).Ok);
            Assert.IsTrue(account.HasTank(targetName));
        }

        [Test]
        public void GunSidecar_AlsoRequiresTheSourceTurret_NotOnlyASharedGun()
        {
            var db = Database(); var account = new AccountData(); account.AddTank("vk4502p", true);
            var service = new TankResearchService(account, db);
            var link = db.Get("vk4502p").AdditionalResearchLinks["vk7201"];
            service.MarkModuleResearched(link.RequiredModule.Name);
            Assert.IsFalse(service.CanResearchTank("vk7201"));
            service.MarkModuleResearched(link.RequiredTurret.Name);
            Assert.IsTrue(service.CanResearchTank("vk7201"));
        }

        [TestCase("pzvi_tiger_p")]
        [TestCase("vk4502p")]
        [TestCase("g103_ru_251")]
        public void NewBranches_DoNotRevokePreviouslyCompletedEliteStatus(string name)
        {
            var db = Database(); var card = db.Get(name); var account = new AccountData(); account.AddTank(name, true);
            var service = new TankResearchService(account, db);
            for (var kind = ModuleKind.Gun; kind <= ModuleKind.Radio; kind++)
            { foreach (var module in TankResearchService.Modules(card, kind)) { service.MarkModuleResearched(module.Name); } }
            foreach (var edge in card.UnlockTanks)
            { if (!card.AdditionalResearchLinks.ContainsKey(edge.Key)) { account.AddTank(edge.Key, true); } }
            Assert.IsTrue(service.IsTankElite(name));
            foreach (var edge in card.AdditionalResearchLinks) { Assert.IsFalse(service.TankResearched(edge.Key)); }
        }

        [Test]
        public void EveryModule_IsResearchedAndInstalledThroughTheNormalRuntime()
        {
            var db = Database(); var checkedModules = 0;
            foreach (var name in NewNames)
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
            Assert.AreEqual(207, checkedModules);
        }

        [Test]
        public void PremiumAndCollectibleAlternativeGuns_AreFreeToInstall()
        {
            var db = Database(); var alternatives = 0;
            foreach (var name in NewNames)
            {
                var card = db.Get(name); if (!card.Premium) { continue; }
                Assert.IsFalse(card.TreeTank); Assert.IsEmpty(card.UnlockTanks); Assert.AreEqual(0, card.CostXp);
                var account = new AccountData(); account.AddTank(name, true);
                var service = new TankResearchService(account, db);
                foreach (var turret in card.Turrets)
                {
                    for (var g = 0; g < turret.Guns.Count; g++)
                    {
                        var id = card.Guns.FindIndex(x => x.Name == turret.Guns[g].Name);
                        Assert.AreEqual(0, card.Guns[id].CostXp); Assert.IsTrue(service.InstallModule(name, ModuleKind.Gun, id).Ok);
                        if (g > 0) { alternatives++; }
                    }
                }
            }
            Assert.AreEqual(3, alternatives, "Waffen/Erich/LKpz keep both authored gun options");
        }

        [Test]
        public void AllNewModels_ImportSourceNormalsAndPixelsAtTheClientRate()
        {
            var count = 0;
            foreach (var name in NewNames)
            {
                var id = TankModelAssets.ResourceId(name); var folder = "Assets/Blitz/Resources/Models/Tanks/" + id;
                foreach (var suffix in new[] { "", "_armor", "_phys" })
                {
                    var importer = AssetImporter.GetAtPath(folder + "/" + id + suffix + ".obj") as ModelImporter;
                    Assert.IsNotNull(importer, name + suffix); Assert.AreEqual("blitz-source-11.20", importer.userData);
                    Assert.AreEqual(ModelImporterNormals.Import, importer.importNormals);
                    Assert.AreEqual(ModelImporterMeshCompression.Off, importer.meshCompression); Assert.IsTrue(importer.isReadable);
                    count++;
                }
                foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folder + "/Textures" }))
                {
                    var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guid));
                    Assert.AreEqual(TextureImporterCompression.Uncompressed, importer.textureCompression);
                    BlitzAssetPostprocessorTests.RequireSourceAndroidFormat(importer);
                    Assert.AreEqual(TextureImporterAlphaSource.FromInput, importer.alphaSource); Assert.IsFalse(importer.alphaIsTransparency);
                }
            }
            Assert.AreEqual(NewNames.Length * 3, count);
        }

        [Test]
        public void EveryBattleConfiguration_HasExactArmorAndWorkingEngineTrackSelections()
        {
            _root = new GameObject("German1120Configurations"); var spawn = new GameObject("Spawn").transform; spawn.SetParent(_root.transform);
            var configurations = 0;
            foreach (var name in NewNames)
            {
                var card = Database().Get(name); var assets = TankModelAssets.For(card);
                var table = TankArmorTable.Parse(Resources.Load<TextAsset>("Data/" + assets.ArmorDataResource).text);
                for (var turret = 0; turret < card.Turrets.Count; turret++)
                foreach (var slot in card.Turrets[turret].Guns)
                for (var engine = 0; engine < card.Engines.Count; engine++)
                for (var tracks = 0; tracks < card.Tracks.Count; tracks++)
                {
                    var gun = card.Guns.FindIndex(g => g.Name == slot.Name);
                    var tank = BattleTank.Spawn(_root.transform, name, card, spawn,
                        new Bounds(Vector3.zero, new Vector3(1000, 100, 1000)), 1, gun, engine, tracks, null, turret);
                    var expected = 0;
                    foreach (var plate in table.Plates)
                    {
                        if (!string.IsNullOrEmpty(plate.RequiredTurret) && plate.RequiredTurret != card.Turrets[turret].Mesh) { continue; }
                        if (plate.Mount == "hull" || plate.Mount == card.Turrets[turret].Mesh || plate.Mount == slot.Mesh) { expected++; }
                    }
                    Assert.AreEqual(expected, tank.Plates.Count, name + "/" + slot.Mesh);
                    Assert.AreEqual(card.Engines[engine].Power, tank.Suspension.EnginePower);
                    Assert.AreEqual(card.Tracks[tracks].Name, tank.TrackInfo.Name);
                    foreach (var plate in tank.Plates)
                    {
                        Assert.IsNotNull(plate.Shape.sharedMesh, plate.PlateName);
                        if (plate.Module == TankModuleIds.Tracks) { Assert.AreEqual(card.Tracks[tracks].Armor, plate.Armor); }
                        if (plate.Mount == slot.Mesh) { Assert.IsTrue(plate.transform.IsChildOf(tank.Turret.GunPivot)); }
                    }
                    tank.Turret.TurretPivot.localRotation *= Quaternion.Euler(0, 65, 0);
                    tank.Turret.GunPivot.localRotation *= Quaternion.Euler(-7, 0, 0);
                    Physics.SyncTransforms();
                    foreach (var plate in tank.Plates) { Assert.IsFalse(float.IsNaN(plate.Shape.bounds.center.y)); }
                    configurations++; Object.DestroyImmediate(tank.gameObject);
                }
            }
            Assert.GreaterOrEqual(configurations, 46);
        }

        [Test]
        public void EveryMountedShell_TracesTheWorldWithANonCollapsedReticle()
        {
            _root = new GameObject("German1120Aim"); var spawn = new GameObject("Spawn").transform; spawn.SetParent(_root.transform);
            var wall = new GameObject("AimWall"); wall.transform.SetParent(_root.transform); wall.layer = BattleLayers.Static;
            wall.transform.position = new Vector3(0, 0, 100); var box = wall.AddComponent<BoxCollider>(); box.size = new Vector3(100, 100, .5f);
            var count = 0; var shellCount = 0;
            foreach (var card in Overlay().Tanks)
            for (var turret = 0; turret < card.Turrets.Count; turret++)
            foreach (var slot in card.Turrets[turret].Guns)
            {
                var gun = card.Guns.FindIndex(g => g.Name == slot.Name);
                var tank = BattleTank.Spawn(_root.transform, card.Name, card, spawn,
                    new Bounds(Vector3.zero, new Vector3(1000, 100, 1000)), 1, gun, 0, card.Tracks.Count - 1, null, turret);
                Physics.SyncTransforms();
                for (var shell = 0; shell < tank.GunInfo.Shells.Count; shell++)
                {
                    Assert.Greater(tank.GunInfo.Shells[shell].Speed, 0, card.Name);
                    tank.ShellId = shell; tank.UpdateGunTrace();
                    Assert.AreSame(box, tank.GunTraceCollider, card.Name + "/" + slot.Mesh + "/" + shell);
                    Assert.Greater(tank.GunDistanceToAim(), 80, card.Name);
                    Assert.Greater(tank.DispersionOffset.magnitude, .1f, card.Name);
                    Assert.AreEqual(tank.GunTracePoint, BattleHud.ReticlePoint(tank)); shellCount++;
                }
                count++; Object.DestroyImmediate(tank.gameObject);
            }
            Assert.AreEqual(46, count); Assert.AreEqual(137, shellCount);
        }

        [Test]
        public void AllGunReports_UseTheUnchanged480SoundBankIncludingTheRocketLauncher()
        {
            var bank = Json("tank_sounds"); var expected = Json("blitz_germany_1120_preservation")["shot_bank_sha256"].AsString();
            Assert.AreEqual(expected, Hash(Resources.Load<TextAsset>("Data/tank_sounds").text));
            var count = 0;
            foreach (var card in Overlay().Tanks)
            foreach (var gun in card.Guns)
            {
                var key = TankSoundTable.GunKey((ShootSound)gun.ShotSound); var report = bank["guns"][key];
                foreach (var position in new[] { "sound_near", "sound_dist", "sound_inside" })
                {
                    Assert.Greater(report[position]["streams"].Count, 0, card.Name + "/" + key);
                    foreach (var stream in report[position]["streams"].Items)
                    { Assert.IsNotNull(Resources.Load<AudioClip>("Audio/" + stream.AsString()), stream.AsString()); }
                }
                count++;
            }
            Assert.AreEqual(45, count);
            // gun_effects.xml fires the KPz 70 R gun with shot_huge; only its
            // guided round takes the launcher report, picked per shot.
            Assert.AreEqual((int)ShootSound.ShotHuge, Database().Get("g14_kpfpz_70r").Guns[0].ShotSound);
        }

        [Test]
        public void SourceBurstAndGuidedRocketFeatures_AreNotFlattenedByRebalance()
        {
            var db = Database(); var gun = db.Get("g190_vk_1602_quby").Guns[0];
            CollectionAssert.AreEqual(new[] { 5, 5, 5 }, gun.Burst.Bursts);
            Assert.AreEqual(1f, gun.Burst.IntraClip); Assert.AreEqual(.15f, gun.Burst.IntraBurst);
            var rocket = db.Get("g14_kpfpz_70r").Guns[0];
            Assert.IsFalse(rocket.Shells[0].Atgm); Assert.IsTrue(rocket.Shells[1].Atgm);
            Assert.Greater(rocket.Shells[1].TurnSpeed, 0); Assert.Greater(rocket.Shells[1].MaxDistance, 0);
        }

        [Test]
        public void AllNewCards_RespectTheirRecordedHealthAndSustainedOutputCaps()
        {
            var reports = Json("blitz_germany_1120_balance")["tanks"]; var db = Database();
            Assert.AreEqual(39, reports.Count);
            foreach (var report in reports.Items)
            {
                var card = db.Get(report["name"].AsString());
                Assert.LessOrEqual(card.TotalHealth, report["caps"]["total_health"].AsFloat() + .01f, card.Name);
                var output = 0f;
                for (var t = 0; t < card.Turrets.Count; t++)
                foreach (var slot in card.Turrets[t].Guns)
                {
                    var id = card.Guns.FindIndex(g => g.Name == slot.Name); var gun = card.GunFor(t, id);
                    var shots = 0; var gap = 0f;
                    foreach (var group in gun.Burst.Bursts) { shots += group; gap += Math.Max(0, group - 1) * gun.Burst.IntraBurst; }
                    gap += Math.Max(0, gun.Burst.Bursts.Count - 1) * gun.Burst.IntraClip;
                    shots = Math.Max(shots, 1); output = Math.Max(output, 60f * gun.Shells[0].Damage * shots / (gun.Reload + gap));
                }
                Assert.LessOrEqual(output, report["caps"]["sustained_dpm"].AsFloat() + .01f, card.Name);
                var product = card.TotalHealth / report["peer_medians"]["total_health"].AsFloat()
                    * output / report["peer_medians"]["sustained_dpm"].AsFloat();
                Assert.LessOrEqual(product, 1.15001f, card.Name);
            }
        }

        [Test]
        public void EveryNewVehicle_HasHangarSmallIconsAndAnAuthoredCrashState()
        {
            var atlas = Json("atlas_regions"); var crash = Json("legacy1120_crash_manifest");
            foreach (var name in NewNames)
            {
                var id = TankModelAssets.ResourceId(name).ToLowerInvariant();
                Assert.IsNotNull(Resources.Load<Texture2D>("Textures/TankIcons/" + id), name);
                Assert.IsNotNull(Resources.Load<Texture2D>("Textures/SmallTankIcons/" + id), name);
                Assert.AreEqual("TankIcons/" + id, atlas["regions"]["tank_icon_" + id]["texture"].AsString(), name);
                Assert.AreEqual("SmallTankIcons/" + id, atlas["regions"]["flow_tank_" + id + "_small"]["texture"].AsString(), name);
                Assert.IsNotEmpty(crash["vehicles"][name]["sourceScene"].AsString(), name);
            }
        }

        [Test]
        public void Barkhan_HullDetailUsesItsOwnSourceTextureAlphaAndCullState()
        {
            var card = Database().Get("g_vk3502_bp"); var assets = TankModelAssets.For(card);
            var nodes = TankNodeTable.Parse(Resources.Load<TextAsset>("Data/" + assets.NodeDataResource).text);
            const string detail = "hull_detail_G_VK3502H_BP_PALM";
            Assert.AreEqual("G_VK3502H_BP_PALM", nodes.AlbedoOf(detail)); Assert.IsTrue(nodes.HasMaterialSettings(detail));
            var texture = Resources.Load<Texture2D>(assets.AlbedoTextureResource(nodes.AlbedoOf(detail)));
            Assert.IsNotNull(texture); var material = BattleTank.CreateTankNodeMaterial("Palm", texture, nodes, detail);
            try
            {
                Assert.IsTrue(material.IsKeywordEnabled("_ALPHATEST_ON"));
                Assert.AreEqual(nodes.AlphaCutoffOf(detail), material.GetFloat("_Cutoff"));
                Assert.AreEqual(0, material.GetFloat("_Cull"));
            }
            finally { Object.DestroyImmediate(material); }
            Assert.IsFalse(nodes.HasMaterialSettings("hull_vis")); Assert.AreEqual(0, nodes.AlphaCutoffOf("hull_vis")); Assert.AreEqual(2, nodes.CullModeOf("hull_vis"));
        }

        [Test]
        public void FullResearchAndInstalledChoices_SurviveSaveLoadAndAccountCleanup()
        {
            var db = Database();
            foreach (var name in NewNames)
            {
                var card = db.Get(name); var account = new AccountData { Credits = 100000000, FreeXp = 100000000 };
                account.AddTank(name, true); var service = new TankResearchService(account, db);
                ResearchAll(service, card);
                for (var kind = ModuleKind.Gun; kind <= ModuleKind.Radio; kind++)
                {
                    if (kind == ModuleKind.Gun) { continue; }
                    var modules = TankResearchService.Modules(card, kind);
                    Assert.IsTrue(service.InstallModule(name, kind, modules.Count - 1).Ok);
                }
                var gun = card.Guns.FindIndex(g => g.Name == card.Turrets[card.Turrets.Count - 1].Guns[card.Turrets[card.Turrets.Count - 1].Guns.Count - 1].Name);
                Assert.IsTrue(service.InstallModule(name, ModuleKind.Gun, gun).Ok);
                var saved = account.GetTank(name);
                JsonNode serialized; Assert.IsTrue(JsonNode.TryParse(account.ToJson().ToJson(), out serialized));
                var restored = AccountData.FromJson(serialized);
                CollectionAssert.AreEquivalent(account.ResearchedModules, restored.ResearchedModules, name + ": JSON round trip");
                var expectedResearch = new HashSet<string>(account.ResearchedModules, StringComparer.Ordinal)
                { card.Guns[saved.GunId].Name, card.Turrets[saved.TurretId].Name,
                  card.Engines[saved.EngineId].Name, card.Tracks[saved.TrackId].Name, card.Radios[saved.RadioId].Name };
                var loaded = new TankResearchService(restored, db);
                // Cleanup explicitly records installed stock modules that were
                // already implicitly researched; it must not lose any progress.
                loaded.CleanupAccount(); var installed = restored.GetTank(name);
                Assert.AreEqual(saved.GunId, installed.GunId, name);
                Assert.AreEqual(saved.TurretId, installed.TurretId, name);
                Assert.AreEqual(saved.EngineId, installed.EngineId, name);
                Assert.AreEqual(saved.TrackId, installed.TrackId, name);
                Assert.AreEqual(saved.RadioId, installed.RadioId, name);
                CollectionAssert.AreEquivalent(expectedResearch, restored.ResearchedModules, name);
                for (var kind = ModuleKind.Gun; kind <= ModuleKind.Radio; kind++)
                {
                    var modules = TankResearchService.Modules(card, kind);
                    for (var id = 0; id < modules.Count; id++)
                    { Assert.That(loaded.ModuleStatus(name, kind, id), Is.EqualTo(ResearchStatus.Researched).Or.EqualTo(ResearchStatus.InstalledBought), name); }
                }
            }
        }

        [Test]
        public void Kpz07Rh_GunBarrelCollisionUsesAuthored35MmAndGunModule()
        {
            var plates = Json("g154kpz07rh_armor")["plates"]; var found = 0;
            foreach (var plate in plates.Items)
            {
                if (plate["source_plate_id"].AsInt() != -1) { continue; }
                Assert.AreEqual(35, plate["armor"].AsInt()); Assert.IsTrue(plate["screen"].AsBool());
                Assert.AreEqual("gun", plate["module"].AsString()); found++;
            }
            Assert.AreEqual(1, found);
        }

        [Test]
        public void Tiger131_UsesTheClientDavaTrunnion_NotTheObsoletePcXmlFrame()
        {
            var nodes = TankNodeTable.Parse(Resources.Load<TextAsset>("Data/g136tiger131_nodes").text);
            var turret = nodes.SourceWorld("turret_01"); var gun = nodes.SourceWorld("gun_01");
            Assert.AreEqual(.002886f, turret.X, .000002f);
            Assert.AreEqual(1.766589f, turret.Y, .000002f);
            Assert.AreEqual(.164822f, turret.Z, .000002f);
            Assert.AreEqual(-.001008f, gun.X, .000002f);
            Assert.AreEqual(2.206265f, gun.Y, .000002f);
            Assert.AreEqual(-1.06916f, gun.Z, .000002f);
            Assert.Less(Math.Abs(nodes.SourceWorld("gun_01_shoot_pos").Y - gun.Y), .03f);
        }

        [Test]
        public void ErichKoncept_KeepsTheSourceOscillatingBodyAndStationaryPivotShell()
        {
            var nodes = TankNodeTable.Parse(Resources.Load<TextAsset>("Data/g165erichkoncept_nodes").text);
            CollectionAssert.AreEqual(new[] { "turret_01", "gun_01", "gun_02" }, nodes.CollisionVisualHostsOf("turret_01"));
            Assert.AreEqual(2.065762f, nodes.SourceWorld("turret_01").Y, .000002f);
            Assert.AreEqual(2.0658f, nodes.SourceWorld("gun_01").Y, .000002f);
            Assert.AreEqual(2.0658f, nodes.SourceWorld("gun_02").Y, .000002f);
            var prefab = Resources.Load<GameObject>(TankModelAssets.For(Database().Get("g165_erich_koncept")).ModelResource);
            var carriers = 0;
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.name != "turret_01") { continue; }
                Assert.Less(filter.sharedMesh.bounds.size.y, .02f, "Source yaw node is a carrier plane, not the turret body");
                carriers++;
            }
            Assert.AreEqual(1, carriers);
            var table = Json("g165erichkoncept_armor");
            Assert.AreEqual(0, table["mountOffsets"].Count, "Do not move the source internal pivot shell onto the empty plane");
            var plates = 0; var upperArmour = 0;
            foreach (var plate in table["plates"].Items)
            {
                if (plate["mount"].AsString() == "turret_01")
                {
                    Assert.AreEqual(1, plate["source_plate_id"].AsInt());
                    Assert.AreEqual(5, plate["armor"].AsInt());
                    Assert.AreEqual(4, plate["triangles"].AsInt());
                    Assert.IsTrue(plate["screen"].AsBool()); plates++;
                }
                if (plate["mount"].AsString() == "gun_01" && !plate["screen"].AsBool()) { upperArmour++; }
            }
            // XML also lists unused armour values: do not invent faces for
            // them. The SCG carrier is precisely four triangles of plate 1.
            Assert.AreEqual(1, plates, "Keep the one source lower-pivot screen");
            Assert.Greater(upperArmour, 3, "HP-bearing turret plates belong to the oscillating upper body");
            Assert.IsEmpty(nodes.CollisionVisualHostsOf("gun_01"));
        }

        private static void AssertEdge(TanksDatabase db, string from, string to, int cost)
        { Assert.IsTrue(db.Get(from).UnlockTanks.ContainsKey(to), from); Assert.AreEqual(cost, db.Get(from).UnlockTanks[to]); }

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

        private static string Hash(string text)
        {
            using (var hash = SHA256.Create())
            { return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
        }
        /// <summary>
        /// Tools/tech_tree_layout.py owns the tree rows, so the preservation
        /// digests leave tree_branch out, exactly like the registries' card_hash.
        /// </summary>
        private static string WithoutTreeRow(string minified)
        {
            return System.Text.RegularExpressions.Regex.Replace(minified, ",\"tree_branch\":-?[0-9]+", string.Empty);
        }
        private static string Minify(string json)
        {
            var result = new StringBuilder(); var quoted = false; var escaped = false;
            foreach (var c in json)
            {
                if (quoted)
                {
                    result.Append(c);
                    if (escaped) { escaped = false; }
                    else if (c == '\\') { escaped = true; }
                    else if (c == '"') { quoted = false; }
                }
                else if (c == '"') { quoted = true; result.Append(c); }
                else if (!char.IsWhiteSpace(c)) { result.Append(c); }
            }
            return result.ToString();
        }
        private static IEnumerable<string> CardFragments(string json)
        {
            var position = json.IndexOf("\"tanks\"", StringComparison.Ordinal);
            position = json.IndexOf('[', position); var depth = 0; var first = -1; var quoted = false; var escaped = false;
            for (var i = position + 1; i < json.Length; i++)
            {
                var c = json[i];
                if (quoted)
                {
                    if (escaped) { escaped = false; }
                    else if (c == '\\') { escaped = true; }
                    else if (c == '"') { quoted = false; }
                    continue;
                }
                if (c == '"') { quoted = true; continue; }
                if (c == '{') { if (depth++ == 0) { first = i; } }
                else if (c == '}') { if (--depth == 0) { yield return json.Substring(first, i - first + 1); } }
                else if (c == ']' && depth == 0) { yield break; }
            }
        }
    }
}
