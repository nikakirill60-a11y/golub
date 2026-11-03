using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    public sealed class TrackSidesTests
    {
        private GameObject _root;
        private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [TearDown] public void Cleanup() { if (_root != null) Object.DestroyImmediate(_root); }
        private BattleTank Tank(string id = "m-6-y")
        {
            _root = new GameObject("side-track-tests");
            var host = new GameObject("tank", typeof(Rigidbody)); host.transform.SetParent(_root.transform, false);
            var tank = host.AddComponent<BattleTank>();
            tank.Initialize(TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData)).Get(id)); return tank;
        }
        private static GameObject Visual(BattleTank tank, string field) => (GameObject)typeof(BattleTank).GetField(field, Private).GetValue(tank);
        private static Vector3 SideCentre(BattleTank tank, TrackSide side)
        {
            var filter = Visual(tank, side == TrackSide.Left ? "_trackObjectLeft" : "_trackObjectRight").GetComponent<MeshFilter>();
            return filter.transform.TransformPoint(filter.sharedMesh.bounds.center);
        }
        [Test] public void PairHasIndependentHpTimersAndOnePanelRow()
        {
            var set = new TankModuleSet(); var tracks = new TankTracksModule(100, () => 5, () => false) { RegenHealth = 40 };
            set.Register(tracks); Assert.AreEqual(1, set.Modules.Count);
            Assert.AreSame(tracks.Left, set.Get(TankModuleIds.TrackLeft)); Assert.AreSame(tracks.Right, set.Get(TankModuleIds.TrackRight));
            set.Damage(TankModuleIds.TrackLeft, 100, 0); set.Damage(TankModuleIds.TrackRight, 100, 1);
            set.Damage(TankModuleIds.TrackLeft, 50, 3); // left re-crit delays only left
            set.Tick(6.1f);
            Assert.AreEqual(0, tracks.Left.HealthComponent.Health); Assert.AreEqual(40, tracks.Right.HealthComponent.Health);
            Assert.IsTrue(tracks.Left.RepairPending); Assert.IsFalse(tracks.Right.RepairPending);
            Assert.AreEqual(TankModule.Status.Destroyed, set.StatusOf(TankModuleIds.Tracks));
            set.Tick(8.1f); Assert.AreEqual(40, tracks.Left.HealthComponent.Health);
            Assert.AreEqual(TankModule.Status.Damaged, tracks.GetStatus()); Assert.AreEqual(40, tracks.HealthComponent.Health);
            Assert.IsTrue(set.RepairEverything()); Assert.AreEqual(100, tracks.Left.HealthComponent.Health); Assert.AreEqual(100, tracks.Right.HealthComponent.Health);
            Assert.IsFalse(set.RepairEverything());
        }
        [Test] public void GenericFacadeKeepsBroadcastApiAndCoalescesNotifications()
        {
            var pair = new TankTracksModule(100, () => 5, () => false); var crit = 0; var repairs = 0;
            pair.OnCrit += _ => crit++; pair.OnRepaired += _ => repairs++;
            pair.ReceiveDamage(100, 0); Assert.AreEqual(1, crit); Assert.AreEqual(0, pair.Left.HealthComponent.Health); Assert.AreEqual(0, pair.Right.HealthComponent.Health);
            pair.Repair(); Assert.AreEqual(1, repairs);
            pair.ReceiveDamage(75, 1); Assert.AreEqual(25, pair.Left.HealthComponent.Health); Assert.AreEqual(25, pair.Right.HealthComponent.Health);
        }
        [Test] public void DeadOwnerCannotRepairEitherSideButRespawnCan()
        {
            var dead = false; var pair = new TankTracksModule(100, () => 5, () => dead);
            pair.Left.ReceiveDamage(100, 0); pair.Right.ReceiveDamage(100, 1); dead = true;
            Assert.IsFalse(pair.Repair()); Assert.IsFalse(pair.Left.Repair()); pair.Tick(10);
            Assert.AreEqual(0, pair.Left.HealthComponent.Health); Assert.AreEqual(0, pair.Right.HealthComponent.Health);
            dead = false; Assert.IsTrue(pair.Repair()); Assert.AreEqual(TankModule.Status.Normal, pair.GetStatus());
        }
        [TestCase("m-6-y")][TestCase("m-5-y")][TestCase("a141_m-4-y")][TestCase("m-3-y-midas")]
        [TestCase("a125_aep_1")][TestCase("a128_concept_1b")][TestCase("a153_xm66f")][TestCase("maus")][TestCase("m-7-y")]
        public void WorldSpaceTrackHitDamagesOnlyTheAuthoredVisualSide(string id)
        {
            var tank = Tank(id); tank.transform.SetPositionAndRotation(new Vector3(13, 2, -8), Quaternion.Euler(0, 137, 0));
            var plate = tank.Plates.Last(p => p.Module == TankModuleIds.Tracks);
            var left = tank.Modules.Get(TankModuleIds.TrackLeft); var right = tank.Modules.Get(TankModuleIds.TrackRight);
            Assert.AreEqual(TrackSide.Left, tank.TrackSideAt(SideCentre(tank, TrackSide.Left)));
            Assert.AreEqual(TrackSide.Right, tank.TrackSideAt(SideCentre(tank, TrackSide.Right)));
            tank.ModuleCollide(plate, 10000, SideCentre(tank, TrackSide.Left));
            Assert.IsTrue(tank.TrackCrashed(TrackSide.Left)); Assert.IsFalse(tank.TrackCrashed(TrackSide.Right));
            Assert.AreEqual(right.HealthComponent.MaxHealth, right.HealthComponent.Health);
            Assert.IsFalse(Visual(tank, "_trackObjectLeft").activeSelf); Assert.IsTrue(Visual(tank, "_trackObjectRight").activeSelf);
            Assert.IsTrue(Visual(tank, "_crashedTrackLeft").activeSelf); Assert.IsFalse(Visual(tank, "_crashedTrackRight").activeSelf);
            tank.ModuleCollide(plate, 10000, SideCentre(tank, TrackSide.Right)); Assert.IsTrue(tank.TrackCrashed(TrackSide.Right));
            left.Repair(); Assert.IsFalse(tank.TrackCrashed(TrackSide.Left)); Assert.IsTrue(tank.TrackCrashed(TrackSide.Right));
            Assert.IsTrue(Visual(tank, "_trackObjectLeft").activeSelf); Assert.IsFalse(Visual(tank, "_trackObjectRight").activeSelf);
        }
        [TestCase("m-6-y")][TestCase("m-5-y")][TestCase("a141_m-4-y")][TestCase("m-3-y-midas")]
        [TestCase("a125_aep_1")][TestCase("a128_concept_1b")][TestCase("a153_xm66f")]
        public void SingleEmergencyTrackKeepsOppositeBeltAndRepairLimits(string id)
        {
            var tank = Tank(id); tank.DamageModule(TankModuleIds.TrackLeft, 10000); tank.Drive(-1, 1);
            Assert.AreEqual(-1, tank.ForwardAxis); Assert.AreNotEqual(0, tank.RightAxis);
            Assert.AreEqual(0, tank.Suspension.LeftTrackForwardLimit); Assert.AreEqual(1, tank.Suspension.RightTrackForwardLimit);
            Assert.AreEqual(tank.TrackInfo.InternalTrack.LeftBackwardSpeedLimit, tank.Suspension.DriveBackwardLimitMultiplier, 1e-6);
            tank.DamageModule(TankModuleIds.TrackRight, 10000); tank.Modules.Get(TankModuleIds.TrackLeft).Repair();
            Assert.IsTrue(tank.TracksCrashed); Assert.AreEqual(1, tank.Suspension.LeftTrackForwardLimit); Assert.AreEqual(0, tank.Suspension.RightTrackForwardLimit);
            Assert.AreEqual(tank.TrackInfo.InternalTrack.RightBackwardSpeedLimit, tank.Suspension.DriveBackwardLimitMultiplier, 1e-6);
            tank.ReceiveDamage(new DamageInfo { Dmg = 100000, DmgBase = 100000 }); tank.RestoreLife(_root.transform);
            Assert.IsFalse(tank.TracksCrashed); Assert.AreEqual(1, tank.Suspension.DriveBackwardLimitMultiplier);
            Assert.AreEqual(tank.Modules.Get(TankModuleIds.TrackLeft).HealthComponent.MaxHealth, tank.Modules.Get(TankModuleIds.TrackLeft).HealthComponent.Health);
            Assert.AreEqual(tank.Modules.Get(TankModuleIds.TrackRight).HealthComponent.MaxHealth, tank.Modules.Get(TankModuleIds.TrackRight).HealthComponent.Health);
        }
        [Test] public void AsymmetricAndOneSidedJsonDoesNotInventEmergencyBelts()
        {
            var limits = InternalTrackInfo.FromJson(JsonNode.Parse("{\"forward_speed_limit\":0,\"backward_speed_limit\":0.47,\"limitations\":[{\"track\":\"left\",\"forward_speed_limit\":0.2,\"backward_speed_limit\":0.58},{\"track\":\"right\",\"forward_speed_limit\":0.4,\"backward_speed_limit\":0.47}]}"));
            Assert.AreEqual(.2f, limits.ForwardLimit(TrackSide.Left)); Assert.AreEqual(.4f, limits.ForwardLimit(TrackSide.Right));
            Assert.AreEqual(.58f, limits.BackwardLimit(TrackSide.Left)); Assert.AreEqual(.47f, limits.BackwardLimit(TrackSide.Right));
            limits = InternalTrackInfo.FromJson(JsonNode.Parse("{\"limitations\":[{\"track\":\"left\",\"backward_speed_limit\":0.47}]}"));
            Assert.IsTrue(limits.ActiveFor(TrackSide.Left)); Assert.IsFalse(limits.ActiveFor(TrackSide.Right)); Assert.AreEqual(0, limits.BackwardLimit(TrackSide.Right));
            var legacy = InternalTrackInfo.FromJson(JsonNode.Parse("{\"backward_speed_limit\":0.47}")); Assert.AreEqual(.47f, legacy.BackwardLimit(TrackSide.Left)); Assert.AreEqual(.47f, legacy.BackwardLimit(TrackSide.Right));
        }
        [TestCase(0f, 0f, .47f, 1f, 1f)][TestCase(0f, 1f, 1f, 0f, .47f)]
        [TestCase(-2f, 0f, .47f, 1f, 1f)][TestCase(-2f, 1f, 1f, 0f, .47f)]
        public void DifferentialYawRespectsEveryBeltLimit(float speed, float lf, float lb, float rf, float rb)
        {
            foreach (var demand in new[] { -100f, -1f, 0f, 1f, 100f })
            {
                var yaw = TankSuspension.LimitTrackYaw(demand, speed, 12, 6, 1.5f, lf, lb, rf, rb);
                var left = speed + yaw * 1.5f; var right = speed - yaw * 1.5f;
                Assert.That(left, Is.InRange(-6 * lb - 1e-5f, 12 * lf + 1e-5f)); Assert.That(right, Is.InRange(-6 * rb - 1e-5f, 12 * rf + 1e-5f));
            }
            Assert.AreEqual(0, TankSuspension.LimitTrackYaw(10, -100, 12, 6, 1.5f, lf, lb, rf, rb), "External overspeed is not rewritten by the motor");
        }
        [Test] public void NativeSideNamesFollowMeasuredPhysicalMountsRatherThanAssumingObjHandedness()
        {
            var negative = TankSuspension.LimitTrackYaw(1, 0, 12, 6, 1.5f, 0, .47f, 1, 1, true);
            var positive = TankSuspension.LimitTrackYaw(1, 0, 12, 6, 1.5f, 0, .47f, 1, 1, false);
            Assert.AreEqual(0, negative); Assert.Greater(positive, 0);
            Assert.AreEqual(positive, TankSuspension.LimitTrackYaw(1, 0, 12, 6, 1.5f, 1, 1, 0, .47f));
        }
        [Test] public void FakeWreckAndRepairDoNotCorruptSideHealth()
        {
            var tank = Tank(); tank.DamageModule(TankModuleIds.TrackLeft, 10000); tank.SetFakeWreck(true);
            Assert.IsTrue(Visual(tank, "_crashedTracks").activeSelf);
            Assert.IsFalse(Visual(tank, "_crashedTracks").GetComponent<MeshRenderer>().forceRenderingOff);
            Assert.IsFalse(Visual(tank, "_crashedTrackLeft").activeSelf); Assert.IsFalse(Visual(tank, "_crashedTrackRight").activeSelf);
            tank.Modules.Get(TankModuleIds.TrackLeft).Repair(); Assert.IsTrue(Visual(tank, "_crashedTracks").activeSelf);
            Assert.IsFalse(Visual(tank, "_crashedTracks").GetComponent<MeshRenderer>().forceRenderingOff);
            tank.SetFakeWreck(false); Assert.IsFalse(tank.TracksCrashed); Assert.IsTrue(Visual(tank, "_trackObjectLeft").activeSelf); Assert.IsTrue(Visual(tank, "_trackObjectRight").activeSelf);
        }
        [Test] public void EveryModernUsaCrashPartitionsAllSourceTrianglesWithoutChangingVertexStreams()
        {
            var cards = JsonNode.Parse(BlitzResources.LoadText("blitz_usa_1120_cards"))["tanks"];
            var inspected = 0;
            foreach (var card in cards.Items)
            {
                var tank = Tank(card["name"].AsString()); var crash = Visual(tank, "_crashedTracks");
                Assert.IsNotNull(crash, card["name"].AsString());
                var original = crash.GetComponent<MeshFilter>().sharedMesh;
                var left = Visual(tank, "_crashedTrackLeft").GetComponent<MeshFilter>().sharedMesh;
                var right = Visual(tank, "_crashedTrackRight").GetComponent<MeshFilter>().sharedMesh;
                Assert.Greater(left.triangles.Length, 0); Assert.Greater(right.triangles.Length, 0);
                CollectionAssert.AreEqual(original.vertices, left.vertices); CollectionAssert.AreEqual(original.uv, right.uv);
                for (var submesh = 0; submesh < original.subMeshCount; submesh++)
                {
                    var source = original.GetTriangles(submesh); var halves = left.GetTriangles(submesh).Concat(right.GetTriangles(submesh)).ToArray();
                    Array.Sort(source); Array.Sort(halves); CollectionAssert.AreEqual(source, halves, card["name"].AsString());
                }
                Assert.IsTrue(crash.GetComponent<MeshRenderer>().forceRenderingOff);
                tank.SetSpotted(true); Assert.IsTrue(crash.GetComponent<MeshRenderer>().forceRenderingOff);
                Object.DestroyImmediate(_root); _root = null; inspected++;
            }
            Assert.AreEqual(42, inspected);
        }
    }
}
