using System.Collections.Generic;
using System.Text;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Guards every shipped card against the SU-152 defect: its superstructure
    /// is painted from two materials, the converter split it into sibling mesh
    /// nodes, and the exporter wrote only the addressed, by then geometry-free
    /// <c>hull_vis</c> node. The vehicle rendered as tracks and a floating
    /// barrel while its collision hull, its armour shells and the bots kept
    /// shooting the hull that was not there.
    ///
    /// A tank is only visible if every part the card can install reaches the
    /// model with triangles, and if the hull that takes the hits is actually
    /// drawn where the collision hull sits.
    /// </summary>
    public sealed class TankVisibleGeometryTests
    {
        /// <summary>A casemate turret slot is a bare mount: its shell belongs
        /// to the hull, so it ships no mesh of its own in the client either.
        /// It is only allowed when the hull is drawn around its pivot.</summary>
        private const float MountInsideHullMargin = 0.75f;

        /// <summary>The client authors a few collision hulls around the
        /// vehicle centre rather than the visible hull, so full containment is
        /// not reachable; a vanished hull scores near zero.</summary>
        private const float MinimumHullCoverage = 0.4f;

        [Test]
        public void EveryCard_DrawsEveryPartItCanInstall()
        {
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            Assert.That(database.Count, Is.GreaterThan(0));

            var failures = new StringBuilder();
            var checkedCards = 0;

            foreach (var card in database.Tanks)
            {
                var assets = TankModelAssets.For(card);
                var model = Resources.Load<GameObject>(assets.ModelResource);
                if (model == null)
                {
                    Fail(failures, card.Name, "has no visible model");
                    continue;
                }

                checkedCards++;
                var meshes = MeshesOf(model);

                RequireMesh(failures, card.Name, meshes, "hull_vis");
                RequireMesh(failures, card.Name, meshes, "trackL");
                RequireMesh(failures, card.Name, meshes, "trackR");

                // An object that reaches the model without triangles is the
                // same disappearance with a placeholder left behind.
                foreach (var entry in meshes)
                {
                    if (entry.Value.triangles == 0)
                    {
                        Fail(failures, card.Name, entry.Key + " has no triangles");
                    }
                }

                var nodesAsset = Resources.Load<TextAsset>("Data/" + assets.NodeDataResource);
                var nodes = nodesAsset != null ? TankNodeTable.Parse(nodesAsset.text) : null;

                for (var t = 0; t < card.Turrets.Count; t++)
                {
                    var turret = card.Turrets[t];
                    if (meshes.ContainsKey(turret.Mesh))
                    {
                        RequireMesh(failures, card.Name, meshes, turret.Mesh);
                    }
                    else if (nodes != null && nodes.Has(turret.Mesh))
                    {
                        // Casemate mount: the hull has to cover its pivot, or
                        // the guns hang in the air like SU-152's did.
                        var pivot = nodes.SourceWorld(turret.Mesh);
                        var hull = BoundsOf(meshes, "hull_vis");
                        if (hull.HasValue && !Covers(hull.Value, ToVector(pivot)))
                        {
                            Fail(failures, card.Name,
                                turret.Mesh + " is a mount with no mesh and no hull around it");
                        }
                    }
                    else
                    {
                        Fail(failures, card.Name, turret.Mesh + " is missing entirely");
                    }

                    for (var g = 0; g < turret.Guns.Count; g++)
                    {
                        RequireMesh(failures, card.Name, meshes, turret.Guns[g].Mesh);
                    }
                }

                var physics = Resources.Load<GameObject>(assets.PhysicsResource);
                var hullBounds = BoundsOf(meshes, "hull_vis");
                if (physics != null && hullBounds.HasValue)
                {
                    var collision = BoundsOf(MeshesOf(physics));
                    if (collision.HasValue)
                    {
                        var covered = Coverage(collision.Value, hullBounds.Value);
                        if (covered < MinimumHullCoverage)
                        {
                            Fail(failures, card.Name,
                                "hull_vis covers only " + Mathf.RoundToInt(covered * 100f)
                                + "% of the collision hull that takes the hits");
                        }
                    }
                }
            }

            Assert.That(checkedCards, Is.GreaterThan(200), "cards with a model");
            Assert.That(failures.ToString(), Is.Empty);
        }

        private static void Fail(StringBuilder failures, string card, string problem)
        {
            failures.Append(card).Append(": ").Append(problem).Append('\n');
        }

        private static void RequireMesh(
            StringBuilder failures,
            string card,
            Dictionary<string, (int triangles, Bounds bounds, int copies)> meshes,
            string part)
        {
            if (string.IsNullOrEmpty(part))
            {
                Fail(failures, card, "names an empty mesh slot");
                return;
            }

            (int triangles, Bounds bounds, int copies) entry;
            if (!meshes.TryGetValue(part, out entry))
            {
                Fail(failures, card, part + " is missing from the visible model");
                return;
            }

            if (entry.triangles == 0)
            {
                Fail(failures, card, part + " reached the model without triangles");
            }

            // The runtime shows a slot by name, so a second copy is drawn at
            // the same place: the two z-fight and each adds its own outline.
            if (entry.copies > 1)
            {
                Fail(failures, card,
                    part + " is in the model " + entry.copies + " times");
            }
        }

        private static Dictionary<string, (int triangles, Bounds bounds, int copies)> MeshesOf(
            GameObject prefab)
        {
            var meshes = new Dictionary<string, (int triangles, Bounds bounds, int copies)>();
            var filters = prefab.GetComponentsInChildren<MeshFilter>(true);
            for (var i = 0; i < filters.Length; i++)
            {
                var mesh = filters[i].sharedMesh;
                var name = filters[i].name;
                (int triangles, Bounds bounds, int copies) seen;
                var copies = meshes.TryGetValue(name, out seen) ? seen.copies + 1 : 1;
                if (mesh == null)
                {
                    meshes[name] = (0, new Bounds(), copies);
                    continue;
                }

                meshes[name] = (mesh.triangles.Length / 3, mesh.bounds, copies);
            }
            return meshes;
        }

        private static Bounds? BoundsOf(
            Dictionary<string, (int triangles, Bounds bounds, int copies)> meshes, string part)
        {
            (int triangles, Bounds bounds, int copies) entry;
            if (!meshes.TryGetValue(part, out entry) || entry.triangles == 0)
            {
                return null;
            }
            return entry.bounds;
        }

        private static Bounds? BoundsOf(
            Dictionary<string, (int triangles, Bounds bounds, int copies)> meshes)
        {
            Bounds? total = null;
            foreach (var entry in meshes)
            {
                if (entry.Value.triangles == 0)
                {
                    continue;
                }

                if (total.HasValue)
                {
                    var grown = total.Value;
                    grown.Encapsulate(entry.Value.bounds);
                    total = grown;
                }
                else
                {
                    total = entry.Value.bounds;
                }
            }
            return total;
        }

        private static bool Covers(Bounds hull, Vector3 point)
        {
            var grown = hull;
            grown.Expand(MountInsideHullMargin * 2f);
            return grown.Contains(point);
        }

        private static float Coverage(Bounds inner, Bounds outer)
        {
            var low = Vector3.Max(inner.min, outer.min);
            var high = Vector3.Min(inner.max, outer.max);
            var overlap = Vector3.Max(Vector3.zero, high - low);
            var size = inner.size;
            var volume = Mathf.Max(size.x * size.y * size.z, 1e-6f);
            return overlap.x * overlap.y * overlap.z / volume;
        }

        private static Vector3 ToVector(BlitzLegacy.Core.Maps.Vec3 value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }
    }
}
