using System.Collections.Generic;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The observer mode: set_combat_hud_visible hides the aiming half of the
    /// HUD and brings up spectator_controls, and cycle_spectator walks the
    /// player's team with posmod, skipping the wrecks. The numbers come from
    /// the "spectator" section of
    /// Resources/Data/battle_menu_source_manifest.json, which
    /// Tools/port_godot_battle_menu.py transcribes from
    /// ui_player_battle._ensure_spectator_controls and from the death branch
    /// of player_controller._physics_process.
    /// </summary>
    public sealed class BattleSpectatorTests
    {
        private const float Tolerance = 0.0005f;

        private readonly List<GameObject> _spawned = new List<GameObject>();

        private JsonNode _manifest;

        [SetUp]
        public void SetUp()
        {
            var text = Resources.Load<TextAsset>("Data/battle_menu_source_manifest");
            Assert.IsNotNull(text, "battle menu source manifest is missing");
            _manifest = JsonNode.Parse(text.text);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawned)
            {
                if (go != null) { Object.DestroyImmediate(go); }
            }

            _spawned.Clear();
        }

        private JsonNode Spectator { get { return _manifest["spectator"]; } }

        [Test]
        public void Strip_MatchesTheSourceOffsetsSeparationAndCaptions()
        {
            var controls = Controls();
            controls.SetCombatVisible(false);

            var strip = controls.SpectatorControls;
            Assert.IsNotNull(strip, "spectator_controls was not built");
            Assert.AreEqual("spectator_controls", strip.name);

            var anchors = Spectator["anchors"];
            var offsets = Spectator["offsets"];
            var expected = GodotAnchors.Convert(
                anchors[0].AsFloat(), anchors[1].AsFloat(),
                anchors[2].AsFloat(), anchors[3].AsFloat(),
                offsets[0].AsFloat(), offsets[1].AsFloat(),
                offsets[2].AsFloat(), offsets[3].AsFloat());
            Assert.AreEqual(expected.AnchorMinX, strip.anchorMin.x, Tolerance);
            Assert.AreEqual(expected.AnchorMinY, strip.anchorMin.y, Tolerance);
            Assert.AreEqual(expected.AnchorMaxX, strip.anchorMax.x, Tolerance);
            Assert.AreEqual(expected.AnchorMaxY, strip.anchorMax.y, Tolerance);
            Assert.AreEqual(expected.OffsetMinX, strip.offsetMin.x, Tolerance);
            Assert.AreEqual(expected.OffsetMinY, strip.offsetMin.y, Tolerance);
            Assert.AreEqual(expected.OffsetMaxX, strip.offsetMax.x, Tolerance);
            Assert.AreEqual(expected.OffsetMaxY, strip.offsetMax.y, Tolerance);

            Assert.AreEqual(
                offsets[0].AsFloat(), ControlsTuning.SpectatorOffsetLeft, Tolerance);
            Assert.AreEqual(
                offsets[1].AsFloat(), ControlsTuning.SpectatorOffsetTop, Tolerance);
            Assert.AreEqual(
                offsets[2].AsFloat(), ControlsTuning.SpectatorOffsetRight, Tolerance);
            Assert.AreEqual(
                offsets[3].AsFloat(), ControlsTuning.SpectatorOffsetBottom, Tolerance);
            Assert.AreEqual(
                Spectator["separation"].AsFloat(),
                ControlsTuning.SpectatorSeparation, Tolerance);

            var button = Spectator["button"];
            Assert.AreEqual(
                button["minimumSize"][0].AsFloat(),
                ControlsTuning.SpectatorButtonWidth, Tolerance);
            Assert.AreEqual(
                button["minimumSize"][1].AsFloat(),
                ControlsTuning.SpectatorButtonHeight, Tolerance);
            Assert.AreEqual(
                button["fontSize"].AsInt(), ControlsTuning.SpectatorFontSize);

            // SIZE_EXPAND_FILL over a 132 wide strip: two buttons, one gap and
            // the leftovers split evenly, laid out left to right.
            var previous = (RectTransform)controls.SpectatorPrevious.transform;
            var next = (RectTransform)controls.SpectatorNext.transform;
            var width = ControlsTuning.SpectatorButtonWidth
                + BattleControls.SpectatorSlack();
            Assert.AreEqual(width, previous.rect.width, Tolerance);
            Assert.AreEqual(width, next.rect.width, Tolerance);
            Assert.AreEqual(
                ControlsTuning.SpectatorOffsetBottom
                    - ControlsTuning.SpectatorOffsetTop,
                previous.rect.height, Tolerance);
            Assert.AreEqual(
                width + ControlsTuning.SpectatorSeparation,
                next.anchoredPosition.x - previous.anchoredPosition.x, Tolerance);

            // tr(key + "/short") on both buttons.
            var lang = BattleFlowUi.LoadLanguage();
            Assert.AreEqual(
                lang.Get(Spectator["buttons"][0]["textKey"].AsString()),
                previous.GetComponentInChildren<Text>().text);
            Assert.AreEqual(
                lang.Get(Spectator["buttons"][1]["textKey"].AsString()),
                next.GetComponentInChildren<Text>().text);
            Assert.AreEqual(
                ControlsTuning.SpectatorFontSize,
                previous.GetComponentInChildren<Text>().fontSize);
        }

        [Test]
        public void CombatHud_SwapsTheAimingWidgetsForTheObserverStrip()
        {
            var controls = Controls();
            Assert.IsTrue(controls.CombatVisible);
            Assert.IsNull(
                controls.SpectatorControls,
                "the strip is only built when the combat HUD goes away");

            controls.SetCombatVisible(false);
            Assert.IsFalse(controls.Joystick.gameObject.activeSelf);
            Assert.IsFalse(controls.FireButton.gameObject.activeSelf);
            Assert.IsFalse(controls.SniperButton.gameObject.activeSelf);
            Assert.IsFalse(controls.AutoAimButton.gameObject.activeSelf);
            Assert.IsTrue(
                controls.MenuButton.gameObject.activeSelf,
                "the menu stays reachable while observing");
            Assert.IsTrue(controls.SpectatorControls.gameObject.activeSelf);

            Assert.IsTrue(
                Spectator["behaviour"]["touchLayerOnly"].AsBool(),
                "the strip hangs off mobile_shit");
            var desktop = Controls(mobile: false);
            desktop.SetCombatVisible(false);
            Assert.IsNull(
                desktop.SpectatorControls,
                "a desktop battle has no mobile_shit to hang the strip on");

            controls.SetCombatVisible(true);
            Assert.IsTrue(controls.Joystick.gameObject.activeSelf);
            Assert.IsTrue(controls.FireButton.gameObject.activeSelf);
            Assert.IsFalse(controls.SpectatorControls.gameObject.activeSelf);
        }

        [Test]
        public void Hud_HidesOnlyTheNodesTheSourceLists()
        {
            var hud = BattleHud.Create(null, null, true);
            _spawned.Add(hud.gameObject);

            Assert.IsTrue(hud.CombatVisible);
            hud.SetCombatVisible(false);

            var hidden = Spectator["behaviour"]["hiddenNodes"];
            var names = new List<string>();
            for (var i = 0; i < hidden.Count; i++)
            {
                names.Add(hidden[i].AsString());
            }

            Assert.Contains("crosshair_dynamic", names);
            Assert.IsFalse(hud.DynamicCrosshair.gameObject.activeSelf);
            Assert.Contains("crosshair_static_handle", names);
            Assert.IsFalse(hud.StaticCrosshair.gameObject.activeSelf);
            Assert.Contains("ui_speed", names);
            Assert.IsFalse(hud.SpeedLabel.gameObject.activeSelf);
            Assert.Contains("ui_dist", names);
            Assert.IsFalse(hud.DistanceLabel.gameObject.activeSelf);
            Assert.Contains("reload", names);
            Assert.IsFalse(hud.ReloadLabel.gameObject.activeSelf);
            Assert.Contains("hp", names);
            var health = hud.Root.Find("hp");
            Assert.IsNotNull(health, "the hp group is missing");
            Assert.IsFalse(health.gameObject.activeSelf);

            // The score, the clock and the frame counter stay up for the
            // observer, so the list is a list and not "hide everything".
            Assert.IsTrue(hud.AllyScoreLabel.gameObject.activeSelf);
            Assert.IsTrue(hud.EnemyScoreLabel.gameObject.activeSelf);
            Assert.IsTrue(hud.FpsLabel.gameObject.activeSelf);

            hud.SetCombatVisible(true);
            Assert.IsTrue(hud.DynamicCrosshair.gameObject.activeSelf);
            Assert.IsTrue(hud.SpeedLabel.gameObject.activeSelf);
        }

        [Test]
        public void Cycle_NeedsADeadTankAndWalksTheTeamWithPosmod()
        {
            var player = Tank("player", dead: false);
            var view = BattleCamera.Create(null, false);
            _spawned.Add(view.gameObject);

            var controls = Controls(player, view);
            var ally = Tank("ally", dead: false);
            var wreck = Tank("wreck", dead: true);
            var second = Tank("second", dead: false);
            var team = new List<BattleParticipantStats>
            {
                Participant(player), Participant(ally),
                Participant(wreck), Participant(second),
            };
            controls.PlayerTeam = () => team;

            // "if !tank.dead(): return": a living player observes nothing.
            Assert.IsTrue(Spectator["behaviour"]["requiresDeadTank"].AsBool());
            controls.CycleSpectator(1);
            Assert.IsNull(view.Tank);
            Assert.AreEqual(
                Spectator["behaviour"]["startIndex"].AsInt(), controls.TeamTankId);
            Assert.AreEqual(
                ControlsTuning.SpectatorStartIndex,
                Spectator["behaviour"]["startIndex"].AsInt());

            // The wreck the player is sitting in is skipped like any other, so
            // the walk starts on the first team mate that is still fighting.
            Kill(player);
            controls.CycleSpectator(1);
            Assert.AreEqual(1, controls.TeamTankId);
            Assert.AreEqual(ally, view.Tank);

            // The wreck at index two is skipped, which lands on the last one.
            controls.CycleSpectator(1);
            Assert.AreEqual(second, view.Tank);
            Assert.AreEqual(3, controls.TeamTankId);

            // posmod wraps in both directions.
            controls.CycleSpectator(1);
            Assert.AreEqual(1, controls.TeamTankId);
            Assert.AreEqual(ally, view.Tank);
            controls.CycleSpectator(-1);
            Assert.AreEqual(3, controls.TeamTankId);
            Assert.AreEqual(second, view.Tank);
            Assert.AreEqual(
                3, BattleControls.PosMod(-1, 4), "posmod never goes negative");

            // The buttons press the same two directions.
            controls.SetCombatVisible(false);
            controls.SpectatorPrevious.onClick.Invoke();
            Assert.AreEqual(
                Spectator["buttons"][0]["direction"].AsInt(), -1);
            Assert.AreEqual(ally, view.Tank);
            controls.SpectatorNext.onClick.Invoke();
            Assert.AreEqual(Spectator["buttons"][1]["direction"].AsInt(), 1);
            Assert.AreEqual(second, view.Tank);
        }

        [Test]
        public void Cycle_IgnoresATeamWithNothingLeftToWatch()
        {
            var player = Tank("player", dead: true);
            var view = BattleCamera.Create(null, false);
            _spawned.Add(view.gameObject);

            var controls = Controls(player, view);
            controls.PlayerTeam = () => new List<BattleParticipantStats>();
            controls.CycleSpectator(1);
            Assert.IsNull(view.Tank);

            var wreck = Tank("wreck", dead: true);
            var team = new List<BattleParticipantStats>
            {
                Participant(player), Participant(wreck),
            };
            controls.PlayerTeam = () => team;
            controls.CycleSpectator(1);
            Assert.IsNull(view.Tank, "an all-dead team keeps the camera put");
        }

        // ------------------------------------------------------------------

        private BattleControls Controls(
            BattleTank tank = null, BattleCamera view = null, bool mobile = true)
        {
            var controls = BattleControls.Create(tank, view, mobile);
            _spawned.Add(controls.gameObject);
            return controls;
        }

        private BattleTank Tank(string name, bool dead)
        {
            var go = new GameObject(name, typeof(BattleTank));
            _spawned.Add(go);
            var tank = go.GetComponent<BattleTank>();
            var health = new HealthComponent();
            health.MaxHealth = 100;
            if (dead) { health.Health = 0; }
            typeof(BattleTank).GetProperty("Health").SetValue(tank, health);
            return tank;
        }

        private static void Kill(BattleTank tank)
        {
            tank.Health.Health = 0;
        }

        private static BattleParticipantStats Participant(BattleTank tank)
        {
            var stats = new BattleParticipantStats();
            typeof(BattleParticipantStats).GetProperty("Tank")
                .SetValue(stats, tank);
            return stats;
        }
    }
}
