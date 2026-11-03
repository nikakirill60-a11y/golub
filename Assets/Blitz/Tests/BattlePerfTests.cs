using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Battle.Ai;
using BlitzLegacy.Runtime.Battle.CombatFx;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The battle frame-budget rules: the mobile AI gun trace cadence ported
    /// from base_tank.gd, the armour plan reuse window, the allocation-free
    /// effect sort and the idle combat effects that skip their frame work.
    /// </summary>
    public sealed class BattlePerfTests
    {
        private bool _forced;

        [SetUp]
        public void SetUp()
        {
            _forced = GodotParticleEffect.ForceEnabled;
            GodotParticleEffect.ForceEnabled = true;
        }

        [TearDown]
        public void TearDown()
        {
            GodotParticleEffect.ForceEnabled = _forced;
        }

        [Test]
        public void GunTraceDue_DesktopAndThePlayerTraceEveryTick()
        {
            var next = -1f;
            for (var tick = 0; tick < 10; tick++)
            {
                Assert.IsTrue(AimMath.GunTraceDue(false, true, tick / 60f, 7, ref next));
                Assert.IsTrue(AimMath.GunTraceDue(true, false, tick / 60f, 7, ref next));
            }

            Assert.AreEqual(-1f, next);
        }

        [Test]
        public void GunTraceDue_MobileAiTracesOncePerIntervalAfterItsPhase()
        {
            // Instance 500 starts half an interval late, like _gun_trace_due.
            var next = -1f;
            Assert.IsFalse(AimMath.GunTraceDue(true, true, 1f, 500, ref next));
            Assert.AreEqual(1f + 0.5f * AimMath.MobileAiGunTraceInterval, next, 1e-6f);

            var traces = 0;
            for (var tick = 1; tick <= 60; tick++)
            {
                if (AimMath.GunTraceDue(true, true, 1f + tick / 60f, 500, ref next))
                {
                    traces++;
                }
            }

            // One second at 60 Hz with a 50 ms cache: one trace per three
            // ticks, or per four where float rounding lands a tick just short.
            Assert.LessOrEqual(traces, 20);
            Assert.GreaterOrEqual(traces, 15);
        }

        [Test]
        public void GunTraceDue_NegativeInstanceIdsStillSpreadInsideTheWindow()
        {
            var next = -1f;
            AimMath.GunTraceDue(true, true, 2f, -250, ref next);
            Assert.GreaterOrEqual(next, 2f);
            Assert.Less(next, 2f + AimMath.MobileAiGunTraceInterval);
        }

        [Test]
        public void PlanStillFresh_OnlyWhileNothingRelevantChanged()
        {
            Assert.IsTrue(ArmorShotPlanner.PlanStillFresh(0.5f, 0.1f, 0.1f, 1f, true));
            Assert.IsFalse(ArmorShotPlanner.PlanStillFresh(0.5f, 0.1f, 0.1f, 1f, false), "ammo changed");
            Assert.IsFalse(ArmorShotPlanner.PlanStillFresh(ArmorShotPlanner.MaxPlanAge, 0f, 0f, 0f, true), "too old");
            Assert.IsFalse(ArmorShotPlanner.PlanStillFresh(0.5f, ArmorShotPlanner.ReplanShift, 0f, 0f, true), "muzzle moved");
            Assert.IsFalse(ArmorShotPlanner.PlanStillFresh(0.5f, 0f, ArmorShotPlanner.ReplanShift, 0f, true), "target moved");
            Assert.IsFalse(ArmorShotPlanner.PlanStillFresh(0.5f, 0f, 0f, ArmorShotPlanner.ReplanTurnDegrees, true), "target turned");
        }

        [Test]
        public void SortBackToFront_MatchesTheStableDepthOrder()
        {
            var random = new System.Random(1120);
            for (var round = 0; round < 50; round++)
            {
                var count = random.Next(0, 12);
                var depth = new List<float>();
                var order = new List<int>();
                for (var i = 0; i < count; i++)
                {
                    // Few distinct values so equal depths are common.
                    depth.Add(random.Next(0, 4));
                    order.Add(i);
                }

                var expected = new List<int>(order);
                expected.Sort((a, b) =>
                {
                    var c = depth[b].CompareTo(depth[a]);
                    return c != 0 ? c : a.CompareTo(b);
                });

                GodotParticleEffect.SortBackToFront(order, depth);
                CollectionAssert.AreEqual(expected, order);
            }
        }

        [Test]
        public void ParticleEffect_IdlesWithoutFrameWorkBeforeAndAfterItsParticles()
        {
            var scene = CombatFxManifest.Shared.Scene("armor_ap");
            var effect = GodotParticleEffect.Create(scene, "test_effect");
            var camera = new GameObject("test_camera").transform;
            try
            {
                camera.position = new Vector3(0f, 0f, -10f);
                var rootReads = 0;
                effect.RootProvider = () =>
                {
                    rootReads++;
                    return GodotXform.Identity;
                };

                // Not played yet: nothing to read, simulate or draw.
                effect.Tick(1f / 60f, camera);
                Assert.AreEqual(0, rootReads);
                Assert.IsFalse(effect.GetComponent<MeshRenderer>().enabled);

                effect.Play(false);
                Assert.IsFalse(effect.AllDormant());
                effect.Tick(1f / 60f, camera);
                Assert.AreEqual(1, rootReads);
                Assert.Greater(effect.Mesh.vertexCount, 0);
                Assert.IsTrue(effect.GetComponent<MeshRenderer>().enabled);

                // Long past every lifetime: the effect goes dormant and stops
                // reading its root until the next shot.
                for (var frame = 0; frame < 60 * 30 && !effect.AllDormant(); frame++)
                {
                    effect.Tick(1f / 60f, camera);
                }

                Assert.IsTrue(effect.AllDormant());
                var reads = rootReads;
                effect.Tick(1f / 60f, camera);
                Assert.AreEqual(reads, rootReads);
                Assert.IsFalse(effect.GetComponent<MeshRenderer>().enabled);

                effect.Play(false);
                Assert.IsFalse(effect.AllDormant());
                effect.Tick(1f / 60f, camera);
                Assert.AreEqual(reads + 1, rootReads);
            }
            finally
            {
                Object.DestroyImmediate(camera.gameObject);
                Object.DestroyImmediate(effect.gameObject);
            }
        }

        [Test]
        public void RaycastBatch_ReportsWhatTheSingleQueriesReport()
        {
            var boxes = new List<GameObject>();
            try
            {
                for (var i = 0; i < 4; i++)
                {
                    var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    box.transform.position = new Vector3(0f, 0f, 3f + 2f * (3 - i));
                    boxes.Add(box);
                }

                var trigger = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                trigger.transform.position = new Vector3(0f, 0f, 1.5f);
                trigger.GetComponent<Collider>().isTrigger = true;
                boxes.Add(trigger);
                Physics.SyncTransforms();

                var origin = new Vector3(0f, 0f, -1f);
                var batch = new RaycastBatch(8);
                var nearest = new RaycastBatch(1);
                var forward = batch.Add(origin, Vector3.forward, 20f, ~0, QueryTriggerInteraction.Ignore);
                var withTriggers = batch.Add(origin, Vector3.forward, 20f, ~0, QueryTriggerInteraction.Collide);
                var miss = batch.Add(origin, Vector3.up, 20f, ~0, QueryTriggerInteraction.Collide);
                nearest.Add(origin, Vector3.forward, 20f, ~0, QueryTriggerInteraction.Collide);
                batch.Run();
                nearest.Run();

                var buffer = new RaycastHit[8];
                var expected = new RaycastHit[8];
                Assert.AreEqual(Physics.RaycastNonAlloc(origin, Vector3.forward, expected, 20f, ~0,
                    QueryTriggerInteraction.Ignore), batch.Hits(forward, buffer));
                Assert.AreEqual(4, batch.Hits(forward, buffer));
                Assert.AreEqual(5, batch.Hits(withTriggers, buffer));
                Assert.AreEqual(0, batch.Hits(miss, buffer));

                RaycastHit single;
                Assert.IsTrue(Physics.Raycast(origin, Vector3.forward, out single, 20f, ~0,
                    QueryTriggerInteraction.Collide));
                RaycastHit first;
                Assert.IsTrue(nearest.First(0, out first));
                Assert.AreSame(single.collider, first.collider);
                Assert.AreEqual(single.distance, first.distance, 1e-4f);
            }
            finally
            {
                foreach (var box in boxes) Object.DestroyImmediate(box);
            }
        }

        [Test]
        public void JsonResources_ParsesEachAssetOnce()
        {
            JsonResources.Clear();
            var first = JsonResources.Load("Data/legacy480_wreck_manifest");
            Assert.IsNotNull(first);
            Assert.AreSame(first, JsonResources.Load("Data/legacy480_wreck_manifest"));
            Assert.AreEqual(JsonKind.Object, first["vehicles"].Kind);
            Assert.IsNull(JsonResources.Load("Data/no_such_json_resource"));
        }
    }
}
