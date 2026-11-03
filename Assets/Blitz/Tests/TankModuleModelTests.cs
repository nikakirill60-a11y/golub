using System.Collections.Generic;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Guards the module geometry exported from the Blitz client pack by
    /// <c>Tools/port_blitz_branch_models.py</c>. The client ships one mesh per
    /// researchable turret and gun inside the vehicle scene and mounts the
    /// installed one; the port keeps that layout, so every slot a card can
    /// research has to exist as its own object, pivot and armour mount.
    /// </summary>
    public sealed class TankModuleModelTests
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

        [Test]
        public void EveryCard_ShipsEveryResearchableTurretAndGun()
        {
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));

            foreach (var name in Branch)
            {
                var card = database.Get(name);
                Assert.That(card, Is.Not.Null, name + " card");
                Assert.That(card.Turrets.Count, Is.GreaterThan(0), name + " turrets");

                var assets = TankModelAssets.For(card);
                var model = Resources.Load<GameObject>(assets.ModelResource);
                var nodes = TankNodeTable.Parse(
                    Resources.Load<TextAsset>("Data/" + assets.NodeDataResource).text);
                var armor = TankArmorTable.Parse(
                    Resources.Load<TextAsset>("Data/" + assets.ArmorDataResource).text);
                var objects = ChildNames(model);

                for (var t = 0; t < card.Turrets.Count; t++)
                {
                    var turret = card.Turrets[t];
                    var slot = turret.Mesh;
                    Assert.That(slot, Is.Not.Empty, name + " turret mesh");
                    Assert.That(objects, Does.Contain(slot), name + " " + slot + " object");
                    Assert.That(nodes.Has(slot), Is.True, name + " " + slot + " pivot");
                    Assert.That(nodes.TurretNodes, Does.Contain(slot));
                    // The hit shell of a turret is cut per armour zone, so a
                    // researched turret is never left without armour.
                    Assert.That(
                        armor.OnMount(slot).Count,
                        Is.GreaterThan(0),
                        name + " " + slot + " armour");

                    for (var g = 0; g < turret.Guns.Count; g++)
                    {
                        var gun = turret.Guns[g].Mesh;
                        Assert.That(
                            objects, Does.Contain(gun), name + " " + gun + " object");
                        Assert.That(nodes.Has(gun), Is.True, name + " " + gun + " pivot");
                        Assert.That(nodes.ParentOf(gun), Is.EqualTo(slot));
                        Assert.That(nodes.GunsOf(slot), Does.Contain(gun));
                        Assert.That(
                            nodes.Has(nodes.MuzzleOf(slot, gun)),
                            Is.True,
                            name + " " + gun + " muzzle");
                    }
                }
            }
        }

        [Test]
        public void Spawn_RigsTheInstalledTurretAndHidesTheOtherModules()
        {
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            var card = database.Get("ms-1");
            var turret = card.Turret(1);
            var gunIndex = GunIndexOf(card, turret);

            _root = new GameObject("TankModuleModelTest");
            var spawn = new GameObject("Spawn").transform;
            spawn.SetParent(_root.transform, false);
            var tank = BattleTank.Spawn(
                _root.transform,
                "ms-1",
                card,
                spawn,
                new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f)),
                0,
                gunIndex,
                0,
                0,
                null,
                1);

            var installedGun = turret.Slot(card.Guns[gunIndex].Name).Mesh;
            Assert.That(tank.Turret.TurretNodeName, Is.EqualTo(turret.Mesh));
            Assert.That(tank.Turret.GunNodeName, Is.EqualTo(installedGun));
            Assert.That(tank.GunMesh, Is.EqualTo(installedGun));

            // Only the installed pair renders: the client swaps meshes when a
            // module is mounted rather than showing every researched barrel.
            var active = ActiveModuleNames(tank.Model, card);
            Assert.That(active, Is.EquivalentTo(new[] { turret.Mesh, installedGun }));

            // Armour follows the same rule, so the stock turret's shell and the
            // mantlets of the other guns never take a hit.
            for (var i = 0; i < tank.Plates.Count; i++)
            {
                var mount = tank.Plates[i].Mount;
                Assert.That(
                    mount == TankArmorTable.HullMount
                        || mount == turret.Mesh
                        || mount == installedGun,
                    Is.True,
                    tank.Plates[i].PlateName + " rides " + mount);
            }
        }

        private static int GunIndexOf(TankInfo card, TurretInfo turret)
        {
            for (var i = 0; i < card.Guns.Count; i++)
            {
                if (turret.CanMount(card.Guns[i].Name))
                {
                    return i;
                }
            }

            return 0;
        }

        private static List<string> ActiveModuleNames(GameObject model, TankInfo card)
        {
            var slots = new List<string>();
            for (var t = 0; t < card.Turrets.Count; t++)
            {
                slots.Add(card.Turrets[t].Mesh);
                for (var g = 0; g < card.Turrets[t].Guns.Count; g++)
                {
                    slots.Add(card.Turrets[t].Guns[g].Mesh);
                }
            }

            var active = new List<string>();
            var filters = model.GetComponentsInChildren<MeshFilter>(true);
            for (var i = 0; i < filters.Length; i++)
            {
                var name = TankModuleView.TrimCloneSuffix(filters[i].gameObject.name);
                if (slots.Contains(name) && filters[i].gameObject.activeSelf)
                {
                    active.Add(name);
                }
            }

            return active;
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
