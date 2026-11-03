using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>Regression coverage for the Blitz 11.20 Mark I Male collectible.</summary>
    public sealed class MarkIMale1120Tests
    {
        private const string TankName = "gb86_mark_i_male";
        private GameObject _root;

        private static TanksDatabase Database()
        {
            return TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
        }

        [TearDown]
        public void TearDown()
        {
            BattleGun.Clock = null;
            if (_root != null) { UnityEngine.Object.DestroyImmediate(_root); }
        }

        [Test]
        public void Card_IsATierOneCollectibleOutsideTheResearchTreeAndWithinItsBudget()
        {
            var database = Database();
            var card = database.Get(TankName);
            Assert.That(card, Is.Not.Null);
            Assert.That(card.Level, Is.EqualTo(1));
            Assert.That(card.DisplayName, Is.EqualTo("Mark I Male"));
            Assert.That(card.Collector, Is.True);
            Assert.That(card.Premium, Is.True);
            Assert.That(card.TreeTank, Is.False);
            Assert.That(card.ShopTank, Is.False);
            Assert.That(card.Hidden, Is.False);
            Assert.That(card.Acquisition, Is.EqualTo("collectible"));
            Assert.That(card.CostGold, Is.EqualTo(10));
            Assert.That(card.CollectorSellGold, Is.EqualTo(10));
            Assert.That(card.UnlockTanks, Is.Empty);
            Assert.That(database.PrecedingTanks(TankName), Is.Empty);
            Assert.That(card.TotalHealth, Is.EqualTo(275));

            var gun = card.GunFor(0, 0);
            Assert.That(gun, Is.Not.Null);
            Assert.That(gun.Shells.Count, Is.EqualTo(1));
            Assert.That(gun.Shells[0].Damage, Is.EqualTo(45f));
            Assert.That(gun.Shells[0].Penetration, Is.EqualTo(46f));
            Assert.That(60f * gun.Shells[0].Damage / gun.Reload,
                Is.EqualTo(591.8919f).Within(0.01f));
            Assert.That(card.SpeedForward, Is.EqualTo(32.775f).Within(0.001f));
            Assert.That(card.SpeedBackward, Is.EqualTo(9.2f).Within(0.001f));
        }

        [Test]
        public void RuntimeBundle_HasIconsLocalizationRigAndTheAuthoredTenMillimetreMask()
        {
            var card = Database().Get(TankName);
            var assets = TankModelAssets.For(card);
            var model = Resources.Load<GameObject>(assets.ModelResource);
            var physics = Resources.Load<GameObject>(assets.PhysicsResource);
            var armorModel = Resources.Load<GameObject>(assets.ArmorModelResource);
            var body = Resources.Load<Texture2D>(assets.BodyTextureResource);
            var tracks = Resources.Load<Texture2D>(assets.TrackTextureResource);
            var bigIcon = Resources.Load<Texture2D>("Textures/TankIcons/gb86markimale");
            var smallIcon = Resources.Load<Texture2D>("Textures/SmallTankIcons/gb86markimale");
            var nodesAsset = Resources.Load<TextAsset>("Data/" + assets.NodeDataResource);
            var armorAsset = Resources.Load<TextAsset>("Data/" + assets.ArmorDataResource);
            Assert.That(model, Is.Not.Null);
            Assert.That(physics, Is.Not.Null);
            Assert.That(armorModel, Is.Not.Null);
            Assert.That(body, Is.Not.Null);
            Assert.That(tracks, Is.Not.Null);
            Assert.That(bigIcon, Is.Not.Null);
            Assert.That(smallIcon, Is.Not.Null);
            Assert.That(nodesAsset, Is.Not.Null);
            Assert.That(armorAsset, Is.Not.Null);

            var nodes = TankNodeTable.Parse(nodesAsset.text);
            Assert.That(nodes.Has("turret_01"), Is.True);
            Assert.That(nodes.Has("gun_01"), Is.True);
            Assert.That(nodes.Has("gun_01_shoot_pos"), Is.True);

            var armor = TankArmorTable.Parse(armorAsset.text);
            Assert.That(armor.Count, Is.EqualTo(18));
            var gunMask = default(TankArmorTable.Plate);
            var foundGunMask = false;
            foreach (var plate in armor.Plates)
            {
                if (plate.Name == "gun_01_mask_10")
                {
                    gunMask = plate;
                    foundGunMask = true;
                    break;
                }
            }
            Assert.That(foundGunMask, Is.True, "authored 10 mm gun mask");
            Assert.That(gunMask.Mount, Is.EqualTo("gun_01"));
            Assert.That(gunMask.RequiredTurret, Is.EqualTo("turret_01"));
            Assert.That(gunMask.Armor, Is.EqualTo(10));
            Assert.That(gunMask.Screen, Is.True);
            Assert.That(gunMask.Module, Is.EqualTo(TankModuleIds.Gun));
            var armorMesh = armorModel.GetComponentInChildren<MeshFilter>(true);
            Assert.That(armorMesh, Is.Not.Null);
            Assert.That(armorMesh.sharedMesh.subMeshCount, Is.EqualTo(armor.Count));

            var languageAsset = Resources.Load<TextAsset>("Data/lang");
            Assert.That(languageAsset, Is.Not.Null);
            var language = LocalizationTable.Parse(languageAsset.text);
            language.Language = "en";
            Assert.That(language.GetTankName(card), Is.EqualTo("Mark I Male"));
            language.Language = "ru";
            Assert.That(language.GetTankName(card), Is.EqualTo("Mark I Male"));
        }

        [Test]
        public void BattleSpawn_MountsTheGunMaskOnTheMovingGunAndBuildsTheModelRig()
        {
            var card = Database().Get(TankName);
            _root = new GameObject("MarkIMale1120Test");
            var spawn = new GameObject("Spawn").transform;
            spawn.SetParent(_root.transform, false);
            var tank = BattleTank.Spawn(
                _root.transform,
                TankName,
                card,
                spawn,
                new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f)));

            Assert.That(tank.Model, Is.Not.Null);
            Assert.That(tank.Model.name, Is.EqualTo("GB86MARKIMALE"));
            Assert.That(tank.Turret, Is.Not.Null);
            Assert.That(tank.Turret.TurretPivot, Is.Not.Null);
            Assert.That(tank.Turret.GunPivot, Is.Not.Null);
            Assert.That(tank.Turret.Muzzle, Is.Not.Null);
            Assert.That(tank.Plates.Count, Is.EqualTo(18));

            BattleArmorPlate gunMask = null;
            foreach (var plate in tank.Plates)
            {
                if (plate.PlateName == "gun_01_mask_10") { gunMask = plate; break; }
            }
            Assert.That(gunMask, Is.Not.Null);
            Assert.That(gunMask.Armor, Is.EqualTo(10));
            Assert.That(gunMask.Module, Is.EqualTo(TankModuleIds.Gun));
            Assert.That(gunMask.Screen, Is.True);
            Assert.That(gunMask.FollowMount, Is.SameAs(tank.Turret.GunPivot));
            Assert.That(gunMask.Shape, Is.Not.Null);
            Assert.That(gunMask.Shape.sharedMesh, Is.Not.Null);
        }
    }
}
