using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Every card has to be shot where it is drawn. Part of the client models
    /// store their collision shell around a part-local origin, and a converter
    /// run that misses that translation buries the hit box and the sniper
    /// armour overlay under the tracks: the "Новогодний Jagdtiger 8.8" was
    /// penetrated 0.85 m below its hull, and so were the Churchill Mk VI,
    /// SU-100, B1, StuG III and nine more cards.
    /// <c>Tools/port_blitz_branch_models.py</c> grounds such a shell on export;
    /// these tests measure the shipped geometry of the whole roster so the next
    /// exported vehicle cannot bring the bug back.
    /// </summary>
    public sealed class TankArmorGroundingTests
    {
        /// <summary>
        /// How far the collision floor may sit below the lowest rendered hull
        /// vertex. Correctly placed cards stay within 8 cm of it, because the
        /// shell is the armoured hull itself; a displaced one misses by 0.28 m
        /// at the very least.
        /// </summary>
        private const float MaxSink = 0.10f;

        /// <summary>
        /// The physics hull and the hull plates are cut from one shell, so they
        /// share a floor. Lifting only the physics hull left the Churchill
        /// solid where it stands and penetrable 0.86 m lower.
        /// </summary>
        private const float MaxSplit = 0.02f;

        [Test]
        public void EveryHullCollisionShellStandsOnTheRenderedHull()
        {
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            var failures = new List<string>();
            var measured = 0;

            foreach (var card in database.Tanks)
            {
                var assets = TankModelAssets.For(card);
                var hullFloor = MeshFloor(assets.ModelResource, "hull_vis");
                var shellFloor = MeshFloor(assets.PhysicsResource, null);
                var plateFloor = HullPlateFloor(assets);

                Assert.IsNotNull(hullFloor, card.Name + ": no rendered hull");
                Assert.IsNotNull(shellFloor, card.Name + ": no collision hull");
                Assert.IsNotNull(plateFloor, card.Name + ": no hull armour plates");

                if (shellFloor.Value < hullFloor.Value - MaxSink)
                {
                    failures.Add(Report(
                        card.Name, "collision hull", shellFloor.Value, hullFloor.Value));
                }

                if (plateFloor.Value < hullFloor.Value - MaxSink)
                {
                    failures.Add(Report(
                        card.Name, "hull armour", plateFloor.Value, hullFloor.Value));
                }

                if (Mathf.Abs(plateFloor.Value - shellFloor.Value) > MaxSplit)
                {
                    failures.Add(string.Format(CultureInfo.InvariantCulture,
                        "{0}: hull armour floor {1:0.###} m does not follow the "
                        + "collision hull floor {2:0.###} m",
                        card.Name, plateFloor.Value, shellFloor.Value));
                }

                measured++;
                if (measured % 32 == 0)
                {
                    Resources.UnloadUnusedAssets();
                }
            }

            Assert.AreEqual(database.Count, measured, "cards measured");
            Assert.IsEmpty(failures, Join(failures));
        }

        private static string Report(string card, string part, float floor, float hull)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0}: {1} floor {2:0.###} m sits under the hull floor {3:0.###} m",
                card, part, floor, hull);
        }

        private static string Join(IReadOnlyList<string> failures)
        {
            var text = new StringBuilder(failures.Count + " sunken shells");
            foreach (var failure in failures)
            {
                text.Append('\n').Append(failure);
            }

            return text.ToString();
        }

        /// <summary>
        /// Lowest vertex of an exported model, or of one named object in it.
        /// The runtime reads the shared meshes straight out of these prefabs
        /// and only mirrors them across X, so mesh space is tank space here.
        /// </summary>
        private static float? MeshFloor(string resource, string objectName)
        {
            var prefab = Resources.Load<GameObject>(resource);
            if (prefab == null)
            {
                return null;
            }

            var floor = float.MaxValue;
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (objectName != null && filter.name != objectName)
                {
                    continue;
                }

                var mesh = filter.sharedMesh;
                if (mesh != null)
                {
                    floor = Mathf.Min(floor, mesh.bounds.min.y);
                }
            }

            return floor == float.MaxValue ? (float?)null : floor;
        }

        /// <summary>Lowest vertex of the plates mounted on the hull.</summary>
        private static float? HullPlateFloor(TankModelAssets assets)
        {
            var text = Resources.Load<TextAsset>("Data/" + assets.ArmorDataResource);
            var prefab = Resources.Load<GameObject>(assets.ArmorModelResource);
            var filter = prefab != null
                ? prefab.GetComponentInChildren<MeshFilter>(true)
                : null;
            var mesh = filter != null ? filter.sharedMesh : null;
            if (text == null || mesh == null)
            {
                return null;
            }

            var table = TankArmorTable.Parse(text.text);
            var vertices = mesh.vertices;
            var floor = float.MaxValue;
            foreach (var plate in table.Plates)
            {
                // The track run reaches the ground by design, so the module
                // body of the source is not part of the hull armour floor.
                if (plate.IsModule
                    || plate.Mount != TankArmorTable.HullMount
                    || plate.Submesh < 0
                    || plate.Submesh >= mesh.subMeshCount)
                {
                    continue;
                }

                foreach (var index in mesh.GetIndices(plate.Submesh))
                {
                    floor = Mathf.Min(floor, vertices[index].y);
                }
            }

            return floor == float.MaxValue ? (float?)null : floor;
        }
    }
}
