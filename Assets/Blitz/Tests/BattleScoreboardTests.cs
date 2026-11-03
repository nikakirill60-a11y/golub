using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// team_ally and team_enemy from ui_player.tscn, held against
    /// Resources/Data/battle_menu_source_manifest.json, which
    /// Tools/port_godot_battle_menu.py generates from the headless dump of
    /// ui_player_scoreboard_team.tscn and the anims_scoreboard player.
    /// </summary>
    public sealed class BattleScoreboardTests
    {
        private const float Tolerance = 0.0005f;

        private JsonNode _manifest;
        private GameObject _root;
        private BattleScoreboard _board;

        [SetUp]
        public void SetUp()
        {
            var text = Resources.Load<TextAsset>("Data/battle_menu_source_manifest");
            Assert.IsNotNull(text, "battle menu source manifest is missing");
            _manifest = JsonNode.Parse(text.text);

            _root = new GameObject("BattleScoreboardTests", typeof(RectTransform));
            _board = BattleScoreboard.Create((RectTransform)_root.transform);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }

            _board = null;

            foreach (var tank in _tanks)
            {
                if (tank != null) { Object.DestroyImmediate(tank); }
            }

            _tanks.Clear();
        }

        private JsonNode Board { get { return _manifest["scoreboard"]; } }

        [Test]
        public void Teams_MatchTheDumpedGodotGeometry()
        {
            AssertRect(_board.AllyRoot, Board["ally"]);
            AssertRect(_board.EnemyHandle, Board["enemyHandle"]);
            AssertRect(_board.EnemyRoot, Board["enemy"]);
            AssertRect((RectTransform)_board.AllyButton.transform, Board["allyButton"]);
            AssertRect((RectTransform)_board.EnemyButton.transform, Board["enemyButton"]);

            var box = Board["allyBox"];
            var ally = _board.AllyRoot.Find("team_box") as RectTransform;
            Assert.IsNotNull(ally, "team_box is missing");
            AssertRect(ally, box);
            Assert.AreEqual(box["scale"][0].AsFloat(), ally.localScale.x, Tolerance);
            Assert.AreEqual(BattleScoreboard.BoxScale, box["scale"][0].AsFloat(), Tolerance);

            Assert.AreEqual(
                Board["enemyHandle"]["size"][0].AsFloat(),
                BattleScoreboard.EnemyHandleWidth, Tolerance,
                "the enemy list is positioned inside the right-edge handle");

            // The authored offsets are the RESET pose of anims_scoreboard.
            var reset = Board["reset"]["tracks"];
            Assert.AreEqual(
                reset["team_ally:position"]["keys"][0]["value"][0].AsFloat(),
                BattleScoreboard.AllyRestX, Tolerance);
            Assert.AreEqual(
                reset["team_enemy_handle/team_enemy:position"]["keys"][0]["value"][0]
                    .AsFloat(),
                BattleScoreboard.EnemyRestX, Tolerance);
        }

        [Test]
        public void Slide_MatchesTheDumpedAnimationKeys()
        {
            var clip = Board["animation"];
            Assert.AreEqual("anims/scoreboard_slide", clip["name"].AsString());
            Assert.AreEqual(
                clip["length"].AsFloat(), BattleScoreboard.SlideSeconds, Tolerance);

            var ally = clip["tracks"]["team_ally:position"]["keys"];
            Assert.AreEqual(
                ally[0]["value"][0].AsFloat(), BattleScoreboard.AllyClosedX, Tolerance);
            Assert.AreEqual(
                ally[1]["value"][0].AsFloat(), BattleScoreboard.AllyOpenX, Tolerance);
            Assert.AreEqual(
                ally[0]["transition"].AsFloat(), BattleScoreboard.SlideTransition,
                Tolerance);

            var enemy = clip["tracks"]["team_enemy_handle/team_enemy:position"]["keys"];
            Assert.AreEqual(
                enemy[0]["value"][0].AsFloat(), BattleScoreboard.EnemyClosedX, Tolerance);
            Assert.AreEqual(
                enemy[1]["value"][0].AsFloat(), BattleScoreboard.EnemyOpenX, Tolerance);
        }

        [Test]
        public void Toggle_SlidesBothListsAndResumesWhereItWasCaught()
        {
            Assert.IsFalse(_board.IsOpen);
            Assert.AreEqual(0f, _board.Playhead, Tolerance);

            _board.Toggle();
            _board.Advance(BattleScoreboard.SlideSeconds);
            Assert.IsTrue(_board.IsOpen);
            Assert.AreEqual(1f, _board.Playhead, Tolerance);
            Assert.AreEqual(
                BattleScoreboard.AllyOpenX, _board.AllyRoot.offsetMin.x, Tolerance);
            Assert.AreEqual(
                BattleScoreboard.EnemyOpenX - BattleScoreboard.EnemyHandleWidth,
                _board.EnemyRoot.offsetMin.x, Tolerance);

            // play_backwards resumes from the playhead instead of restarting.
            _board.Toggle();
            _board.Advance(BattleScoreboard.SlideSeconds * 0.5f);
            Assert.AreEqual(0.5f, _board.Playhead, Tolerance);
            _board.Toggle();
            _board.Advance(BattleScoreboard.SlideSeconds * 0.5f);
            Assert.AreEqual(1f, _board.Playhead, Tolerance);

            _board.Toggle();
            _board.Advance(BattleScoreboard.SlideSeconds);
            Assert.IsFalse(_board.IsOpen);
            Assert.AreEqual(
                BattleScoreboard.AllyClosedX, _board.AllyRoot.offsetMin.x, Tolerance);
            Assert.AreEqual(
                BattleScoreboard.EnemyClosedX - BattleScoreboard.EnemyHandleWidth,
                _board.EnemyRoot.offsetMin.x, Tolerance);
        }

        [Test]
        public void TeamButtons_RaiseTheSameToggle()
        {
            _board.AllyButton.onClick.Invoke();
            Assert.IsTrue(_board.IsOpen);
            _board.EnemyButton.onClick.Invoke();
            Assert.IsFalse(_board.IsOpen);
            Assert.IsTrue(Board["behaviour"]["toggledByTeamButtons"].AsBool());
        }

        [Test]
        public void Rows_FollowTheRosterAndTheTemplateGeometry()
        {
            var roster = Roster();
            _board.SetRoster(roster, 0);

            Assert.AreEqual(2, _board.AllyRowCount);
            Assert.AreEqual(1, _board.EnemyRowCount);

            var box = _board.AllyRoot.Find("team_box");
            var first = (RectTransform)box.GetChild(0);
            var second = (RectTransform)box.GetChild(1);
            var template = Board["row"]["root"];
            Assert.AreEqual(
                template["size"][0].AsFloat(), first.sizeDelta.x, Tolerance);
            Assert.AreEqual(
                template["size"][1].AsFloat(), first.sizeDelta.y, Tolerance);

            // VBoxContainer stacking: row height plus the default separation.
            var step = second.anchoredPosition.y - first.anchoredPosition.y;
            Assert.AreEqual(
                -(BattleScoreboard.RowHeight + BattleScoreboard.BoxSeparation),
                step, Tolerance);

            // The nickname is clipped the way the source clips it.
            var name = first.Find("name_ply").GetComponent<UnityEngine.UI.Text>();
            Assert.AreEqual(
                Board["behaviour"]["nameLimit"].AsInt(), BattleScoreboard.NameLimit);
            Assert.AreEqual(
                new string('x', BattleScoreboard.NameLimit) + "...", name.text);

            // kills stay hidden until the player scores one.
            var kills = first.Find("kills").gameObject;
            Assert.IsFalse(kills.activeSelf);
        }

        [Test]
        public void Rows_UseTheDumpedBackgroundsAndHighlightThePlayer()
        {
            var atlas = BattleFlowUi.LoadAtlas();
            var textures = Board["textures"];
            foreach (var pair in textures.Fields)
            {
                var sprite = atlas.Get(pair.Key);
                Assert.IsNotNull(sprite, pair.Key);
                Assert.AreEqual(
                    pair.Value["region"][2].AsFloat(), sprite.rect.width, Tolerance);
                Assert.AreEqual(
                    pair.Value["region"][3].AsFloat(), sprite.rect.height, Tolerance);
            }

            _board.SetRoster(Roster(), 0);
            var box = _board.AllyRoot.Find("team_box");
            var player = box.GetChild(0).GetComponent<UnityEngine.UI.Image>();
            var ally = box.GetChild(1).GetComponent<UnityEngine.UI.Image>();
            Assert.AreEqual(
                atlas.Get("battle_scoreboard_row_me").rect, player.sprite.rect,
                "the local player's row uses TankIconBG");
            Assert.AreEqual(
                atlas.Get("battle_scoreboard_row").rect, ally.sprite.rect,
                "every other row uses TankIconBG02");
        }

        [Test]
        public void TeamButton_CoversEveryRowTheListShows()
        {
            var button = (RectTransform)_board.AllyButton.transform;
            Assert.AreEqual(
                BattleScoreboard.ButtonHeight, button.rect.height, Tolerance,
                "an empty list keeps the authored click area");

            _board.SetRoster(Roster(), 0);

            var box = _board.AllyRoot.Find("team_box");
            var last = (RectTransform)box.GetChild(_board.AllyRowCount - 1);
            var buttonCorners = new Vector3[4];
            var rowCorners = new Vector3[4];
            button.GetWorldCorners(buttonCorners);
            last.GetWorldCorners(rowCorners);

            // A row that hangs below ButtonCustom hands its tap to the touch
            // surface under the board instead of toggling the lists.
            Assert.LessOrEqual(
                buttonCorners[0].y, rowCorners[0].y + Tolerance,
                "the click area stops above the last row it lists");
            Assert.AreEqual(
                BattleScoreboard.ButtonRight - BattleScoreboard.ButtonLeft,
                button.rect.width, Tolerance,
                "the click area keeps the authored width");
        }

        [Test]
        public void DeadRows_FadeAndSinkToTheEndOfTheBox()
        {
            var roster = Roster();
            _board.SetRoster(roster, 0);

            var box = _board.AllyRoot.Find("team_box");
            var playerRow = box.GetChild(0);
            Assert.AreEqual(1f, playerRow.GetComponent<CanvasGroup>().alpha, Tolerance);

            // A row with no live tank behind it reads as destroyed, which is
            // what the participant list looks like once a tank is gone.
            SetStat(roster[0], "Tank", null);
            _board.Refresh();

            Assert.AreEqual(
                Board["behaviour"]["deadRowAlpha"].AsFloat(),
                playerRow.GetComponent<CanvasGroup>().alpha, Tolerance);
            Assert.AreEqual(
                _board.AllyRowCount - 1, playerRow.GetSiblingIndex(),
                "team_box.move_child(row, -1) sinks a destroyed row");
        }

        // ------------------------------------------------------------------

        private static readonly List<GameObject> _tanks = new List<GameObject>();

        private static BattleParticipantStats[] Roster()
        {
            return new[]
            {
                Participant("xxxxxxxxxxxxxxxxxxxx", 0, true),
                Participant("Ally", 0, false),
                Participant("Enemy", 1, false),
            };
        }

        private static BattleParticipantStats Participant(
            string nickname, int team, bool isPlayer)
        {
            var stats = new BattleParticipantStats();
            SetStat(stats, "Nickname", nickname);
            SetStat(stats, "Team", team);
            SetStat(stats, "IsPlayer", isPlayer);

            // A live participant still owns its tank; a row reads the tank to
            // tell a fighting crew from a destroyed one.
            var host = new GameObject("tank", typeof(BattleTank));
            _tanks.Add(host);
            SetStat(stats, "Tank", host.GetComponent<BattleTank>());
            return stats;
        }

        /// <summary>The stat setters are internal to the runtime assembly.</summary>
        private static void SetStat(
            BattleParticipantStats stats, string property, object value)
        {
            typeof(BattleParticipantStats)
                .GetProperty(property).SetValue(stats, value);
        }

        private static void AssertRect(RectTransform rt, JsonNode node)
        {
            Assert.IsNotNull(rt, "missing control for " + node["path"].AsString());
            var anchors = node["anchors"];
            var offsets = node["offsets"];
            var expected = GodotAnchors.Convert(
                anchors[0].AsFloat(), anchors[1].AsFloat(),
                anchors[2].AsFloat(), anchors[3].AsFloat(),
                offsets[0].AsFloat(), offsets[1].AsFloat(),
                offsets[2].AsFloat(), offsets[3].AsFloat());

            var path = node["path"].AsString();
            Assert.AreEqual(expected.AnchorMinX, rt.anchorMin.x, Tolerance, path);
            Assert.AreEqual(expected.AnchorMinY, rt.anchorMin.y, Tolerance, path);
            Assert.AreEqual(expected.AnchorMaxX, rt.anchorMax.x, Tolerance, path);
            Assert.AreEqual(expected.AnchorMaxY, rt.anchorMax.y, Tolerance, path);
            Assert.AreEqual(expected.OffsetMinX, rt.offsetMin.x, Tolerance, path);
            Assert.AreEqual(expected.OffsetMinY, rt.offsetMin.y, Tolerance, path);
            Assert.AreEqual(expected.OffsetMaxX, rt.offsetMax.x, Tolerance, path);
            Assert.AreEqual(expected.OffsetMaxY, rt.offsetMax.y, Tolerance, path);
        }
    }
}
