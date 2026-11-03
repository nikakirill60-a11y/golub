using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Battle.Ai;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>Complete playable USA 11.20 additive roster.</summary>
    public sealed class BlitzUsa1120Tests
    {
        private GameObject _root;
        private static readonly string[] NewNames = {
            "a95_t95e6", "a115_chrysler_k", "a68_t28d", "a117_t26e5", "a99_t92e1", "a116_xm551",
            "a124_t54e2", "a111_t25_pilot", "a121_t26e3", "m4a3e8_bp", "m4a3e8tu", "a116_xm551_exp",
            "m6e2bp", "a125_aep_1", "a122_ts-5", "m-6-y", "m-5-y", "m-3-y", "m-7-y",
            "a130_super_hellcat", "m18e2bp", "a128_concept_1b", "m-3-y-midas", "m46_cl", "a132_t77",
            "t21_bp", "a141_m-4-y", "a05_m4_105bd", "m36_bp", "a140_astron_rex_105", "m24_chaffee_bp",
            "m6e2v2_bp", "a146_tl_7_120", "a142_pawlack_tank", "t49_fearless", "a151_ts_60",
            "a136_t42", "a153_xm66f", "m4a3e8tur", "a150_mbt_b", "a175_atac", "a178_spht",
        };

        private static TanksDatabase Database()
        { return TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData)); }

        private static TanksDatabase Overlay()
        { return TanksDatabase.Parse(Resources.Load<TextAsset>("Data/blitz_usa_1120_cards").text); }

        private static JsonNode Json(string resource)
        {
            var asset = Resources.Load<TextAsset>("Data/" + resource);
            Assert.IsNotNull(asset, resource);
            JsonNode node;
            Assert.IsTrue(JsonNode.TryParse(asset.text, out node), resource);
            return node;
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null) { Object.DestroyImmediate(_root); }
        }

        [Test]
        public void Roster_AccountsForEveryPlayableUsaSourceRecord()
        {
            var db = Database();
            var overlay = Overlay();
            Assert.AreEqual(522, db.Count);
            Assert.AreEqual(42, overlay.Count);
            var usa = 0; var researchable = 0; var premium = 0; var collectible = 0;
            foreach (var tank in db.Tanks) { if (tank.Nation == Nation.Usa) { usa++; } }
            Assert.AreEqual(107, usa);
            foreach (var name in NewNames)
            {
                var card = db.Get(name); Assert.IsNotNull(card, name);
                Assert.IsNotNull(overlay.Get(name), name);
                Assert.AreEqual(Nation.Usa, card.Nation, name);
                Assert.IsFalse(card.Hidden, name);
                if (card.TreeTank) { researchable++; Assert.IsFalse(card.Premium, name); }
                if (card.Collector) { collectible++; Assert.IsTrue(card.Premium, name); }
                if (card.Acquisition == "premium") { premium++; Assert.IsTrue(card.Premium, name); }
            }
            Assert.AreEqual(6, researchable);
            Assert.AreEqual(8, premium);
            Assert.AreEqual(28, collectible);
        }

        [Test]
        public void EverySourceModuleAndTurretGunCompatibility_IsPresent()
        {
            var inventory = Json("blitz_usa_1120_inventory");
            var db = Database();
            var totals = new int[5];
            var configurations = 0;
            var groups = new[] { "turrets", "guns", "tracks", "engines", "radios" };
            foreach (var record in inventory["records"].Items)
            {
                var card = db.Get(record["name"].AsString()); Assert.IsNotNull(card);
                var lists = new System.Collections.Generic.IReadOnlyList<ModuleInfo>[] {
                    card.Turrets, card.Guns, card.Tracks, card.Engines, card.Radios };
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
                    Assert.IsNotNull(turret, card.Name);
                    var slot = turret.Slot(configuration["gun"].AsString());
                    Assert.IsNotNull(slot, card.Name);
                    Assert.AreEqual(configuration["turret_mesh"].AsString(), turret.Mesh);
                    Assert.AreEqual(configuration["gun_mesh"].AsString(), slot.Mesh);
                    configurations++;
                }
                Assert.IsNotNull(card.AmmoBay, card.Name);
                Assert.IsNotNull(card.FuelTank, card.Name);
                Assert.IsNotEmpty(card.Crew, card.Name);
            }
            CollectionAssert.AreEqual(new[] { 43, 49, 46, 49, 42 }, totals);
            Assert.AreEqual(51, configurations);
        }

        [Test]
        public void ResearchBranches_ReachAllSixTanksWithoutReplacingOldRoutes()
        {
            var db = Database();
            AssertEdge(db, "m6", "m-7-y", 66700);
            AssertEdge(db, "m-7-y", "m-3-y", 112000);
            AssertEdge(db, "m-3-y", "m-5-y", 172000);
            AssertEdge(db, "m-5-y", "m-6-y", 255500);
            AssertEdge(db, "a100_t49", "a99_t92e1", 170000);
            AssertEdge(db, "a99_t92e1", "a116_xm551", 250000);
            Assert.IsTrue(db.Get("m6").UnlockTanks.ContainsKey("t29"), "Original M6 path must survive");
            Assert.IsTrue(db.Get("a100_t49").UnlockTanks.ContainsKey("t54e1"), "Original T49 path must survive");
        }

        [TestCase("m6", "m-7-y", ModuleKind.Gun)]
        [TestCase("a100_t49", "a99_t92e1", ModuleKind.Engine)]
        public void AdditionalLink_RequiresTheAuthoredOldModule_AndChargesTankXp(
            string parentName, string targetName, ModuleKind kind)
        {
            var db = Database();
            var account = new AccountData { FreeXp = 0, Credits = 100000000 };
            account.AddTank(parentName, true);
            var service = new TankResearchService(account, db);
            var parent = db.Get(parentName);
            var link = parent.AdditionalResearchLinks[targetName];
            Assert.AreEqual(kind, link.RequiredKind);
            Assert.IsFalse(service.CanResearchTank(targetName));
            account.FreeXp = 100000000;
            ResearchAll(service, parent);
            Assert.IsTrue(service.CanResearchTank(targetName));
            account.FreeXp = 0;
            var payer = account.GetTank(parentName);
            var cost = db.ResearchCost(targetName);
            payer.Exp = cost + 7;
            var researched = service.ResearchTank(targetName);
            Assert.IsTrue(researched.Ok, researched.ErrorKey);
            Assert.AreEqual(cost, researched.Paid);
            Assert.AreEqual(0, researched.PaidFreeXp);
            Assert.AreEqual(7, payer.Exp);
        }

        [Test]
        public void SheridanMissile_IsTheOnlyNewUsaTankWithAtgm()
        {
            var db = Database();
            var sheridan = db.Get("a116_xm551");
            var missile = db.Get("a116_xm551_exp");
            Assert.IsNotNull(sheridan);
            Assert.IsNotNull(missile);
            foreach (var name in NewNames)
            {
                if (name == missile.Name) { continue; }
                foreach (var gun in db.Get(name).Guns)
                foreach (var shell in gun.Shells)
                { Assert.IsFalse(shell.Atgm, name + "/" + shell.Name); }
            }
            var atgm = 0;
            foreach (var shell in missile.Guns[0].Shells)
            {
                if (!shell.Atgm) { continue; }
                atgm++;
                Assert.Greater(shell.TurnSpeed, 0f);
                Assert.Greater(shell.MaxDistance, 0f);
                Assert.AreEqual(AtgmVisualProfile.Blitz1120Marker, shell.AtgmVisual);
                Assert.IsTrue(shell.UsesModernAtgmVisual);
            }
            Assert.AreEqual(1, atgm, "Only the missile shell should be guided");
        }

        [TestCase("m-6-y", 0, 0.47f)]
        [TestCase("m-5-y", 0, 0.58f)]
        [TestCase("m-5-y", 1, 0.58f)]
        [TestCase("a141_m-4-y", 0, 0.389f)]
        [TestCase("m-3-y-midas", 0, 0.438f)]
        [TestCase("a125_aep_1", 0, 0.47f)]
        [TestCase("a128_concept_1b", 0, 0.438f)]
        [TestCase("a153_xm66f", 0, 0.4f)]
        public void DestroyedTracks_AllowOnlyAuthoredEmergencyReverse(
            string name, int trackIndex, float backwardLimit)
        {
            _root = new GameObject("Usa1120Yoh");
            var host = new GameObject("tank", typeof(Rigidbody));
            host.transform.SetParent(_root.transform, false);
            var tank = host.AddComponent<BattleTank>();
            tank.Initialize(Database().Get(name), trackIndex: trackIndex);
            Assert.IsTrue(tank.HasEmergencyTrack);
            Assert.AreEqual(0f, tank.EmergencyTrackForwardSpeedLimit, 1e-4f);
            Assert.AreEqual(backwardLimit, tank.EmergencyTrackBackwardSpeedLimit, 1e-4f);
            tank.Drive(1f, 1f);
            tank.Drive(1f, 1f, true);
            tank.Suspension.Enqueue(1f, 1f);
            tank.DamageModule(TankModuleIds.Tracks, 10000);
            Assert.IsTrue(tank.TracksCrashed);
            Assert.AreEqual(0f, tank.ForwardAxis);
            Assert.AreEqual(0f, tank.RightAxis);
            Assert.AreEqual(0f, tank.ForwardAxisLow);
            Assert.AreEqual(0f, tank.RightAxisLow);
            Assert.AreEqual(0f, tank.Suspension.ForwardAxis);
            Assert.AreEqual(0f, tank.Suspension.RightAxis);
            Assert.AreEqual(0f, tank.Suspension.DriveForwardLimitMultiplier);
            Assert.AreEqual(backwardLimit, tank.Suspension.DriveBackwardLimitMultiplier, 1e-4f);

            tank.Drive(1f, 1f);
            Assert.AreEqual(0f, tank.ForwardAxis, 1e-4f);
            Assert.AreEqual(0f, tank.RightAxis, 1e-4f);
            Assert.AreEqual(0f, tank.Suspension.DriveForwardLimitMultiplier, 1e-4f);
            Assert.AreEqual(backwardLimit, tank.Suspension.DriveBackwardLimitMultiplier, 1e-4f);

            tank.Drive(-1f, 1f);
            Assert.AreEqual(-1f, tank.ForwardAxis, 1e-4f);
            Assert.AreEqual(0f, tank.RightAxis, 1e-4f);

            tank.Modules.Get(TankModuleIds.Tracks).Tick(Time.time + tank.GetRepairTime() + .1f);
            Assert.IsFalse(tank.TracksCrashed);
            Assert.AreEqual(TankModule.Status.Damaged, tank.ModuleStatus(TankModuleIds.Tracks));
            Assert.AreEqual(1f, tank.Suspension.DriveForwardLimitMultiplier, 1e-4f);
            Assert.AreEqual(1f, tank.Suspension.DriveBackwardLimitMultiplier, 1e-4f);
            tank.Drive(1f, 1f);
            Assert.AreEqual(1f, tank.ForwardAxis);
            Assert.AreNotEqual(0f, tank.RightAxis);

            tank.DamageModule(TankModuleIds.Tracks, 10000);
            tank.Drive(-1f, 0f);
            Assert.IsTrue(tank.RepairEverything());
            Assert.AreEqual(TankModule.Status.Normal, tank.ModuleStatus(TankModuleIds.Tracks));
            Assert.AreEqual(1f, tank.Suspension.DriveForwardLimitMultiplier, 1e-4f);
            Assert.AreEqual(1f, tank.Suspension.DriveBackwardLimitMultiplier, 1e-4f);
        }

        [TestCase("m-7-y", 0)]
        [TestCase("m-7-y", 1)]
        [TestCase("m-3-y", 0)]
        [TestCase("m-3-y", 1)]
        [TestCase("maus", 0)]
        public void TracksWithoutInternalTrack_RemainImmobilized(string name, int trackIndex)
        {
            var tank = CreateTank(name, trackIndex);
            Assert.IsFalse(tank.HasEmergencyTrack);
            tank.DamageModule(TankModuleIds.Tracks, 10000);
            tank.Drive(-1f, 1f);
            tank.Drive(-1f, 1f, true);
            Assert.IsTrue(tank.TracksCrashed);
            Assert.AreEqual(0f, tank.ForwardAxis);
            Assert.AreEqual(0f, tank.RightAxis);
            Assert.AreEqual(0f, tank.ForwardAxisLow);
            Assert.AreEqual(0f, tank.RightAxisLow);
            Assert.AreEqual(1f, tank.Suspension.DriveBackwardLimitMultiplier);
        }

        [TestCase("m-6-y")]
        [TestCase("m-5-y")]
        [TestCase("a141_m-4-y")]
        [TestCase("m-3-y-midas")]
        [TestCase("a125_aep_1")]
        [TestCase("a128_concept_1b")]
        [TestCase("a153_xm66f")]
        public void EmergencyReverse_UsesSourceSpeedCapsInThePropulsionEquation(string name)
        {
            var tank = CreateTank(name);
            var suspension = tank.Suspension;
            var forward = suspension.MaxSpeedForward;
            var back = suspension.MaxSpeedBack;
            tank.DamageModule(TankModuleIds.Tracks, 10000);
            tank.Drive(-1f, 0f);
            var cap = Mathf.Abs(back) * suspension.DriveBackwardLimitMultiplier / 3.6f;
            Assert.Greater(cap, 0f);
            Assert.Less(TankSuspension.DriveAcceleration(0f, -1f, 50000f, 1000f,
                forward * suspension.DriveForwardLimitMultiplier,
                back * suspension.DriveBackwardLimitMultiplier, 1f, false, .02f), 0f);
            Assert.GreaterOrEqual(TankSuspension.DriveAcceleration(-cap, -1f, 50000f, 1000f,
                forward * suspension.DriveForwardLimitMultiplier,
                back * suspension.DriveBackwardLimitMultiplier, 1f, false, .02f), 0f,
                "Emergency reverse must stop applying thrust at the authored limit");
            const float delta = .02f;
            var belowCap = -(cap - .001f);
            var acceleration = TankSuspension.DriveAcceleration(belowCap, -1f, 50000f, 1000f,
                forward * suspension.DriveForwardLimitMultiplier,
                back * suspension.DriveBackwardLimitMultiplier, 1f, false, delta);
            Assert.GreaterOrEqual(belowCap + acceleration * delta, -cap - 1e-4f,
                "One powered physics step cannot overshoot the emergency limit");
            Assert.AreEqual(0f, TankSuspension.DriveAcceleration(0f, 1f, 50000f, 1000f,
                forward * suspension.DriveForwardLimitMultiplier,
                back * suspension.DriveBackwardLimitMultiplier, 1f, false, .02f));
            Assert.AreEqual(forward, suspension.MaxSpeedForward, "The base card speed is not rewritten");
            Assert.AreEqual(back, suspension.MaxSpeedBack);
        }

        [TestCase("m-6-y")]
        [TestCase("m-5-y")]
        [TestCase("a141_m-4-y")]
        [TestCase("m-3-y-midas")]
        [TestCase("a125_aep_1")]
        [TestCase("a128_concept_1b")]
        [TestCase("a153_xm66f")]
        public void EmergencyReverse_AlsoConstrainsBotInputAndRespawn(string name)
        {
            var tank = CreateTank(name);
            var ai = tank.gameObject.AddComponent<AiTankController>();
            ai.Init(tank, default);
            var stationary = AiTankController.BotsStationary;
            AiTankController.BotsStationary = false;
            try
            {
                tank.DamageModule(TankModuleIds.Tracks, 10000);
                ai.Drive(1f, 1f, true);
                Assert.AreEqual(0f, tank.ForwardAxis);
                Assert.AreEqual(0f, tank.ForwardAxisLow);
                ai.Drive(-1f, 1f, true);
                Assert.AreEqual(-1f, tank.ForwardAxis);
                Assert.AreEqual(-1f, tank.ForwardAxisLow);
                Assert.AreEqual(0f, tank.RightAxis);
                Assert.AreEqual(0f, tank.RightAxisLow);
                Assert.AreEqual(tank.EmergencyTrackBackwardSpeedLimit,
                    tank.Suspension.DriveBackwardLimitMultiplier, 1e-4f);
                tank.ReceiveDamage(new DamageInfo { Dmg = 100000, DmgBase = 100000 });
                Assert.IsTrue(tank.Dead);
                ai.Drive(-1f, 1f, true);
                Assert.AreEqual(0f, tank.ForwardAxis);
                Assert.AreEqual(0f, tank.ForwardAxisLow);
                tank.RestoreLife(null);
                Assert.IsFalse(tank.Dead);
                Assert.IsFalse(tank.TracksCrashed);
                Assert.AreEqual(1f, tank.Suspension.DriveForwardLimitMultiplier);
                Assert.AreEqual(1f, tank.Suspension.DriveBackwardLimitMultiplier);
            }
            finally { AiTankController.BotsStationary = stationary; }
        }

        private BattleTank CreateTank(string name, int trackIndex = 0)
        {
            _root = new GameObject("Usa1120Runtime");
            var host = new GameObject(name, typeof(Rigidbody));
            host.transform.SetParent(_root.transform, false);
            var tank = host.AddComponent<BattleTank>();
            tank.Initialize(Database().Get(name), trackIndex: trackIndex);
            return tank;
        }

        [Test]
        public void EveryNewVehicle_HasModelAndAuthoredIcons()
        {
            var crash = Json("legacy1120_crash_manifest");
            foreach (var name in NewNames)
            {
                var card = Database().Get(name);
                Assert.IsNotNull(card, name);
                var assets = TankModelAssets.For(card);
                Assert.IsNotNull(Resources.Load<GameObject>(assets.ModelResource), name + " model");
                Assert.IsNotNull(Resources.Load<Texture2D>("Textures/TankIcons/" + assets.DataId), name + " big icon");
                Assert.IsNotNull(Resources.Load<Texture2D>("Textures/SmallTankIcons/" + assets.DataId), name + " small icon");
                Assert.IsNotEmpty(crash["vehicles"][name]["sourceScene"].AsString(), name + " crash");
            }
        }

        private static void AssertEdge(TanksDatabase db, string from, string to, int cost)
        {
            Assert.IsTrue(db.Get(from).UnlockTanks.ContainsKey(to), from);
            Assert.AreEqual(cost, db.Get(from).UnlockTanks[to]);
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
