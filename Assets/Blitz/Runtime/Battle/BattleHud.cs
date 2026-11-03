using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Code-first port of the always-visible half of ui_player.tscn: both
    /// crosshairs, the reload ring, HP, speed, distance, team header and clock.
    /// The geometry below is copied from the source Control offsets and is
    /// interpreted by the same Expand CanvasScaler as the other ported screens.
    ///
    /// Health, reload, dispersion and the aim highlight stay plain setters, and
    /// <c>RefreshCombatReadouts</c>/<c>RefreshCombatAim</c> feed them from the
    /// live <see cref="HealthComponent"/>, <see cref="BattleGun"/> and gun
    /// trace. The minimap, killfeed and damage flags still wait for their own
    /// slice.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(2)]
    public sealed class BattleHud : MonoBehaviour
    {
        public const int SortingOrder = 4000;

        private const string SpeedFormatKey = "battle:hud/speed";
        private const string DistanceFormatKey = "battle:hud/distance";
        private const string DistanceUnknownKey = "battle:hud/distance_unknown";
        private const string HealthFormatKey = "battle:hud/health";

        private static readonly Color AllyScore =
            new Color(0.06666667f, 0.9529412f, 0.0627451f, 1f);

        private static readonly Color EnemyScore =
            new Color(0.8156863f, 0.07058824f, 0.07058824f, 1f);

        private static readonly Color LabelShadow =
            new Color(0f, 0f, 0f, 1f);

        private static readonly Color ClockShadow =
            new Color(0.103743516f, 0.10374353f, 0.103743516f, 1f);

        private BattleTank _tank;
        private int _teamSize = BattleSession.TeamSize;
        private BattleCamera _view;
        private AtlasRegionTable _atlas;
        private LocalizationTable _lang;
        private Font _bold;
        private RectTransform _root;
        private RectTransform _dynamicCrosshair;
        private RectTransform _staticCrosshair;
        private RectMask2D _staticMask;
        private Image _dynamicCrosshairImage;
        private Image _staticCrosshairLit;
        private readonly Image[] _dots = new Image[HudTuning.DotCount];
        private readonly List<BattleTankPlate> _tankPlates =
            new List<BattleTankPlate>();
        private readonly List<Image> _allyTeamIcons = new List<Image>();
        private readonly List<Image> _enemyTeamIcons = new List<Image>();
        private readonly List<BattleTank> _headerAllies = new List<BattleTank>();
        private readonly List<BattleTank> _headerEnemies = new List<BattleTank>();
        private RectTransform _allyTeamBox;
        private RectTransform _enemyTeamBox;
        private RectTransform _healthRoot;
        private Image _healthFill;
        private Text _healthText;
        private Text _speedText;
        private Text _distanceText;
        private Text _reloadText;
        private Shadow _reloadShadow;
        private Text _scoreAlly;
        private Text _scoreEnemy;
        private Text _clockMinutes;
        private Text _clockSeconds;
        private float _elapsed;

        /// <summary>
        /// Global.MAX_BATTLE_TIME_MIN in seconds; 0 or less is the endless
        /// Sandbox battle, which Godot shows as "-:--".
        /// </summary>
        private float _timeLimit = BattleRoster.DefaultTimeLimitSeconds;

        /// <summary>Tanks the roster reported destroyed, even before Health settles.</summary>
        private readonly HashSet<BattleTank> _destroyedTanks = new HashSet<BattleTank>();

        /// <summary>Battle modes that already redraw the header when their crew returns.</summary>
        private readonly HashSet<BattleHudMode> _headerWatches = new HashSet<BattleHudMode>();

        /// <summary>Icon cell width of a header that counts lives; -1 keeps the classic native size.</summary>
        private float _livesSlotWidth = -1f;
        private float _nextHudUpdate;
        private float _nextClockUpdate;
        private float _nextFpsUpdate;
        private Text _fpsText;
        private Text _pingText;
        private float _nextPingUpdate;
        private int _fpsFrames;
        private float _fpsWindow;
        private float _reloadRemaining;
        private float _reloadTotal;
        private bool _canShoot;
        private bool _clockRunning = true;
        private BattleShellSelector _shells;
        private BattleGunBurstUi _burst;
        private BattleGunBurst _burstGun;
        private BattleDamagePanel _damagePanel;
        private BattleWaterIndicator _waterIndicator;
        private BattleDamageMarkers _damageMarkers;
        private BattleStatusRow _statusRow;

        // Authored places of the bottom readouts, so the damage stack can lift
        // them without the offsets drifting frame after frame.
        private Vector2 _healthHome;
        private Vector2 _speedHome;
        private Vector2 _distanceHome;
        private float _bottomLift = -1f;

        public RectTransform Root { get { return _root; } }

        /// <summary>The ability status row above the health bar, filled by the battle mode.</summary>
        public BattleStatusRow StatusRow { get { return _statusRow; } }

        /// <summary>Local observer camera, also for rockets fired by remote/bot tanks.</summary>
        public Camera ActiveCamera { get { return _view != null ? _view.Active : Camera.main; } }

        /// <summary>The module and crew plates of the bottom HUD.</summary>
        public BattleDamagePanel DamagePanel { get { return _damagePanel; } }

        /// <summary>The 4.8.0 <c>DestroyTimer</c> drowning countdown.</summary>
        public BattleWaterIndicator WaterIndicator { get { return _waterIndicator; } }

        /// <summary>The hit direction markers round the crosshair.</summary>
        public BattleDamageMarkers DamageMarkers { get { return _damageMarkers; } }

        public RectTransform DynamicCrosshair { get { return _dynamicCrosshair; } }

        public RectTransform StaticCrosshair { get { return _staticCrosshair; } }

        public Image HealthFill { get { return _healthFill; } }

        public Text HealthLabel { get { return _healthText; } }

        public Text SpeedLabel { get { return _speedText; } }

        public Text DistanceLabel { get { return _distanceText; } }

        public Text ReloadLabel { get { return _reloadText; } }

        public Text FpsLabel { get { return _fpsText; } }
        public Text PingLabel { get { return _pingText; } }

        public Text AllyScoreLabel { get { return _scoreAlly; } }

        public Text EnemyScoreLabel { get { return _scoreEnemy; } }

        public int CrosshairDotCount { get { return _dots.Length; } }

        public bool AimLit { get; private set; }

        /// <summary>
        /// The source builds the battle UI with the map, but the map loading
        /// screen covers the whole viewport until the intro hands over, so the
        /// HUD (crosshair included) must not be visible before that.
        /// </summary>
        public bool Visible { get; private set; }

        /// <summary>
        /// <c>_combat_hud_alive</c>: false once the player's tank is gone, when
        /// the aiming and driving readouts make no sense any more. Score,
        /// killfeed, clock and menu stay up for the observer.
        /// </summary>
        public bool CombatVisible { get; private set; } = true;

        /// <summary>
        /// <c>set_combat_hud_visible</c>, the ported half of the node list:
        /// crosshair_dynamic, crosshair_static_handle, hp, ui_speed, ui_dist,
        /// reload and shells_equipment. The mobile widgets in the list live on
        /// the controls layer here and are hidden by
        /// <see cref="BattleControls.SetCombatVisible" />.
        /// </summary>
        public void SetCombatVisible(bool on)
        {
            CombatVisible = on;
            SetNodeVisible(_dynamicCrosshair, on);
            SetNodeVisible(_staticCrosshair, on);
            SetNodeVisible(_healthRoot, on);
            SetNodeVisible(
                _speedText == null ? null : _speedText.rectTransform, on);
            SetNodeVisible(
                _distanceText == null ? null : _distanceText.rectTransform, on);
            SetNodeVisible(
                _reloadText == null ? null : _reloadText.rectTransform, on);
            if (_shells != null)
            {
                _shells.SetVisible(Visible && on);
            }

            if (_burst != null) { _burst.SetVisible(on); }

            if (_damageMarkers != null) { _damageMarkers.SetCombatVisible(on); }
            if (_waterIndicator != null) { _waterIndicator.SetCombatVisible(on); }
        }

        private static void SetNodeVisible(RectTransform node, bool on)
        {
            if (node != null)
            {
                node.gameObject.SetActive(on);
            }
        }

        public void SetVisible(bool visible)
        {
            Visible = visible;
            if (_root != null)
            {
                _root.gameObject.SetActive(visible);
            }

            // ui_player_battle.gd toggles "shells_equipment" together with the
            // rest of the HUD nodes, even though the rack is built on the
            // controls layer here so taps reach it.
            if (_shells != null)
            {
                _shells.SetVisible(visible && CombatVisible);
            }
        }

        /// <summary>
        /// Registers the shell rack so it follows the HUD's own visibility.
        /// </summary>
        public void AttachShellSelector(BattleShellSelector shells)
        {
            _shells = shells;
            if (_shells != null)
            {
                _shells.SetVisible(Visible && CombatVisible);
            }
        }

        public int TeamIconCount { get; private set; }

        public int TankPlateCount { get { return _tankPlates.Count; } }

        public static BattleHud Create(
            BattleTank tank, BattleCamera view, bool mobile,
            int teamSize = BattleSession.TeamSize)
        {
            var go = new GameObject("BattleHud");
            if (tank != null)
            {
                go.transform.SetParent(tank.transform, false);
            }

            var hud = go.AddComponent<BattleHud>();
            hud._tank = tank;
            hud._view = view;
            hud._teamSize = Mathf.Max(1, teamSize);
            hud.Build(mobile);
            return hud;
        }

        public Image GetCrosshairDot(int index)
        {
            return index >= 0 && index < _dots.Length ? _dots[index] : null;
        }

        public BattleTankPlate GetTankPlate(int index)
        {
            return index >= 0 && index < _tankPlates.Count ? _tankPlates[index] : null;
        }

        /// <summary>Adds the source external plate for one non-player tank.</summary>
        public BattleTankPlate AddTankPlate(BattleTank tank)
        {
            if (tank == null || tank == _tank || _root == null)
            {
                return null;
            }

            for (var i = 0; i < _tankPlates.Count; i++)
            {
                if (_tankPlates[i] != null && _tankPlates[i].Tank == tank)
                {
                    return _tankPlates[i];
                }
            }

            var displayName = tank.Card != null
                ? _lang.GetTankName(tank.Card, true)
                : string.Empty;
            var plate = BattleTankPlate.Create(
                _root, tank, _tank, _view, _atlas, _bold, displayName);
            if (plate != null)
            {
                _tankPlates.Add(plate);
            }

            return plate;
        }

        /// <summary>update_health from ui_player_battle.gd.</summary>
        public void SetHealth(int health, int maxHealth)
        {
            var safeMax = Mathf.Max(1, maxHealth);
            var safeHealth = Mathf.Clamp(health, 0, safeMax);
            _healthFill.fillAmount = safeHealth / (float)safeMax;

            float red;
            float green;
            float blue;
            HudTuning.HealthColour(safeHealth, safeMax, out red, out green, out blue);
            _healthFill.color = new Color(red, green, blue, 1f);
            _healthText.text = HudTuning.HealthText(
                safeHealth, safeMax, Str(HealthFormatKey));
        }

        /// <summary>
        /// Feeds get_reload_remaining / get_reload_total / can_shoot into the
        /// label and 35-dot ring. Remaining equals total while loaded in Godot.
        /// </summary>
        public void SetReload(float remaining, float total, bool canShoot)
        {
            _reloadTotal = Mathf.Max(0f, total);
            _reloadRemaining = Mathf.Clamp(remaining, 0f, _reloadTotal);
            _canShoot = canShoot;

            _reloadText.text = HudTuning.ReloadText(_reloadRemaining);
            var complete = canShoot;
            _reloadText.color = complete
                ? ReloadCompleteColour()
                : ReloadColour();
            _reloadShadow.effectColor = complete ? Color.white : Color.black;
            RefreshDots();
        }

        /// <summary>The unbounded screen-space diameter term from _process.</summary>
        public void SetDispersion(float dispersion)
        {
            var radius = HudTuning.ClampDispersion(dispersion);
            for (var i = 0; i < _dots.Length; i++)
            {
                float x;
                float y;
                HudTuning.DotPosition(i, radius, out x, out y);
                // Godot UI Y points down; RectTransform local Y points up.
                _dots[i].rectTransform.anchoredPosition = new Vector2(x, -y);
            }
        }

        /// <summary>Enemy under the static sight switches both source textures.</summary>
        public void SetAimLit(bool lit)
        {
            AimLit = lit;
            _staticCrosshairLit.gameObject.SetActive(lit);
            _dynamicCrosshairImage.sprite = Art(
                lit ? "battle_crosshair_dyn_lit" : "battle_crosshair_dyn");
        }

        public void SetTeamScores(int ally, int enemy)
        {
            _scoreAlly.text = Mathf.Max(0, ally).ToString();
            _scoreEnemy.text = Mathf.Max(0, enemy).ToString();
        }

        /// <summary>Mirrors update_team_boxes after one roster slot is destroyed.</summary>
        public void SetTeamTankAlive(bool enemy, int slot, bool alive)
        {
            var icons = enemy ? _enemyTeamIcons : _allyTeamIcons;
            if (slot < 0 || slot >= icons.Count || icons[slot] == null)
            {
                return;
            }

            if (_headerAllies.Count > 0 || _headerEnemies.Count > 0)
            {
                RefreshTeamRoster();
                return;
            }
            icons[slot].sprite = Art(alive
                ? (enemy ? "battle_tank_icon_06" : "battle_tank_icon_02")
                : "battle_tank_icon_014");
        }

        public void SetBattleTime(float seconds)
        {
            _elapsed = Mathf.Max(0f, seconds);
            RefreshClock();
        }

        /// <summary>The battle length the clock counts down from; 0 = endless.</summary>
        public void SetTimeLimit(float seconds)
        {
            _timeLimit = seconds;
            RefreshClock();
        }

        /// <summary>Seconds left on the clock, or -1 for an endless battle.</summary>
        public float TimeRemaining
        {
            get { return _timeLimit > 0f ? Mathf.Max(0f, _timeLimit - _elapsed) : -1f; }
        }

        public string ClockText
        {
            get { return _clockMinutes.text + ":" + _clockSeconds.text; }
        }

        /// <summary>
        /// ui_player_battle.gd prints Battle.get_time_elapsed(), which despite
        /// its name is start + MAX_BATTLE_TIME_MIN * 60 - now: the clock runs
        /// down to 0:00, and an endless battle reads "-:--".
        /// </summary>
        private void RefreshClock()
        {
            if (_clockMinutes == null || _clockSeconds == null) return;
            if (_timeLimit <= 0f)
            {
                _clockMinutes.text = "-";
                _clockSeconds.text = "--";
                return;
            }
            var remaining = TimeRemaining;
            _clockMinutes.text = HudTuning.ClockMinutesText(remaining);
            _clockSeconds.text = HudTuning.ClockSecondsText(remaining);
        }

        public void SetClockRunning(bool running)
        {
            _clockRunning = running;
        }

        private void Build(bool mobile)
        {
            _atlas = AtlasRegionTable.Parse(
                BlitzResources.LoadText(BlitzResources.AtlasRegionsData));
            _lang = LocalizationTable.Parse(
                BlitzResources.LoadText(BlitzResources.LanguageData));
            if (BlitzBootstrap.Current != null && BlitzBootstrap.Current.Settings != null)
            {
                _lang.Language = BlitzBootstrap.Current.Settings.Language;
            }

            _bold = BlitzResources.LoadFont(BlitzResources.BoldFontName);

            var canvas = CreateCanvas(transform, mobile);
            _root = UiFactory.Node("ui_root", canvas);
            GodotRect.FullRect(_root);

            // Source sibling order: dynamic sight, markers, static sight, header, labels,
            // then health. Later siblings draw in front in both Godot and uGUI.
            BuildDynamicCrosshair();
            // markers: right after crosshair_dynamic, under the static sight.
            _damageMarkers = BattleDamageMarkers.Create(
                _root, Art(DamageMarkerMath.Region), _tank, () => ActiveCamera);
            BuildStaticCrosshair();
            BuildHeader();
            BuildReadouts();
            BuildHealth();
            // BottomHUD/VerticalContainer: the two damage plates sit under the
            // readouts and above the bottom edge.
            _damagePanel = BattleDamagePanel.Create(_root, _atlas, _tank);
            // HUDLayer.yaml DestroyTimer: the drowning countdown under the sight.
            _waterIndicator = BattleWaterIndicator.Create(_root, _atlas, _lang, _bold, _tank);
            // The damage plates are created after the bottom readouts, so they
            // would draw over the health number while they slide out. Keep the
            // health plate and the speed/distance labels on top of them.
            if (_healthRoot != null) { _healthRoot.SetAsLastSibling(); }
            if (_speedText != null) { _speedText.rectTransform.SetAsLastSibling(); }
            if (_distanceText != null) { _distanceText.rectTransform.SetAsLastSibling(); }
            _healthHome = _healthRoot != null
                ? _healthRoot.anchoredPosition : Vector2.zero;
            _speedHome = _speedText != null
                ? _speedText.rectTransform.anchoredPosition : Vector2.zero;
            _distanceHome = _distanceText != null
                ? _distanceText.rectTransform.anchoredPosition : Vector2.zero;
            // GunComponentBurst.setup adds ui_gun_burst.tscn to %UI_player
            // after the scene's own children, so the fan draws on top.
            BuildBurstIndicator();
            // The battle mode's ability status row above the health bar; the standard
            // battle leaves it empty and hidden.
            _statusRow = BattleStatusRow.Create(_root, _tank);

            // The hull the tank was built with already holds hull plus turret;
            // the card is only the fallback for a HUD built without one.
            var maxHealth = _tank != null && _tank.Health != null
                ? Mathf.Max(1, _tank.Health.MaxHealth)
                : (_tank != null && _tank.Card != null
                    ? _tank.Card.TotalHealthWith(_tank.TurretModule)
                    : 1);
            // The mounted barrel, because the same gun reloads at a different
            // speed in the stock turret than in the top one.
            var gun = _tank != null ? _tank.GunInfo : null;
            var reload = gun != null ? gun.Reload : 0f;

            SetHealth(maxHealth, maxHealth);
            SetReload(reload, reload, true);
            SetDispersion(HudTuning.DispersionMin);
            Visible = true;
            SetAimLit(false);
            SetTeamScores(0, 0);
            SetBattleTime(0f);
            RefreshMotion();

            Debug.Log(
                "[Blitz] battle HUD built: regions=" + _atlas.Count +
                " strings=" + _lang.Count +
                " dots=" + CrosshairDotCount +
                " team_icons=" + TeamIconCount);
        }

        /// <summary>
        /// <c>GunComponentBurst.setup</c>: the magazine fan, its round counter
        /// and the two scene connections - sniper mode widens the fan, death
        /// hides it.
        /// </summary>
        private void BuildBurstIndicator()
        {
            _burstGun = _tank != null ? _tank.Gun as BattleGunBurst : null;
            if (_burstGun == null) { return; }

            _burst = BattleGunBurstUi.Create(
                _root, Art("battle_burst_bullet"), Art("battle_burst_bullet_empty"));
            _burst.Setup(_burstGun.Bursts.Count);
            _burstGun.BurstsChanged += OnBurstsChanged;

            if (_view != null) { _view.SniperModeSwitched += OnBurstSniperMode; }

            if (_tank.Health != null) { _tank.Health.OnDeath += OnBurstOwnerDeath; }
        }

        /// <summary>The magazine indicator, once the gun loads a clip.</summary>
        public BattleGunBurstUi BurstIndicator { get { return _burst; } }

        private void OnBurstsChanged(int bursts)
        {
            if (_burst != null) { _burst.UpdateBursts(bursts); }
        }

        private void OnBurstSniperMode(bool on)
        {
            if (_burst != null) { _burst.SniperMode(on); }
        }

        private void OnBurstOwnerDeath()
        {
            if (_burst != null) { _burst.SetVisible(false); }
        }

        private void OnDestroy()
        {
            if (_burstGun != null) { _burstGun.BurstsChanged -= OnBurstsChanged; }

            if (_view != null) { _view.SniperModeSwitched -= OnBurstSniperMode; }

            if (_tank != null && _tank.Health != null)
            {
                _tank.Health.OnDeath -= OnBurstOwnerDeath;
            }
        }

        private void BuildDynamicCrosshair()
        {
            _dynamicCrosshair = UiFactory.Node("crosshair_dynamic", _root);
            GodotRect.Centered(_dynamicCrosshair, 0f, 0f, 0f, 0f);
            _dynamicCrosshair.localScale = new Vector3(
                HudTuning.CrosshairDynScale,
                HudTuning.CrosshairDynScale,
                1f);

            _dynamicCrosshairImage = UiFactory.Picture(
                "reticle", _dynamicCrosshair, Art("battle_crosshair_dyn"));
            CenterNative(_dynamicCrosshairImage.rectTransform,
                _dynamicCrosshairImage.sprite, 0f, -HudTuning.CrosshairDynOffsetY);

            for (var i = 0; i < _dots.Length; i++)
            {
                var dot = UiFactory.Picture(
                    "reload_dot_" + i.ToString("00"),
                    _dynamicCrosshair,
                    Art("battle_dot_green"));
                CenterNative(dot.rectTransform, dot.sprite, 0f, 0f);
                dot.rectTransform.localScale = new Vector3(
                    HudTuning.DotScale, HudTuning.DotScale, 1f);
                _dots[i] = dot;
            }
        }

        private void BuildStaticCrosshair()
        {
            _staticCrosshair = UiFactory.Node("crosshair_static_handle", _root);
            var width = HudTuning.CrosshairHandleRight - HudTuning.CrosshairHandleLeft;
            var height = HudTuning.CrosshairHandleBottom - HudTuning.CrosshairHandleTop;
            var pivot = new Vector2(
                HudTuning.CrosshairHandlePivotX / width,
                1f - HudTuning.CrosshairHandlePivotY / height);
            GodotRect.Apply(
                _staticCrosshair,
                0.5f, 0.5f, 0.5f, 0.5f,
                HudTuning.CrosshairHandleLeft,
                HudTuning.CrosshairHandleTop,
                HudTuning.CrosshairHandleRight,
                HudTuning.CrosshairHandleBottom,
                pivot);
            _staticCrosshair.localScale = new Vector3(
                HudTuning.CrosshairHandleScale,
                HudTuning.CrosshairHandleScale,
                1f);
            _staticMask = _staticCrosshair.gameObject.AddComponent<RectMask2D>();
            _staticMask.enabled = false;

            var normal = UiFactory.Picture(
                "crosshair_static", _staticCrosshair,
                Art("battle_crosshair_static"));
            GodotRect.Centered(
                normal.rectTransform,
                HudTuning.CrosshairStaticLeft,
                HudTuning.CrosshairStaticTop,
                HudTuning.CrosshairStaticRight,
                HudTuning.CrosshairStaticBottom);

            _staticCrosshairLit = UiFactory.Picture(
                "crosshair_static_lit", _staticCrosshair,
                Art("battle_crosshair_static_lit"));
            GodotRect.Centered(
                _staticCrosshairLit.rectTransform,
                HudTuning.CrosshairStaticLeft,
                HudTuning.CrosshairStaticLitTop,
                HudTuning.CrosshairStaticRight,
                HudTuning.CrosshairStaticLitBottom);
        }

        private void BuildHeader()
        {
            var header = UiFactory.Node("total_tanks", _root);
            GodotRect.Apply(header, 0.5f, 0f, 0.5f, 0f, 0f, 0f, 0f, 0f);

            var bg1 = UiFactory.Picture("bg1", header, Art("battle_tanks_bg"));
            GodotRect.Apply(bg1.rectTransform, 0, 0, 0, 0, -226, 0, -42.66667f, 25, GodotRect.TopLeftPivot);
            bg1.rectTransform.localScale = new Vector3(1.1f, 1, 1);
            var bg2 = UiFactory.Picture("bg2", header, Art("battle_tanks_bg"));
            GodotRect.Apply(bg2.rectTransform, 0, 0, 0, 0, 222, 0, 405.3333f, 25, GodotRect.TopLeftPivot);
            bg2.rectTransform.localScale = new Vector3(-1.1f, 1, 1);
            var clock = UiFactory.Picture("bg_time", header, Art("battle_clock_bg"));
            GodotRect.Apply(clock.rectTransform, 0, 0, 0, 0, -35, 0, 93, 70, GodotRect.TopLeftPivot);
            clock.rectTransform.localScale = new Vector3(.52f, .52f, 1);
            _allyTeamBox = BuildTeamBox(header, false);
            _enemyTeamBox = BuildTeamBox(header, true);

            _scoreEnemy = Label(
                "score_enemy", header, 22, EnemyScore, TextAnchor.MiddleCenter,
                0f, 0f, 0f, 0f, 33f, 1f, 48f, 26f);
            _scoreAlly = Label(
                "score_ally", header, 22, AllyScore, TextAnchor.MiddleCenter,
                0f, 0f, 0f, 0f, -51f, 1f, -36f, 26f);

            _clockMinutes = Label(
                "time_remaining_min", header, 17, Color.white, TextAnchor.MiddleRight,
                0f, 0f, 0f, 0f, -51f, 0f, -10f, 36f);
            AddShadow(_clockMinutes, ClockShadow, 1f, -1f);

            var separator = Label(
                "time_remaining_separator", header, 17, Color.white,
                TextAnchor.MiddleCenter, 0f, 0f, 0f, 0f,
                -22f, -1f, 12f, 35f);
            separator.text = ":";
            AddShadow(separator, ClockShadow, 1f, -1f);

            _clockSeconds = Label(
                "time_remaining_sec", header, 17, Color.white, TextAnchor.MiddleLeft,
                0f, 0f, 0f, 0f, -1f, 0f, 40f, 36f);
            AddShadow(_clockSeconds, ClockShadow, 1f, -1f);

            for (var slot = 0; slot < _teamSize; slot++)
            {
                BuildTeamIcon(header, false, slot);
                BuildTeamIcon(header, true, slot);
            }
        }

        private static RectTransform BuildTeamBox(RectTransform header, bool enemy)
        {
            var box = UiFactory.Node(enemy ? "box_enemy" : "box_ally", header);
            if (enemy)
            {
                GodotRect.Apply(box, 0f, 0f, 0f, 0f, 63f, 0f, 331f, 35f, GodotRect.TopLeftPivot);
            }
            else
            {
                GodotRect.Apply(box, 0f, 0f, 0f, 0f, -228f, 1f, 40f, 36f, GodotRect.TopLeftPivot);
            }
            box.localScale = new Vector3(0.6f, 0.6f, 1f);

            // Authored HBox: dead tanks remain as dark class marks, never removed.
            var layout = box.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 4f;
            layout.padding = new RectOffset();
            layout.childAlignment = enemy
                ? TextAnchor.UpperLeft
                : TextAnchor.UpperRight;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            return box;
        }

        public static string TeamClassRegion(BlitzLegacy.Core.Tanks.TankClass kind, bool enemy, bool dead)
        {
            var suffix = kind == BlitzLegacy.Core.Tanks.TankClass.TankDestroyer ? 0
                : kind == BlitzLegacy.Core.Tanks.TankClass.Light ? 1
                : kind == BlitzLegacy.Core.Tanks.TankClass.Medium ? 2 : 3;
            if (dead) return "battle_tank_icon_" + (13 + suffix).ToString("000");
            if (enemy) return "battle_tank_icon_" + (5 + suffix).ToString("00");
            return suffix == 0 ? "battle_tank_icon" : "battle_tank_icon_" + (1 + suffix).ToString("00");
        }

        public void SetTeamRoster(IList<BattleTank> allies, IList<BattleTank> enemies)
        {
            _headerAllies.Clear(); _headerEnemies.Clear(); _destroyedTanks.Clear();
            foreach (var mode in _headerWatches) mode.UnwatchHeader(RefreshTeamRoster);
            _headerWatches.Clear();
            if (allies != null) _headerAllies.AddRange(allies);
            if (enemies != null) _headerEnemies.AddRange(enemies);
            RefreshTeamRoster();
        }

        /// <summary>
        /// The roster's death event. The last kill also ends the battle in the
        /// same call, so the icon must not wait for Health.Dead to be read
        /// back later: the tank is marked dead here and the row redrawn.
        /// </summary>
        public void MarkTankDestroyed(BattleTank tank)
        {
            if (tank != null) _destroyedTanks.Add(tank);
            RefreshTeamRoster();
        }

        /// <summary>
        /// The lives the battle mode counts for this crew in the header (Uprising:
        /// remaining_lives()), or -1 for one per tank still alive.
        /// </summary>
        private static int HeaderLives(BattleTank tank)
        {
            return tank != null ? BattleHudMode.Of(tank).HeaderLives : -1;
        }

        private bool ShownDead(BattleTank tank)
        {
            if (tank == null) return true;
            // One icon per tank when the mode counts lives: the score already
            // counts every life, so the icon only blackens once the last one is spent.
            var lives = HeaderLives(tank);
            if (lives >= 0) return lives <= 0;
            return _destroyedTanks.Contains(tank) || tank.ShownDeadToLocalPlayer;
        }

        /// <summary>
        /// _battle.gd get_team_lives(): the lives the mode counts (Uprising),
        /// otherwise one per tank still alive.
        /// </summary>
        private int TeamLives(List<BattleTank> side)
        {
            var total = 0;
            for (var i = 0; i < side.Count; i++)
            {
                var tank = side[i];
                var lives = HeaderLives(tank);
                if (lives >= 0) total += lives;
                else if (!ShownDead(tank)) total++;
            }
            return total;
        }

        private bool RosterCountsLives()
        {
            for (var i = 0; i < _headerAllies.Count; i++) if (HeaderLives(_headerAllies[i]) >= 0) return true;
            for (var i = 0; i < _headerEnemies.Count; i++) if (HeaderLives(_headerEnemies[i]) >= 0) return true;
            return false;
        }

        private void WatchHeaders(List<BattleTank> side)
        {
            for (var i = 0; i < side.Count; i++)
            {
                if (side[i] == null) continue;
                var mode = BattleHudMode.Of(side[i]);
                if (_headerWatches.Add(mode)) mode.WatchHeader(RefreshTeamRoster);
            }
        }

        /// <summary>
        /// update_team_boxes in Uprising, for every header that counts lives: the
        /// boxes pack at separation 1, the two scores sit 28 wide either side of
        /// the clock, and each icon is squeezed into min(30, 268 / slots - 1) x 35
        /// so a full roster fits.
        /// </summary>
        private void ApplyLivesLayout(bool lives)
        {
            var spacing = lives ? 1f : 4f;
            var allyLayout = _allyTeamBox.GetComponent<HorizontalLayoutGroup>();
            var enemyLayout = _enemyTeamBox.GetComponent<HorizontalLayoutGroup>();
            if (allyLayout != null) allyLayout.spacing = spacing;
            if (enemyLayout != null) enemyLayout.spacing = spacing;
            if (lives)
            {
                SetLabelX(_scoreAlly, -63f, -35f);
                // score_enemy.position.x = box_enemytanks.position.x - width - 6
                SetLabelX(_scoreEnemy, 63f - 28f - 6f, 63f - 6f);
            }
            else
            {
                SetLabelX(_scoreAlly, -51f, -36f);
                SetLabelX(_scoreEnemy, 33f, 48f);
            }
        }

        private static void SetLabelX(Text label, float left, float right)
        {
            if (label == null) return;
            var rt = label.rectTransform;
            rt.offsetMin = new Vector2(left, rt.offsetMin.y);
            rt.offsetMax = new Vector2(right, rt.offsetMax.y);
        }

        /// <summary>Public for the battle end: redraw from the final roster state.</summary>
        public void RefreshTeamRoster()
        {
            ClearHeader(_allyTeamBox); ClearHeader(_enemyTeamBox);
            _allyTeamIcons.Clear(); _enemyTeamIcons.Clear(); TeamIconCount = 0;
            var lives = RosterCountsLives();
            ApplyLivesLayout(lives);
            WatchHeaders(_headerAllies); WatchHeaders(_headerEnemies);
            _livesSlotWidth = lives
                ? Mathf.Min(30f, 268f / Mathf.Max(1, Mathf.Max(_headerAllies.Count, _headerEnemies.Count)) - 1f)
                : -1f;
            BuildRosterSide(_headerAllies, false);
            BuildRosterSide(_headerEnemies, true);
            // The numbers beside the rows run down: tanks still alive, or every
            // life the side has left when the mode counts lives, as get_team_lives.
            if (_headerAllies.Count > 0 || _headerEnemies.Count > 0)
            {
                SetTeamScores(TeamLives(_headerAllies), TeamLives(_headerEnemies));
            }
        }

        private static void ClearHeader(Transform box)
        {
            for (var i = box.childCount - 1; i >= 0; i--)
            {
                var child = box.GetChild(i).gameObject;
                child.SetActive(false);
                if (Application.isPlaying) Destroy(child); else DestroyImmediate(child);
            }
        }

        private void BuildRosterSide(List<BattleTank> source, bool enemy)
        {
            var sorted = new List<BattleTank>(source);
            sorted.Sort((a, b) => {
                var aDead = ShownDead(a); var bDead = ShownDead(b);
                if (aDead != bDead) return aDead ? 1 : -1;
                var ca = a.Card.Class == BlitzLegacy.Core.Tanks.TankClass.TankDestroyer ? -1 : (int)a.Card.Class;
                var cb = b.Card.Class == BlitzLegacy.Core.Tanks.TankClass.TankDestroyer ? -1 : (int)b.Card.Class;
                var compared = cb.CompareTo(ca);
                return compared != 0 ? compared : source.IndexOf(a).CompareTo(source.IndexOf(b));
            });
            if (!enemy) sorted.Reverse();
            for (var i = 0; i < sorted.Count; i++) BuildTeamIcon(null, enemy, i,
                TeamClassRegion(sorted[i].Card.Class, enemy, ShownDead(sorted[i])));
        }

        private void BuildTeamIcon(RectTransform header, bool enemy, int slot, string region = null)
        {
            var box = enemy ? _enemyTeamBox : _allyTeamBox;
            var sprite = Art(region ?? (enemy ? "battle_tank_icon_06" : "battle_tank_icon_02"));
            var cell = UiFactory.Node("slot_" + slot, box);
            cell.sizeDelta = new Vector2(_livesSlotWidth > 0f ? _livesSlotWidth : sprite.rect.width, 35);
            var image = UiFactory.KeepCentered(cell, sprite);
            if (_livesSlotWidth > 0f && sprite != null && sprite.rect.width > 0f && sprite.rect.height > 0f)
            {
                // EXPAND_IGNORE_SIZE + STRETCH_KEEP_ASPECT_CENTERED
                var fit = Mathf.Min(_livesSlotWidth / sprite.rect.width, 35f / sprite.rect.height);
                image.rectTransform.sizeDelta = new Vector2(sprite.rect.width * fit, sprite.rect.height * fit);
            }
            image.name = (enemy ? "tank_enemy_" : "tank_ally_") + (slot + 1);
            (enemy ? _enemyTeamIcons : _allyTeamIcons).Add(image);
            TeamIconCount++;
        }

        private void BuildReadouts()
        {
            // ui_speed keeps Godot's default vertical_alignment (TOP): the
            // 49 px box reaches down over the health plate, and only its top
            // line carries the text. Centring it vertically dropped the speed
            // half a box lower, under the health bar.
            _speedText = Label(
                "ui_speed", _root, HudTuning.SpeedFontSize, Color.white,
                TextAnchor.UpperCenter, 0.5f, 1f, 0.5f, 1f,
                HudTuning.SpeedLeft, HudTuning.SpeedTop,
                HudTuning.SpeedRight, HudTuning.SpeedBottom);
            AddShadow(_speedText, LabelShadow, 1f, -1f);

            _distanceText = Label(
                "ui_dist", _root, HudTuning.DistanceFontSize,
                new Color(
                    HudTuning.DistanceColourR,
                    HudTuning.DistanceColourG,
                    HudTuning.DistanceColourB,
                    1f),
                TextAnchor.MiddleCenter, 0.5f, 0.5f, 0.5f, 0.5f,
                HudTuning.DistanceLeft, HudTuning.DistanceTop,
                HudTuning.DistanceRight, HudTuning.DistanceBottom);
            AddShadow(
                _distanceText, Color.white,
                HudTuning.DistanceShadowX,
                -HudTuning.DistanceShadowY);

            _reloadText = Label(
                "reload", _root, HudTuning.ReloadFontSize, ReloadColour(),
                TextAnchor.MiddleRight, 0.5f, 0.5f, 0.5f, 0.5f,
                HudTuning.ReloadLeft, HudTuning.ReloadTop,
                HudTuning.ReloadRight, HudTuning.ReloadBottom);
            _reloadShadow = AddShadow(_reloadText, Color.black, 1f, -1f);

            // fps_meter from ui_player.tscn: bottom right, no shadow.
            _fpsText = Label(
                "fps_meter", _root, HudTuning.FpsFontSize, Color.white,
                TextAnchor.MiddleCenter, 1f, 1f, 1f, 1f,
                HudTuning.FpsLeft, HudTuning.FpsTop,
                HudTuning.FpsRight, HudTuning.FpsBottom);
            _fpsText.text = HudTuning.FpsText(0);

            // Online only: the round trip, mirrored into the bottom left corner.
            _pingText = Label(
                "ping_meter", _root, HudTuning.FpsFontSize, Color.white,
                TextAnchor.MiddleCenter, 0f, 1f, 0f, 1f,
                HudTuning.PingLeft, HudTuning.FpsTop,
                HudTuning.PingRight, HudTuning.FpsBottom);
            _pingText.text = string.Empty;
            _pingText.gameObject.SetActive(false);
        }

        private void BuildHealth()
        {
            var health = UiFactory.Node("hp", _root);
            _healthRoot = health;
            GodotRect.Apply(
                health,
                0.5f, 1f, 0.5f, 1f,
                HudTuning.HealthLeft,
                HudTuning.HealthTop,
                HudTuning.HealthRight,
                HudTuning.HealthBottom,
                GodotRect.TopLeftPivot);
            health.localScale = new Vector3(
                HudTuning.HealthScale, HudTuning.HealthScale, 1f);

            var background = Picture(
                "hp_back", health, "battle_hp_bar",
                0.5f, 1f, 0.5f, 1f,
                HudTuning.HealthBackLeft,
                HudTuning.HealthBackTop,
                HudTuning.HealthBackRight,
                HudTuning.HealthBackBottom);
            background.raycastTarget = false;

            _healthFill = Picture(
                "hp_bar", health, "battle_hp_back",
                0.5f, 1f, 0.5f, 1f,
                HudTuning.HealthBarLeft,
                HudTuning.HealthBarTop,
                HudTuning.HealthBarRight,
                HudTuning.HealthBarBottom);
            GodotRect.Apply(
                _healthFill.rectTransform,
                0.5f, 1f, 0.5f, 1f,
                HudTuning.HealthBarLeft,
                HudTuning.HealthBarTop,
                HudTuning.HealthBarRight,
                HudTuning.HealthBarBottom,
                GodotRect.TopLeftPivot);
            _healthFill.type = Image.Type.Filled;
            _healthFill.fillMethod = Image.FillMethod.Horizontal;
            _healthFill.fillOrigin = 0;
            _healthFill.fillClockwise = true;
            _healthFill.rectTransform.localScale = new Vector3(
                HudTuning.HealthBarScaleX,
                HudTuning.HealthBarScaleY,
                1f);

            _healthText = Label(
                "hp_text", health, HudTuning.HealthTextFontSize,
                Color.white, TextAnchor.MiddleCenter,
                0.5f, 0.5f, 0.5f, 0.5f,
                HudTuning.HealthTextLeft,
                HudTuning.HealthTextTop,
                HudTuning.HealthTextRight,
                HudTuning.HealthTextBottom);
            AddShadow(
                _healthText,
                LabelShadow,
                HudTuning.HealthTextShadowX,
                -HudTuning.HealthTextShadowY);
        }

        private void Update()
        {
            var now = Time.unscaledTime;
            if (_clockRunning)
            {
                _elapsed += Time.unscaledDeltaTime;
            }

            // The source reads Engine.get_frames_per_second(), an average over
            // the last whole second. Writing the reciprocal of a single frame
            // time instead makes the label swing by tens of frames every tick,
            // so the window is only closed once it is a second long; the label
            // still refreshes on the HUD cadence from the running average.
            _fpsFrames++;
            _fpsWindow += Time.unscaledDeltaTime;
            if (now >= _nextFpsUpdate)
            {
                _nextFpsUpdate = now + HudTuning.FpsUpdateInterval;
                _fpsText.text = HudTuning.FpsText(FramesPerSecond());
            }

            if (now >= _nextPingUpdate)
            {
                _nextPingUpdate = now + HudTuning.PingUpdateInterval;
                var ping = BattleNetHooks.PingMs != null ? BattleNetHooks.PingMs() : -1;
                var online = ping >= 0;
                if (_pingText.gameObject.activeSelf != online) { _pingText.gameObject.SetActive(online); }
                if (online) { _pingText.text = string.Format(Str(HudTuning.PingKey), ping); }
            }

            if (_fpsWindow >= HudTuning.FpsSampleWindow)
            {
                _fpsFrames = 0;
                _fpsWindow = 0f;
            }

            if (now >= _nextClockUpdate)
            {
                _nextClockUpdate = now + HudTuning.ClockUpdateInterval;
                RefreshClock();
            }

            if (now >= _nextHudUpdate)
            {
                _nextHudUpdate = now + HudTuning.HudUpdateInterval;
                RefreshMotion();
                RefreshCombatReadouts();
            }

            // Reload readiness must not linger green for the 0.1 s label
            // cadence after a shot; sample the firing state every render frame.
            RefreshReload();
            RefreshCombatAim();
            _staticMask.enabled = _view != null && _view.SniperMode;
            ProjectDynamicCrosshair();
            LiftBottomReadouts();
            if (_statusRow != null) { _statusRow.Refresh(); }
        }

        /// <summary>
        /// The damage plates and the health plate share the bottom edge of the
        /// HUD, so the plates used to cover the health bar as soon as a module
        /// broke. The readouts ride the damage stack instead: they move up by
        /// exactly as much of it as is currently out.
        /// </summary>
        private void LiftBottomReadouts()
        {
            var lift = _damagePanel != null ? _damagePanel.StackHeight : 0f;
            if (Mathf.Approximately(lift, _bottomLift)) { return; }

            _bottomLift = lift;
            var offset = new Vector2(0f, lift);
            if (_healthRoot != null)
            {
                _healthRoot.anchoredPosition = _healthHome + offset;
            }

            if (_speedText != null)
            {
                _speedText.rectTransform.anchoredPosition = _speedHome + offset;
            }

            if (_distanceText != null)
            {
                _distanceText.rectTransform.anchoredPosition = _distanceHome + offset;
            }
        }

        /// <summary>
        /// <c>Engine.get_frames_per_second()</c>: the frame rate averaged over
        /// the running sample window (up to a second of frames), truncated like
        /// the source's <c>int()</c> cast. Only the very first frames of a
        /// battle, before the window has any samples, fall back to the
        /// reciprocal of the last frame time.
        /// </summary>
        public int FramesPerSecond()
        {
            if (_fpsWindow <= 0f || _fpsFrames <= 0)
            {
                var delta = Time.unscaledDeltaTime;
                return delta > 0f ? (int)(1f / delta) : 0;
            }

            return (int)(_fpsFrames / _fpsWindow);
        }

        private void RefreshMotion()
        {
            var speed = _tank != null && _tank.Suspension != null
                ? _tank.Suspension.DisplaySpeed
                : 0f;
            _speedText.text = HudTuning.SpeedText(speed, Str(SpeedFormatKey));

            var distance = _view != null ? _view.AimTargetDistance : 0f;
            var hit = _view != null && _view.TraceHitLast;
            _distanceText.text = HudTuning.DistanceText(
                distance,
                hit,
                Str(DistanceFormatKey),
                Str(DistanceUnknownKey));
        }

        private void RefreshCombatReadouts()
        {
            if (_tank == null) { return; }

            if (_tank.Health != null)
            {
                SetHealth(_tank.Health.Health, _tank.Health.MaxHealth);
            }

        }

        private void RefreshReload()
        {
            if (_tank != null && _tank.Gun != null)
            {
                SetReload(
                    _tank.Gun.ReloadRemaining(),
                    _tank.Gun.ReloadTotal,
                    _tank.Gun.CanShoot());
            }
        }

        private void RefreshCombatAim()
        {
            if (_tank == null)
            {
                SetAimLit(false);
                return;
            }

            var target = _tank.GunTraceTank;
            SetAimLit(target != null && !target.LooksDeadTo(_tank) && target.Team != _tank.Team);

            var camera = _view != null ? _view.Active : null;
            if (camera == null || _tank.GunTracePoint == Vector3.zero)
            {
                SetDispersion(HudTuning.DispersionMin);
                return;
            }

            var centre = camera.WorldToScreenPoint(_tank.GunTracePoint);
            var near = camera.WorldToScreenPoint(
                _tank.GunTracePoint + _tank.DispersionOffset);
            var far = camera.WorldToScreenPoint(
                _tank.GunTracePoint - _tank.DispersionOffset);
            if (centre.z <= 0f || near.z <= 0f || far.z <= 0f)
            {
                SetDispersion(HudTuning.DispersionMin);
                return;
            }

            var radius = HudTuning.DispersionRadius(
                Vector2.Distance(centre, near),
                Vector2.Distance(centre, far));
            SetDispersion(radius);
        }

        /// <summary>
        /// <c>ui_player_battle.gd:987</c> unprojects <c>tank.gun_trace_result</c>,
        /// the parabola that leaves the barrel, not the camera's aim target. The
        /// aim target is free to sit above or below the gun arc, so following it
        /// would let the reticle drift past the elevation (УВН) and traverse
        /// (УГН) limits while the gun is still clamped.
        /// </summary>
        public static Vector3 ReticlePoint(BattleTank tank)
        {
            if (tank == null) { return Vector3.zero; }

            var trace = tank.GunTracePoint;
            if (trace != Vector3.zero) { return trace; }

            // Before the first trace lands, aim straight down the clamped barrel
            // rather than at the unclamped target.
            var turret = tank.Turret;
            if (turret == null || turret.GunPivot == null) { return tank.Pos(); }

            return turret.GunPosition + turret.GunPivot.forward * AimMath.TraceDistance;
        }

        private void ProjectDynamicCrosshair()
        {
            if (_view == null || _view.Active == null ||
                _tank == null || _tank.Turret == null)
            {
                return;
            }

            // ui_player_battle.gd:988 hides the reticle for a dead tank.
            if (_tank.Dead)
            {
                _dynamicCrosshair.gameObject.SetActive(false);
                return;
            }

            var point = _view.Active.WorldToScreenPoint(ReticlePoint(_tank));
            if (point.z <= 0f)
            {
                _dynamicCrosshair.gameObject.SetActive(false);
                return;
            }

            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _root, point, null, out local))
            {
                _dynamicCrosshair.gameObject.SetActive(false);
                return;
            }

            _dynamicCrosshair.gameObject.SetActive(true);
            _dynamicCrosshair.anchoredPosition = local;
        }

        private void RefreshDots()
        {
            var coefficient = _reloadTotal <= 0f
                ? 1f
                : Mathf.Clamp01(_reloadRemaining / _reloadTotal);

            for (var i = 0; i < _dots.Length; i++)
            {
                string region;
                switch (HudTuning.DotState(i, coefficient, _canShoot))
                {
                    case CrosshairDot.Green:
                        region = "battle_dot_green";
                        break;
                    case CrosshairDot.Yellow:
                        region = "battle_dot_yellow";
                        break;
                    default:
                        region = "battle_dot_red";
                        break;
                }

                _dots[i].sprite = Art(region);
            }
        }

        private Image Picture(
            string name,
            RectTransform parent,
            string region,
            float anchorLeft,
            float anchorTop,
            float anchorRight,
            float anchorBottom,
            float offsetLeft,
            float offsetTop,
            float offsetRight,
            float offsetBottom)
        {
            var image = UiFactory.Picture(name, parent, Art(region));
            GodotRect.Apply(
                image.rectTransform,
                anchorLeft, anchorTop, anchorRight, anchorBottom,
                offsetLeft, offsetTop, offsetRight, offsetBottom);
            return image;
        }

        private Text Label(
            string name,
            RectTransform parent,
            int fontSize,
            Color colour,
            TextAnchor alignment,
            float anchorLeft,
            float anchorTop,
            float anchorRight,
            float anchorBottom,
            float offsetLeft,
            float offsetTop,
            float offsetRight,
            float offsetBottom)
        {
            var label = UiFactory.Label(
                name, parent, _bold, fontSize, colour, alignment, string.Empty);
            GodotRect.Apply(
                label.rectTransform,
                anchorLeft, anchorTop, anchorRight, anchorBottom,
                offsetLeft, offsetTop, offsetRight, offsetBottom);
            return label;
        }

        private static Shadow AddShadow(
            Text text, Color colour, float offsetX, float offsetY)
        {
            var shadow = text.gameObject.AddComponent<Shadow>();
            shadow.effectColor = colour;
            shadow.effectDistance = new Vector2(offsetX, offsetY);
            return shadow;
        }

        private static void CenterNative(
            RectTransform rect, Sprite sprite, float offsetX, float offsetY)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(offsetX, offsetY);
            rect.sizeDelta = sprite == null
                ? Vector2.zero
                : new Vector2(sprite.rect.width, sprite.rect.height);
        }

        private Sprite Art(string name)
        {
            return _atlas == null ? null : _atlas.Get(name);
        }

        private string Str(string key)
        {
            return _lang == null ? key : _lang.Get(key);
        }

        private static Color ReloadColour()
        {
            return new Color(
                HudTuning.ReloadColourR,
                HudTuning.ReloadColourG,
                HudTuning.ReloadColourB,
                1f);
        }

        private static Color ReloadCompleteColour()
        {
            return new Color(
                HudTuning.ReloadCompleteColourR,
                HudTuning.ReloadCompleteColourG,
                HudTuning.ReloadCompleteColourB,
                1f);
        }

        private static RectTransform CreateCanvas(Transform parent, bool mobile)
        {
            var go = new GameObject("Canvas", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.pixelPerfect = false;
            canvas.sortingOrder = SortingOrder;

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
            return (RectTransform)go.transform;
        }
    }
}
