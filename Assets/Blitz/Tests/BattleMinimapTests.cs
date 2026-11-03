using System.Collections.Generic;
using System.Linq;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// minimap_handle of ui_player.tscn and the minimap code of
    /// ui_player_battle.gd, held against
    /// Resources/Data/battle_minimap_source_manifest.json
    /// (Tools/port_battle_minimap.py) and the minimapArea / respawn data of
    /// every map manifest.
    /// </summary>
    public sealed class BattleMinimapTests
    {
        private const float Tolerance = 0.0005f;

        private static readonly string[] MapIds = { "rudniki", "ordeal", "karieri", "lake", "asia", "moon" };

        private readonly List<Object> _owned = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var item in _owned)
            {
                if (item != null) { Object.DestroyImmediate(item); }
            }
            _owned.Clear();
        }

        private static JsonNode Manifest()
        {
            var text = Resources.Load<TextAsset>("Data/battle_minimap_source_manifest");
            Assert.IsNotNull(text, "battle minimap source manifest is missing");
            return JsonNode.Parse(text.text);
        }

        private static void AssertControl(JsonNode node, float left, float top, float right, float bottom, float scale)
        {
            Assert.AreEqual(left, node["offsets"][0].AsFloat(), Tolerance);
            Assert.AreEqual(top, node["offsets"][1].AsFloat(), Tolerance);
            Assert.AreEqual(right, node["offsets"][2].AsFloat(), Tolerance);
            Assert.AreEqual(bottom, node["offsets"][3].AsFloat(), Tolerance);
            Assert.AreEqual(scale, node["scale"][0].AsFloat(), Tolerance);
            Assert.AreEqual(scale, node["scale"][1].AsFloat(), Tolerance);
        }

        [Test]
        public void Constants_MatchTheGodotSources()
        {
            var manifest = Manifest();
            AssertControl(manifest["handle"], BattleMinimap.HandleLeft, BattleMinimap.HandleTop,
                BattleMinimap.HandleRight, BattleMinimap.HandleBottom, 1f);
            AssertControl(manifest["minimap"], 0f, 0f, BattleMinimap.MapControlSize,
                BattleMinimap.MapControlSize, BattleMinimap.MapScale);
            AssertControl(manifest["dmgLog"], BattleMinimap.DmgLogLeft, BattleMinimap.DmgLogTop,
                BattleMinimap.DmgLogRight, BattleMinimap.DmgLogBottom, 1f);
            Assert.AreEqual(0.5f, manifest["dmgLog"]["anchors"][1].AsFloat(), Tolerance);
            Assert.AreEqual(0.5f, manifest["dmgLog"]["anchors"][3].AsFloat(), Tolerance);
            AssertControl(manifest["dmgIcon"], BattleMinimap.DmgIconLeft, BattleMinimap.DmgIconTop,
                BattleMinimap.DmgIconRight, BattleMinimap.DmgIconBottom, BattleMinimap.DmgIconScale);
            AssertControl(manifest["blockIcon"], BattleMinimap.BlockIconLeft, BattleMinimap.BlockIconTop,
                BattleMinimap.BlockIconRight, BattleMinimap.BlockIconBottom, BattleMinimap.BlockIconScale);

            var behaviour = manifest["behaviour"];
            Assert.AreEqual(BattleMinimap.MissileScale, behaviour["missileScale"].AsFloat(), Tolerance);
            Assert.AreEqual(BattleMinimap.DestroyerScale, behaviour["destroyerScale"].AsFloat(), Tolerance);
            Assert.AreEqual(BattleMinimap.GhostLighten, behaviour["ghostLighten"].AsFloat(), Tolerance);
            Assert.AreEqual(BattleMinimap.GhostDim, behaviour["ghostDim"].AsFloat(), Tolerance);
            Assert.AreEqual(BattleMinimap.DeathDim, behaviour["deathDim"].AsFloat(), Tolerance);
            Assert.AreEqual(BattleMinimap.DeathFadeSeconds, behaviour["deathFadeSeconds"].AsFloat(), Tolerance);
            Assert.AreEqual(BattleMinimap.PlayerRotationOffset, behaviour["playerRotationOffset"].AsFloat(), Tolerance);
            Assert.AreEqual(BlitzLegacy.Core.Ui.HudTuning.MinimapUpdateInterval,
                behaviour["updateSeconds"].AsFloat(), Tolerance);

            var ally = manifest["colours"]["ally"];
            var enemy = manifest["colours"]["enemy"];
            for (var i = 0; i < 4; i++)
            {
                Assert.AreEqual(BattleMinimap.AllyColor[i], ally[i].AsFloat(), Tolerance);
                Assert.AreEqual(BattleMinimap.EnemyColor[i], enemy[i].AsFloat(), Tolerance);
            }
        }

        [Test]
        public void HandleSlide_MatchesTheScoreboardAnimationTrack()
        {
            var text = Resources.Load<TextAsset>("Data/battle_menu_source_manifest");
            var keys = JsonNode.Parse(text.text)["scoreboard"]["animation"]["tracks"]["minimap_handle:position"]["keys"];
            Assert.AreEqual(2, keys.Count);
            Assert.AreEqual(0f, keys[0]["time"].AsFloat(), Tolerance);
            Assert.AreEqual(BattleMinimap.HandleLeft, keys[0]["value"][0].AsFloat(), Tolerance);
            Assert.AreEqual(BattleMinimap.HandleShownY, keys[0]["value"][1].AsFloat(), Tolerance);
            Assert.AreEqual(BattleMinimap.HandleTransition, keys[0]["transition"].AsFloat(), Tolerance);
            Assert.AreEqual(BattleMinimap.HandleSlideSeconds, keys[1]["time"].AsFloat(), Tolerance);
            Assert.AreEqual(BattleMinimap.HandleParkedY, keys[1]["value"][1].AsFloat(), Tolerance);

            // Lists folded (Tab): the minimap is down. Lists out: parked above.
            Assert.AreEqual(0f, BattleMinimap.HandleY(0f), Tolerance);
            Assert.AreEqual(-216f, BattleMinimap.HandleY(1f), Tolerance);
            Assert.AreEqual(-216f, BattleMinimap.HandleY(0.5f), Tolerance);
            var half = BattleMinimap.HandleSlideSeconds * 0.5f / BattleScoreboard.SlideSeconds;
            Assert.AreEqual(-216f * Mathf.Pow(0.5f, BattleMinimap.HandleTransition),
                BattleMinimap.HandleY(half), 0.01f);
        }

        [Test]
        public void AtlasRegions_AreTheGodotIconRects()
        {
            var manifest = Manifest();
            var table = JsonNode.Parse(BlitzResources.LoadText(BlitzResources.AtlasRegionsData))["regions"];
            var atlas = BlitzLegacy.Runtime.Ui.BattleFlowUi.LoadAtlas();
            var names = new List<string>
            {
                "battle_minimap_player", "battle_minimap_look_direction", "battle_rocket_marker",
                BattleMinimap.ClassRegion(TankClass.Light), BattleMinimap.ClassRegion(TankClass.Medium),
                BattleMinimap.ClassRegion(TankClass.Heavy), BattleMinimap.ClassRegion(TankClass.TankDestroyer),
            };
            for (var n = 1; n <= BattleMinimap.RespawnSprites; n++)
            {
                names.Add(BattleMinimap.RespawnRegion(true, n));
                names.Add(BattleMinimap.RespawnRegion(false, n));
            }
            foreach (var name in names)
            {
                var rect = manifest["regions"][name]["rect"];
                Assert.IsNotNull(rect, name + " is not in the source manifest");
                Assert.AreEqual(rect[0].AsFloat(), table[name]["x"].AsFloat(), Tolerance, name);
                Assert.AreEqual(rect[1].AsFloat(), table[name]["y"].AsFloat(), Tolerance, name);
                Assert.AreEqual(rect[2].AsFloat(), table[name]["w"].AsFloat(), Tolerance, name);
                Assert.AreEqual(rect[3].AsFloat(), table[name]["h"].AsFloat(), Tolerance, name);
                Assert.IsNotNull(atlas.Get(name), name + " does not load");
            }
            // Godot's own pair, MinimapRespawn and MinimapRespawn05.
            StringAssert.EndsWith("parsed/MinimapRespawn.tres",
                manifest["regions"]["battle_minimap_respawn_ally_1"]["source"].AsString());
            StringAssert.EndsWith("parsed/MinimapRespawn05.tres",
                manifest["regions"]["battle_minimap_respawn_enemy_1"]["source"].AsString());
            Assert.AreEqual(BattleMinimap.RespawnRegion(true, 4), BattleMinimap.RespawnRegion(true, 9));
        }

        [Test]
        public void EveryMap_KnowsTheGroundUnderItsMinimap()
        {
            foreach (var id in MapIds)
            {
                var map = BattleWorld.LoadDefinition(id);
                var area = map.MinimapArea;
                Assert.IsNotNull(area, id + " has no minimapArea");
                var texture = Resources.Load<Texture2D>(map.Minimap);
                Assert.IsNotNull(texture, id + " minimap texture");
                Assert.AreEqual(512, texture.width, id);
                Assert.AreEqual(512, texture.height, id);
                Assert.LessOrEqual(area.Frame[0] + area.Frame[2], texture.width, id);
                Assert.LessOrEqual(area.Frame[1] + area.Frame[3], texture.height, id);

                if (id == "rudniki")
                {
                    // Godot: the alpha used rect of rudniki_Out.png over the
                    // map_border_min/max markers.
                    CollectionAssert.AreEqual(new[] { 0, 0, 328, 328 }, area.Frame);
                    Assert.AreEqual(map.BorderMin[0], area.Min[0], Tolerance);
                    Assert.AreEqual(map.BorderMin[2], area.Min[1], Tolerance);
                    Assert.AreEqual(map.BorderMax[0], area.Max[0], Tolerance);
                    Assert.AreEqual(map.BorderMax[2], area.Max[1], Tolerance);
                }
                else
                {
                    // 4.8.0: MiniMapSmall.txt frame over the whole landscape;
                    // 11.20 (moon): the alpha box of MiniMapSmall@2x, drawn
                    // at the 4.8.0 frame's size.
                    var side = id == "moon" ? 505 : 326;
                    CollectionAssert.AreEqual(new[] { 0, 0, side, side }, area.Frame, id);
                    if (id == "moon")
                    {
                        CollectionAssert.AreEqual(new[] { 326, 326 }, area.Size, id);
                    }
                    else
                    {
                        Assert.IsNull(area.Size, id);
                    }
                    Assert.AreEqual(map.Terrain.Origin[0], area.Min[0], Tolerance, id);
                    Assert.AreEqual(map.Terrain.Origin[2], area.Min[1], Tolerance, id);
                    Assert.AreEqual(map.Terrain.Origin[0] + map.Terrain.Extent, area.Max[0], Tolerance, id);
                    Assert.AreEqual(map.Terrain.Origin[2] + map.Terrain.Extent, area.Max[1], Tolerance, id);
                }

                var corner = BattleMinimap.Project(area, area.Min[0], area.Min[1]);
                Assert.AreEqual(0f, corner.x, Tolerance);
                Assert.AreEqual(0f, corner.y, Tolerance);
                corner = BattleMinimap.Project(area, area.Max[0], area.Max[1]);
                Assert.AreEqual(area.Width, corner.x, Tolerance);
                Assert.AreEqual(area.Height, corner.y, Tolerance);

                foreach (var spawn in map.TeamA.Concat(map.TeamB))
                {
                    Assert.GreaterOrEqual(spawn.Respawn, 1, id + " " + spawn.Name);
                    Assert.LessOrEqual(spawn.Respawn, BattleMinimap.RespawnSprites, id + " " + spawn.Name);
                    var point = BattleMinimap.Project(area, spawn.Origin[0], spawn.Origin[2]);
                    Assert.That(point.x, Is.InRange(0f, area.Width), id + " " + spawn.Name);
                    Assert.That(point.y, Is.InRange(0f, area.Height), id + " " + spawn.Name);
                }
            }
        }

        [Test]
        public void SpawnAreas_AreNumberedPerTeam()
        {
            // Lost Temple keeps two of its three authored areas per team.
            var asia = Build("asia", 0);
            Assert.AreEqual(4, asia.SpawnIconCount);
            var regions = new List<string>();
            for (var i = 0; i < asia.SpawnIconCount; i++) { regions.Add(asia.SpawnRegionAt(i)); }
            CollectionAssert.AreEquivalent(new[]
            {
                "battle_minimap_respawn_ally_1", "battle_minimap_respawn_ally_2",
                "battle_minimap_respawn_enemy_1", "battle_minimap_respawn_enemy_2",
            }, regions);

            // Seen from team B the colours swap, the numbers stay.
            var swapped = Build("asia", 1);
            Assert.AreEqual("battle_minimap_respawn_enemy_1", swapped.SpawnRegionAt(0));
            Assert.AreEqual("battle_minimap_respawn_ally_1", swapped.SpawnRegionAt(2));

            // Rudniki: Godot's single averaged pair.
            var map = BattleWorld.LoadDefinition("rudniki");
            var rudniki = Build("rudniki", 0);
            Assert.AreEqual(2, rudniki.SpawnIconCount);
            Assert.AreEqual("battle_minimap_respawn_ally_1", rudniki.SpawnRegionAt(0));
            Assert.AreEqual("battle_minimap_respawn_enemy_1", rudniki.SpawnRegionAt(1));
            float x = 0f, z = 0f;
            foreach (var spawn in map.TeamA) { x += spawn.Origin[0]; z += spawn.Origin[2]; }
            var expected = BattleMinimap.Project(map.MinimapArea, x / map.TeamA.Count, z / map.TeamA.Count);
            var actual = BattleMinimap.AnchorOf(rudniki.SpawnIconAt(0));
            Assert.AreEqual(expected.x, actual.x, 0.01f);
            Assert.AreEqual(expected.y, actual.y, 0.01f);
        }

        [Test]
        public void Widget_FollowsTheGodotLayout()
        {
            var minimap = Build("rudniki", 0);
            Assert.AreEqual(new Vector2(BattleMinimap.HandleLeft, 0f), minimap.Handle.anchoredPosition);
            Assert.AreEqual(new Vector2(328f, 328f), minimap.Map.rect.size);
            Assert.AreEqual(BattleMinimap.MapScale, minimap.Map.localScale.x, Tolerance);
            Assert.IsNotNull(minimap.Map.GetComponent<RectMask2D>(), "clip_contents");
            Assert.IsNotNull(minimap.Handle.Find("dmg_log/dico"));
            Assert.IsNotNull(minimap.Handle.Find("dmg_log/bico"));

            var playhead = 1f;
            minimap.Playhead = () => playhead;
            minimap.FollowScoreboard();
            Assert.AreEqual(216f, minimap.Handle.anchoredPosition.y, Tolerance);
            playhead = 0f;
            minimap.FollowScoreboard();
            Assert.AreEqual(0f, minimap.Handle.anchoredPosition.y, Tolerance);
        }

        [Test]
        public void EveryClientMinimap_ShowsAtOneSize_Whole()
        {
            // The 11.20 retina minimap (moon) paints 505 px where the 4.8.0
            // client maps paint 326: it shows at their size, its whole frame
            // is drawn and the ground corners still land on the frame corners.
            var reference = Build("lake", 0);
            var expected = Vector2.Scale(reference.Map.rect.size, reference.Map.localScale);
            foreach (var id in new[] { "ordeal", "karieri", "asia", "moon" })
            {
                var minimap = Build(id, 0);
                var shown = Vector2.Scale(minimap.Map.rect.size, minimap.Map.localScale);
                Assert.AreEqual(expected.x, shown.x, Tolerance, id);
                Assert.AreEqual(expected.y, shown.y, Tolerance, id);

                var area = minimap.Area;
                var sprite = minimap.Map.GetComponent<Image>().sprite;
                Assert.AreEqual(area.Frame[2], sprite.rect.width, Tolerance, id);
                Assert.AreEqual(area.Frame[3], sprite.rect.height, Tolerance, id);

                var far = BattleMinimap.Project(area, area.Max[0], area.Max[1]);
                Assert.AreEqual(minimap.Map.rect.width, far.x, Tolerance, id);
                Assert.AreEqual(minimap.Map.rect.height, far.y, Tolerance, id);
            }

            var moon = Build("moon", 0);
            Assert.AreEqual(new Vector2(326f, 326f), moon.Map.rect.size);
            Assert.AreEqual(BattleMinimap.MapScale, moon.Map.localScale.x, Tolerance);
        }

        [Test]
        public void Tanks_PlayerAndCamera_AreTrackedLikeGodot()
        {
            var map = BattleWorld.LoadDefinition("rudniki");
            var host = new GameObject("MinimapTanks");
            _owned.Add(host);
            var db = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            var player = Spawn(host, db, "t-34", 0, new Vector3(300f, 50f, -300f));
            var ally = Spawn(host, db, "ms-1", 0, new Vector3(200f, 50f, -250f));
            var enemy = Spawn(host, db, "is-7", 1, new Vector3(400f, 50f, -450f));
            var destroyer = Spawn(host, db, "su-100", 1, new Vector3(150f, 50f, -500f));

            var minimap = Build("rudniki", 0, false);
            minimap.ViewForward = () => Vector3.right;
            minimap.Bind(player, 0, new[] { player, ally, enemy, destroyer });
            Assert.AreEqual(3, minimap.TankMarkerCount, "every tank but the player");
            Assert.IsNull(minimap.MarkerOf(player));

            var expected = BattleMinimap.Project(map.MinimapArea, 200f, 250f);
            var anchor = BattleMinimap.AnchorOf(minimap.MarkerOf(ally));
            Assert.AreEqual(expected.x, anchor.x, 0.01f);
            Assert.AreEqual(expected.y, anchor.y, 0.01f);
            Assert.IsTrue(minimap.MarkerOf(ally).gameObject.activeSelf);
            Assert.AreEqual(BattleMinimap.AllyColor, minimap.MarkerColour(ally));
            Assert.AreEqual(BattleMinimap.EnemyColor, minimap.MarkerColour(enemy));
            Assert.AreEqual(28f * BattleMinimap.DestroyerScale, minimap.MarkerOf(destroyer).sizeDelta.x, Tolerance);

            // The arrow's texture points right and the cone's up; the tank
            // looks along +Z (map up), the camera along +X (map right).
            Assert.AreEqual(90f, Mathf.DeltaAngle(0f, minimap.PlayerArrow.localEulerAngles.z), 0.01f);
            Assert.AreEqual(-90f, Mathf.DeltaAngle(0f, minimap.LookDirection.localEulerAngles.z), 0.01f);
            Assert.AreEqual(new Vector2(0.5f, 0f), minimap.LookDirection.pivot);
            var playerAnchor = BattleMinimap.Project(map.MinimapArea, 300f, 300f);
            Assert.AreEqual(playerAnchor.x, BattleMinimap.AnchorOf(minimap.PlayerArrow).x, 0.01f);
            Assert.AreEqual(playerAnchor.y, BattleMinimap.AnchorOf(minimap.LookDirection).y, 0.01f);

            // Godot: an enemy appears only while spotted and leaves a dimmed
            // copy at its last drawn position.
            Assert.IsFalse(minimap.MarkerOf(enemy).gameObject.activeSelf);
            enemy.SetSpotted(true);
            minimap.Tick(0f);
            Assert.IsTrue(minimap.MarkerOf(enemy).gameObject.activeSelf);
            var seen = minimap.MarkerOf(enemy).anchoredPosition;
            enemy.transform.position = new Vector3(100f, 50f, -100f);
            enemy.SetSpotted(false);
            minimap.Tick(0f);
            Assert.IsFalse(minimap.MarkerOf(enemy).gameObject.activeSelf);
            Assert.IsTrue(minimap.GhostOf(enemy).gameObject.activeSelf);
            Assert.AreEqual(seen, minimap.GhostOf(enemy).anchoredPosition);
            Assert.AreEqual(BattleMinimap.GhostColor(BattleMinimap.EnemyColor),
                minimap.GhostOf(enemy).GetComponent<Image>().color);
            var ghost = BattleMinimap.GhostColor(BattleMinimap.EnemyColor);
            Assert.AreEqual(0.49f, ghost.r, Tolerance);
            Assert.AreEqual(0.25f, ghost.g, Tolerance);
            Assert.AreEqual(1f, ghost.a, Tolerance);

            // on_death: half the tint, then a one second fade and hide.
            ally.Health.ReceiveDamageBasic(1000000);
            minimap.Tick(0f);
            var dim = minimap.MarkerColour(ally);
            Assert.AreEqual(BattleMinimap.AllyColor.g * BattleMinimap.DeathDim, dim.g, Tolerance);
            Assert.AreEqual(1f, dim.a, Tolerance);
            minimap.Tick(0.5f);
            Assert.AreEqual(0.5f, minimap.MarkerColour(ally).a, 0.01f);
            minimap.Tick(0.6f);
            Assert.IsFalse(minimap.MarkerOf(ally).gameObject.activeSelf);
        }

        private static BattleTank Spawn(GameObject host, TanksDatabase db, string id, int team, Vector3 position)
        {
            var tank = BattleTank.Spawn(host.transform, id, db.Get(id), host.transform,
                new Bounds(Vector3.zero, new Vector3(2000f, 200f, 2000f)), team);
            tank.transform.position = position;
            tank.transform.rotation = Quaternion.identity;
            return tank;
        }

        private BattleMinimap Build(string mapId, int playerTeam, bool bind = true)
        {
            var root = new GameObject("MinimapRoot", typeof(RectTransform));
            _owned.Add(root);
            var minimap = BattleMinimap.Create((RectTransform)root.transform, BattleWorld.LoadDefinition(mapId));
            Assert.IsNotNull(minimap, mapId);
            minimap.Playhead = () => 0f;
            minimap.FollowScoreboard();
            if (bind) { minimap.Bind(null, playerTeam, null); }
            return minimap;
        }
    }
}
