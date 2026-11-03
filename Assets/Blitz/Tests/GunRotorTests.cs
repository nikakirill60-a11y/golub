using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The Annihilator (Oth25 HWT20 FalconFire) draws its gun as a skinned
    /// three-barrel Gatling cylinder below the `gun_01` stub. It is exported as
    /// `gun_01_rotor`, rides the elevating gun and turns one barrel per shell of
    /// the three-shell burst one trigger pull fires.
    /// </summary>
    public sealed class GunRotorTests
    {
        private const string Annihilator = "oth25_hwt20_falconfire";

        [Test]
        public void Annihilator_SpinsItsGatlingOneBarrelPerShell()
        {
            var database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            var card = database.Get(Annihilator);
            Assert.IsNotNull(card);
            var root = new GameObject("rotor-test");
            LogAssert.ignoreFailingMessages = true;
            try
            {
                var spawn = new GameObject("Spawn").transform;
                spawn.SetParent(root.transform, false);
                var tank = BattleTank.Spawn(root.transform, card.Name, card, spawn,
                    new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f)));
                Assert.IsNotNull(tank);
                var turret = tank.Turret;
                Assert.IsNotNull(turret.GunRotor, "rotor pivot");
                Assert.AreSame(turret.GunPivot, turret.GunRotor.parent, "the rotor rides the gun");

                var rotor = turret.GunRotor.GetComponentInChildren<MeshFilter>(true);
                Assert.IsNotNull(rotor, "rotor mesh");
                Assert.AreEqual("gun_01_rotor", rotor.gameObject.name.Replace("(Clone)", string.Empty));
                Assert.IsTrue(rotor.gameObject.activeInHierarchy, "the installed gun shows its rotor");
                Assert.Greater(rotor.sharedMesh.vertexCount, 100);

                // The barrels reach the muzzle the shells leave from.
                var bounds = rotor.GetComponent<MeshRenderer>().bounds;
                var muzzle = turret.Muzzle.position;
                Assert.Less(Vector2.Distance(new Vector2(bounds.center.x, bounds.center.y),
                    new Vector2(muzzle.x, muzzle.y)), 0.35f, "muzzle on the bore axis");
                Assert.GreaterOrEqual(bounds.max.z + 0.3f, muzzle.z);
                Assert.LessOrEqual(bounds.min.z - 0.3f, muzzle.z);

                // One trigger pull: a magazine of one three-shell burst.
                Assert.IsInstanceOf<BattleGunBurst>(tank.Gun);
                Assert.AreEqual(1, ((BattleGunBurst)tank.Gun).MagazineSize);
                Assert.AreEqual(3, tank.GunInfo.Burst.Bursts[0]);
                Assert.AreEqual(3, turret.RotorBarrels);

                turret.SpinRotor();
                Assert.AreEqual(120f, turret.RotorTarget, 1e-3f);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// The client clip (`Oth25_HWT20_FalconFire.anim`) is baked into the
        /// nodes table: one burst turns the cylinder through a full circle in a
        /// second while each barrel in turn recoils 0.36 m along the bore, the
        /// recoil levers swing and the belt feeds; reloading turns the drum.
        /// </summary>
        [Test]
        public void Annihilator_PlaysTheClientGatlingAndDrumClips()
        {
            var database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            var card = database.Get(Annihilator);
            var root = new GameObject("clip-test");
            LogAssert.ignoreFailingMessages = true;
            try
            {
                var spawn = new GameObject("Spawn").transform;
                spawn.SetParent(root.transform, false);
                var tank = BattleTank.Spawn(root.transform, card.Name, card, spawn,
                    new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f)));
                var turret = tank.Turret;
                Assert.AreEqual(2, turret.GunClips.Count, "burst and reload clips");

                var barrel = turret.GunClipPivot("gun_01_anim_gatling_gun_01");
                var drum = turret.GunClipPivot("gun_01_anim_reload_drum");
                var lever = turret.GunClipPivot("gun_01_anim_rec_sys_helper_01");
                Assert.IsNotNull(barrel, "barrel pivot");
                Assert.IsNotNull(drum, "drum pivot");
                Assert.IsNotNull(lever, "lever pivot");
                Assert.AreSame(turret.GunRotor, turret.GunClipPivot("gun_01_rotor"));
                foreach (var pivot in new[] { barrel, drum, lever })
                {
                    Assert.AreSame(turret.GunPivot, pivot.parent, "parts ride the gun");
                    Assert.IsTrue(pivot.GetComponentInChildren<MeshFilter>(true).gameObject.activeInHierarchy);
                }

                var barrelMesh = barrel.GetComponentInChildren<MeshRenderer>();
                var rest = barrelMesh.bounds.center;
                var bore = (turret.Muzzle.position - turret.GunPivot.position).normalized;
                var rotorRest = turret.GunRotor.localRotation;
                var drumRest = drum.localRotation;

                turret.PlayGunClip(GunClip.BurstTrigger);
                turret.StepGunClips(0.1f);
                Assert.AreEqual(0.1f, turret.GunClipTime(GunClip.BurstTrigger), 1e-4f);
                // 36 degrees into the turn, the first barrel fully recoiled.
                Assert.AreEqual(36f, Quaternion.Angle(rotorRest, turret.GunRotor.localRotation), 1.5f);
                Assert.AreEqual(36f, Quaternion.Angle(Quaternion.identity, barrel.localRotation), 1.5f);
                var moved = barrelMesh.bounds.center - rest;
                Assert.Less(Vector3.Dot(moved, bore), -0.3f, "the barrel recoils backwards");
                Assert.Greater(Quaternion.Angle(Quaternion.identity, lever.localRotation), 30f, "lever swings");
                Assert.AreEqual(drumRest, drum.localRotation, "the drum waits for the reload");

                // After the full turn every part is back at rest and the clip idles.
                turret.StepGunClips(5f);
                Assert.AreEqual(-1f, turret.GunClipTime(GunClip.BurstTrigger));
                Assert.Less(Quaternion.Angle(rotorRest, turret.GunRotor.localRotation), 0.5f);
                Assert.Less((barrelMesh.bounds.center - rest).magnitude, 0.01f);

                turret.PlayGunClip(GunClip.ReloadedTrigger);
                turret.StepGunClips(0.5f);
                Assert.Greater(Quaternion.Angle(drumRest, drum.localRotation), 20f, "reload turns the drum");
                Assert.AreEqual(-1f, turret.GunClipTime(GunClip.BurstTrigger));
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
                Object.DestroyImmediate(root);
            }
        }
    }
}
