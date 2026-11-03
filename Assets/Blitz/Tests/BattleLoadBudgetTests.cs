using System.Reflection;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The battle queue used to build the whole session inside one frame,
    /// which Android reported as an ANR (REMASTERDELUXE-2). These lock in the
    /// stepped build and the mytank highlight staying inside its column.
    /// </summary>
    public sealed class BattleLoadBudgetTests
    {
        private GameObject _root;
        private BattleWorld _world;

        [TearDown]
        public void TearDown()
        {
            if (_world != null)
            {
                Object.DestroyImmediate(_world.gameObject);
                _world = null;
            }
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        [Test]
        public void WorldBuild_StepsAcrossFramesAndMatchesTheSyncBuild()
        {
            _root = new GameObject("BattleLoadBudgetTests");
            _world = BattleWorld.Create(_root.transform, BattleWorld.DefaultMapId);

            BattleWorld stepped = null;
            var steps = BattleWorld.CreateSteps(
                _root.transform, BattleWorld.DefaultMapId, built => stepped = built, false);
            var frames = 0;
            while (steps.MoveNext())
            {
                frames++;
            }

            Assert.That(stepped, Is.Not.Null);
            Assert.That(frames, Is.GreaterThanOrEqualTo(6), "world build frames");
            Assert.That(stepped.Map.Id, Is.EqualTo(_world.Map.Id));
            Assert.That(stepped.PlayerSpawns.Count,
                Is.EqualTo(_world.PlayerSpawns.Count));
            Assert.That(stepped.BotSpawns.Count, Is.EqualTo(_world.BotSpawns.Count));
            Assert.That(stepped.AiMarkers.Count, Is.EqualTo(_world.AiMarkers.Count));
        }

        [Test]
        public void SessionBuild_YieldsAtLeastOncePerTank()
        {
            Random.InitState(982451653);
            _root = new GameObject("BattleLoadBudgetTests");

            BattleSession stepped = null;
            var steps = BattleSession.CreateSteps(
                _root.transform, false, built => stepped = built,
                BattleWorld.DefaultMapId, "t-34", false, "Tester", null, false);
            var frames = 0;
            while (steps.MoveNext())
            {
                frames++;
            }

            Assert.That(stepped, Is.Not.Null);
            Assert.That(stepped.Participants.Count, Is.EqualTo(14));
            Assert.That(stepped.Allies.Count, Is.EqualTo(7));
            Assert.That(stepped.Enemies.Count, Is.EqualTo(7));
            Assert.That(stepped.Hud, Is.Not.Null);
            Assert.That(stepped.View, Is.Not.Null);
            Assert.That(frames,
                Is.GreaterThan(stepped.Participants.Count), "session build frames");
        }

        [Test]
        public void LoadingScreen_MyTankHighlightStaysInsideItsRow()
        {
            Random.InitState(982451653);
            _root = new GameObject("BattleLoadBudgetTests");
            var session = BattleSession.Create(
                _root.transform, false, BattleWorld.DefaultMapId, "t-34", false, "Tester");

            var host = new GameObject("BattleLoadingScreen");
            host.transform.SetParent(_root.transform, false);
            var screen = host.AddComponent<BattleLoadingScreen>();
            typeof(BattleLoadingScreen)
                .GetMethod("Build", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(screen, new object[]
                {
                    session.Participants,
                    session.World == null ? null : session.World.Map,
                    false,
                    session.Mode,
                });

            var found = 0;
            var active = 0;
            var all = screen.GetComponentsInChildren<RectTransform>(true);
            for (var i = 0; i < all.Length; i++)
            {
                if (all[i].name != "mytank")
                {
                    continue;
                }

                found++;
                if (all[i].gameObject.activeSelf)
                {
                    active++;
                }

                var row = all[i].parent as RectTransform;
                Assert.That(row, Is.Not.Null, "mytank row");
                // Godot: the 872x52 strip hangs off the spacer at (-194, -6),
                // spanning the ally table art instead of stopping at the row.
                Assert.That(all[i].rect.width,
                    Is.EqualTo(BattleLoadingScreen.MyTankWidth).Within(0.01f), "mytank width");
                Assert.That(all[i].rect.height,
                    Is.EqualTo(BattleLoadingScreen.MyTankHeight).Within(0.01f), "mytank height");
                Assert.That(all[i].GetSiblingIndex(), Is.EqualTo(0), "mytank drawn first");

                var mine = new Vector3[4];
                var bounds = new Vector3[4];
                all[i].GetWorldCorners(mine);
                row.GetWorldCorners(bounds);
                var scale = row.lossyScale.x;
                var spacerX = ((RectTransform)row.Find("spacer")).anchoredPosition.x;
                Assert.That((mine[0].x - bounds[0].x) / scale,
                    Is.EqualTo(spacerX + BattleLoadingScreen.MyTankLeft).Within(0.05f),
                    "mytank left");
                Assert.That((bounds[1].y - mine[1].y) / scale,
                    Is.EqualTo(BattleLoadingScreen.MyTankTop).Within(0.05f), "mytank top");
            }

            Assert.That(found, Is.GreaterThan(0), "mytank nodes");
            Assert.That(active, Is.EqualTo(1), "active mytank");
        }

        [Test]
        public void LoadingScreen_IconsSitOnGodotsVBoxBaseline()
        {
            // Dumped from a headless layout of ui_scoreboard_loading_team.tscn.
            Assert.That(BattleLoadingScreen.IconRect(false, 124f, 48f), Is.EqualTo(new Vector2(-17f, 0f)));
            Assert.That(BattleLoadingScreen.IconRect(false, 124f, 45f), Is.EqualTo(new Vector2(-17f, 0f)));
            Assert.That(BattleLoadingScreen.IconRect(false, 124f, 40f), Is.EqualTo(new Vector2(-17f, 5f)));
            Assert.That(BattleLoadingScreen.IconRect(true, 124f, 48f), Is.EqualTo(new Vector2(-71f, -1f)));
            Assert.That(BattleLoadingScreen.IconRect(true, 124f, 40f), Is.EqualTo(new Vector2(-71f, 4f)));
        }

        [Test]
        public void LoadingScreen_RowsStackOnGodotsWholePixelPitch()
        {
            Random.InitState(982451653);
            _root = new GameObject("BattleLoadBudgetTests");
            var session = BattleSession.Create(
                _root.transform, false, BattleWorld.DefaultMapId, "t-34", false, "Tester");
            var screen = BattleLoadingScreen.Create(
                _root.transform, session.Participants, null, false, null);
            var tanks = screen.transform.Find("Canvas/authored_1040x550/teams/team_ally/tanks");
            Assert.That(tanks, Is.Not.Null, "ally list");
            var rows = 0;
            for (var i = 0; i < tanks.childCount; i++)
            {
                var row = tanks.GetChild(i) as RectTransform;
                Assert.That(-row.anchoredPosition.y,
                    Is.EqualTo(rows * BattleLoadingScreen.RowPitch).Within(0.01f), "row " + rows);
                rows++;
            }
            Assert.That(rows, Is.EqualTo(7), "ally rows");
        }

        [Test]
        public void LoadingScreen_RowsMatchGodotColumnsAndEveryTankHasAnIcon()
        {
            Random.InitState(982451653);
            _root = new GameObject("BattleLoadBudgetTests");
            var session = BattleSession.Create(
                _root.transform, false, BattleWorld.DefaultMapId, "t-34", false, "Tester");

            var host = new GameObject("BattleLoadingScreen");
            host.transform.SetParent(_root.transform, false);
            var screen = host.AddComponent<BattleLoadingScreen>();
            typeof(BattleLoadingScreen)
                .GetMethod("Build", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(screen, new object[]
                {
                    session.Participants,
                    session.World == null ? null : session.World.Map,
                    false,
                    session.Mode,
                });

            var teams = screen.transform.Find("Canvas/authored_1040x550/teams");
            Assert.That(teams, Is.Not.Null, "teams root");
            var ally = teams.Find("team_ally/tanks/tank") as RectTransform;
            var enemy = teams.Find("team_enemies/tanks/tank_reversed") as RectTransform;
            Assert.That(ally, Is.Not.Null, "ally row");
            Assert.That(enemy, Is.Not.Null, "enemy row");
            Assert.That(ally.rect.width, Is.EqualTo(622f).Within(0.01f));
            Assert.That(enemy.rect.width, Is.EqualTo(622f).Within(0.01f));

            // Actual Container positions measured from the Godot scene after
            // hidden platoon icons are removed from both HBoxContainers.
            Assert.That(((RectTransform)ally.Find("spacer")).anchoredPosition.x,
                Is.EqualTo(0f).Within(0.01f), "ally spacer");
            Assert.That(((RectTransform)ally.Find("name_player_handle")).anchoredPosition.x,
                Is.EqualTo(35f).Within(0.01f), "ally player");
            Assert.That(((RectTransform)ally.Find("spacer2")).anchoredPosition.x,
                Is.EqualTo(39f).Within(0.01f), "ally flexible gap");
            Assert.That(((RectTransform)ally.Find("name_handle")).anchoredPosition.x,
                Is.EqualTo(493f).Within(0.01f), "ally tank name");
            Assert.That(((RectTransform)ally.Find("lvl")).anchoredPosition.x,
                Is.EqualTo(561f).Within(0.01f), "ally tank level");

            Assert.That(((RectTransform)enemy.Find("lvl")).anchoredPosition.x,
                Is.EqualTo(0f).Within(0.01f), "enemy tank level");
            Assert.That(((RectTransform)enemy.Find("name_handle")).anchoredPosition.x,
                Is.EqualTo(36f).Within(0.01f), "enemy tank name");
            Assert.That(((RectTransform)enemy.Find("spacer2")).anchoredPosition.x,
                Is.EqualTo(104f).Within(0.01f), "enemy flexible gap");
            Assert.That(((RectTransform)enemy.Find("name_player_handle")).anchoredPosition.x,
                Is.EqualTo(558f).Within(0.01f), "enemy player");
            Assert.That(((RectTransform)enemy.Find("spacer")).anchoredPosition.x,
                Is.EqualTo(562f).Within(0.01f), "enemy spacer");

            var iconCount = 0;
            var images = screen.GetComponentsInChildren<Image>(true);
            for (var i = 0; i < images.Length; i++)
            {
                if (images[i].name != "tank")
                {
                    continue;
                }
                iconCount++;
                Assert.That(images[i].sprite, Is.Not.Null,
                    "loading row " + images[i].transform.parent.parent.name);
            }
            Assert.That(iconCount, Is.EqualTo(14), "one icon per participant");
        }
    }
}
