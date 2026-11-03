using System.Collections.Generic;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Hangar;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The hangar has to paint a part with the sheet the client scene binds to
    /// it. A card whose second turret ships its own atlas (IS-3's Mod. T-10
    /// turret and all three of its guns, E-100, Panther II, Pershing ...)
    /// records that binding per node. Battle read it; the hangar painted the
    /// whole vehicle with the body atlas, so the IS-3 barrel sampled the hull
    /// sheet and showed torn paint with black patches on the turret.
    /// </summary>
    public sealed class HangarTankPaintTests
    {
        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        [Test]
        public void HangarStand_PaintsEveryPartWithItsOwnClientSheet()
        {
            var database = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));

            var checkedNodes = 0;
            foreach (var name in PerNodeAlbedoCards(database))
            {
                var card = database.Get(name);
                _root = new GameObject("HangarPaintTest");
                var world = HangarWorld.Create(
                    _root.transform, new SettingsData(), card);
                Assert.That(world, Is.Not.Null, name + " stand");
                Assert.That(world.TankModel, Is.Not.Null, name + " model");

                var assets = TankModelAssets.For(card);
                var nodes = TankNodeTable.Parse(Resources.Load<TextAsset>(
                    "Data/" + assets.NodeDataResource).text);
                var body = Resources.Load<Texture2D>(assets.BodyTextureResource);

                var renderers =
                    world.TankModel.GetComponentsInChildren<MeshRenderer>(true);
                for (var i = 0; i < renderers.Length; i++)
                {
                    var part = TankModuleView.TrimCloneSuffix(renderers[i].name);
                    if (part == "trackL" || part == "trackR" ||
                        part == "tracks_crash")
                    {
                        // The broken tracks wear the track sheet, and the
                        // hangar keeps that mesh hidden anyway.
                        continue;
                    }

                    var albedoId = nodes.AlbedoOf(part);
                    var expected = string.IsNullOrEmpty(albedoId) || albedoId == assets.Id
                        ? body
                        : Resources.Load<Texture2D>(assets.AlbedoTextureResource(albedoId));
                    Assert.That(expected, Is.Not.Null, name + "/" + part + " sheet");
                    Assert.That(
                        renderers[i].sharedMaterial.mainTexture,
                        Is.SameAs(expected),
                        name + "/" + part + " must use " + expected.name);
                    if (!string.IsNullOrEmpty(albedoId))
                    {
                        checkedNodes++;
                    }
                }

                Object.DestroyImmediate(_root);
                _root = null;
            }

            // IS-3 alone binds four sheets, so a regression that drops the
            // table cannot pass by finding nothing to check.
            Assert.That(checkedNodes, Is.GreaterThanOrEqualTo(10));
        }

        /// <summary>Cards whose client scene binds a sheet per node.</summary>
        private static IEnumerable<string> PerNodeAlbedoCards(TanksDatabase database)
        {
            var names = new List<string>();
            foreach (var card in database.Tanks)
            {
                var assets = TankModelAssets.For(card);
                var asset = Resources.Load<TextAsset>("Data/" + assets.NodeDataResource);
                if (asset == null)
                {
                    continue;
                }

                var nodes = TankNodeTable.Parse(asset.text);
                if (nodes.HasNodeAlbedos)
                {
                    names.Add(card.Name);
                }
            }
            return names;
        }
    }
}
