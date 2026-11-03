using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Locks the complete source-backed SmallTankIcons roster used by both the
    /// map-loading team list and the in-battle scoreboard.
    /// </summary>
    public sealed class SmallTankIconTests
    {
        [Test]
        public void EveryTankHasItsAuthoredSmallIcon()
        {
            var manifestAsset = Resources.Load<TextAsset>(
                "Data/small_tank_icons_manifest");
            Assert.That(manifestAsset, Is.Not.Null, "small icon manifest");
            var manifest = JsonNode.Parse(manifestAsset.text);
            Assert.That(manifest["generator"].AsString(),
                Is.EqualTo("Tools/port_blitz_small_tank_icons.py"));
            Assert.That(manifest["counts"]["4.8.0"].AsInt(), Is.EqualTo(342));
            Assert.That(manifest["counts"]["11.20"].AsInt(), Is.EqualTo(180));
            Assert.That(manifest["icons"].Count, Is.EqualTo(522));

            var evidence = new Dictionary<string, JsonNode>(
                StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < manifest["icons"].Count; i++)
            {
                var entry = manifest["icons"][i];
                evidence.Add(entry["tank"].AsString(), entry);
                Assert.That(entry["sourceSha256"].AsString().Length, Is.EqualTo(64));
                Assert.That(entry["outputSha256"].AsString().Length, Is.EqualTo(64));
                Assert.That(entry["width"].AsInt(), Is.GreaterThan(0));
                Assert.That(entry["height"].AsInt(), Is.GreaterThan(0));
            }

            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            var atlas = BattleFlowUi.LoadAtlas();
            Assert.That(database.Count, Is.EqualTo(522));
            for (var i = 0; i < database.Tanks.Count; i++)
            {
                var card = database.Tanks[i];
                JsonNode source;
                Assert.That(evidence.TryGetValue(card.Name, out source), Is.True,
                    card.Name + " provenance");

                var id = TankModelAssets.For(card).DataId;
                Assert.That(source["dataId"].AsString(), Is.EqualTo(id), card.Name);
                var texture = Resources.Load<Texture2D>(
                    "Textures/SmallTankIcons/" + id);
                Assert.That(texture, Is.Not.Null, card.Name + " texture");
                Assert.That(texture.width, Is.EqualTo(source["width"].AsInt()),
                    card.Name + " width");
                Assert.That(texture.height, Is.EqualTo(source["height"].AsInt()),
                    card.Name + " height");

                var sprite = atlas.Get(BattleFlowUi.SmallTankIconRegion(card));
                Assert.That(sprite, Is.Not.Null, card.Name + " region");
                Assert.That(sprite.rect.width, Is.EqualTo(texture.width).Within(0.01f),
                    card.Name + " sprite width");
                Assert.That(sprite.rect.height, Is.EqualTo(texture.height).Within(0.01f),
                    card.Name + " sprite height");
            }

            AssertDimensions(evidence, "ms-1", "4.8.0", 50, 27);
            AssertDimensions(evidence, "t49_rocket", "4.8.0", 83, 45);
            AssertDimensions(evidence, "f01_vickers_mkf", "11.20", 55, 29);
            AssertDimensions(evidence, "ch57_bzt_70", "11.20", 152, 37);
        }

        private static void AssertDimensions(
            IDictionary<string, JsonNode> evidence, string tank, string version,
            int width, int height)
        {
            var entry = evidence[tank];
            Assert.That(entry["version"].AsString(), Is.EqualTo(version), tank);
            Assert.That(entry["width"].AsInt(), Is.EqualTo(width), tank);
            Assert.That(entry["height"].AsInt(), Is.EqualTo(height), tank);
        }
    }
}
