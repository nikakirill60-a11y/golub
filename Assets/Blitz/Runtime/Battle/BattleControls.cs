using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Input;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Platform;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// The player's input layer for battle: the PlayerController node embedded
    /// in base_tank.tscn, together with the widgets ui_player_battle.gd binds.
    ///
    /// The source reads the drive axes from Input.get_axis over move_fw /
    /// move_back / move_left / move_right, which both the keyboard and the on
    /// screen stick press, drops anything below 0.33 and calls BaseTank.drive
    /// every physics tick. turret_block is set from block_turret afterwards,
    /// and only when the tank is not blocked, so the order here is the source's
    /// order.
    ///
    /// project.godot binds those actions to W / S / A / D, block_turret to the
    /// right mouse button, attack1 to the left one, sniper_mode to Shift, menu
    /// to Escape and P, and zoom_in / zoom_out to the wheel.
    ///
    /// battle_input_bindings.gd also binds shell1 / shell2 / shell3 to the 1,
    /// 2 and 3 keys, and the touch build selects the same slots by tapping the
    /// rack the controls layer owns (see <see cref="BattleShellSelector"/>).
    ///
    /// Once the player's tank is gone the physics tick only cycles the
    /// observer camera: attack1 steps forward through the player's team and
    /// block_turret steps back, exactly as the death branch of
    /// player_controller._physics_process does.
    ///
    /// Deferred on purpose: shooting (the fire button only keeps its held state
    /// here), the pause menu and the scoreboard, and the soft auto aim that
    /// also reads block_turret.
    /// </summary>
    [DefaultExecutionOrder(-2)]
    public sealed class BattleControls : MonoBehaviour
    {
        private BattleTank _tank;
        private BattleCamera _view;
        private BattleJoystick _joystick;
        private BattleTouchSurface _surface;
        private BattleHoldButton _fire;
        private BattleHoldButton _sniper;
        private BattleHoldButton _autoAim;
        private BattleHoldButton _menu;
        private BattleHoldButton _chat;
        private BattleShellSelector _shells;
        private BattleConsumableBar _consumables;
        private bool _firePhysical;
        private int _buttons;
        private RectTransform _mobileLayer;
        private RectTransform _spectatorControls;
        private Button _spectatorPrevious;
        private Button _spectatorNext;
        private bool _combatVisible = true;

        private bool _inputActive = true;

        /// <summary>
        /// save_settings.pc_control_bindings, i.e. the InputMap the player
        /// edited in the controls tab. Null falls back to the project
        /// defaults, which is what an unsaved profile answers with anyway.
        /// </summary>
        private static PcControlBindings Bindings
        {
            get
            {
                return BlitzBootstrap.Current == null
                    || BlitzBootstrap.Current.Settings == null
                    ? null
                    : BlitzBootstrap.Current.Settings.PcControls;
            }
        }

        private int _teamTankId = ControlsTuning.SpectatorStartIndex;

        /// <summary>The shells half of shells_equipment in ui_player.tscn.</summary>
        public BattleShellSelector Shells { get { return _shells; } }

        /// <summary>shells_equipment/equipment, the consumable column.</summary>
        public BattleConsumableBar Consumables { get { return _consumables; } }

        public BattleJoystick Joystick { get { return _joystick; } }

        public BattleTouchSurface Surface { get { return _surface; } }

        public BattleHoldButton FireButton { get { return _fire; } }

        public BattleHoldButton SniperButton { get { return _sniper; } }

        public BattleHoldButton AutoAimButton { get { return _autoAim; } }

        public BattleHoldButton MenuButton { get { return _menu; } }

        /// <summary>The battle chat button right of btn_menu (touch layout).</summary>
        public BattleHoldButton ChatButton { get { return _chat; } }

        /// <summary>Raised by the chat button.</summary>
        public Action ChatRequested;

        /// <summary>btn_autttoaim, bound to the block_turret action in the source.</summary>
        public bool AutoAimHeld { get { return _autoAim != null && _autoAim.Held; } }

        /// <summary>
        /// ui_player_battle.gd keeps its own fire_held for the button, because
        /// Android emulates a left click from every touch and attack1 can
        /// therefore not be polled on mobile. The desktop half is the source's
        /// fire_physical latch.
        /// </summary>
        /// <summary>The raw drive axes of the last physics step, before steering.</summary>
        public float DriveForward { get; private set; }

        /// <summary>The raw steer axis of the last physics step.</summary>
        public float DriveRight { get; private set; }

        public bool FireHeld { get { return (_fire != null && _fire.Held) || _firePhysical; } }

        /// <summary>Raised by btn_menu and by the menu action; the screen lands with the battle HUD.</summary>
        public Action MenuRequested;

        /// <summary>
        /// The "scoreboard" action (Tab). Both team buttons raise the same
        /// toggle themselves, exactly as ui_player_battle.gd wires them.
        /// </summary>
        public Action ScoreboardRequested;

        /// <summary>
        /// Battle.get_player_team(): the roster entries on the local player's
        /// side, in the order cycle_spectator walks them.
        /// </summary>
        public Func<IList<BattleParticipantStats>> PlayerTeam;

        /// <summary>player_controller.team_tank_id, which starts at -1.</summary>
        public int TeamTankId { get { return _teamTankId; } }

        /// <summary>spectator_controls, the observer strip built in code.</summary>
        public RectTransform SpectatorControls { get { return _spectatorControls; } }

        /// <summary>Its "previous" button, i.e. the -1 direction.</summary>
        public Button SpectatorPrevious { get { return _spectatorPrevious; } }

        /// <summary>Its "next" button, i.e. the +1 direction.</summary>
        public Button SpectatorNext { get { return _spectatorNext; } }

        /// <summary><c>_combat_hud_alive</c> on the controls layer.</summary>
        public bool CombatVisible { get { return _combatVisible; } }

        public static BattleControls Create(BattleTank tank, BattleCamera view, bool mobile)
        {
            var go = new GameObject("BattleControls");
            var controls = go.AddComponent<BattleControls>();
            controls._tank = tank;
            controls._view = view;

            EnsureEventSystem();
            var canvas = CreateCanvas(go.transform, mobile);

            var surface = BattleTouchSurface.Create(canvas);
            if (view != null)
            {
                surface.Looked = delta => { if (controls.BattlePointerAllowed) view.Look(delta); };
                surface.Pinched = distance => { if (controls.BattlePointerAllowed) view.Pinch(distance); };
                surface.PinchEnded = view.PinchReset;
            }

            controls._surface = surface;

            if (mobile)
            {
                controls.BuildMobileLayer(canvas);
            }

            // shells_equipment lives in the player HUD tree in the source, but
            // after the touch surface, so a tap picks a shell instead of
            // turning the turret. Building it here keeps that hit order.
            var uiAtlas = AtlasRegionTable.Parse(
                BlitzResources.LoadText(BlitzResources.AtlasRegionsData));
            var uiBold = BlitzResources.LoadFont(BlitzResources.BoldFontName);
            controls._shells = BattleShellSelector.Create(
                canvas,
                tank,
                uiAtlas,
                BattleFlowUi.LoadLanguage(),
                uiBold);

            // The consumable column is the second child of shells_equipment in
            // ui_player.tscn, so it hangs off the very same scaled root.
            controls._consumables = BattleConsumableBar.Create(
                controls._shells != null ? controls._shells.Root : null,
                tank,
                uiAtlas,
                uiBold);

            Debug.Log("[Blitz] battle controls built: mobile=" + (mobile ? "yes" : "no")
                + " joystick=" + (controls._joystick != null ? "yes" : "no")
                + " buttons=" + controls._buttons
                + " reference=" + Mathf.RoundToInt(UiLayout.BattleReferenceWidth(mobile))
                + "x" + Mathf.RoundToInt(UiLayout.BattleReferenceHeight(mobile)));
            return controls;
        }

        private void BuildMobileLayer(RectTransform canvas)
        {
            // ui_player.tscn hangs the mobile widgets off "mobile_shit", a full
            // rect Control with mouse_filter IGNORE, so the parent itself never
            // swallows a touch. It is added after the surface, which puts it in
            // front: Unity hit tests later siblings first.
            var layerGo = new GameObject("mobile_shit", typeof(RectTransform));
            layerGo.transform.SetParent(canvas, false);
            GodotRect.FullRect((RectTransform)layerGo.transform);
            var layer = (RectTransform)layerGo.transform;

            var atlas = AtlasRegionTable.Parse(
                BlitzResources.LoadText(BlitzResources.AtlasRegionsData));
            _joystick = BattleJoystick.Create(
                layer,
                atlas.Get("battle_joypad_base"),
                atlas.Get("battle_joypad_knob"));

            // btn_fire, btn_sniper and btn_autttoaim are anchors_preset 3, the
            // bottom right corner; btn_menu is anchors_preset 5, centre top.
            _fire = BattleHoldButton.Create(
                layer, "btn_fire", 1f, 1f, 1f, 1f,
                ControlsTuning.FireOffsetLeft,
                ControlsTuning.FireOffsetTop,
                ControlsTuning.FireOffsetRight,
                ControlsTuning.FireOffsetBottom,
                atlas.Get("battle_fire_button"),
                atlas.Get("battle_ammo_type"),
                0.635f);

            _sniper = BattleHoldButton.Create(
                layer, "btn_sniper", 1f, 1f, 1f, 1f,
                ControlsTuning.SniperOffsetLeft,
                ControlsTuning.SniperOffsetTop,
                ControlsTuning.SniperOffsetRight,
                ControlsTuning.SniperOffsetBottom,
                atlas.Get("battle_sniper_button"));
            _sniper.Changed = OnSniperButton;

            _autoAim = BattleHoldButton.Create(
                layer, "btn_autttoaim", 1f, 1f, 1f, 1f,
                ControlsTuning.AutoAimOffsetLeft,
                ControlsTuning.AutoAimOffsetTop,
                ControlsTuning.AutoAimOffsetRight,
                ControlsTuning.AutoAimOffsetBottom,
                atlas.Get("battle_autoaim_button"));

            _menu = BattleHoldButton.Create(
                layer, "btn_menu", 0.5f, 0f, 0.5f, 0f,
                ControlsTuning.MenuOffsetLeft,
                ControlsTuning.MenuOffsetTop,
                ControlsTuning.MenuOffsetRight,
                ControlsTuning.MenuOffsetBottom,
                null,
                atlas.Get("hangar_menu"),
                48f / 88f);
            _menu.Changed = OnMenuButton;

            var chatIcon = atlas.Get("chat_button_icon");
            _chat = BattleHoldButton.Create(
                layer, "btn_chat", 0.5f, 0f, 0.5f, 0f,
                ControlsTuning.ChatOffsetLeft,
                ControlsTuning.ChatOffsetTop,
                ControlsTuning.ChatOffsetRight,
                ControlsTuning.ChatOffsetBottom,
                null,
                chatIcon,
                chatIcon != null ? 48f / chatIcon.rect.height : 1f);
            _chat.Changed = held => { if (held && ChatRequested != null) { ChatRequested(); } };

            _mobileLayer = layer;
            _buttons = 5;
        }

        /// <summary>
        /// <c>_ensure_spectator_controls</c>: an HBoxContainer under
        /// mobile_shit with a previous and a next button, built on demand and
        /// hidden until the combat HUD goes away. Both buttons carry the
        /// source's custom_minimum_size and expand into what the strip has
        /// left over, which is what SIZE_EXPAND_FILL does to a 132 wide row
        /// holding two 56 wide buttons and one 16 wide gap.
        /// </summary>
        private void EnsureSpectatorControls()
        {
            if (_spectatorControls != null || _mobileLayer == null) { return; }

            _spectatorControls = UiFactory.Node("spectator_controls", _mobileLayer);
            GodotRect.Apply(
                _spectatorControls, 0.5f, 1f, 0.5f, 1f,
                ControlsTuning.SpectatorOffsetLeft,
                ControlsTuning.SpectatorOffsetTop,
                ControlsTuning.SpectatorOffsetRight,
                ControlsTuning.SpectatorOffsetBottom,
                GodotRect.TopLeftPivot);

            var lang = BattleFlowUi.LoadLanguage();
            var atlas = AtlasRegionTable.Parse(
                BlitzResources.LoadText(BlitzResources.AtlasRegionsData));
            var bold = BlitzResources.LoadFont(BlitzResources.BoldFontName);

            _spectatorPrevious = SpectatorButton(
                "previous", -1, "spectator:previous", lang, atlas, bold);
            _spectatorNext = SpectatorButton(
                "next", 1, "spectator:next", lang, atlas, bold);

            RefreshSpectatorStrip();
        }

        /// <summary>
        /// The strip follows the game state every frame rather than only the
        /// death event: that event can arrive before the mode has decided
        /// whether a return is pending, and a spent last life (or a pending
        /// return that turns out to be the final one) changes the answer later.
        /// </summary>
        private void RefreshSpectatorStrip()
        {
            if (_spectatorControls == null) { return; }
            var wanted = !_combatVisible && CanShowSpectator;
            if (_spectatorControls.gameObject.activeSelf != wanted)
            {
                _spectatorControls.gameObject.SetActive(wanted);
            }
        }

        private Button SpectatorButton(
            string name,
            int direction,
            string key,
            LocalizationTable lang,
            AtlasRegionTable atlas,
            Font bold)
        {
            // Button.new() picks up the theme's own Button entry, whose normal
            // stylebox is style_button_generic and whose font is the bold one.
            var button = UiFactory.TextButton(
                name, _spectatorControls, atlas.Get("button_generic"), bold,
                ControlsTuning.SpectatorFontSize, lang.Get(key + "/short"));
            button.GetComponent<Image>().type = Image.Type.Sliced;

            var width = ControlsTuning.SpectatorButtonWidth + SpectatorSlack();
            var step = width + ControlsTuning.SpectatorSeparation;
            var index = direction < 0 ? 0f : 1f;
            GodotRect.TopLeft(
                (RectTransform)button.transform,
                index * step,
                0f,
                index * step + width,
                ControlsTuning.SpectatorOffsetBottom
                    - ControlsTuning.SpectatorOffsetTop);

            button.onClick.AddListener(() => CycleSpectator(direction));
            return button;
        }

        /// <summary>
        /// Hides the whole control layer and stops every look input while a
        /// flow screen owns the display. ui_player.tscn is simply not visible
        /// during the map intro in the source, so the stick, the buttons and
        /// the touch surface all go away and the camera stays where the spawn
        /// put it until the battle UI comes up.
        /// </summary>
        public void SetInputActive(bool on)
        {
            _inputActive = on;
            RefreshPointerOwnership();
            SetWidgetVisible(_mobileLayer == null ? null : _mobileLayer.transform, on);
            SetWidgetVisible(_surface == null ? null : _surface.transform, on);
            if (_shells != null) { _shells.SetVisible(on && _combatVisible); }

            if (on) { return; }

            _firePhysical = false;
            if (_joystick != null) { _joystick.Release(); }
        }

        /// <summary>The pixels SIZE_EXPAND_FILL hands to each button.</summary>
        public static float SpectatorSlack()
        {
            var strip = ControlsTuning.SpectatorOffsetRight
                - ControlsTuning.SpectatorOffsetLeft;
            var content = 2f * ControlsTuning.SpectatorButtonWidth
                + ControlsTuning.SpectatorSeparation;
            return Mathf.Max(0f, strip - content) * 0.5f;
        }

        /// <summary>
        /// The controls half of <c>set_combat_hud_visible</c>: the stick and
        /// the three combat buttons go away, the observer strip comes up, and
        /// a hidden HUD also drops whatever the player was holding.
        /// </summary>
        public void SetCombatVisible(bool on)
        {
            _combatVisible = on;
            EnsureSpectatorControls();

            SetWidgetVisible(_joystick == null ? null : _joystick.transform, on);
            SetWidgetVisible(_fire == null ? null : _fire.transform, on);
            SetWidgetVisible(_sniper == null ? null : _sniper.transform, on);
            SetWidgetVisible(_autoAim == null ? null : _autoAim.transform, on);
            RefreshSpectatorStrip();

            if (on) { return; }

            // fire_held = false, the released actions and the reset stick.
            _firePhysical = false;
            if (_joystick != null) { _joystick.Release(); }
        }

        private static void SetWidgetVisible(Transform widget, bool on)
        {
            if (widget != null) { widget.gameObject.SetActive(on); }
        }

        /// <summary>
        /// <c>cycle_spectator</c>: only a dead tank observes, the walk wraps
        /// with posmod and skips team mates that are gone, and the first live
        /// candidate takes over camera_controller.tank.
        /// </summary>
        // Preserve normal-mode HUD visibility semantics; a mode that returns its crews only
        // exposes this strip once no return is left (Uprising: final death, Big Boss: the
        // boss's third life).
        private bool CanShowSpectator
        {
            get { return _tank == null || !BattleHudMode.Of(_tank).Respawns || CanSpectate; }
        }

        /// <summary>A battle-mode screen owns the pointer (the Uprising perk offer). Modal
        /// ownership comes from game state, not cursor state: mobile always reports
        /// MouseLock.Locked, and a drag may predate death.</summary>
        public bool ModalOpen
        {
            get { return _tank != null && BattleHudMode.Of(_tank).ModalOpen; }
        }
        private bool BattlePointerAllowed => _inputActive && !ModalOpen;

        private void RefreshPointerOwnership()
        {
            if (_surface != null) _surface.SetInputEnabled(BattlePointerAllowed);
            if (!BattlePointerAllowed)
            {
                _firePhysical = false;
                if (_joystick != null) _joystick.Release();
            }
        }

        /// <summary>A dead crew watches its team once its battle mode has no return left.</summary>
        public bool CanSpectate
        {
            get { return _tank != null && _tank.Dead && BattleHudMode.Of(_tank).CanSpectate; }
        }

        public void CycleSpectator(int direction)
        {
            if (!CanSpectate) { return; }

            var team = PlayerTeam == null ? null : PlayerTeam();
            if (team == null || team.Count == 0) { return; }

            for (var step = 0; step < team.Count; step++)
            {
                _teamTankId = PosMod(_teamTankId + direction, team.Count);
                var entry = team[_teamTankId];
                var candidate = entry == null ? null : entry.Tank;
                if (candidate != null && !candidate.Dead)
                {
                    if (_view != null) { _view.Tank = candidate; }
                    return;
                }
            }
        }

        /// <summary>Godot's posmod, which never returns a negative index.</summary>
        public static int PosMod(int value, int modulus)
        {
            var wrapped = value % modulus;
            return wrapped < 0 ? wrapped + modulus : wrapped;
        }

        private bool _autoAimWas;
        private bool _blockWas;

        private void FixedUpdate()
        {
            DriveForward = 0f;
            DriveRight = 0f;
            if (_tank == null || !_inputActive || ModalOpen)
            {
                return;
            }

            // "if !Util.get_mouse_lock(): return" at the top of the source's
            // physics tick: while the escape menu or a popup owns the pointer
            // on a desktop build, the player drives nothing. The gate is
            // always open on a phone, where the menu never releases anything.
            if (!MouseLock.Locked)
            {
                return;
            }

            // The death branch: a wreck drives nothing and its two combat
            // inputs walk the observer camera instead.
            if (_tank.Dead)
            {
                var mouse = Mouse.current;
                if (mouse != null)
                {
                    if (mouse.leftButton.wasPressedThisFrame)
                    {
                        CycleSpectator(1);
                    }

                    if (mouse.rightButton.wasPressedThisFrame)
                    {
                        CycleSpectator(-1);
                    }
                }

                return;
            }

            float forward;
            float right;
            ReadDriveAxes(out forward, out right);
            DriveForward = forward;
            DriveRight = right;

            // The source drives first and only then bails out on a blocked tank.
            _tank.Drive(forward, right);

            if (_tank.Blocked)
            {
                return;
            }

            // block_turret only holds the turret while nothing is captured:
            // was_in_auto_aim hands the very same input over to the capture.
            var auto = _tank.AutoAim;

            // The overhead view aims by hand; a capture would drag the turret
            // off the point the player is walking the missile onto.
            var arty = _view != null && _view.ArtyMode;
            var blockHeld = AutoAimHeld || TurretBlockHeld();
            _tank.TurretBlock = blockHeld && !auto.Engaged;

            var settings = BlitzBootstrap.Current != null
                ? BlitzBootstrap.Current.Settings
                : null;
            var recaptureDelay = settings != null ? settings.AutoAimRecaptureDelay : 0f;
            var aimPoint = _view != null ? _view.AimTarget : _tank.transform.position;
            var aimCollider = _view != null ? _view.TraceCollider : null;

            if (blockHeld && !_blockWas && !arty)
            {
                if (auto.CanCapture(_tank.TankInAim, false))
                {
                    auto.Capture(_tank.TankInAim, aimPoint, aimCollider);
                }
                else
                {
                    // A deliberate press with nothing capturable under the
                    // crosshair means the player is letting go, so the released
                    // tank goes on cooldown and auto capture cannot hand it
                    // straight back.
                    auto.Release(true, recaptureDelay);
                }
            }

            _blockWas = blockHeld;

            // Auto capture: lock on to the enemy under the crosshair without
            // requiring the block_turret press. It re-targets every tick, so
            // sliding the crosshair onto another spotted enemy hands the
            // capture over at once and it can never stay glued to a tank the
            // player has already looked away from. The post-release cooldown
            // inside CanCapture is what stops it from re-locking the tank the
            // player has just shaken off on the very next tick.
            if (settings != null && settings.AutoCaptureTarget && !arty
                && auto.CanCapture(_tank.TankInAim, true))
            {
                auto.Capture(_tank.TankInAim, aimPoint, aimCollider);
            }

            // auto_aim_start / auto_aim_stop: the cue plays on the edges of the
            // capture, which is what the source connects those signals to.
            if (auto.Engaged != _autoAimWas)
            {
                _autoAimWas = auto.Engaged;
                var audio = BlitzBootstrap.Current != null
                    ? BlitzBootstrap.Current.Audio : null;
                if (audio != null)
                {
                    audio.PlayPool2D(audio.Sounds.Sound(
                        auto.Engaged ? "ui_autoaim_on" : "ui_autoaim_off"));
                }
            }

            // ui_player_battle.gd fires from its physics tick. Holding either
            // input simply retries until BattleGun's reload clock allows it.
            if (FireHeld && _tank.Gun != null)
            {
                _tank.Gun.ShootAttempt();
            }
        }

        private void Update()
        {
            RefreshSpectatorStrip();

            // A hidden ui_player.tscn reads no input at all, so the map intro
            // cannot be looked, zoomed or fired through.
            if (!_inputActive)
            {
                return;
            }

            RefreshPointerOwnership();
            if (ModalOpen)
            {
                // Escape/pause stays available above the modal; no combat,
                // spectator, raw-mouse, wheel or keyboard battle action leaks.
                PollMenu();
                return;
            }
            PollLook();
            PollZoom();
            PollSniper();
            PollMenu();
            PollScoreboard();
            PollReloadBurst();
            PollFire();

            // The shell and consumable hotkeys sit behind the same
            // get_mouse_lock() gate in the source's _unhandled_input, so an
            // open menu swallows them while the sniper and menu actions above
            // it keep working.
            if (!MouseLock.Locked)
            {
                return;
            }

            PollShells();
            PollConsumables();
            PollModeAbility();
        }

        /// <summary>
        /// Input.get_axis("move_back", "move_fw") and its left / right twin. The
        /// stick presses the very same actions, so it simply wins while it is
        /// past its deadzone.
        /// </summary>
        private void ReadDriveAxes(out float forward, out float right)
        {
            if (_joystick != null && _joystick.Engaged)
            {
                forward = _joystick.Forward;
                right = _joystick.Right;
                return;
            }

            forward = 0f;
            right = 0f;

            var bindings = Bindings;
            if (PcControlInput.Held(bindings, PcControlBindings.MoveForward))
            {
                forward += 1f;
            }

            if (PcControlInput.Held(bindings, PcControlBindings.MoveBack))
            {
                forward -= 1f;
            }

            if (PcControlInput.Held(bindings, PcControlBindings.MoveRight))
            {
                right += 1f;
            }

            if (PcControlInput.Held(bindings, PcControlBindings.MoveLeft))
            {
                right -= 1f;
            }

            forward = ControlsTuning.ApplyThreshold(forward);
            right = ControlsTuning.ApplyThreshold(right);
        }

        private static bool TurretBlockHeld()
        {
            return PcControlInput.Held(Bindings, PcControlBindings.BlockTurret);
        }

        /// <summary>
        /// The look half of input_camera.gd's _unhandled_input:
        ///
        ///   var look_mouse := Input.mouse_mode == MOUSE_MODE_CAPTURED
        ///       and event is InputEventMouseMotion
        ///
        /// so on a desktop the camera turns with the raw mouse motion while the
        /// pointer is captured. Unity stops sending pointer drags the moment
        /// Cursor.lockState is Locked, because the cursor no longer moves, so
        /// BattleTouchSurface saw nothing and only the cursor disappeared. The
        /// captured delta is therefore read straight off the device here, and
        /// the surface keeps serving the touch half.
        ///
        /// Godot measures event.relative with Y pointing down; Unity's delta
        /// points up, hence the same flip BattleTouchSurface.OnDrag applies.
        /// </summary>
        /// <summary>
        /// Coming back to the window (alt-tab, a notification, a second
        /// monitor) is where desktop pointer grabs are lost, so take it again.
        /// </summary>
        private float _lastEdgeRelock = -10f;

        private void OnApplicationFocus(bool focused)
        {
            if (focused) { MouseLock.Relock(); }
        }

        private void PollLook()
        {
            if (_view == null || !BattlePointerAllowed || !MouseLock.Captured)
            {
                return;
            }

            MouseLock.Enforce();
            var mouse = Mouse.current;
            if (mouse == null || Time.frameCount <= MouseLock.IgnoreDeltaUntilFrame)
            {
                return;
            }

            // A grab that is reported as held but no longer warps the pointer
            // (XWayland) shows up as a hidden cursor parked on a window edge.
            // Retake it then, at most once a second so a pointer position that
            // a platform simply freezes there cannot keep eating the input.
            var position = mouse.position.ReadValue();
            if ((position.y <= 1f || position.y >= Screen.height - 2f
                    || position.x <= 1f || position.x >= Screen.width - 2f)
                && Time.unscaledTime - _lastEdgeRelock > 1f)
            {
                _lastEdgeRelock = Time.unscaledTime;
                MouseLock.Relock();
            }

            var delta = mouse.delta.ReadValue();
            if (Mathf.Approximately(delta.x, 0f) && Mathf.Approximately(delta.y, 0f))
            {
                return;
            }

            _view.Look(new Vector2(delta.x, -delta.y));
        }

        private void PollZoom()
        {
            if (_view == null)
            {
                return;
            }

            // input_camera.gd zooms from _unhandled_input, so a Control that
            // takes the wheel first consumes it and the camera never sees it.
            // The settings ScrollContainer opened over a battle is exactly
            // that case. Unity polls the wheel straight off the device, so the
            // gate the rest of this layer already uses stands in for it: the
            // pointer is released only while a menu or a popup owns it.
            if (!MouseLock.Locked)
            {
                return;
            }

            // zoom_in is the wheel going up by default, and
            // get_axis("zoom_in", "zoom_out") is therefore negative there,
            // which is the direction that pulls the camera closer. Both ends
            // are rebindable, so they are read as actions.
            var bindings = Bindings;
            var axis = 0f;
            if (PcControlInput.Pressed(bindings, PcControlBindings.ZoomIn))
            {
                axis -= 1f;
            }

            if (PcControlInput.Pressed(bindings, PcControlBindings.ZoomOut))
            {
                axis += 1f;
            }

            if (Mathf.Approximately(axis, 0f))
            {
                return;
            }

            _view.Zoom(axis);
        }

        /// <summary>
        /// The "reload_burst" action, bound to C. It sits above the
        /// get_mouse_lock() gate in the source, and only a magazine gun answers
        /// it: dump whatever is left in the clip and load a fresh one.
        /// </summary>
        private void PollReloadBurst()
        {
            if (!PcControlInput.Pressed(Bindings, PcControlBindings.ReloadBurst))
            {
                return;
            }

            var burst = _tank != null ? _tank.Gun as BattleGunBurst : null;
            if (burst != null) { burst.ForceMainReload(); }
        }

        private void PollSniper()
        {
            if (PcControlInput.Pressed(Bindings, PcControlBindings.SniperMode))
            {
                ToggleSniper();
            }
        }

        private void PollMenu()
        {
            // The source binds two events to "menu" (Escape and P); the port
            // keeps one editable slot, so P stays as the fixed second event.
            if (PcControlInput.Pressed(Bindings, PcControlBindings.Menu)
                || (!PcControlInput.Suppressed && Keyboard.current != null
                    && Keyboard.current[Key.P].wasPressedThisFrame))
            {
                RequestMenu();
            }
        }

        /// <summary>
        /// The "scoreboard" action, bound to Tab. It sits above the
        /// get_mouse_lock() gate in the source, so the board still answers
        /// while the escape menu holds the pointer.
        /// </summary>
        private void PollScoreboard()
        {
            if (PcControlInput.Pressed(Bindings, PcControlBindings.Scoreboard)
                && ScoreboardRequested != null)
            {
                ScoreboardRequested();
            }
        }

        /// <summary>
        /// battle_input_bindings.gd binds shell1 / shell2 / shell3 to the 1, 2
        /// and 3 keys; base_tank.select_shell clamps the id to the rack the
        /// mounted gun actually carries.
        /// </summary>
        private void PollShells()
        {
            if (_shells == null) { return; }

            var bindings = Bindings;
            if (PcControlInput.Pressed(bindings, PcControlBindings.Shell1))
            {
                _shells.Select(0);
            }
            else if (PcControlInput.Pressed(bindings, PcControlBindings.Shell2))
            {
                _shells.Select(1);
            }
            else if (PcControlInput.Pressed(bindings, PcControlBindings.Shell3))
            {
                _shells.Select(2);
            }
        }

        /// <summary>
        /// battle_input_bindings.gd binds consumable1 / consumable2 /
        /// consumable3 to the 4, 5 and 6 keys, and ui_player_battle wires each
        /// of them to tank.use_consumable(N).
        /// </summary>
        private void PollConsumables()
        {
            if (_consumables == null) { return; }

            var bindings = Bindings;
            if (PcControlInput.Pressed(bindings, PcControlBindings.Consumable1))
            {
                _consumables.Use(0);
            }
            else if (PcControlInput.Pressed(bindings, PcControlBindings.Consumable2))
            {
                _consumables.Use(1);
            }
            else if (PcControlInput.Pressed(bindings, PcControlBindings.Consumable3))
            {
                _consumables.Use(2);
            }
        }

        /// <summary>Digit7 presses the shared fourth slot for a battle-mode ability.</summary>
        private void PollModeAbility()
        {
            if (_consumables == null) { return; }

            if (PcControlInput.Pressed(Bindings, PcControlBindings.ModeAbility))
            {
                _consumables.UseModeAbility();
            }
        }

        private void PollFire()
        {
            var bindings = Bindings;
            if (PcControlInput.Pressed(bindings, PcControlBindings.Attack))
            {
                _firePhysical = true;
            }

            // fire_physical_held(): the latch is re-confirmed against the button
            // every read, because a release can be consumed elsewhere.
            _firePhysical = _firePhysical
                && PcControlInput.Held(bindings, PcControlBindings.Attack);
        }

        /// <summary>
        /// btn_sniper presses and releases the sniper_mode action, and the
        /// controller toggles once per press.
        /// </summary>
        private void OnSniperButton(bool held)
        {
            if (!held)
            {
                return;
            }

            ToggleSniper();
        }

        private void OnMenuButton(bool held)
        {
            if (!held)
            {
                return;
            }

            RequestMenu();
        }

        /// <summary>
        /// The camera half of sniper_mode_switch. Its audio muffling and the
        /// aim bookkeeping land with the battle HUD and the shooting slice.
        /// </summary>
        private void ToggleSniper()
        {
            if (_view == null)
            {
                return;
            }

            // The switch cycles arcade, scope, arty and back, so the mobile
            // sniper button reaches the overhead view on a second tap too.
            if (_view.ArtyMode)
            {
                _view.SetArtyMode(false);
                return;
            }

            if (_view.SniperMode && _view.ArtyAvailable)
            {
                _view.SetArtyMode(true);
                return;
            }

            var on = !_view.SniperMode;

            // "if on and tank.blocked: return" in the source.
            if (on && _tank != null && _tank.Blocked)
            {
                return;
            }

            _view.SetSniperMode(on);
        }

        private void RequestMenu()
        {
            if (MenuRequested != null)
            {
                MenuRequested();
            }
        }

        private static RectTransform CreateCanvas(Transform parent, bool mobile)
        {
            var go = new GameObject("Canvas", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.pixelPerfect = false;
            // ui_player.tscn puts mobile_shit after the non-interactive HUD.
            canvas.sortingOrder = BattleHud.SortingOrder + 1;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            // SaveSettings.ui_scale_battle, not the menu factor: the source
            // switches the UI context before the battle is loaded, so the HUD
            // is laid out against the authored 1040x550 canvas on a phone.
            scaler.referenceResolution = new Vector2(
                UiLayout.BattleReferenceWidth(mobile),
                UiLayout.BattleReferenceHeight(mobile));
            // SaveSettings.ui_scale_battle rides on top of the authored
            // window, and stays live so the row applies without a restart.
            BlitzLegacy.Runtime.Ui.BattleUiScale.Register(
                scaler, scaler.referenceResolution);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            scaler.referencePixelsPerUnit = 100f;
            go.AddComponent<GraphicRaycaster>();
            return (RectTransform)go.transform;
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null)
            {
                return;
            }

            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            var input = go.AddComponent<InputSystemUIInputModule>();
            input.AssignDefaultActions();
        }
    }
}
