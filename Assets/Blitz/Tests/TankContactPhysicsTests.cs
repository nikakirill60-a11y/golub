using System.Collections.Generic;
using System.Linq;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
namespace BlitzLegacy.Tests
{
    public sealed class TankContactPhysicsTests
    {
        private GameObject _root;
        private TanksDatabase _db;
        [SetUp] public void Prepare(){_root=new GameObject("contact regression");_db=TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));}
        [TearDown] public void Cleanup(){Object.DestroyImmediate(_root);}
        private BattleTank Tank(string id="ms-1",float y=0)
        {
            var go=new GameObject(id,typeof(Rigidbody));go.transform.SetParent(_root.transform);go.transform.position=new Vector3(0,y,0);
            var tank=go.AddComponent<BattleTank>();tank.Initialize(_db.Get(id));Physics.SyncTransforms();return tank;
        }
        [TestCase(50)][TestCase(60)][TestCase(100)]
        public void VerticalContactCostsExactlyFiveHpPerSecondOnBothSides(int hz)
        {
            var bottom=Tank();var top=Tank(y:3);var a=bottom.GetComponent<TankContactPhysics>();var b=top.GetComponent<TankContactPhysics>();
            var hp=bottom.Health.Health;var hp2=top.Health.Health;
            for(var i=0;i<hz;i++){var at=i/(float)hz;a.ObserveTankContact(top,Vector3.down,at);b.ObserveTankContact(bottom,Vector3.up,at);a.TickCrush(1f/hz,at);b.TickCrush(1f/hz,at);}
            Assert.AreEqual(hp-5,bottom.Health.Health);Assert.AreEqual(hp2-5,top.Health.Health);
        }
        [Test] public void DuplicateCollidersDoNotMultiplyCrushRateAndWreckStillHurts()
        {
            var bottom=Tank();var top=Tank(y:3);top.ReceiveDamage(new DamageInfo{Dmg=100000});
            var physics=bottom.GetComponent<TankContactPhysics>();var hp=bottom.Health.Health;
            for(var i=0;i<60;i++){for(var c=0;c<8;c++)physics.ObserveTankContact(top,Vector3.down,i/60f);physics.TickCrush(1f/60,i/60f);}
            Assert.AreEqual(hp-5,bottom.Health.Health);Assert.IsTrue(top.Dead);
            Assert.AreEqual(DamageType.Environment,bottom.Health.DamageInfosTaken.Last().DmgType);
            Assert.AreSame(top,bottom.Health.DamageInfosTaken.Last().AttackerTank);
        }
        [Test] public void SideContactSeparationAndRespawnDoNotLeaveCrushDamage()
        {
            var a=Tank();var b=Tank(y:3);var p=a.GetComponent<TankContactPhysics>();var hp=a.Health.Health;
            p.ObserveTankContact(b,Vector3.right,0);p.TickCrush(1,0);Assert.AreEqual(hp,a.Health.Health);
            p.ObserveTankContact(b,Vector3.down,0);p.TickCrush(1,1);Assert.AreEqual(hp,a.Health.Health);
            p.ObserveTankContact(b,Vector3.down,2);p.TickCrush(.1f,2);p.ResetState();p.TickCrush(1,2);Assert.AreEqual(hp,a.Health.Health);
        }
        [Test] public void SmallDropDoesNotHurtButHighDropDisablesTracks()
        {
            var t=Tank();var p=t.GetComponent<TankContactPhysics>();var hp=t.Health.Health;
            p.ApplyLanding(4,1);Assert.AreEqual(hp,t.Health.Health);Assert.AreEqual(0,p.LandingCount);
            p.ApplyLanding(18,16);Assert.Less(t.Health.Health,hp);Assert.Greater(t.Health.Health,0);Assert.IsTrue(t.TracksCrashed);Assert.AreEqual(1,p.LandingCount);
        }
        [Test] public void LandingUsesEnergyMassAndChassisStrength()
        {
            var light=TankContactPhysics.LandingDamage(12,7,5000,200,1000,1,1);
            var heavy=TankContactPhysics.LandingDamage(12,7,50000,200,1000,1,1);
            var gentle=TankContactPhysics.LandingDamage(4,7,50000,200,1000,1,1);
            Assert.Greater(heavy.HullDamage,light.HullDamage);Assert.Greater(heavy.TrackDamage,light.TrackDamage);Assert.AreEqual(0,gentle.HullDamage);
            Assert.AreEqual(0,TankContactPhysics.LandingDamage(float.NaN,7,5000,200,1000,1,1).HullDamage);
        }
        [TestCase("g14_kpfpz_70r")][TestCase("maus")][TestCase("ms-1")]
        public void HillsPreserveHullAndMobilityAndTracksRemainRepairable(string id)
        {
            var t = Tank(id); var p = t.GetComponent<TankContactPhysics>();
            var hp = t.Health.Health; var tracks = t.Modules.Get(TankModuleIds.Tracks).HealthComponent;
            p.ApplyLanding(40, 3); // exaggerated solver speed cannot override measured height
            Assert.AreEqual(hp, t.Health.Health); Assert.AreEqual(tracks.MaxHealth, tracks.Health);
            Assert.AreEqual(0, p.LandingCount);
            p.ApplyLanding(10, 5);
            Assert.AreEqual(hp, t.Health.Health); Assert.Greater(tracks.Health, 0);
            Assert.Less(tracks.Health, tracks.MaxHealth); Assert.IsFalse(t.TracksCrashed);
            Assert.AreEqual(1, p.LandingCount); Assert.AreEqual(0, p.LastFallDamage);
            Assert.IsTrue(t.RepairEverything()); Assert.AreEqual(tracks.MaxHealth, tracks.Health);
            p.ApplyLanding(25, 16);
            Assert.Less(t.Health.Health, hp); Assert.Greater(t.Health.Health, 0); Assert.IsTrue(t.TracksCrashed);
            Assert.IsTrue(t.RepairEverything()); Assert.IsFalse(t.TracksCrashed);
            t.RestoreLife(null); Assert.AreEqual(0, p.LandingCount); Assert.AreEqual(0, p.LastFallDamage);
        }

        [Test]
        public void EntireRosterAndEveryInstalledModuleCombinationTolerateHills()
        {
            Assert.GreaterOrEqual(_db.Count, 398);
            foreach (var card in _db.Tanks)
            for (var turret = 0; turret < Mathf.Max(1, card.Turrets.Count); turret++)
            for (var gun = 0; gun < card.Guns.Count; gun++)
            for (var engine = 0; engine < card.Engines.Count; engine++)
            for (var track = 0; track < card.Tracks.Count; track++)
            {
                var mass = BattleTank.MassOf(card, gun, engine, track, turret);
                var chassis = Mathf.RoundToInt(card.Tracks[track].Health);
                var hp = card.TotalHealthWith(card.Turrets.Count > 0 ? card.Turrets[turret] : null);
                Assert.Greater(mass, 0, card.Name); Assert.Greater(chassis, 0, card.Name);
                foreach (var normal in new[] { .45f, .7f, 1f })
                {
                    var small = TankContactPhysics.LandingDamage(40, 3, mass, chassis, hp, 1, normal);
                    var hill = TankContactPhysics.LandingDamage(40, 5, mass, chassis, hp, 1, normal);
                    Assert.AreEqual(0, small.HullDamage, card.Name); Assert.AreEqual(0, small.TrackDamage, card.Name);
                    Assert.AreEqual(0, hill.HullDamage, card.Name); Assert.Less(hill.TrackDamage, chassis / 2f, card.Name);
                    var medium = TankContactPhysics.LandingDamage(40, 10, mass, chassis, hp, 1, normal);
                    Assert.LessOrEqual(medium.HullDamage, Mathf.CeilToInt(hp * .09f), card.Name);
                }
            }
        }

        [Test]
        public void LandingCurveIsMonotoneBoundedAndSlopesAreGentler()
        {
            foreach (var mass in new[] { 5000f, 50000f, 200000f })
            {
                var previous = default(TankContactPhysics.FallResult);
                for (var height = 0f; height <= 40; height += .25f)
                {
                    var fall = TankContactPhysics.LandingDamage(40, height, mass, 200, 1000, 1, 1);
                    var slope = TankContactPhysics.LandingDamage(40, height, mass, 200, 1000, 1, .7f);
                    Assert.GreaterOrEqual(fall.HullDamage, previous.HullDamage);
                    Assert.GreaterOrEqual(fall.TrackDamage, previous.TrackDamage);
                    Assert.That(fall.HullDamage, Is.InRange(0, 1000)); Assert.That(fall.TrackDamage, Is.InRange(0, 200));
                    Assert.LessOrEqual(slope.HullDamage, fall.HullDamage); Assert.LessOrEqual(slope.TrackDamage, fall.TrackDamage);
                    previous = fall;
                }
                Assert.AreEqual(1000, previous.HullDamage); Assert.AreEqual(200, previous.TrackDamage);
                var slow = TankContactPhysics.LandingDamage(4, 40, mass, 200, 1000, 1, 1);
                Assert.AreEqual(0, slow.HullDamage); Assert.AreEqual(0, slow.TrackDamage);
            }
        }

        [Test]
        public void FallThresholdsAndInvalidInputsCannotCreateDamage()
        {
            var trackEdge = TankContactPhysics.LandingDamage(40, 3, 50000, 200, 1000, 0, 1);
            var hullEdge = TankContactPhysics.LandingDamage(40, 6, 50000, 200, 1000, 0, 1);
            Assert.AreEqual(0, trackEdge.TrackDamage); Assert.AreEqual(0, trackEdge.HullDamage);
            Assert.Greater(hullEdge.TrackDamage, 0); Assert.AreEqual(0, hullEdge.HullDamage);
            foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            for (var i = 0; i < 5; i++)
            {
                var values = new[] { 20f, 20f, 50000f, 1f, 1f }; values[i] = invalid;
                var fall = TankContactPhysics.LandingDamage(values[0], values[1], values[2], 200, 1000, values[3], values[4]);
                Assert.AreEqual(0, fall.HullDamage); Assert.AreEqual(0, fall.TrackDamage);
            }
            var side = TankContactPhysics.LandingDamage(40, 40, 50000, 200, 1000, 1, .4f);
            Assert.AreEqual(0, side.HullDamage); Assert.AreEqual(0, side.TrackDamage);
            var extreme = TankContactPhysics.LandingDamage(float.MaxValue, float.MaxValue, 50000, 200, 1000, 1, 1);
            Assert.AreEqual(1000, extreme.HullDamage); Assert.AreEqual(200, extreme.TrackDamage);
        }

        [Test] public void ContactBandOnlyTouchesTankLayerAndCentreOfMassIsInsideHull()
        {
            var t=Tank("maus");var p=t.GetComponent<TankContactPhysics>();var rb=t.GetComponent<Rigidbody>();
            Assert.AreEqual(0,p.ContactBand.excludeLayers.value&(1<<BattleLayers.Physics));Assert.AreNotEqual(0,p.ContactBand.excludeLayers.value&(1<<BattleLayers.Static));
            var bounds=new Bounds(p.ContactBand.center,p.ContactBand.size);Assert.IsTrue(bounds.Contains(rb.centerOfMass));Assert.Less(rb.centerOfMass.y,bounds.center.y);
            Assert.AreEqual(12,rb.solverIterations);Assert.AreEqual(5,rb.maxAngularVelocity);
        }
        [TestCase(50)][TestCase(60)][TestCase(100)]
        public void WheelBudgetSupportsEntireRosterEveryFixedStep(int hz)
        {
            var request = typeof(TankSuspension).GetMethod("RequestWheel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            TankSuspension.ResetWheelQueue();
            for (var step = 0; step < hz; step++)
            {
                var at = step / (float)hz;
                for (var tank = 0; tank < 14; tank++)
                    Assert.IsTrue((bool)request.Invoke(null, new object[] { at }), "Wheel load skipped for tank " + tank + " at step " + step);
            }
            TankSuspension.ResetWheelQueue();
        }
        public static IEnumerable<string> NewVehicles()
        {
            var m=JsonNode.Parse(Resources.Load<TextAsset>("Data/legacy1120_crash_manifest").text);
            return m["vehicles"].Fields.Select(f=>f.Key).ToArray();
        }
        [TestCaseSource(nameof(NewVehicles))]
        public void EveryNewVehicleUses1120CrashTextureAndOriginalBrokenTracks(string id)
        {
            var t=Tank(id);var renderer=t.Model.GetComponentsInChildren<MeshRenderer>().First(r=>r.sharedMaterial!=null&&r.sharedMaterial.HasProperty("_BaseMap"));
            var live=renderer.sharedMaterials;var mesh=renderer.GetComponent<MeshFilter>().sharedMesh;
            Assert.IsTrue(t.Model.GetComponentsInChildren<Transform>(true).Any(n=>n.name=="tracks_crash"));
            t.ReceiveDamage(new DamageInfo{Dmg=100000});Assert.AreSame(Resources.Load<Texture2D>("Legacy1120Crash/albedo"),renderer.sharedMaterial.GetTexture("_BaseMap"));
            Assert.AreSame(mesh,renderer.GetComponent<MeshFilter>().sharedMesh);
            t.RestoreLife(null);Assert.AreSame(live[0],renderer.sharedMaterial);Assert.AreEqual(0,t.GetComponent<TankContactPhysics>().StackContactCount);
        }
    }
}
