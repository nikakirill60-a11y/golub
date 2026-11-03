using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The armour shells of a turret and of a mantlet are authored around the
    /// pivots the client ships. Reading those pivots wrong left the T-34 with
    /// its turret plates on the hull roof, so shells flew straight through the
    /// turret and the scope painted no armour on it at all.
    ///
    /// Fire a horizontal trace into each researchable turret and into each
    /// mantlet of the branch and require the armour to be where the tank is.
    /// </summary>
    public sealed class BattleTurretArmorTests
    {
        private static readonly string[] Branch =
        {
            "ms-1", "bt-2", "bt-7", "a-20", "t-34",
            "t-34-85", "t-43", "t-44", "t-54", "t62a",
        };

        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        [Test]
        public void EveryBranchTurretStopsAShellAtItsOwnHeight()
        {
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            _root = new GameObject("BattleTurretArmorTests");

            for (var i = 0; i < Branch.Length; i++)
            {
                var card = database.Get(Branch[i]);
                Assert.IsNotNull(card, Branch[i]);
                var tank = SpawnAt(card, Branch[i], new Vector3(i * 60f, 0f, 0f));

                var turret = PlateBounds(tank, "turret");
                Assert.IsTrue(turret.HasValue,
                    Branch[i] + " carries no turret armour");
                AssertTraceHits(tank, turret.Value, "turret", Branch[i]);

                // A mantlet can sit entirely inside the turret shell, as the
                // MS-1's does, so trace nothing at it and require instead that
                // it rides the elevation axis it was authored around.
                var mask = PlateBounds(tank, "mask");
                if (mask.HasValue)
                {
                    var axis = tank.Turret.GunPivot.position.y;
                    Assert.LessOrEqual(mask.Value.min.y, axis,
                        Branch[i] + " hangs its mantlet above the gun");
                    Assert.GreaterOrEqual(mask.Value.max.y, axis,
                        Branch[i] + " drops its mantlet below the gun");
                }
            }
        }

        [Test]
        public void TurretArmourRidesAboveTheHullArmour()
        {
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            _root = new GameObject("BattleTurretArmorTests");

            for (var i = 0; i < Branch.Length; i++)
            {
                var card = database.Get(Branch[i]);
                var tank = SpawnAt(card, Branch[i], new Vector3(i * 60f, 0f, 0f));

                var hull = PlateBounds(tank, "hull");
                var turret = PlateBounds(tank, "turret");
                Assert.IsTrue(hull.HasValue && turret.HasValue, Branch[i]);
                Assert.Greater(turret.Value.max.y, hull.Value.max.y,
                    Branch[i] + " keeps its turret armour inside the hull");
            }
        }

        private BattleTank SpawnAt(TankInfo card, string id, Vector3 position)
        {
            var spawn = new GameObject(id + "Spawn").transform;
            spawn.SetParent(_root.transform, false);
            spawn.position = position + new Vector3(0f, 10f, 0f);
            var tank = BattleTank.Spawn(
                _root.transform, id, card, spawn,
                new Bounds(position, new Vector3(1000f, 100f, 1000f)), 1);
            Physics.SyncTransforms();
            return tank;
        }

        private static Bounds? PlateBounds(BattleTank tank, string part)
        {
            Bounds? box = null;
            for (var i = 0; i < tank.Plates.Count; i++)
            {
                var plate = tank.Plates[i];
                if (plate.PlateName == null
                    || plate.PlateName.IndexOf(part, System.StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                if (box.HasValue)
                {
                    var grown = box.Value;
                    grown.Encapsulate(plate.Shape.bounds);
                    box = grown;
                }
                else
                {
                    box = plate.Shape.bounds;
                }
            }

            return box;
        }

        private static void AssertTraceHits(
            BattleTank tank, Bounds target, string part, string id,
            params Vector3[] directions)
        {
            if (directions == null || directions.Length == 0)
            {
                directions = new[] { Vector3.left };
            }

            var centre = target.center;
            var report = string.Empty;
            for (var i = 0; i < directions.Length; i++)
            {
                var direction = directions[i].normalized;
                RaycastHit hit;
                var found = Physics.Raycast(
                    centre - direction * 20f, direction, out hit, 40f,
                    BattleLayers.ProjectileMask, QueryTriggerInteraction.Collide);
                if (!found)
                {
                    report += " nothing;";
                    continue;
                }

                var plate = hit.collider.GetComponent<BattleArmorPlate>();
                if (plate == null)
                {
                    report += " " + hit.collider.name + ";";
                    continue;
                }

                if (plate.Tank == tank
                    && plate.PlateName.IndexOf(
                        part, System.StringComparison.Ordinal) >= 0)
                {
                    return;
                }

                report += " " + plate.PlateName + ";";
            }

            Assert.Fail(
                id + ": a shell aimed at its " + part + " hit" + report);
        }
    }
}
