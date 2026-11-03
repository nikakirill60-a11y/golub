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
    /// The runtime elevates a gun around the pivot the node table ships for
    /// it, so that pivot has to be the trunnion of the barrel it turns. The
    /// converter used to recover it from a bounding box, and on an open mount
    /// whose mask covers the whole barrel that centre lands metres down the
    /// barrel: the Pz.Sfl. IVc Flak 41 trunnion sat 2.52 m behind its own
    /// gun, so the first degree of elevation threw the gun off the tank and
    /// into the air. The pivots now come from the client's own
    /// <c>blitz/points/normal</c>, and this test keeps every card that way
    /// without needing the client pack: a trunnion that is not on the gun it
    /// elevates cannot hold the gun on the tank.
    /// </summary>
    public sealed class TankGunTrunnionTests
    {
        /// <summary>
        /// How far outside the drawn barrel the trunnion may sit per axis.
        /// Sideways and vertically the client authors it on the barrel axis
        /// (worst case T1 heavy 0.33 m sideways, AMX 40 0.09 m vertically).
        /// Lengthwise the breech is often not drawn at all, so the trunnion
        /// legitimately sits behind the mesh - the SU-100Y by 0.96 m - while
        /// a recovered pivot misses by far more (Pz.Sfl. IVc 2.52 m,
        /// Mark I 1.36 m, T-54 first prototype 1.06 m).
        /// </summary>
        private static readonly float[] MaxOutside = { 0.40f, 0.25f, 1.10f };

        private static readonly string[] AxisNames = { "X", "Y", "Z" };

        [Test]
        public void EveryGunElevatesAroundItsOwnBarrel()
        {
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            var failures = new List<string>();
            var measured = 0;
            var guns = 0;

            foreach (var card in database.Tanks)
            {
                var assets = TankModelAssets.For(card);
                var nodesText = Resources.Load<TextAsset>("Data/" + assets.NodeDataResource);
                var modelPrefab = Resources.Load<GameObject>(assets.ModelResource);
                Assert.IsNotNull(nodesText, card.Name + ": no node table");
                Assert.IsNotNull(modelPrefab, card.Name + ": no visible model");

                var nodes = TankNodeTable.Parse(nodesText.text);
                var parts = PartsOf(modelPrefab);

                foreach (var turret in nodes.TurretNodes)
                {
                    foreach (var gun in nodes.GunsOf(turret))
                    {
                        Bounds drawn;
                        // A mount-only slot (a casemate gun that shares the
                        // hull mesh) draws no barrel of its own.
                        if (!parts.TryGetValue(gun, out drawn) || !nodes.Has(gun))
                        {
                            continue;
                        }

                        var pivot = ToVector(nodes.SourceWorld(gun));
                        // Unity's OBJ importer flips X while the pivot table
                        // keeps the exporter's own space.
                        pivot.x = -pivot.x;
                        guns++;

                        for (var axis = 0; axis < 3; axis++)
                        {
                            var outside = Mathf.Max(
                                drawn.min[axis] - pivot[axis],
                                pivot[axis] - drawn.max[axis]);
                            if (outside <= MaxOutside[axis])
                            {
                                continue;
                            }

                            failures.Add(string.Format(CultureInfo.InvariantCulture,
                                "{0}: the {1}/{2} trunnion is {3:0.###} m off its own barrel along {4}",
                                card.Name, turret, gun, outside, AxisNames[axis]));
                        }
                    }
                }

                measured++;
                if (measured % 32 == 0)
                {
                    Resources.UnloadUnusedAssets();
                }
            }

            Assert.AreEqual(database.Count, measured, "cards measured");
            Assert.That(guns, Is.GreaterThan(900), "guns measured");
            Assert.IsEmpty(failures, Join(failures));
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

        private static string Join(IReadOnlyList<string> failures)
        {
            var text = new StringBuilder(failures.Count + " trunnions off their gun");
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
