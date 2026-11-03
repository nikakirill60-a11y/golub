using System.Collections.Generic;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Utils;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Guards the complete model bundle exported for the only playable tree.
    /// A card is not playable merely because it appears in tanks.json: battle
    /// also needs its visible mesh, collision donor, armour, rig and two sheets.
    /// </summary>
    public sealed class TankModelAssetsTests
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
            }
        }

        [TestCase("ms-1", "MS1", "ms1_nodes")]
        [TestCase("t-34-85", "T3485", "t3485_nodes")]
        [TestCase("t62a", "T62A", "t62a_nodes")]
        public void Naming_UsesOneStableIdAcrossTheBundle(
            string cardName, string expectedId, string expectedNodes)
        {
            var assets = TankModelAssets.For(cardName);

            Assert.That(assets.Id, Is.EqualTo(expectedId));
            Assert.That(assets.NodeDataResource, Is.EqualTo(expectedNodes));
            Assert.That(assets.ModelResource,
                Is.EqualTo("Models/Tanks/" + expectedId + "/" + expectedId));
            Assert.That(assets.PhysicsResource, Is.EqualTo(assets.ModelResource + "_phys"));
            Assert.That(assets.ArmorModelResource, Is.EqualTo(assets.ModelResource + "_armor"));
        }

        [Test]
        public void E100_UsesTheTurretAlbedoFromItsBlitzPackMaterial()
        {
            var assets = TankModelAssets.For("e-100");
            var nodesText = Resources.Load<TextAsset>("Data/" + assets.NodeDataResource);
            Assert.That(nodesText, Is.Not.Null);

            var nodes = TankNodeTable.Parse(nodesText.text);
            var albedo = nodes.AlbedoOf("turret_02");
            Assert.That(albedo, Is.EqualTo("E100_2"));
            Assert.That(Resources.Load<Texture2D>(assets.AlbedoTextureResource(albedo)), Is.Not.Null);
        }

        [Test]
        public void ShippedBranch_CarriesEveryRuntimeAsset()
        {
            var database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));

            foreach (var name in Branch)
            {
                var card = database.Get(name);
                Assert.That(card, Is.Not.Null, name + " card");
                var assets = TankModelAssets.For(card);

                var model = Resources.Load<GameObject>(assets.ModelResource);
                var physics = Resources.Load<GameObject>(assets.PhysicsResource);
                var armorModel = Resources.Load<GameObject>(assets.ArmorModelResource);
                var body = Resources.Load<Texture2D>(assets.BodyTextureResource);
                var tracks = Resources.Load<Texture2D>(assets.TrackTextureResource);
                var nodesText = Resources.Load<TextAsset>("Data/" + assets.NodeDataResource);
                var armorText = Resources.Load<TextAsset>("Data/" + assets.ArmorDataResource);

                Assert.That(model, Is.Not.Null, name + " visible model");
                Assert.That(physics, Is.Not.Null, name + " physics model");
                Assert.That(armorModel, Is.Not.Null, name + " armour model");
                Assert.That(body, Is.Not.Null, name + " body texture");
                Assert.That(tracks, Is.Not.Null, name + " track texture");
                Assert.That(nodesText, Is.Not.Null, name + " node table");
                Assert.That(armorText, Is.Not.Null, name + " armour table");

                var nodes = TankNodeTable.Parse(nodesText.text);
                Assert.That(nodes.Has(nodes.TurretNode), Is.True, name + " turret pivot");
                Assert.That(nodes.Has(nodes.GunNode), Is.True, name + " gun pivot");
                Assert.That(nodes.Has(nodes.MuzzleNode), Is.True, name + " muzzle pivot");

                var armor = TankArmorTable.Parse(armorText.text);
                Assert.That(armor.Count, Is.GreaterThan(0), name + " armour plates");
                var armorMesh = armorModel.GetComponentInChildren<MeshFilter>(true);
                Assert.That(armorMesh, Is.Not.Null, name + " armour mesh");
                Assert.That(armorMesh.sharedMesh.subMeshCount,
                    Is.EqualTo(armor.Count), name + " armour submeshes");

                var names = ChildNames(model);
                Assert.That(names, Does.Contain(nodes.TurretNode), name + " turret object");
                Assert.That(names, Does.Contain(nodes.GunNode), name + " gun object");
                Assert.That(names, Does.Contain("trackL"), name + " left track");
                Assert.That(names, Does.Contain("trackR"), name + " right track");
            }
        }

        [TestCase("bt-2")]
        [TestCase("t-34")]
        [TestCase("t-34-85")]
        [TestCase("t62a")]
        public void Spawn_NonMs1CardBuildsItsOwnRigArmorAndCollider(string name)
        {
            var database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            var card = database.Get(name);
            Assert.That(card, Is.Not.Null);

            _root = new GameObject("TankModelAssetsTest");
            var spawn = new GameObject("Spawn").transform;
            spawn.SetParent(_root.transform, false);
            var tank = BattleTank.Spawn(
                _root.transform,
                name,
                card,
                spawn,
                new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f)));

            var assets = TankModelAssets.For(card);
            Assert.That(tank.Model, Is.Not.Null);
            Assert.That(tank.Model.name, Is.EqualTo(assets.Id));
            Assert.That(tank.Turret, Is.Not.Null);
            Assert.That(tank.Turret.TurretPivot, Is.Not.Null);
            Assert.That(tank.Turret.GunPivot, Is.Not.Null);
            Assert.That(tank.Turret.Muzzle, Is.Not.Null);
            Assert.That(tank.Plates.Count, Is.GreaterThan(0));
            Assert.That(tank.GetComponentInChildren<MeshCollider>(), Is.Not.Null);
        }

        [TestCase("is-7")]
        [TestCase("e-100")]
        [TestCase("amx_50b")]
        public void ArmorBodies_FollowTheMovingHullAndTurretMounts(string name)
        {
            var database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            var card = database.Get(name);
            Assert.That(card, Is.Not.Null);

            _root = new GameObject("ArmorFollowTest");
            var spawn = new GameObject("Spawn").transform;
            spawn.SetParent(_root.transform, false);
            var tank = BattleTank.Spawn(
                _root.transform,
                name,
                card,
                spawn,
                new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f)));

            Assert.That(tank.Plates.Count, Is.GreaterThan(0));
            // Plates stay in the local space of their BlitzPACK mount. The
            // only thing allowed on top is the measured seating correction:
            // the exporter recovers a part pivot from bounding box centres, so
            // a cupola lifts the shell off the part it armours.
            var assets = TankModelAssets.For(card);
            var table = TankArmorTable.Parse(
                Resources.Load<TextAsset>("Data/" + assets.ArmorDataResource).text);
            var before = new Vector3[tank.Plates.Count];
            var localToMount = new Vector3[tank.Plates.Count];
            for (var i = 0; i < tank.Plates.Count; i++)
            {
                var plate = tank.Plates[i];
                var correction = table.MirroredOffsetOf(table.MountOf(plate.PlateName));
                Assert.That(
                    plate.transform.localPosition,
                    Is.EqualTo(new Vector3(correction.X, correction.Y, correction.Z))
                        .Using(Vector3ComparerWithEqualsOperator.Instance),
                    name + "/" + plate.PlateName + " must use mount-local BlitzPACK geometry");
                Assert.That(
                    Quaternion.Angle(plate.transform.localRotation, Quaternion.identity),
                    Is.LessThan(0.001f),
                    name + "/" + plate.PlateName + " must not subtract a baked pivot");
                before[i] = plate.transform.position;
                localToMount[i] = plate.FollowMount.InverseTransformPoint(before[i]);
            }

            var displacement = new Vector3(3f, 7f, -2f);
            tank.transform.position += displacement;
            tank.Turret.TurretPivot.localRotation = Quaternion.Euler(0f, 27f, 0f);
            tank.Turret.GunPivot.localRotation = Quaternion.Euler(11f, 0f, 0f);
            for (var i = 0; i < tank.Plates.Count; i++)
            {
                var plate = tank.Plates[i];
                plate.SyncToMount();
                var expected = plate.FollowMount.TransformPoint(localToMount[i]);
                Assert.That(
                    plate.transform.position,
                    Is.EqualTo(expected).Using(Vector3ComparerWithEqualsOperator.Instance),
                    name + "/" + plate.PlateName + " lost its authored mount");
                if (plate.FollowMount == tank.transform)
                {
                    Assert.That(
                        plate.transform.position - before[i],
                        Is.EqualTo(displacement).Using(Vector3ComparerWithEqualsOperator.Instance),
                        name + "/" + plate.PlateName);
                }
            }
        }

        private static List<string> ChildNames(GameObject prefab)
        {
            var names = new List<string>();
            var transforms = prefab.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                names.Add(transforms[i].name);
            }
            return names;
        }
    }
}
