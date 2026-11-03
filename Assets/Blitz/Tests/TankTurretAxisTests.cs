using System.Collections.Generic;
using System.Globalization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Spawns every tank of the shipped roster and checks that the turret and
    /// the gun turn around their own geometry. The pivots come from the node
    /// table while the meshes come through Unity's OBJ importer, which flips
    /// X; a pivot that missed that flip sat mirrored across the hull from any
    /// off-centre turret (Chi-Ni, M3 Lee, B1, Pz IV...) and the turret slid
    /// around the hull as soon as it traversed. The hangar never turns the
    /// turret, so only a battle rig shows it.
    /// </summary>
    public sealed class TankTurretAxisTests
    {
        /// <summary>Largest sideways gap between a pivot and its part's centre.</summary>
        private const float MaxLateral = 0.3f;

        /// <summary>How far a muzzle may stand beside the drawn barrel.</summary>
        private const float MuzzleSlack = 0.05f;

        /// <summary>Below this a pivot is on the hull axis and has no side.</summary>
        private const float MinOffCentre = 0.05f;

        /// <summary>
        /// Casemate tanks export a stub "turret" a few centimetres across that
        /// draws nothing; there is no footprint to turn around.
        /// </summary>
        private const float MinFootprint = 0.3f;

        [Test]
        public void EveryTurretAndGunTurnAroundTheirOwnGeometry()
        {
            var database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            var failures = new List<string>();
            var turrets = 0;
            var guns = 0;
            var spawned = 0;
            LogAssert.ignoreFailingMessages = true;
            try
            {
                foreach (var card in database.Tanks)
                {
                    var root = new GameObject("axis-" + card.Name);
                    try
                    {
                        var spawn = new GameObject("Spawn").transform;
                        spawn.SetParent(root.transform, false);
                        var tank = BattleTank.Spawn(root.transform, card.Name, card, spawn,
                            new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f)));
                        if (tank == null || tank.Turret == null || tank.Turret.TurretPivot == null)
                        {
                            failures.Add(card.Name + ": no turret rig");
                            continue;
                        }

                        spawned++;
                        var hull = tank.transform;
                        var turret = Find(tank.Turret.TurretPivot, tank.Turret.TurretNodeName);
                        Bounds box;
                        if (turret != null && Footprint(hull, turret, out box) &&
                            Mathf.Min(box.size.x, box.size.z) >= MinFootprint)
                        {
                            turrets++;
                            var pivot = hull.InverseTransformPoint(tank.Turret.TurretPivot.position);
                            Check(failures, card.Name, "turret", pivot, box);
                        }

                        var gun = Find(tank.Turret.GunPivot, tank.Turret.GunNodeName);
                        Bounds barrel;
                        if (gun != null && Footprint(hull, gun, out barrel))
                        {
                            guns++;
                            // The gun only pitches, so its pivot may sit
                            // anywhere on the trunnion line; what has to stay
                            // on the drawn barrel is the point shells leave.
                            var muzzle = hull.InverseTransformPoint(tank.Turret.Muzzle.position);
                            var gap = Mathf.Abs(muzzle.x - barrel.center.x) - barrel.extents.x;
                            if (gap > MuzzleSlack)
                            {
                                failures.Add(string.Format(CultureInfo.InvariantCulture,
                                    "{0}: the muzzle (x {1:0.###}) is {2:0.###} m beside its gun mesh (x {3:0.###}, {4:0.###} m wide)",
                                    card.Name, muzzle.x, gap, barrel.center.x, barrel.size.x));
                            }
                        }
                    }
                    finally
                    {
                        Object.DestroyImmediate(root);
                    }

                    if (spawned % 32 == 0)
                    {
                        Resources.UnloadUnusedAssets();
                    }
                }
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }

            Assert.That(spawned, Is.EqualTo(database.Count), "tanks spawned");
            Assert.That(turrets, Is.GreaterThan(300), "turrets measured");
            Assert.That(guns, Is.GreaterThan(300), "muzzles measured");
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        private static void Check(List<string> failures, string tank, string part,
            Vector3 pivot, Bounds box)
        {
            // A pivot mirrored across the hull axis is the slide the player
            // sees: the part swings around a point on the wrong side. Off the
            // axis the pivot therefore has to sit nearer its own geometry than
            // its mirror image does; cupolas and bustles make the bounding box
            // lopsided, but never by more than the turret is off-centre.
            var dx = box.center.x - pivot.x;
            var mirrored = box.center.x + pivot.x;
            if (Mathf.Abs(pivot.x) > MinOffCentre && Mathf.Abs(dx) >= Mathf.Abs(mirrored))
            {
                failures.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0}: the {1} pivot (x {2:0.###}) sits mirrored across the hull from its mesh (x {3:0.###}, {4:0.###} m wide)",
                    tank, part, pivot.x, box.center.x, box.size.x));
            }

            if (Mathf.Abs(dx) > MaxLateral)
            {
                failures.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0}: the {1} pivot is {2:0.###} m sideways off its mesh (pivot x {3:0.###}, mesh x {4:0.###})",
                    tank, part, dx, pivot.x, box.center.x));
            }
        }

        private static MeshFilter Find(Transform pivot, string node)
        {
            if (pivot == null || string.IsNullOrEmpty(node)) { return null; }
            foreach (var filter in pivot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.gameObject.name.Replace("(Clone)", string.Empty) == node)
                {
                    return filter;
                }
            }

            return null;
        }

        /// <summary>The part's bounds in hull space, as the battle draws it.</summary>
        private static bool Footprint(Transform hull, MeshFilter filter, out Bounds box)
        {
            box = default(Bounds);
            var mesh = filter.sharedMesh;
            if (mesh == null || mesh.vertexCount == 0) { return false; }
            var vertices = mesh.vertices;
            var min = Vector3.one * float.MaxValue;
            var max = -min;
            for (var i = 0; i < vertices.Length; i++)
            {
                var p = hull.InverseTransformPoint(filter.transform.TransformPoint(vertices[i]));
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            box.SetMinMax(min, max);
            return true;
        }
    }
}
