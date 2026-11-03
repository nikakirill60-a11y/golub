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
    /// Type 5 Heavy (11.20 <c>J20_Type_2605</c>): an additive Japanese tier X
    /// collectible with every source module, a lossless model import and a
    /// calibration that keeps its 11.20 standing among the tier X heavies.
    /// </summary>
    public sealed class BlitzJapan1120Tests
    {
        private const string Card = "j20_type_2605";

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
        public void TypeFiveHeavy_IsATierTenJapaneseCollectible()
        {
            var db = Database();
            Assert.AreEqual(522, db.Count);
            var card = db.Get(Card);
            Assert.IsNotNull(card, Card);
            Assert.AreEqual("Type 5 Heavy", card.DisplayName);
            Assert.AreEqual(Nation.Japan, card.Nation);
            Assert.AreEqual(10, card.Level);
            Assert.AreEqual(TankClass.Heavy, card.Class);
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
        public void TypeFiveHeavy_CarriesEverySourceModule()
        {
            var card = Database().Get(Card);
            var inventory = Json("blitz_japan_1120_inventory");
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
            Assert.IsNotNull(Resources.Load<TextAsset>("Data/j20type2605_nodes"), "node table");
            Assert.IsNotNull(Resources.Load<TextAsset>("Data/j20type2605_armor"), "armour table");
        }

        [Test]
        public void TypeFiveHeavy_ImportsTheSourceMeshesLosslesslyAndPixelsAtTheClientRate()
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
        public void Calibration_KeepsThe1120StandingWithoutAHeavyNerf()
        {
            var entry = Json("blitz_japan_1120_balance")["tanks"][0];
            Assert.AreEqual(Card, entry["name"].AsString());
            Assert.Greater(entry["competitors"].Count, 5);
            Assert.Greater(entry["competitors_1120"].Count, 5);
            var standing = entry["standing_1120"];
            var balanced = entry["standing_balanced"];
            foreach (var key in new[] { "total_health", "sustained_dpm" })
            {
                Assert.That(balanced[key].AsDouble(), Is.EqualTo(standing[key].AsDouble()).Within(0.01),
                    key + " keeps its 11.20 standing");
            }
            var hp = balanced["total_health"].AsDouble();
            var dpm = balanced["sustained_dpm"].AsDouble();
            Assert.That(hp, Is.InRange(1.0, 1.2), "still the toughest kind of heavy, as in Blitz");
            Assert.LessOrEqual(hp * dpm, 1.15, "HP x DPM budget of the 11.20 overlays");
            foreach (var key in new[] { "health", "output" })
            {
                Assert.That(entry["factors"][key].AsDouble(), Is.InRange(0.85, 1.0),
                    key + ": never buffed, never cut by more than 15 %");
            }
            Assert.AreEqual(entry["balanced_metrics"]["total_health"].AsInt(), Database().Get(Card).TotalHealth);

            // Alpha, penetration and shell speed are the gun's identity.
            var original = entry["original"]["guns"];
            var calibrated = entry["balanced"]["guns"];
            Assert.AreEqual(original.Count, calibrated.Count);
            for (var g = 0; g < original.Count; g++)
            {
                for (var s = 0; s < original[g]["shells"].Count; s++)
                {
                    foreach (var field in new[] { "dmg", "pen", "speed" })
                    {
                        Assert.AreEqual(original[g]["shells"][s][field].AsDouble(),
                            calibrated[g]["shells"][s][field].AsDouble(), field);
                    }
                }
            }
        }
    }
}
