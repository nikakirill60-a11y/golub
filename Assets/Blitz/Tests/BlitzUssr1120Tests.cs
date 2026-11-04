using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Shop;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Object 907: source assets, acquisition and mounted runtime balance.
    /// </summary>
    public sealed class BlitzUssr1120Tests
    {
        private const string Card = "object_907";

        private static TanksDatabase Database()
        { return TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData)); }

        private static JsonNode Json(string resource)
        {
            var asset = Resources.Load<TextAsset>("Data/" + resource);
            Assert.IsNotNull(asset, resource);
            JsonNode node;
            Assert.IsTrue(JsonNode.TryParse(asset.text, out node), resource);
            return node;
        }

        [Test]
        public void Object907_IsATierTenSovietCollectible()
        {
            var db = Database();
            Assert.AreEqual(523, db.Count);
            var card = db.Get(Card);
            Assert.IsNotNull(card, Card);
            Assert.AreEqual("Объект 907", card.DisplayName);
            Assert.AreEqual(Nation.Ussr, card.Nation);
            Assert.AreEqual(10, card.Level);
            Assert.AreEqual(TankClass.Medium, card.Class);
            Assert.IsTrue(card.Collector && card.Premium, "collectible, like A178 SPHT");
            Assert.IsFalse(card.TreeTank || card.ShopTank);
            Assert.AreEqual("collectible", card.Acquisition);
            Assert.AreEqual(15000, card.CostGold, "11.20 list price");
            Assert.AreEqual(7500, card.CollectorSellGold, "11.20 sell price");
            Assert.AreEqual(0, card.CostXp);
            Assert.IsEmpty(card.UnlockTanks);
            Assert.IsTrue(ShopPricing.IsCollector(card));
            Assert.IsTrue(WeeklyTankRotation.IsCandidate(card, null), "rotates through the weekly shop");
        }

        [Test]
        public void Object907_CarriesEverySourceModule()
        {
            var card = Database().Get(Card);
            var inventory = Json("blitz_ussr_1120_inventory");
            Assert.AreEqual(1, inventory["records"].Count);
            foreach (var record in inventory["records"].Items)
            {
                Assert.AreEqual(Card, record["name"].AsString());
                var lists = new IReadOnlyList<ModuleInfo>[] { card.Turrets, card.Guns, card.Tracks, card.Engines, card.Radios };
                var groups = new[] { "turrets", "guns", "tracks", "engines", "radios" };
                for (var kind = 0; kind < groups.Length; kind++)
                {
                    var expected = record["modules"][groups[kind]];
                    Assert.AreEqual(expected.Count, lists[kind].Count, groups[kind]);
                    for (var m = 0; m < expected.Count; m++)
                    { Assert.AreEqual(expected[m].AsString(), lists[kind][m].Name, groups[kind]); }
                }
            }
            Assert.IsNotNull(Resources.Load<TextAsset>("Data/object907_nodes"), "node table");
            Assert.IsNotNull(Resources.Load<TextAsset>("Data/object907_armor"), "armour table");
        }

        [Test]
        public void Object907_ImportsTheSourceMeshesLosslesslyAndPixelsAtTheClientRate()
        {
            var id = TankModelAssets.ResourceId(Card);
            var folder = "Assets/Blitz/Resources/Models/Tanks/" + id;
            foreach (var suffix in new[] { "", "_armor", "_phys" })
            {
                var importer = AssetImporter.GetAtPath(folder + "/" + id + suffix + ".obj") as ModelImporter;
                Assert.IsNotNull(importer, id + suffix);
                Assert.AreEqual("blitz-source-11.20", importer.userData);
                Assert.AreEqual(ModelImporterMeshCompression.Off, importer.meshCompression);
                Assert.AreEqual(ModelImporterNormals.Import, importer.importNormals);
                Assert.IsTrue(importer.isReadable);
            }
            var textures = AssetDatabase.FindAssets("t:Texture2D", new[] { folder + "/Textures" });
            Assert.IsNotEmpty(textures);
            foreach (var guid in textures)
            {
                var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guid));
                Assert.AreEqual(TextureImporterCompression.Uncompressed, importer.textureCompression);
                BlitzAssetPostprocessorTests.RequireSourceAndroidFormat(importer);
            }
        }


        [Test]
        public void Object907_UsesCalibratedMountWithoutAlteringSourceProjectiles()
        {
            var card = Database().Get(Card);
            Assert.AreEqual(1800, card.TotalHealth);
            Assert.AreEqual(60f, card.SpeedForward);
            Assert.AreEqual(23f, card.SpeedBackward);
            Assert.AreEqual(746f, card.Engines[0].Power);
            var gun = card.Guns[0].WithTurretSlot(card.Turrets[0].Guns[0]);
            Assert.AreEqual(7.1f, gun.Reload);
            Assert.AreEqual(55, gun.MaxAmmo);
            Assert.AreEqual(.3f, gun.Dispersion);
            Assert.AreEqual(1.8f, gun.AimTime);
            Assert.IsFalse(gun.AutoReload);
            var evidence = Json("blitz_ussr_1120_balance")["tanks"][0];
            Assert.AreEqual(10, evidence["competitors"].Count);
            Assert.AreEqual(8, evidence["single_shot_competitors"].Count);
            Assert.LessOrEqual(60d * 320d / gun.Reload,
                evidence["limits"]["single_shot_dpm_median"].AsDouble());
            for (var i = 0; i < 3; i++)
            {
                var before = evidence["original"]["guns"][0]["shells"][i];
                var after = evidence["balanced"]["guns"][0]["shells"][i];
                foreach (var key in new[] { "dmg", "pen", "speed", "type" })
                    Assert.AreEqual(before[key].AsDouble(), after[key].AsDouble(), key);
            }
        }
    }
}
