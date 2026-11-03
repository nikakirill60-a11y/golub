using System;
using BlitzLegacy.Runtime;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Battle.CombatFx;
using BlitzLegacy.Runtime.Battle.WorldOfDucks;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Platform;
using BlitzLegacy.Runtime.Ui;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BlitzLegacy.EditorTools
{
    /// <summary>Play Mode gate for the code-first 2v2 battle slice.</summary>
    [InitializeOnLoad]
    public static class BattleScreenSmoke
    {
        private const string ActiveKey = "Blitz.BattleSmoke.Active";
        private const string ExitCodeKey = "Blitz.BattleSmoke.ExitCode";
        private const string DeadlineKey = "Blitz.BattleSmoke.Deadline";
        private static bool _hooked;
        private static BattleSession _session;
        private static int _healthBefore;
        private static DateTime _shotDeadline;
        private static BattleTankPlate _targetPlate;
        private static BattleGpuTrail _trail;
        private static bool _trailObservedSegments;
        private static Vector3[] _botPositionsBefore;
        private static Quaternion[] _botTurretsBefore;
        private static DateTime _botMotionDeadline;
        private static bool _botMotionObserved;
        private static int _damageInfoCountBefore;

        static BattleScreenSmoke()
        {
            if (SessionState.GetBool(ActiveKey, false))
            {
                Hook();
            }
        }

        /// <summary>BLITZ_SMOKE_MODE=world_of_ducks: the same gate on the War of Ducks.</summary>
        private static bool Ducks
        {
            get { return Environment.GetEnvironmentVariable("BLITZ_SMOKE_MODE") == "world_of_ducks"; }
        }

        public static void Run()
        {
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetInt(ExitCodeKey, -1);
            SessionState.SetString(
                DeadlineKey, DateTime.UtcNow.AddMinutes(5).ToString("O"));
            Hook();
            try
            {
                EditorSceneManager.OpenScene(
                    SceneGen.BootstrapScenePath, OpenSceneMode.Single);
                EditorApplication.isPlaying = true;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Quit(1);
            }
        }

        private static void Hook()
        {
            if (_hooked) { return; }
            _hooked = true;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += OnEditorUpdate;
        }

        private static void Unhook()
        {
            if (!_hooked) { return; }
            _hooked = false;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.update -= OnEditorUpdate;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(ActiveKey, false)) { return; }
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                // The matchmaker draws the enemy line-up from the live random
                // source, so an unseeded run staged its shot against whatever
                // tank it happened to roll and failed on the ones whose gun
                // mantlet covers the aim point. One seed per run keeps the
                // whole path - roster, loadouts, dispersion - reproducible.
                UnityEngine.Random.InitState(12021995);
                EditorApplication.delayCall += Prepare;
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                var exitCode = SessionState.GetInt(ExitCodeKey, -1);
                if (exitCode >= 0) { Quit(exitCode); }
            }
        }

        /// <summary>BLITZ_SMOKE_MOBILE=1 builds the phone control layer.</summary>
        private static bool Mobile
        {
            get
            {
                return Environment.GetEnvironmentVariable("BLITZ_SMOKE_MOBILE") == "1";
            }
        }

        /// <summary>
        /// Walks the widgets the phone build shows. Their geometry comes from
        /// Godot offsets against the battle canvas, so a wrong reference
        /// resolution pushes them off the screen instead of failing loudly.
        /// </summary>
        private static void RequireMobileLayer(BattleControls controls)
        {
            if (!Mobile)
            {
                return;
            }

            Require(controls.Joystick != null, "The mobile joystick is missing");
            Require(controls.FireButton != null, "The fire button is missing");
            Require(controls.SniperButton != null, "The sniper button is missing");
            Require(controls.AutoAimButton != null, "The auto aim button is missing");
            Require(controls.MenuButton != null, "The battle menu button is missing");

            RequireOnCanvas((RectTransform)controls.Joystick.transform);
            RequireOnCanvas((RectTransform)controls.FireButton.transform);
            RequireOnCanvas((RectTransform)controls.SniperButton.transform);
            RequireOnCanvas((RectTransform)controls.AutoAimButton.transform);
            RequireOnCanvas((RectTransform)controls.MenuButton.transform);
        }

        /// <summary>
        /// The "menu" action, one press at a time: the window comes up, the
        /// pointer is handed back to the player, TO HANGAR raises the CONFIRM
        /// popup, cancelling leaves the menu alone and the second press closes
        /// everything and re-captures the pointer, exactly as
        /// player_controller._unhandled_input does it.
        /// </summary>
        private static void RequirePauseMenu(BattleSession session)
        {
            var menu = session.PauseMenu;
            Require(menu != null, "The battle pause menu is missing");
            Require(!menu.Visible, "menu_bg is authored hidden");
            Require(MouseLock.Captured, "The battle did not capture the pointer");
            Require(session.Controls.MenuRequested != null,
                "Nothing listens to the menu action");

            session.Controls.MenuRequested();
            Require(menu.Visible, "The menu action did not open the window");
            Require(!MouseLock.Captured,
                "The open menu did not release the pointer");
            RequireOnCanvas(menu.Panel.rectTransform);

            menu.Hangar.onClick.Invoke();
            Require(menu.Popup != null, "TO HANGAR did not raise the confirmation");
            RequireOnCanvas(menu.Popup.Panel.rectTransform);
            menu.Popup.Cancel.onClick.Invoke();
            Require(menu.Popup == null, "CANCEL did not close the confirmation");
            Require(menu.Visible, "CANCEL closed the menu behind the popup");

            // btn_settings opens ui_settings.tscn over the battle, and the
            // menu action belongs to that screen while it is up.
            Require(menu.SettingsPressed != null, "btn_settings is not wired");
            menu.Settings.onClick.Invoke();
            Require(session.SettingsUi != null,
                "SETTINGS did not open the settings screen");
            Require(session.SettingsUi.Rows.Count >= 32,
                "The settings screen built too few rows");
            session.Controls.MenuRequested();
            Require(menu.Visible,
                "The pause menu answered the menu action behind the settings");
            session.SettingsUi.Close();
            Require(session.SettingsUi == null,
                "btn_menu did not close the settings screen");

            session.Controls.MenuRequested();
            Require(!menu.Visible, "The menu action did not close the window");
            Require(MouseLock.Captured,
                "Closing the menu did not take the pointer back");
        }

        /// <summary>
        /// The "scoreboard" action: both lists slide out of the screen edges
        /// and back, the same way ui_player.gd drives anims_scoreboard, and
        /// every participant owns a row on its own side.
        /// </summary>
        private static void RequireScoreboard(BattleSession session)
        {
            var board = session.Scoreboard;
            Require(board != null, "The battle scoreboard is missing");
            Require(board.IsOpen, "setup() toggles the lists out once");
            Require(board.AllyRowCount > 0 && board.EnemyRowCount > 0,
                "The scoreboard did not list both teams");

            board.Advance(BattleScoreboard.SlideSeconds);
            Require(
                Mathf.Abs(board.AllyRoot.offsetMin.x - BattleScoreboard.AllyOpenX)
                    < 0.01f,
                "The ally list did not slide to its open pose");
            RequireOnCanvas((RectTransform)board.AllyRoot.Find("team_box"));

            session.Controls.ScoreboardRequested();
            board.Advance(BattleScoreboard.SlideSeconds);
            Require(!board.IsOpen, "The scoreboard action did not close the lists");
            Require(
                Mathf.Abs(board.AllyRoot.offsetMin.x - BattleScoreboard.AllyClosedX)
                    < 0.01f,
                "The ally list did not slide back off the edge");

            session.Controls.ScoreboardRequested();
            board.Advance(BattleScoreboard.SlideSeconds);
            Require(board.IsOpen, "The scoreboard action did not open the lists");

            RequireListTapReachesTheButton(board);
        }

        /// <summary>
        /// A tap anywhere on an open list has to land on that list
        /// ButtonCustom. The board owns a canvas above the controls so the
        /// button beats the full screen touch surface, and the button has to
        /// cover every row the battle lists; a row hanging below it drops the
        /// tap through to the surface and the board never closes.
        /// </summary>
        private static void RequireListTapReachesTheButton(BattleScoreboard board)
        {
            var events = UnityEngine.EventSystems.EventSystem.current;
            Require(events != null, "The battle has no EventSystem");

            var box = board.AllyRoot.Find("team_box") as RectTransform;
            Require(box != null, "The ally list has no team_box");

            var hits =
                new System.Collections.Generic.List<
                    UnityEngine.EventSystems.RaycastResult>();
            for (var i = 0; i < box.childCount; i++)
            {
                var row = (RectTransform)box.GetChild(i);
                var data = new UnityEngine.EventSystems.PointerEventData(events)
                {
                    position = RectTransformUtility.WorldToScreenPoint(
                        null, row.TransformPoint(row.rect.center)),
                };

                hits.Clear();
                events.RaycastAll(data, hits);
                Require(
                    hits.Count > 0
                        && hits[0].gameObject == board.AllyButton.gameObject,
                    "A tap on scoreboard row " + i + " misses the team button: "
                        + (hits.Count == 0 ? "nothing" : hits[0].gameObject.name));
            }
        }

        /// <summary>
        /// set_combat_hud_visible(false), the state a destroyed player lands
        /// in: the aiming widgets go away, the observer strip takes their spot
        /// on screen, and turning the combat HUD back on undoes all of it.
        /// </summary>
        private static void RequireSpectatorControls(BattleSession session)
        {
            var controls = session.Controls;
            var hud = session.Hud;
            Require(controls.CombatVisible && hud.CombatVisible,
                "The battle starts with the combat HUD up");

            session.SetCombatHudVisible(false);
            Require(!hud.DynamicCrosshair.gameObject.activeSelf
                    && !hud.StaticCrosshair.gameObject.activeSelf
                    && !hud.SpeedLabel.gameObject.activeSelf
                    && !hud.ReloadLabel.gameObject.activeSelf,
                "The combat readouts survived the player's death");
            Require(hud.AllyScoreLabel.gameObject.activeSelf,
                "The observer lost the team score as well");

            // mobile_shit.visible = is_mobile_platform(), so the observer
            // strip lives on the touch layer and a desktop battle never
            // builds one; the mouse cycles the camera there.
            if (controls.Joystick == null)
            {
                Require(controls.SpectatorControls == null,
                    "A desktop battle built the touch-only observer strip");
            }
            else
            {
                Require(controls.SpectatorControls != null
                        && controls.SpectatorControls.gameObject.activeSelf,
                    "The observer strip stayed hidden for a destroyed player");
                Require(!controls.Joystick.gameObject.activeSelf
                        && !controls.FireButton.gameObject.activeSelf,
                    "The combat widgets survived the player's death");
                RequireOnCanvas(controls.SpectatorControls);
            }

            session.SetCombatHudVisible(true);
            Require(hud.DynamicCrosshair.gameObject.activeSelf
                    && hud.SpeedLabel.gameObject.activeSelf,
                "Restoring the combat HUD did not put the readouts back");
        }

        /// <summary>Exercise the live original-client wreck path. The old
        /// ten-emitter Godot factory remains only as an isolated legacy fixture.</summary>
        private static void RequireWreckEffect(BattleSession session)
        {
            var wreck = session.Bot.Wreck;
            Require(wreck != null, "The bot has no wreck effect");
            Require(wreck.transform.parent == session.Bot.transform,
                "The wreck effect is not parented to its tank");
            var original = wreck.GetComponent<Original480Wreck>();
            Require(original != null, "The live wreck is not using original client effects");
            Require(!original.Playing && original.Effects.Count == 0,
                "A healthy tank is already burning");

            wreck.SetFakeWreck(true);
            Require(original.Playing && original.Effects.Count == 2,
                "The original explosion and continuous fire did not start");
            Require(original.Effects[0] != null && original.Effects[1] != null,
                "An original wreck effect is missing");
            wreck.SetFakeWreck(false);
            Require(!original.Playing && original.Effects.Count == 0,
                "The original wreck effect did not reset");

            Debug.Log("[Blitz] wreck effect ok: original client explosion/fire start and reset");
        }

        private static void RequireOnCanvas(RectTransform widget)
        {
            var canvas = widget.GetComponentInParent<Canvas>();
            Require(canvas != null, "A mobile widget has no canvas: " + widget.name);

            var root = (RectTransform)canvas.transform;
            var area = root.rect;
            var corners = new Vector3[4];
            widget.GetWorldCorners(corners);

            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            for (var i = 0; i < corners.Length; i++)
            {
                var local = root.InverseTransformPoint(corners[i]);
                min = Vector2.Min(min, local);
                max = Vector2.Max(max, local);
            }

            Debug.Log("[Blitz] mobile widget " + widget.name
                + " x=" + Mathf.RoundToInt(min.x) + ".." + Mathf.RoundToInt(max.x)
                + " y=" + Mathf.RoundToInt(min.y) + ".." + Mathf.RoundToInt(max.y)
                + " canvas=" + Mathf.RoundToInt(area.width)
                + "x" + Mathf.RoundToInt(area.height));

            // One pixel of slack: the joystick base is authored flush with the
            // bottom edge in the source scene.
            Require(min.x >= area.xMin - 1f && max.x <= area.xMax + 1f
                && min.y >= area.yMin - 1f && max.y <= area.yMax + 1f,
                "A mobile battle widget sits off the canvas: " + widget.name);
        }

        private static void Prepare()
        {
            try
            {
                var bootstrap = FindSceneObject<BlitzBootstrap>();
                Require(bootstrap != null, "BlitzBootstrap was not created");
                var login = FindSceneObject<LoginScreen>();
                if (login != null)
                {
                    UnityEngine.Object.Destroy(login.gameObject);
                }

                // The joystick and the four thumb buttons only exist on a
                // phone, so the gate has to be able to ask for them: the
                // device run is what found them misplaced, not this stage.
                _botPositionsBefore = null;
                _botTurretsBefore = null;
                _botMotionObserved = false;
                _damageInfoCountBefore = 0;
                _session = BattleSession.Create(bootstrap.transform, Mobile,
                    mode: Ducks ? BlitzLegacy.Core.Battle.BattleMode.WorldOfDucks : BlitzLegacy.Core.Battle.BattleMode.Normal);
                EditorApplication.delayCall += ValidateAndFire;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Finish(1);
            }
        }

        /// <summary>Only WarDucks on Sea of Ducks, each with the event's own audio and FX.</summary>
        private static void RequireWorldOfDucks()
        {
            Require(_session.Ducks != null && WorldOfDucksBattle.Current != null,
                "The War of Ducks rules did not attach");
            Require(_session.World.Map.Id == _session.Ducks.MapId, "The War of Ducks left its map: " + _session.World.Map.Id);
            Require(_session.Net == null, "The War of Ducks got a network seat");
            var guns = new int[WorldOfDucksRules.GunCount];
            var all = new System.Collections.Generic.List<BattleTank>(_session.Allies);
            all.AddRange(_session.Enemies);
            Require(all.Count == _session.MatchRoster.TeamSize * 2, "The War of Ducks roster is incomplete");
            foreach (var tank in all)
            {
                var duck = tank.GetComponent<WorldOfDucksTank>();
                Require(tank.Card.Name == _session.Ducks.CardName && duck != null,
                    "A non-duck seat entered the pond: " + tank.Card.Name);
                Require(tank.GetComponent<TankSoundManager>() == null, "A duck got the tank engine sounds");
                guns[Mathf.Clamp(duck.GunIndex, 0, guns.Length - 1)]++;
            }
            Debug.Log("[Blitz] war of ducks smoke: " + _session.MatchRoster.TeamSize + "v" + _session.MatchRoster.TeamSize
                + " guns=" + string.Join("/", guns) + " seed=" + _session.DucksSeed);
        }

        /// <summary>Every live duck sails on the CollisionPlain floor, not on the sea bed.</summary>
        private static void RequireDucksAfloat()
        {
            var all = new System.Collections.Generic.List<BattleTank>(_session.Allies);
            all.AddRange(_session.Enemies);
            var low = float.PositiveInfinity;
            var high = float.NegativeInfinity;
            foreach (var tank in all)
            {
                if (tank == null || tank.Dead) { continue; }
                var y = tank.transform.position.y;
                low = Mathf.Min(low, y);
                high = Mathf.Max(high, y);
                Require(y > WorldOfDucksRules.CollisionPlaneLevel - 0.6f && y < WorldOfDucksRules.WaterLevel + 1.5f,
                    "A duck is not afloat: " + tank.name + " y=" + y.ToString("F2"));
                Require(WorldOfDucksTank.InWater(y), "A duck left the water: " + tank.name);
            }
            Debug.Log("[Blitz] war of ducks afloat: y=" + low.ToString("F2") + ".." + high.ToString("F2")
                + " slabs=" + _session.World.Objects.FloorSlabCount);
        }

        private static void ValidateAndFire()
        {
            if (Ducks) { RequireDucksAfloat(); }
            try
            {
                Require(_session != null, "BattleSession is missing");
                if (Ducks) { RequireWorldOfDucks(); }
                Require(_session.MatchRoster != null
                        && _session.Bots.Count == (_session.MatchRoster.TeamSize * 2) - 1
                        && _session.Roster != null && _session.Roster.IsOngoing,
                    "The matched bot controllers did not start the battle");
                for (var botIndex = 0; botIndex < _session.Bots.Count; botIndex++)
                {
                    var controller = _session.Bots[botIndex];
                    Require(controller != null && controller.Active,
                        "An AI controller did not activate at battle start");
                    Require(!controller.Tank.Blocked && !controller.Tank.TurretBlock,
                        "An AI tank did not unlock its hull and turret");
                }

                if (!_botMotionObserved)
                {
                    _botPositionsBefore = new Vector3[_session.Bots.Count];
                    _botTurretsBefore = new Quaternion[_session.Bots.Count];
                    for (var botIndex = 0; botIndex < _session.Bots.Count; botIndex++)
                    {
                        var controller = _session.Bots[botIndex];
                        Require(controller.Tank.Turret != null
                                && controller.Tank.Turret.TurretPivot != null,
                            "An AI tank has no authored turret pivot");
                        _botPositionsBefore[botIndex] = controller.Tank.Pos();
                        _botTurretsBefore[botIndex] =
                            controller.Tank.Turret.TurretPivot.rotation;
                    }

                    _botMotionDeadline = DateTime.UtcNow.AddSeconds(6);
                    EditorApplication.update += WaitForBotMotion;
                    return;
                }

                // The staged shot further down needs hulls that stay where
                // they are put, so the bots stop fighting after motion was proven.
                _session.StopBots();
                Require(_session.World != null, "BattleWorld is missing");
                Require(_session.World.Map.Id == (Ducks ? _session.Ducks.MapId : BattleWorld.DefaultMapId),
                    "Wrong battle map was loaded");
                var mapObjects = _session.World.Objects;
                // Rudniki's object census and water: Sea of Ducks is gated by
                // RequireWorldOfDucks and its own navmesh/map tests instead.
                if (!Ducks)
                {
                    Require(mapObjects != null && mapObjects.InstanceCount == 713,
                        "The complete source object set was not built");
                    Require(mapObjects.AiCovers.Count == 224
                            && _session.Roster.AiCovers.Count == 224,
                        "The source navmesh-obstacle cover registry is incomplete");
                    var water = _session.World.Water;
                    Require(water != null && BattleWater.Active == water &&
                            water.RendererCount == mapObjects.WaterCount &&
                            water.TriangleCount > 0,
                        "The Rudniki water surface field was not built");
                    Debug.Log("[Blitz] battle smoke water: renderers=" + water.RendererCount +
                        " triangles=" + water.TriangleCount);
                    Require(_session.Player.Water != null && !_session.Player.Water.Flooded &&
                            !_session.Player.Water.Timer.Running,
                        "The player spawned flooded or without water physics");
                    Require(_session.Hud.WaterIndicator != null && !_session.Hud.WaterIndicator.TimerShown,
                        "The drowning countdown is missing or shown on dry land");
                    // Rudniki renders from the ported Godot scene: its LightmapGI bake
                    // lights the props and the Godot sun casts no shadow of its own
                    // (RudnikiMapTests.World_DrawsPropsFromTheGodotScene).
                    Require(mapObjects.Scene != null && mapObjects.LightmappedCount == 478,
                        "Rudniki lost the Godot scene bake: lightmapped=" + mapObjects.LightmappedCount);
                    Require(_session.World.Sun != null &&
                            _session.World.Sun.GetComponent<BlitzLegacy.Runtime.Lighting.BakedSun>() != null &&
                            _session.World.Sun.shadows == LightShadows.None,
                        "The Rudniki sun casts a realtime shadow over its bake");
                    Require(mapObjects.TreeCount == 121 &&
                            mapObjects.VegetationCount == 106 &&
                            mapObjects.WaterCount == 1 &&
                            mapObjects.DecorationCount == 10,
                        "Rudniki map object families are incomplete: trees=" + mapObjects.TreeCount
                        + " vegetation=" + mapObjects.VegetationCount + " water=" + mapObjects.WaterCount
                        + " decorations=" + mapObjects.DecorationCount);
                    Require(mapObjects.RendererCount == 1498 && // object table + the Godot render scene
                            mapObjects.BillboardQuadCount == 18476 &&
                            mapObjects.ColliderCount == 1160 &&
                            mapObjects.TriggerCount == 362,
                        "Renderers, vegetation billboards or colliders are incomplete: renderers=" + mapObjects.RendererCount
                        + " billboards=" + mapObjects.BillboardQuadCount + " colliders=" + mapObjects.ColliderCount
                        + " triggers=" + mapObjects.TriggerCount);
                }
                Require(_session.Player != null && _session.Ally != null
                        && _session.MatchRoster != null
                        && _session.Allies.Count == _session.MatchRoster.TeamSize
                        && _session.Enemies.Count == _session.MatchRoster.TeamSize
                        && _session.Bot == _session.Enemies[0],
                    "The matched tank line-up is missing");
                Require(_session.Player.Team == 0 && _session.Ally.Team == 0
                        && _session.Enemies.TrueForAll(tank => tank.Team == 1),
                    "The matched tanks are on the wrong teams");
                Require(_session.Bot.Blocked && _session.Bot.TurretBlock,
                    "The target bot is not stationary");
                Require(_session.View != null && _session.Player.View == _session.View,
                    "The player camera is not attached");
                Require(_session.Controls != null && _session.Hud != null,
                    "Battle controls or HUD are missing");
                Require(_session.Hud.TeamIconCount == _session.MatchRoster.TeamSize * 2,
                    "The header does not show the matched tank icons");
                Require(_session.Hud.TankPlateCount ==
                        (_session.MatchRoster.TeamSize * 2) - 1,
                    "The HUD does not have every non-player HP plate");
                _targetPlate = null;
                for (var plateIndex = 0; plateIndex < _session.Hud.TankPlateCount; plateIndex++)
                {
                    var plate = _session.Hud.GetTankPlate(plateIndex);
                    if (plate != null && plate.Tank == _session.Bot)
                    {
                        _targetPlate = plate;
                        break;
                    }
                }
                Require(_targetPlate != null,
                    "The deterministic target has no enemy HP plate");
                RequireMobileLayer(_session.Controls);
                RequirePauseMenu(_session);
                RequireScoreboard(_session);
                RequireSpectatorControls(_session);
                RequireWreckEffect(_session);
                Require(_session.Player.Plates.Count > 0 && _session.Bot.Plates.Count > 0,
                    "The matched tank armour plates were not built");
                Require(_session.Player.Gun != null && _session.Player.Health != null,
                    "Player gun or health is missing");

                // The source MS-1 carries its 44 mm penetration HEAT shell in
                // slot one. Use that authored round for a deterministic armour
                // penetration while keeping the complete firing path intact.
                // A WarDuck water gun has a single Waterspit round.
                _session.Player.ShellId = Ducks ? 0 : 1;
                ParkForShot(_session.Player, _session.Bot);
                var target = HullTargetOf(_session.Bot);
                _session.Player.Turret.SetAim(target);
                _session.Player.Turret.AdjustAim(1f);
                _session.Player.Turret.Rotate(100f);
                // Parking the hulls and the 100-degree turret snap bloom the
                // circle far past the gun's own dispersion (7.7 against the
                // MS-1's 0.54), and a shell rolled in that cone flies wide of
                // a hull 20 m away. A gunner waits for the circle to close
                // before a deterministic armour shot; so does the staged one.
                for (var settle = 0; settle < 32
                        && _session.Player.Dispersion > _session.Player.GunInfo.Dispersion; settle++)
                {
                    _session.Player.TightenDispersion();
                }
                _session.Player.UpdateGunTrace();
                Require(_session.Player.GunTraceTank == _session.Bot,
                    "The 720-metre gun trace did not reach the target bot");

                _healthBefore = _session.Bot.Health.Health;
                _damageInfoCountBefore = _session.Bot.Health.DamageInfosTaken.Count;
                Require(_session.Player.Gun.CanShoot(), "The player gun is not loaded");
                UnityEngine.Random.InitState(12021995);
                // -nographics has no device; the tracer is simulated all the same.
                GodotParticleEffect.ForceEnabled = true;
                _shotTrace = " disp=" + _session.Player.Dispersion.ToString("F3")
                    + " eff=" + _session.Player.EffectiveDispersion().ToString("F3")
                    + " base=" + _session.Player.GunInfo.Dispersion.ToString("F3")
                    + " off=" + Vector3.Angle(_session.Player.Turret.GunPivot.forward,
                        target - _session.Player.Turret.GunPivot.position).ToString("F2")
                    + " trace=" + _session.Player.GunTracePoint.ToString("F1")
                    + " dpt=" + _session.Player.DispersionPoint.ToString("F1")
                    + " fwd=" + _session.Player.Turret.GunPivot.forward.ToString("F3");
                BattleProjectile.Created += TraceShot;
                _session.Player.Gun.ShootAttempt();
                BattleProjectile.Created -= TraceShot;
                Require(!_session.Player.Gun.CanShoot(),
                    "The reload clock did not start after the shot");
                // The MS-1 HEAT tracer census; a WarDuck spits water instead.
                _trail = null;
                if (!Ducks)
                {
                    _trail = FindSceneObject<BattleGpuTrail>();
                    _trailObservedSegments = false;
                    Require(_trail != null,
                        "The projectile did not create its GPUTrail3D shell tracer");
                    Require(_trail.Emitters.Count == 2,
                        "The shell tracer does not have the source's two crossed emitters");
                    Require(Mathf.Abs(_trail.Emitters[0].Local.X.magnitude
                            - CombatFxManifest.Shared.TrailScale(_trail.Caliber)) < 0.0001f,
                        "The shell tracer has the wrong source width");
                    Require(_trail.Heat,
                        "The HEAT tracer did not use the source heat colour ramp");

                    // In game the shot fires from Update and the tracer's LateUpdate caches
                    // the muzzle point in the same frame. The smoke fires from the editor
                    // loop instead, so a slow batch-mode frame could run every physics step
                    // of this 20 m flight before the first LateUpdate and leave nothing to
                    // draw. Take that same-frame tick here.
                    _trail.Tick(0f, Camera.main != null ? Camera.main.transform : null);
                }

                _shotDeadline = DateTime.UtcNow.AddSeconds(5);
                EditorApplication.update += WaitForDamage;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Finish(1);
            }
        }

        // The track run is a module body hung in front of the hull, so a HEAT
        // round aimed at it bursts on that screen instead of reaching armour.
        // Aim the staged shot at the first plate that armours the tank itself.
        private static BattleProjectile _shot;
        private static string _shotTrace = string.Empty;
        private static Vector3 _shotLast;
        private static Collider _shotLastHit;

        private static void TraceShot(BattleProjectile shell)
        {
            _shot = shell;
            _shotLast = shell.transform.position;
            _shotTrace += " created@" + _shotLast.ToString("F1");
        }

        private static void FollowShot()
        {
            if (_shot == null) { return; }
            _shotLast = _shot.transform.position;
            if (_shot.LastHitCollider != null && _shot.LastHitCollider != _shotLastHit)
            {
                _shotLastHit = _shot.LastHitCollider;
                _shotTrace += " hit=" + _shotLastHit.name + "/L" + _shotLastHit.gameObject.layer + "@" + _shotLast.ToString("F1");
            }
        }

        private static Vector3 HullTargetOf(BattleTank tank)
        {
            foreach (var plate in tank.Plates)
            {
                if (string.IsNullOrEmpty(plate.Module) && plate.Shape != null)
                {
                    return plate.Shape.bounds.center;
                }
            }

            throw new InvalidOperationException(
                "The target tank carries no plain armour plate to aim at");
        }

        private static void WaitForBotMotion()
        {
            if (!SessionState.GetBool(ActiveKey, false))
            {
                EditorApplication.update -= WaitForBotMotion;
                return;
            }

            try
            {
                Require(_session != null && _session.MatchRoster != null
                        && _session.Bots.Count == (_session.MatchRoster.TeamSize * 2) - 1,
                    "The bot motion probe lost its matched battle");
                var movingBots = 0;
                var metrics = string.Empty;
                for (var botIndex = 0; botIndex < _session.Bots.Count; botIndex++)
                {
                    var controller = _session.Bots[botIndex];
                    Require(controller != null && controller.Active
                            && controller.Tank != null && !controller.Tank.Dead,
                        "An AI tank stopped before proving movement");
                    var displacement = controller.Tank.Pos() - _botPositionsBefore[botIndex];
                    displacement.y = 0f;
                    var moved = displacement.magnitude;
                    var turretTurned = Quaternion.Angle(
                        _botTurretsBefore[botIndex],
                        controller.Tank.Turret.TurretPivot.rotation);
                    if (moved >= 0.25f && turretTurned >= 1f)
                    {
                        movingBots++;
                    }
                    metrics += (botIndex == 0 ? string.Empty : ",")
                        + "bot" + botIndex
                        + ":tank=" + controller.Tank.Card.Name
                        + ":move=" + moved.ToString("F2")
                        + ":turret=" + turretTurned.ToString("F1");
                }

                // This smoke originally covered a 2x2 battle: player plus
                // three bots. Larger team-size settings must not turn it into
                // an all-roster mobility certification; three independently
                // moving and aiming AI tanks remain the intended gate.
                if (movingBots >= Mathf.Min(3, _session.Bots.Count))
                {
                    EditorApplication.update -= WaitForBotMotion;
                    _botMotionObserved = true;
                    Debug.Log("[Blitz] bot motion ok: " + metrics);
                    EditorApplication.delayCall += ValidateAndFire;
                    return;
                }

                if (DateTime.UtcNow >= _botMotionDeadline)
                {
                    throw new InvalidOperationException(
                        "Three AI tanks did not move and turn their turrets within six seconds: "
                        + metrics);
                }
            }
            catch (Exception exception)
            {
                EditorApplication.update -= WaitForBotMotion;
                Debug.LogException(exception);
                Finish(1);
            }
        }

        private static void ParkForShot(BattleTank player, BattleTank bot)
        {
            // Freezing beats isKinematic here: BattleTank.FixedUpdate keeps
            // writing linearVelocity for the play-area clamp, and a kinematic
            // body would log an engine warning on every physics tick.
            var playerBody = player.GetComponent<Rigidbody>();
            var botBody = bot.GetComponent<Rigidbody>();
            playerBody.collisionDetectionMode = CollisionDetectionMode.Discrete;
            botBody.collisionDetectionMode = CollisionDetectionMode.Discrete;
            playerBody.useGravity = false;
            botBody.useGravity = false;
            playerBody.constraints = RigidbodyConstraints.FreezeAll;
            botBody.constraints = RigidbodyConstraints.FreezeAll;

            var basePosition = new Vector3(
                player.PlayArea.center.x,
                200f,
                player.PlayArea.center.z);
            player.transform.SetPositionAndRotation(basePosition, Quaternion.identity);
            bot.transform.SetPositionAndRotation(
                basePosition + Vector3.forward * 20f,
                Quaternion.Euler(0f, 180f, 0f));
            Physics.SyncTransforms();
        }

        private static void WaitForDamage()
        {
            if (!SessionState.GetBool(ActiveKey, false))
            {
                EditorApplication.update -= WaitForDamage;
                return;
            }

            try
            {
                if (_trail != null && _trail.HasVisibleSegment())
                {
                    _trailObservedSegments = true;
                }

                if (_session != null && _session.Bot != null
                    && _session.Bot.Health.Health < _healthBefore)
                {
                    EditorApplication.update -= WaitForDamage;
                    Require(
                        _session.Bot.Health.DamageInfosTaken.Count
                            == _damageInfoCountBefore + 1,
                        "The staged projectile did not add exactly one tank hit");
                    Require(Ducks || _trailObservedSegments,
                        "The shell tracer never received a visible segment: "
                        + (_trail == null ? "trail freed" : "emitters=" + _trail.Emitters.Count
                            + " concealed=" + (_trail.Concealed != null && _trail.Concealed())));
                    var expectedFill = _session.Bot.Health.Health
                        / (float)_session.Bot.Health.MaxHealth;
                    Require(_targetPlate != null
                            && Mathf.Abs(_targetPlate.TopBar.fillAmount - expectedFill) < 0.0001f,
                        "The enemy HP bar did not follow projectile damage");
                    Require(_targetPlate.HealthLabel.text
                            == _session.Bot.Health.Health + "/" + _session.Bot.Health.MaxHealth,
                        "The enemy HP label did not follow projectile damage");
                    Require(_session.Hud.HealthLabel != null
                        && _session.Hud.ReloadLabel != null,
                        "Live combat values are not connected to the HUD");
                    Debug.Log(
                        "[Blitz] battle smoke ok: player_hp="
                        + _session.Player.Health.Health
                        + " target_hp=" + _session.Bot.Health.Health
                        + " plates=" + _session.Bot.Plates.Count
                        + " reload=" + _session.Player.Gun.ReloadRemaining().ToString("F2"));
                    Finish(0);
                    return;
                }

                FollowShot();
                if (DateTime.UtcNow >= _shotDeadline)
                {
                    var shells = UnityEngine.Object.FindObjectsByType<BattleProjectile>(FindObjectsSortMode.None);
                    var shellText = string.Empty;
                    foreach (var shell in shells)
                    {
                        shellText += " shell@" + shell.transform.position.ToString("F1")
                            + (shell.LastHitCollider != null ? " last=" + shell.LastHitCollider.name : string.Empty);
                    }
                    throw new InvalidOperationException(
                        "The fired projectile did not damage the target within five seconds: hp="
                        + _healthBefore + "->" + _session.Bot.Health.Health
                        + " infos=" + _damageInfoCountBefore + "->" + _session.Bot.Health.DamageInfosTaken.Count
                        + " player=" + _session.Player.transform.position.ToString("F1")
                        + " bot=" + _session.Bot.transform.position.ToString("F1")
                        + " shells=" + shells.Length + shellText + " trace:" + _shotTrace + " last@" + _shotLast.ToString("F1"));
                }
            }
            catch (Exception exception)
            {
                EditorApplication.update -= WaitForDamage;
                Debug.LogException(exception);
                Finish(1);
            }
        }

        private static void Finish(int code)
        {
            EditorApplication.update -= WaitForBotMotion;
            EditorApplication.update -= WaitForDamage;
            EditorApplication.delayCall -= ValidateAndFire;
            SessionState.SetInt(ExitCodeKey, code);
            _botPositionsBefore = null;
            _botTurretsBefore = null;
            _session = null;
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
            }
            else
            {
                Quit(code);
            }
        }

        private static void OnEditorUpdate()
        {
            if (!SessionState.GetBool(ActiveKey, false)) { return; }
            DateTime deadline;
            if (DateTime.TryParse(
                    SessionState.GetString(DeadlineKey, string.Empty), out deadline)
                && DateTime.UtcNow > deadline)
            {
                Debug.LogError("[Blitz] battle smoke timed out");
                Finish(1);
            }
        }

        private static T FindSceneObject<T>() where T : UnityEngine.Object
        {
            var objects = Resources.FindObjectsOfTypeAll<T>();
            for (var i = 0; i < objects.Length; i++)
            {
                var component = objects[i] as Component;
                if (component != null && component.gameObject.scene.IsValid())
                {
                    return objects[i];
                }
            }
            return null;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
        }

        private static void Quit(int code)
        {
            SessionState.EraseBool(ActiveKey);
            SessionState.EraseInt(ExitCodeKey);
            SessionState.EraseString(DeadlineKey);
            Unhook();
            EditorApplication.Exit(code);
        }
    }
}
