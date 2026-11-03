using System.Collections.Generic;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;

namespace BlitzLegacy.Tests
{
    public sealed class Blitz1120Tests
    {
        private static readonly string[] European = {
            "f01_vickers_mkf", "pl06_10tp", "pl07_14tp", "s04_lago-i", "s02_strvm42",
            "s07_strv_74", "s13_leo", "s18_emil_1951e1", "s17_emil_1952e2", "s16_kranvagn" };
        private static readonly string[] French = {
            "f126_amx_13_m24", "f123_erac_105_proto", "f120_project_louis", "f119_projet_murat" };
        private GameObject _root;
        private float _now;
        private static TanksDatabase Database()
        { return TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData)); }

        [TearDown]
        public void TearDown()
        {
            BattleGun.Clock = null;
            if (_root != null) { Object.DestroyImmediate(_root); }
        }

        [Test]
        public void CompleteBranches_KeepEveryTierAndSourceModule()
        {
            var db = Database();
            Assert.AreEqual(15, db.TreeTanks(Nation.European).Count);
            for (var i = 0; i < European.Length; i++)
            {
                var card = db.Get(European[i]);
                Assert.AreEqual(i + 1, card.Level);
                Assert.IsTrue(card.TreeTank);
                Assert.IsFalse(card.Premium);
                if (i + 1 < European.Length) { Assert.IsTrue(card.UnlockTanks.ContainsKey(European[i + 1])); }
            }
            Assert.IsTrue(db.Get("amx_12t").UnlockTanks.ContainsKey(French[0]));
            for (var i = 0; i < French.Length; i++)
            {
                var card = db.Get(French[i]);
                Assert.AreEqual(i + 7, card.Level);
                Assert.IsFalse(card.Premium);
                if (i + 1 < French.Length) { Assert.IsTrue(card.UnlockTanks.ContainsKey(French[i + 1])); }
            }
            var collector = db.Get("ch57_bzt_70");
            Assert.IsTrue(collector.Collector);
            Assert.IsFalse(collector.TreeTank);
            Assert.IsEmpty(collector.UnlockTanks);
        }

        [Test]
        public void NewModels_ImportAuthoredNormalsAndPixelsAtTheClientRate()
        {
            var cards = TanksDatabase.Parse(Resources.Load<TextAsset>("Data/blitz_1120_cards").text);
            var models = 0;
            foreach (var card in cards.Tanks)
            {
                var id = TankModelAssets.ResourceId(card.Name);
                var folder = "Assets/Blitz/Resources/Models/Tanks/" + id;
                foreach (var suffix in new[] { "", "_armor", "_phys" })
                {
                    var importer = AssetImporter.GetAtPath(folder + "/" + id + suffix + ".obj") as ModelImporter;
                    Assert.IsNotNull(importer, id + suffix);
                    Assert.AreEqual("blitz-source-11.20", importer.userData);
                    Assert.AreEqual(ModelImporterMeshCompression.Off, importer.meshCompression);
                    Assert.AreEqual(ModelImporterNormals.Import, importer.importNormals);
                    Assert.IsTrue(importer.isReadable);
                    models++;
                }
                foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folder + "/Textures" }))
                {
                    var importer = AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guid)) as TextureImporter;
                    Assert.AreEqual(TextureImporterCompression.Uncompressed, importer.textureCompression);
                    BlitzAssetPostprocessorTests.RequireSourceAndroidFormat(importer);
                    Assert.AreEqual(TextureImporterAlphaSource.FromInput, importer.alphaSource);
                    Assert.IsFalse(importer.alphaIsTransparency);
                }
            }
            Assert.AreEqual(45, models);
        }

        [Test]
        public void NewNationRoot_IsFreeAndReachableForAnExistingAccount()
        {
            var account = new AccountData();
            account.AddTank("ms-1", true);
            var service = new TankResearchService(account, Database());
            Assert.AreEqual(ResearchStatus.Available, service.TankStatus(European[0]));
            Assert.IsTrue(service.ResearchTank(European[0]).Ok);
            Assert.IsTrue(service.BuyTank(European[0]).Ok);
            Assert.IsTrue(account.HasTank(European[0]));
        }

        [Test]
        public void EveryModule_CanBeResearchedAndInstalledWithoutDeadEnds()
        {
            var db = Database();
            var names = new List<string>(European);
            names.AddRange(French);
            foreach (var name in names)
            {
                var account = new AccountData { FreeXp = 100000000, Credits = 100000000 };
                account.AddTank(name, true);
                var service = new TankResearchService(account, db);
                var card = db.Get(name);
                // Resolve the graph to a fixed point, in runtime order, without
                // bypassing prerequisites by calling MarkModuleResearched.
                for (var pass = 0; pass < 20; pass++)
                {
                    for (var kind = ModuleKind.Gun; kind <= ModuleKind.Radio; kind++)
                    {
                        var modules = TankResearchService.Modules(card, kind);
                        for (var id = 0; id < modules.Count; id++)
                        {
                            if (service.ModuleStatus(name, kind, id) == ResearchStatus.Available)
                            { Assert.IsTrue(service.ResearchModule(name, kind, id).Ok, name + "/" + modules[id].Name); }
                        }
                    }
                }
                for (var kind = ModuleKind.Gun; kind <= ModuleKind.Radio; kind++)
                {
                    var modules = TankResearchService.Modules(card, kind);
                    for (var id = 0; id < modules.Count; id++)
                    {
                        Assert.That(service.ModuleStatus(name, kind, id),
                            Is.EqualTo(ResearchStatus.Researched).Or.EqualTo(ResearchStatus.InstalledBought), name + "/" + modules[id].Name);
                    }
                }
                for (var t = 0; t < card.Turrets.Count; t++)
                {
                    service.InstallModule(name, ModuleKind.Turret, t);
                    foreach (var slot in card.Turrets[t].Guns)
                    {
                        var id = card.Guns.FindIndex(g => g.Name == slot.Name);
                        var status = service.ModuleStatus(name, ModuleKind.Gun, id);
                        if (status != ResearchStatus.InstalledBought)
                        { Assert.IsTrue(service.InstallModule(name, ModuleKind.Gun, id).Ok, name + "/" + slot.Name); }
                    }
                }
            }
        }

        [Test]
        public void EveryConfiguration_MountsOnlyItsExactPlatesAndTrackModule()
        {
            _root = new GameObject("All1120Configurations");
            var spawn = new GameObject("Spawn").transform;
            spawn.SetParent(_root.transform);
            var cards = TanksDatabase.Parse(Resources.Load<TextAsset>("Data/blitz_1120_cards").text);
            var checkedConfigurations = 0;
            foreach (var card in cards.Tanks)
            {
                var assets = TankModelAssets.For(card);
                var table = TankArmorTable.Parse(Resources.Load<TextAsset>("Data/" + assets.ArmorDataResource).text);
                for (var t = 0; t < card.Turrets.Count; t++)
                {
                    foreach (var slot in card.Turrets[t].Guns)
                    {
                        var id = card.Guns.FindIndex(g => g.Name == slot.Name);
                        var tank = BattleTank.Spawn(_root.transform, card.Name, card, spawn,
                            new Bounds(Vector3.zero, new Vector3(1000, 100, 1000)), 1,
                            id, 0, card.Tracks.Count - 1, null, t);
                        var expected = 0;
                        foreach (var plate in table.Plates)
                        {
                            if (!string.IsNullOrEmpty(plate.RequiredTurret) && plate.RequiredTurret != card.Turrets[t].Mesh) { continue; }
                            if (plate.Mount == "hull" || plate.Mount == card.Turrets[t].Mesh || plate.Mount == slot.Mesh) { expected++; }
                        }
                        Assert.AreEqual(expected, tank.Plates.Count, card.Name + "/" + slot.Mesh);
                        foreach (var plate in tank.Plates)
                        {
                            Assert.IsNotNull(plate.Shape.sharedMesh, plate.PlateName);
                            if (plate.Module == TankModuleIds.Tracks)
                            { Assert.AreEqual(card.Tracks[card.Tracks.Count - 1].Armor, plate.Armor); }
                            if (plate.Mount == slot.Mesh)
                            { Assert.IsTrue(plate.transform.IsChildOf(tank.Turret.GunPivot), plate.PlateName); }
                        }
                        tank.Turret.TurretPivot.localRotation *= Quaternion.Euler(0, 75, 0);
                        tank.Turret.GunPivot.localRotation *= Quaternion.Euler(-10, 0, 0);
                        Physics.SyncTransforms();
                        foreach (var plate in tank.Plates)
                        { Assert.IsFalse(float.IsNaN(plate.Shape.bounds.center.y), plate.PlateName); }
                        checkedConfigurations++;
                        Object.DestroyImmediate(tank.gameObject);
                    }
                }
            }
            Assert.AreEqual(33, checkedConfigurations);
        }

        [Test]
        public void EveryNewShellHasInheritedBallisticsInTheLiveRegistry()
        {
            var overlay=TanksDatabase.Parse(Resources.Load<TextAsset>("Data/blitz_1120_cards").text);
            var live=Database();var checkedShells=0;
            foreach(var card in overlay.Tanks)
                foreach(var gun in card.Guns)
                {
                    var mounted=live.Get(card.Name).Guns.Find(g=>g.Name==gun.Name);
                    for(var shell=0;shell<gun.Shells.Count;shell++)
                    {
                        Assert.Greater(gun.Shells[shell].Speed,0,card.Name+"/"+gun.Name+"/"+shell);
                        Assert.AreEqual(gun.Shells[shell].Speed,mounted.Shells[shell].Speed);
                        checkedShells++;
                    }
                }
            Assert.AreEqual(71,checkedShells);
        }

        [Test]
        public void EveryNewGunTracesToTheWorldInsteadOfItsTurret()
        {
            _root=new GameObject("1120AimRegression");
            var spawn=new GameObject("Spawn").transform;spawn.SetParent(_root.transform);
            var wall=new GameObject("AimWall");wall.transform.SetParent(_root.transform);
            wall.layer=BattleLayers.Static;wall.transform.position=new Vector3(0,0,100);
            wall.AddComponent<BoxCollider>().size=new Vector3(100,100,.5f);
            var cards=TanksDatabase.Parse(Resources.Load<TextAsset>("Data/blitz_1120_cards").text);
            var configurations=0;
            foreach(var card in cards.Tanks)
                for(var t=0;t<card.Turrets.Count;t++)
                    foreach(var slot in card.Turrets[t].Guns)
                    {
                        var id=card.Guns.FindIndex(g=>g.Name==slot.Name);
                        var tank=BattleTank.Spawn(_root.transform,card.Name,card,spawn,
                            new Bounds(Vector3.zero,new Vector3(1000,100,1000)),1,id,0,card.Tracks.Count-1,null,t);
                        Physics.SyncTransforms();
                        for(var shell=0;shell<tank.GunInfo.Shells.Count;shell++)
                        {
                            tank.ShellId=shell;tank.UpdateGunTrace();
                            Assert.AreSame(wall.GetComponent<BoxCollider>(),tank.GunTraceCollider,card.Name+"/"+slot.Mesh+"/"+shell);
                            Assert.Greater(tank.GunDistanceToAim(),80,card.Name);
                            Assert.Greater(tank.DispersionOffset.magnitude,.1f,card.Name+": collapsed circle");
                            Assert.AreEqual(tank.GunTracePoint,BattleHud.ReticlePoint(tank));
                        }
                        configurations++;Object.DestroyImmediate(tank.gameObject);
                    }
            Assert.AreEqual(33,configurations);
        }

        private BattleGunBurst Magazine()
        {
            _now = 100f;
            BattleGun.Clock = () => _now;
            _root = new GameObject("1120Tests");
            var spawn = new GameObject("Spawn").transform;
            spawn.SetParent(_root.transform);
            var tank = BattleTank.Spawn(_root.transform, "Kranvagn", Database().Get("s16_kranvagn"), spawn,
                new Bounds(Vector3.zero, new Vector3(1000, 100, 1000)), 1);
            var host = new GameObject("IsolatedMagazine");
            host.transform.SetParent(_root.transform);
            var magazine = host.AddComponent<BattleGunBurst>();
            magazine.Setup(tank.GunInfo, tank);
            return magazine;
        }

        [Test]
        public void AutoReload_LoadsOneShellWithoutDiscardingTheRest()
        {
            var gun = Magazine();
            Assert.IsTrue(gun.Info.AutoReload);
            Assert.AreEqual(3, gun.Bursts.Count);
            var shots = 0;
            gun.Shoot += first => shots++;
            gun.ShootAttempt();
            Assert.AreEqual(1, shots);
            Assert.AreEqual(2, gun.Bursts.Count);
            Assert.IsFalse(gun.CanShoot());
            gun.ForceMainReload(true);
            Assert.AreEqual(2, gun.Bursts.Count, "manual reload must not dump an autoreloader");
            _now += gun.Info.AutoReloadTimes[2] + .01f;
            gun.Tick();
            Assert.AreEqual(3, gun.Bursts.Count);
            Assert.IsTrue(gun.CanShoot());
        }

        [Test]
        public void AutoReload_InterruptsProgressAndRespectsEmptyAndLongFrames()
        {
            var gun = Magazine();
            gun.ShootAttempt();
            _now += gun.Info.Burst.IntraClip + .01f;
            gun.Tick();
            gun.ShootAttempt();
            Assert.AreEqual(1, gun.Bursts.Count);
            Assert.That(gun.NextShellSeconds, Is.EqualTo(gun.Info.AutoReloadTimes[1]).Within(.02f));
            _now += gun.Info.Burst.IntraClip + .01f;
            gun.Tick();
            gun.ShootAttempt();
            Assert.AreEqual(0, gun.Bursts.Count);
            Assert.IsFalse(gun.CanShoot());
            var reload = 0f;
            foreach (var seconds in gun.Info.AutoReloadTimes) { reload += seconds; }
            _now += reload + .1f;
            gun.Tick();
            Assert.AreEqual(3, gun.Bursts.Count, "long frames may not lose completed shells");
        }

        [Test]
        public void OscillatingGunArmour_KeepsHpPlatesAndLeoTurretContext()
        {
            var kran = TankArmorTable.Parse(Resources.Load<TextAsset>("Data/s16kranvagn_armor").text);
            var hpOnGun = 0;
            foreach (var plate in kran.Plates)
            { if (plate.Mount == "gun_01" && !plate.Screen) { hpOnGun++; } }
            Assert.Greater(hpOnGun, 0, "oscillating armour is not all spaced mantlet armour");
            var leo = TankArmorTable.Parse(Resources.Load<TextAsset>("Data/s13leo_armor").text);
            var contexts = new HashSet<string>();
            foreach (var plate in leo.Plates)
            { if (plate.Mount == "gun_03") { contexts.Add(plate.RequiredTurret); } }
            Assert.IsTrue(contexts.Contains("turret_01"));
            Assert.IsTrue(contexts.Contains("turret_02"));
        }
    }
}
