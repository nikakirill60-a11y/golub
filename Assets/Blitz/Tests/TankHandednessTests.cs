using System.Collections.Generic;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Battle must turn the imported hangar model to face +Z, not reflect it.
    /// Relative to the tank's forward axis, asymmetric details stay on their
    /// authored side. Expected values deliberately use a proper rotation,
    /// independently of the production mesh and pivot conversion helpers.
    /// </summary>
    public sealed class TankHandednessTests
    {
        private static readonly Quaternion HangarToBattle = Quaternion.Euler(0f, 180f, 0f);
        private const float Tolerance = 0.0002f;

        [Test]
        public void BattleConversion_IsAProperRotationAndPreservesMeshStreams()
        {
            var source = new Mesh { name = "AsymmetricTankProbe" };
            Mesh battle = null;
            try
            {
                source.vertices = new[]
                {
                    new Vector3(2f, 0f, -3f), new Vector3(3f, 0f, -3f),
                    new Vector3(2f, 1f, -3f), new Vector3(2f, 0f, -4f),
                };
                source.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.right };
                source.tangents = new[]
                {
                    new Vector4(1f, 0f, 0f, 1f), new Vector4(1f, 0f, 0f, -1f),
                    new Vector4(1f, 0f, 0f, 1f), new Vector4(0f, 0f, 1f, -1f),
                };
                source.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
                source.subMeshCount = 2;
                source.SetTriangles(new[] { 0, 1, 2 }, 0);
                source.SetTriangles(new[] { 0, 2, 3 }, 1);
                battle = BattleTank.Mirror(source);

                for (var i = 0; i < source.vertexCount; i++)
                {
                    Near(battle.vertices[i], HangarToBattle * source.vertices[i], "vertex " + i);
                    Near(battle.normals[i], HangarToBattle * source.normals[i], "normal " + i);
                    var tangent = source.tangents[i];
                    var expected = HangarToBattle * new Vector3(tangent.x, tangent.y, tangent.z);
                    var actual = battle.tangents[i];
                    Near(new Vector3(actual.x, actual.y, actual.z), expected, "tangent " + i);
                    Assert.AreEqual(tangent.w, actual.w, "a rotation preserves tangent handedness");
                }
                Assert.AreEqual(source.subMeshCount, battle.subMeshCount);
                for (var i = 0; i < source.subMeshCount; i++)
                    CollectionAssert.AreEqual(source.GetTriangles(i), battle.GetTriangles(i), "winding " + i);
                CollectionAssert.AreEqual(source.uv, battle.uv);
                Near(battle.bounds.center, HangarToBattle * source.bounds.center, "bounds centre");
                Near(battle.bounds.size, source.bounds.size, "bounds size");
                Assert.AreSame(battle, BattleTank.Mirror(source), "shared converted mesh cache");
                Near(source.vertices[0], new Vector3(2f, 0f, -3f), "hangar mesh must not mutate");
                Assert.IsNull(BattleTank.Mirror(null));
            }
            finally
            {
                if (battle != null) Object.DestroyImmediate(battle);
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void BattlePivotsAndArmourOffsets_RotateWithTheImportedModel()
        {
            var nodes = TankNodeTable.Parse("{\"nodes\":{\"turret_01\":{\"local\":[0.37,1.2,-0.8],\"world\":[0.57,1.7,-1.1]}}}");
            Near(ToVector(nodes.World("turret_01")), HangarToBattle * ToVector(nodes.ModelWorld("turret_01")), "world pivot");
            Near(ToVector(nodes.Local("turret_01")), HangarToBattle * new Vector3(-0.37f, 1.2f, -0.8f), "local pivot");
            var armor = TankArmorTable.Parse("{\"mountOffsets\":{\"turret_01\":[0.23,-0.46,0.08]}}");
            Near(ToVector(armor.MirroredOffsetOf("turret_01")), HangarToBattle * ToVector(armor.ModelOffsetOf("turret_01")), "armour offset");
        }

        [Test]
        public void EveryShippedRig_KeepsTheHangarSideOfItsModules()
        {
            var database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            var checkedPivots = 0;
            foreach (var card in database.Tanks)
            {
                var assets = TankModelAssets.For(card);
                var nodes = TankNodeTable.Parse(Resources.Load<TextAsset>("Data/" + assets.NodeDataResource).text);
                foreach (var turret in nodes.TurretNodes)
                {
                    CheckPivot(nodes, turret, card.Name);
                    checkedPivots++;
                    foreach (var gun in nodes.GunsOf(turret))
                    {
                        CheckPivot(nodes, gun, card.Name);
                        CheckPivot(nodes, nodes.MuzzleOf(turret, gun), card.Name);
                        checkedPivots += 2;
                    }
                }
            }
            Assert.Greater(checkedPivots, 2000, "complete shipped roster");
        }

        [Test]
        public void GunClip_UsesTheSameSourceToBattleAxesAsItsPivot()
        {
            var node = BlitzLegacy.Core.Serialization.JsonNode.Parse(
                "{\"duration\":1,\"fps\":1,\"tracks\":{\"probe\":[[0.3,0.2,-0.4,0.2,0.3,0.4,0.842614977]]}}");
            var clip = GunClip.Parse("probe", node);
            var position = new float[3];
            var rotation = new float[4];
            Assert.IsTrue(clip.Sample("probe", 0f, position, rotation));
            Near(new Vector3(position[0], position[1], position[2]), new Vector3(0.3f, 0.2f, 0.4f), "clip translation");
            // Reflecting source Z conjugates the rotation: (-qx, -qy, qz, qw).
            var expected = new Quaternion(-0.2f, -0.3f, 0.4f, 0.842614977f).normalized;
            var actual = new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3]);
            Assert.Less(Quaternion.Angle(expected, actual), 0.01f, "clip rotation");
        }

        private static void CheckPivot(TankNodeTable nodes, string node, string card)
        {
            Assert.IsTrue(nodes.Has(node), card + "/" + node);
            Near(ToVector(nodes.World(node)), HangarToBattle * ToVector(nodes.ModelWorld(node)), card + "/" + node);
        }

        [TestCase("b1")]
        [TestCase("m3_grant")]
        [TestCase("chi_ni")]
        public void SpawnedAsymmetricTank_MatchesHangarAfterRotation(string name)
        {
            var database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            var card = database.Get(name);
            Assert.IsNotNull(card, name);
            var assets = TankModelAssets.For(card);
            var prefab = Resources.Load<GameObject>(assets.ModelResource);
            Assert.IsNotNull(prefab);
            var originals = new Dictionary<string, MeshFilter>();
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
                originals.Add(filter.name, filter);

            var root = new GameObject("Handedness-" + name);
            try
            {
                var spawn = new GameObject("Spawn").transform;
                spawn.SetParent(root.transform, false);
                // Non-identity world placement catches accidental world-space fixes.
                spawn.position = new Vector3(13f, 4f, -8f);
                spawn.rotation = Quaternion.Euler(0f, 37f, 0f);
                var tank = BattleTank.Spawn(root.transform, name, card, spawn,
                    new Bounds(Vector3.zero, Vector3.one * 1000f));
                var checkedParts = 0;
                foreach (var filter in tank.Model.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (!originals.TryGetValue(filter.name, out var original)) continue;
                    var expectedVertices = original.sharedMesh.vertices;
                    var actualVertices = filter.sharedMesh.vertices;
                    Assert.AreEqual(expectedVertices.Length, actualVertices.Length, filter.name);
                    for (var i = 0; i < expectedVertices.Length; i++)
                    {
                        var modelPoint = prefab.transform.InverseTransformPoint(original.transform.TransformPoint(expectedVertices[i]));
                        var battlePoint = tank.Model.transform.InverseTransformPoint(filter.transform.TransformPoint(actualVertices[i]));
                        Near(battlePoint, HangarToBattle * modelPoint, name + "/" + filter.name + "/" + i);
                    }
                    checkedParts++;
                }
                Assert.Greater(checkedParts, 5, "real model parts checked");
                var nodes = TankNodeTable.Parse(Resources.Load<TextAsset>("Data/" + assets.NodeDataResource).text);
                Near(tank.Model.transform.InverseTransformPoint(tank.Turret.TurretPivot.position),
                    HangarToBattle * ToVector(nodes.ModelWorld(tank.Turret.TurretNodeName)), "turret pivot");
                Near(tank.Model.transform.InverseTransformPoint(tank.Turret.Muzzle.position),
                    HangarToBattle * ToVector(nodes.ModelWorld(tank.Turret.MuzzleNodeName)), "muzzle");
                var armor = TankArmorTable.Parse(Resources.Load<TextAsset>("Data/" + assets.ArmorDataResource).text);
                var armorMesh = Resources.Load<GameObject>(assets.ArmorModelResource).GetComponentInChildren<MeshFilter>(true).sharedMesh;
                var armorVertices = armorMesh.vertices;
                foreach (var plate in tank.Plates)
                {
                    var data = armor.Find(plate.PlateName);
                    var sourceIndices = armorMesh.GetTriangles(data.Submesh);
                    var shape = plate.GetComponent<MeshCollider>().sharedMesh;
                    var shapeIndices = shape.triangles;
                    var shapeVertices = shape.vertices;
                    Assert.AreEqual(sourceIndices.Length, shapeIndices.Length, plate.PlateName);
                    var offset = ToVector(armor.ModelOffsetOf(data.Mount));
                    if (data.Mount != TankArmorTable.HullMount) offset += ToVector(nodes.ModelWorld(data.Mount));
                    for (var i = 0; i < sourceIndices.Length; i++)
                    {
                        var actual = tank.Model.transform.InverseTransformPoint(plate.transform.TransformPoint(shapeVertices[shapeIndices[i]]));
                        Near(actual, HangarToBattle * (armorVertices[sourceIndices[i]] + offset), "armour " + plate.PlateName + "/" + i);
                    }
                }
                var physics = Resources.Load<GameObject>(assets.PhysicsResource).GetComponentInChildren<MeshFilter>(true).sharedMesh;
                var collider = tank.transform.Find("hull_phys_col").GetComponent<MeshCollider>().sharedMesh;
                var physicsVertices = physics.vertices;
                var colliderVertices = collider.vertices;
                for (var i = 0; i < physicsVertices.Length; i++)
                    Near(colliderVertices[i], HangarToBattle * physicsVertices[i], "physics hull " + i);
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static Vector3 ToVector(BlitzLegacy.Core.Maps.Vec3 point)
        {
            return new Vector3(point.X, point.Y, point.Z);
        }

        private static void Near(Vector3 actual, Vector3 expected, string label)
        {
            Assert.Less(Vector3.Distance(actual, expected), Tolerance, label);
        }
    }
}
