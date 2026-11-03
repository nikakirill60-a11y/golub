using System.Linq;
using System.Reflection;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Battle.Ai;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class Legacy480ModulesTests
    {
        private GameObject _root;
        private TanksDatabase _database;
        [SetUp] public void Setup()
        {
            _root = new GameObject("480 module regression");
            _database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
        }
        [TearDown] public void Cleanup() { Object.DestroyImmediate(_root); }
        private BattleTank Tank(string id, Vector3 position = default)
        {
            var host = new GameObject(id, typeof(Rigidbody));
            host.transform.SetParent(_root.transform);host.transform.position = position;
            var tank = host.AddComponent<BattleTank>();tank.Initialize(_database.Get(id));
            return tank;
        }
        [Test] public void MausCarriesOriginalRackAndChassisHealth()
        {
            var tank=Tank("maus");Assert.AreEqual(280,tank.Card.AmmoBay.Health);
            Assert.AreEqual(190,tank.Card.AmmoBay.MaxRegenHealth);
            Assert.AreEqual(300,tank.TrackInfo.Health);Assert.AreEqual(3,tank.TrackInfo.BulkHealthFactor);
            Assert.AreEqual(.27f,tank.AmmoZones.ProjectileChance,.00001f);
            Assert.Greater(tank.AmmoZones.Zones.Count,1);
            foreach(var shell in tank.GunInfo.Shells)Assert.Greater(shell.DeviceDamage,0);
        }
        [Test] public void DamagedEventFiresOnlyOnCrossingHalfHealth()
        {
            var module=new TankModule(TankModuleIds.AmmoBay,280,()=>8,()=>false);
            var events=0;module.OnDamaged+=id=>events++;
            module.ReceiveDamage(100,0);Assert.AreEqual(0,events);
            module.ReceiveDamage(50,0);Assert.AreEqual(1,events);
            module.ReceiveDamage(10,0);Assert.AreEqual(1,events);
            module.Repair();module.ReceiveDamage(150,0);Assert.AreEqual(2,events);
        }
        [TestCase("ms-1")][TestCase("maus")][TestCase("su-152")]
        public void RackIsSmallRotatingGeometryNotTheWholeHull(string id)
        {
            var tank=Tank(id);Assert.Greater(tank.AmmoZones.Zones.Count,0);
            var zone=tank.AmmoZones.Zones[0];var centre=zone.Mount.TransformPoint(zone.Bounds.center);
            Assert.IsTrue(tank.AmmoZones.Intersects(centre,zone.Mount.right,.1f));
            Assert.IsFalse(tank.AmmoZones.Intersects(centre+Vector3.up*100,Vector3.forward,10));
            var shell=new ShellInfo{Caliber=20,DeviceDamage=15};
            var bay=tank.Modules.Get(TankModuleIds.AmmoBay);var before=bay.HealthComponent.Health;
            Assert.IsFalse(tank.ApplyPenetratingModuleHit(shell,centre,zone.Mount.right,null,.27f));
            Assert.AreEqual(before,bay.HealthComponent.Health);
            Assert.IsTrue(tank.ApplyPenetratingModuleHit(shell,centre,zone.Mount.right,null,.269f));
            Assert.AreEqual(before-15,bay.HealthComponent.Health);
            Assert.IsFalse(tank.Dead,"one low-calibre device hit cannot explode this rack");
        }
        [Test] public void MausCannotBeRackedByTwoTwentyMillimetreModuleHits()
        {
            var tank=Tank("maus");var z=tank.AmmoZones.Zones[0];var p=z.Mount.TransformPoint(z.Bounds.center);
            var shell=new ShellInfo{Caliber=20,DeviceDamage=15};
            for(var i=0;i<2;i++)Assert.IsTrue(tank.ApplyPenetratingModuleHit(shell,p,z.Mount.right,null,0));
            Assert.AreEqual(250,tank.Modules.Get(TankModuleIds.AmmoBay).HealthComponent.Health);Assert.IsFalse(tank.Dead);
            Assert.IsFalse(tank.ApplyPenetratingModuleHit(shell,p+Vector3.up*20,Vector3.forward,null,0));
        }
        [Test] public void SavingThrowMatchesXmlAndCritKeepsAttacker()
        {
            var tank=Tank("maus");var z=tank.AmmoZones.Zones[0];var p=z.Mount.TransformPoint(z.Bounds.center);
            var bay=tank.Modules.Get(TankModuleIds.AmmoBay);var shell=new ShellInfo{Caliber=128,DeviceDamage=1};var landed=0;
            for(var i=0;i<1000;i++){bay.Repair();if(tank.ApplyPenetratingModuleHit(shell,p,z.Mount.right,null,i/1000f))landed++;}
            Assert.AreEqual(270,landed);
            var attacker=Tank("ms-1",Vector3.right*20);DamageInfo fatal=null;tank.Health.OnDamageInfoTaken+=d=>fatal=d;
            shell.DeviceDamage=1000;Assert.IsTrue(tank.ApplyPenetratingModuleHit(shell,p,z.Mount.right,new DamageInfo{AttackerTank=attacker,ShotId=123},0));
            Assert.IsTrue(tank.Dead);Assert.IsTrue(tank.AmmoBayExploded);Assert.AreSame(attacker,fatal.AttackerTank);Assert.AreEqual(123,fatal.ShotId);
        }
        [Test] public void TracksUseDeviceDamageAndOriginalBulkFactorThenRepair()
        {
            var tank=Tank("maus");var plate=tank.Plates.Last(p=>p.Module==TankModuleIds.Tracks);
            var module=tank.Modules.Get(TankModuleIds.Tracks);var before=module.HealthComponent.Health;
            tank.ModuleCollide(plate,90,plate.transform.TransformPoint(Vector3.zero));Assert.AreEqual(before-30,module.HealthComponent.Health);
            tank.ModuleCollide(plate,90,plate.transform.TransformPoint(new Vector3(0,0,tank.TracksLength*.48f)));Assert.AreEqual(before-120,module.HealthComponent.Health);
            tank.ModuleCollide(plate,1000,plate.transform.TransformPoint(new Vector3(0,0,tank.TracksLength*.48f)));Assert.IsTrue(tank.TracksCrashed);
            module.Tick(Time.time+tank.GetRepairTime()+.1f);Assert.IsFalse(tank.TracksCrashed);
            // The existing track-visual policy repairs below half HP: mobile,
            // still damaged, and never shown as a destroyed track.
            var repaired = Mathf.Min(tank.TrackInfo.MaxRegenHealth,
                Mathf.Max(1, Mathf.FloorToInt(before * .5f) - 1));
            Assert.AreEqual(repaired,module.HealthComponent.Health);
            Assert.AreEqual(TankModule.Status.Damaged,module.GetStatus());
            module.Repair();Assert.AreEqual(before,module.HealthComponent.Health);
            Assert.AreEqual(TankModule.Status.Normal,module.GetStatus());
        }
        [Test] public void RealDeathFreezesWheelsAppliesCrashAndRespawnRestoresMaterials()
        {
            var tank=Tank("ms-1");var renderer=tank.Model.GetComponentsInChildren<MeshRenderer>().First(r=>r.sharedMaterials.Any(m=>m!=null&&m.HasProperty("_BaseMap")));
            var live=renderer.sharedMaterials;
            tank.ReceiveDamage(new DamageInfo{Dmg=100000,DmgBase=100000});Assert.IsTrue(tank.Dead);
            var wreck=tank.GetComponentInChildren<Original480Wreck>(true);Assert.IsTrue(wreck.Playing);Assert.AreEqual(2,wreck.Effects.Count);
            Assert.IsTrue(renderer.sharedMaterials.Any(m=>m!=null&&m.HasProperty("_BaseMap")&&m.GetTexture("_BaseMap")==Resources.Load<Texture2D>("Legacy480/Textures/crash")));
            wreck.Play();Assert.AreEqual(2,wreck.Effects.Count);
            var transforms=tank.Model.GetComponentsInChildren<Transform>(true);var rotations=transforms.Select(t=>t.localRotation).ToArray();
            typeof(BattleTank).GetProperty("TracksSpeed").SetValue(tank,10f);
            typeof(BattleTank).GetMethod("RotateTracks",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(tank,null);
            CollectionAssert.AreEqual(rotations,transforms.Select(t=>t.localRotation).ToArray());Assert.AreEqual(0,tank.TracksSpeed);
            tank.RestoreLife(null);Assert.IsFalse(tank.Dead);Assert.IsFalse(wreck.Playing);CollectionAssert.AreEqual(live,renderer.sharedMaterials);
        }
        [TestCase("explosion_tank_1")][TestCase("explosion_tank_m")][TestCase("explosion_rush_1")][TestCase("explosion_rush_m")]
        [TestCase("explosion_AMX_Dracula")][TestCase("Fire_and_Smoke")][TestCase("Fire_and_Smoke_small")][TestCase("SmokeFire_tank_movement_new")]
        public void AllOriginalDestructionScenesEmitAtAuthoredMetreScale(string id)
        {
            var fx=DavaFx.Create480(_root.transform,id);for(var i=0;i<18;i++)fx.Simulate(1f/60);
            Assert.Greater(fx.LiveParticles,0,id);Assert.AreEqual(Vector3.one,fx.transform.localScale);
        }
        [Test] public void WorldOfDucksWreckLaysItsScenesOnTheWater()
        {
            // Without an HP_Fire point the scenes hung at 55 % of the hull and
            // rose out of the WarDuck's head.
            var tank=Tank("ms-1");var wreck=tank.Wreck.Original;
            wreck.UseWorldOfDucksFx=true;wreck.ExplosionSource="explosion_tank_1";wreck.FlamingSource="Fire_and_Smoke";
            wreck.WaterSurface=tank.transform.position.y+.3f;
            tank.ReceiveDamage(new DamageInfo{Dmg=100000,DmgBase=100000});Assert.IsTrue(wreck.Playing);Assert.AreEqual(2,wreck.Effects.Count);
            foreach(var fx in wreck.Effects)
            {
                Assert.AreEqual(wreck.WaterSurface.Value,fx.transform.position.y,1e-4f,fx.name);
                foreach(var height in fx.EmitterHeights)Assert.AreEqual(0f,height,1e-5f,fx.name);
            }
        }
        [Test] public void OriginalSingleFlashEmitsImmediatelyAndDisabledLodsStayDisabled()
        {
            var camera=new GameObject("camera").AddComponent<Camera>();camera.transform.SetParent(_root.transform);
            camera.transform.position=new Vector3(0,3,-10);camera.transform.LookAt(Vector3.up);
            var fx=DavaFx.Create480(_root.transform,"explosion_tank_1");fx.Simulate(1f/60);fx.Render(camera);
            var flash=fx.GetComponentsInChildren<MeshFilter>().Single(m=>m.gameObject.name=="exp");Assert.AreEqual(4,flash.sharedMesh.vertexCount);
            var fire=DavaFx.Create480(_root.transform,"Fire_and_Smoke");Assert.IsNull(fire.transform.Find("fire_lod"));Assert.IsNull(fire.transform.Find("fog_lod"));
            var tank=Tank("maus");tank.DamageModule(TankModuleIds.AmmoBay,10000);
            var wreck=tank.GetComponentInChildren<Original480Wreck>();Assert.AreEqual(1.7101213f,wreck.Effects[1].transform.localPosition.y,.00001f);
        }
        [Test] public void PlannerRejectsUnstockedShellsAndOccludedTarget()
        {
            var shooter=Tank("ms-1",new Vector3(0,0,-25));var target=Tank("maus");
            shooter.SetShellAmounts(new[]{0,0,0});Physics.SyncTransforms();Assert.IsFalse(ArmorShotPlanner.Choose(shooter,target).Valid);
            shooter.SetShellAmounts(new[]{20,20,20});
            var wall=GameObject.CreatePrimitive(PrimitiveType.Cube);wall.transform.SetParent(_root.transform);wall.layer=BattleLayers.Static;
            wall.transform.position=new Vector3(0,3,-10);wall.transform.localScale=new Vector3(30,15,2);Physics.SyncTransforms();
            Assert.IsFalse(ArmorShotPlanner.Choose(shooter,target).Valid);
        }
        [Test] public void PlannerUsesStockedHeForTracksAndOtherRoundWhenHeIsEmpty()
        {
            var shooter=Tank("ms-1",new Vector3(0,0,-25));var target=Tank("maus");
            foreach(var shell in shooter.GunInfo.Shells){shell.Penetration=1;shell.Caliber=20;shell.DeviceDamage=30;}
            shooter.GunInfo.Shells[0].Type=ShellType.Ap;shooter.GunInfo.Shells[1].Type=ShellType.Apcr;shooter.GunInfo.Shells[2].Type=ShellType.He;
            shooter.SetShellAmounts(new[]{20,20,20});Physics.SyncTransforms();var plan=ArmorShotPlanner.Choose(shooter,target);
            Assert.IsTrue(plan.Valid);Assert.IsTrue(plan.Tracks);Assert.AreEqual(2,plan.Shell);
            shooter.SetShellAmounts(new[]{20,0,0});plan=ArmorShotPlanner.Choose(shooter,target);Assert.IsTrue(plan.Tracks);Assert.AreEqual(0,plan.Shell);
        }
        [Test] public void PlannerChecksPremiumPenetrationBeforeChoosingTracks()
        {
            var shooter=Tank("ms-1",new Vector3(0,0,-25));var target=Tank("maus");
            foreach(var shell in shooter.GunInfo.Shells){shell.Penetration=1;shell.Caliber=20;}
            shooter.GunInfo.Shells[1].Penetration=1000;shooter.GunInfo.Shells[1].Premium=true;
            shooter.SetShellAmounts(new[]{20,20,20});Physics.SyncTransforms();var plan=ArmorShotPlanner.Choose(shooter,target);
            Assert.IsTrue(plan.Valid);Assert.IsFalse(plan.Tracks);Assert.AreEqual(1,plan.Shell);
        }
        [Test] public void HopelessTargetIsStillShotAtPointBlankOrWithHeButNotFromAfar()
        {
            var shooter=Tank("ms-1",new Vector3(0,0,-25));var target=Tank("maus");
            foreach(var shell in shooter.GunInfo.Shells){shell.Penetration=1;shell.Caliber=20;shell.DeviceDamage=0;shell.Type=ShellType.Ap;}
            shooter.SetShellAmounts(new[]{20,20,20});Physics.SyncTransforms();var plan=ArmorShotPlanner.Choose(shooter,target);
            Assert.IsTrue(plan.Valid,"point blank, a crew fires its least bad round instead of staring");Assert.IsTrue(plan.Hopeless);Assert.IsFalse(plan.Tracks);
            shooter.transform.position=new Vector3(0,0,-(ArmorShotPlanner.HopelessReach*2));Physics.SyncTransforms();
            Assert.IsFalse(ArmorShotPlanner.Choose(shooter,target).Valid,"from afar a shell that cannot hurt is not wasted");
            shooter.GunInfo.Shells[2].Type=ShellType.He;plan=ArmorShotPlanner.Choose(shooter,target);
            Assert.IsTrue(plan.Valid,"HE still splashes at any range");Assert.IsTrue(plan.Hopeless);Assert.AreEqual(2,plan.Shell);
            shooter.GunInfo.Shells[1].Penetration=1000;plan=ArmorShotPlanner.Choose(shooter,target);
            Assert.IsTrue(plan.Valid);Assert.IsFalse(plan.Hopeless);Assert.AreEqual(1,plan.Shell);
        }
    }
}
