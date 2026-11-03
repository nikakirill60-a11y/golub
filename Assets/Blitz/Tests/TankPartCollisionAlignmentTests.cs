using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// <see cref="TankArmorGroundingTests"/> guards the hull shell. The same
    /// displacement happens one level down: Dracula's turret shell was written
    /// around a pivot 0.54 m below the turret's mounting plane, so the shell
    /// hung inside the hull, the drawn turret took no hits at all and the hull
    /// grew a collision lump where nothing is rendered. D2, E 50's second
    /// turret, Krupp 38D, Van Helsing and sixteen more cards shipped the same
    /// defect, and checking 342 cards by eye is not a plan.
    ///
    /// A collision shell is a coarse copy of the part it armours, so it may be
    /// smaller, and a mantlet mask may bury itself in the turret behind the
    /// barrel. What it must never be is moved: when both faces of the shell
    /// sit on the same side of the drawn part's faces, the whole shell slid,
    /// and the tank is shot where it is not drawn.
    /// </summary>
    public sealed class TankPartCollisionAlignmentTests
    {
        /// <summary>
        /// Displacement the coarse authoring is allowed to show. Mounts now
        /// ride the pivot the client authors itself, and on an open gun mount
        /// (Object 263, Sturer Emil, RhB Waffentrager) that pivot sits up to a
        /// quarter of a metre away from the centre of the drawn part's box.
        /// A real displacement is far larger: the buried O-47 turret was out
        /// by 0.59 m and the sunken Dracula one by 0.67 m.
        /// </summary>
        private const float MaxDisplacement = 0.30f;

        /// <summary>A gun mask is authored around the trunnion and runs back
        /// into the turret, so its long axis is measured only towards the
        /// muzzle.</summary>
        private const string GunMountPrefix = "gun_";

        private static readonly string[] AxisNames = { "X", "Y", "Z" };

        [Test]
        public void EveryPartCollisionShellSitsOnThePartItArmours()
        {
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            var failures = new List<string>();
            var measured = 0;
            var mounts = 0;

            foreach (var card in database.Tanks)
            {
                var assets = TankModelAssets.For(card);
                var armorText = Resources.Load<TextAsset>("Data/" + assets.ArmorDataResource);
                var nodesText = Resources.Load<TextAsset>("Data/" + assets.NodeDataResource);
                var armorPrefab = Resources.Load<GameObject>(assets.ArmorModelResource);
                var modelPrefab = Resources.Load<GameObject>(assets.ModelResource);
                Assert.IsNotNull(armorText, card.Name + ": no armour table");
                Assert.IsNotNull(nodesText, card.Name + ": no node table");
                Assert.IsNotNull(armorPrefab, card.Name + ": no armour model");
                Assert.IsNotNull(modelPrefab, card.Name + ": no visible model");

                var table = TankArmorTable.Parse(armorText.text);
                var nodes = TankNodeTable.Parse(nodesText.text);
                var shells = ShellsOf(armorPrefab, table);
                var parts = PartsOf(modelPrefab);

                foreach (var shell in shells)
                {
                    var mount = shell.Key;
                    var part = mount == TankArmorTable.HullMount ? "hull_vis" : mount;
                    Bounds drawn;
                    // A casemate mount draws nothing of its own: its shell
                    // belongs to the hull, which the grounding test covers.
                    if (!parts.TryGetValue(part, out drawn))
                    {
                        continue;
                    }

                    var hosts = nodes.CollisionVisualHostsOf(mount);
                    if (hosts.Count > 0)
                    {
                        foreach (var host in hosts)
                        {
                            Bounds hosted;
                            Assert.IsTrue(nodes.Has(host), card.Name + ": unknown articulated host " + host);
                            Assert.IsTrue(parts.TryGetValue(host, out hosted), card.Name + ": missing articulated body " + host);
                            drawn.Encapsulate(hosted);
                        }
                    }

                    var pivot = mount == TankArmorTable.HullMount
                        ? Vector3.zero
                        : ToVector(nodes.SourceWorld(mount));
                    // The runtime places a plate on its mount and then adds the
                    // fitted correction, so the measurement includes it too.
                    pivot += ToVector(table.OffsetOf(mount));
                    // Unity's OBJ importer flips X, so every mesh read here is
                    // already mirrored while the pivot tables are not. Without
                    // this every off-centre gun reads as twice its own X.
                    pivot.x = -pivot.x;

                    float slide;
                    var axis = Displacement(
                        shell.Value.min + pivot,
                        shell.Value.max + pivot,
                        drawn,
                        mount.StartsWith(GunMountPrefix),
                        out slide);
                    mounts++;
                    if (Mathf.Abs(slide) > MaxDisplacement)
                    {
                        failures.Add(string.Format(CultureInfo.InvariantCulture,
                            "{0}: the {1} collision shell is {2:0.###} m off the drawn part along {3}",
                            card.Name, mount, slide, axis));
                    }
                }

                measured++;
                if (measured % 32 == 0)
                {
                    Resources.UnloadUnusedAssets();
                }
            }

            Assert.AreEqual(database.Count, measured, "cards measured");
            Assert.That(mounts, Is.GreaterThan(900), "mounts measured");
            Assert.IsEmpty(failures, Join(failures));
        }

        /// <summary>
        /// The largest slide of the shell against the drawn part. A slide is
        /// only counted when both faces of the shell moved the same way, which
        /// is what separates a displaced shell from a smaller or coarser one.
        /// </summary>
        private static string Displacement(
            Vector3 low, Vector3 high, Bounds drawn, bool gunMask, out float slide)
        {
            slide = 0f;
            var worst = AxisNames[1];
            for (var axis = 0; axis < 3; axis++)
            {
                var below = low[axis] - drawn.min[axis];
                var above = high[axis] - drawn.max[axis];
                if (below * above <= 0f)
                {
                    continue;
                }

                var moved = Mathf.Min(Mathf.Abs(below), Mathf.Abs(above)) * Mathf.Sign(below);
                if (gunMask && axis == 2 && moved > 0f)
                {
                    continue;
                }

                if (Mathf.Abs(moved) > Mathf.Abs(slide))
                {
                    slide = moved;
                    worst = AxisNames[axis];
                }
            }

            return worst;
        }

        /// <summary>Tank-space box of every object in an exported model.</summary>
        private static Dictionary<string, Bounds> PartsOf(GameObject prefab)
        {
            var parts = new Dictionary<string, Bounds>();
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null || mesh.vertexCount == 0 || parts.ContainsKey(filter.name))
                {
                    continue;
                }

                parts[filter.name] = mesh.bounds;
            }

            return parts;
        }

        /// <summary>
        /// Mount-space box of the plates of every mount, read out of the
        /// submeshes the porter wrote them into.
        /// </summary>
        private static Dictionary<string, Bounds> ShellsOf(
            GameObject prefab, TankArmorTable table)
        {
            var shells = new Dictionary<string, Bounds>();
            var filter = prefab.GetComponentInChildren<MeshFilter>(true);
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null)
            {
                return shells;
            }

            var vertices = mesh.vertices;
            foreach (var plate in table.Plates)
            {
                // A module plate draws its own body, not the part it hangs
                // on, so it never belongs to that part's collision shell.
                if (plate.IsModule)
                {
                    continue;
                }

                if (plate.Submesh < 0 || plate.Submesh >= mesh.subMeshCount)
                {
                    continue;
                }

                var indices = mesh.GetIndices(plate.Submesh);
                if (indices.Length == 0)
                {
                    continue;
                }

                Bounds bounds;
                var known = shells.TryGetValue(plate.Mount, out bounds);
                for (var i = 0; i < indices.Length; i++)
                {
                    var vertex = vertices[indices[i]];
                    if (!known)
                    {
                        bounds = new Bounds(vertex, Vector3.zero);
                        known = true;
                        continue;
                    }

                    bounds.Encapsulate(vertex);
                }

                shells[plate.Mount] = bounds;
            }

            return shells;
        }

        private static string Join(IReadOnlyList<string> failures)
        {
            var text = new StringBuilder(failures.Count + " displaced collision shells");
            foreach (var failure in failures)
            {
                text.Append('\n').Append(failure);
            }

            return text.ToString();
        }

        private static Vector3 ToVector(Vec3 value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }
    }
}
