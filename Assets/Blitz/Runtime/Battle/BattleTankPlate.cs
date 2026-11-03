using System.Collections.Generic;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Code-first port of <c>ui_external.tscn</c> and
    /// <c>ui_tank_external.gd</c>. One plate follows every non-player tank,
    /// shows the source class/level art and keeps the top and delayed HP bars
    /// in sync with the tank's real <see cref="HealthComponent"/>.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(3)]
    public sealed class BattleTankPlate : MonoBehaviour
    {
        private static readonly Color AllyColour = new Color(0.396f, 0.867f, 0f, 1f);
        private static readonly Color EnemyColour = new Color(0.96f, 0f, 0f, 1f);
        /// <summary>WoT Blitz team-killer tint: light blue instead of the ally green.</summary>
        public static readonly Color TeamKillerColour = new Color(0.25f, 0.8f, 1f, 1f);
        private static readonly Color ClassShadowColour =
            new Color(0.9607843f, 1f, 1f, 0.49019608f);
        private static readonly Color DamageColour =
            new Color(
                TankPlateTuning.DamageColourR,
                TankPlateTuning.DamageColourG,
                TankPlateTuning.DamageColourB,
                1f);

        private BattleTank _tank;
        private BattleTank _player;
        private BattleCamera _view;
        private RectTransform _canvasRoot;
        private RectTransform _root;
        private CanvasGroup _group;
        private Image _topBar;
        private Image _underBar;
        private Image _classIcon;
        private Image _levelIcon;
        private Text _healthLabel;
        private Text _damageLabel;
        private Text _nameLabel;
        private int _maxHealth;
        private bool _teamKillerShown;
        private int _healthPrevious;
        private float _underHealth;
        private int _damageAccumulated;
        private float _damageAccumulatorDue;
        private float _damageShownAt = -1f;
        private Color _classFlashFrom;
        private float _classFlashAt = -1f;
        private RectTransform _damagedModules;
        private readonly List<Image> _damagedModuleIcons = new List<Image>();

        public BattleTank Tank { get { return _tank; } }
        public RectTransform Root { get { return _root; } }
        public Image TopBar { get { return _topBar; } }
        public Image UnderBar { get { return _underBar; } }
        public Image ClassIcon { get { return _classIcon; } }
        /// <summary>The Roman tier numeral above the name.</summary>
        public Image LevelIcon { get { return _levelIcon; } }
        public Text HealthLabel { get { return _healthLabel; } }
        public Text DamageLabel { get { return _damageLabel; } }
        public Text NameLabel { get { return _nameLabel; } }
        public bool Visible { get { return _group != null && _group.alpha > 0.5f; } }

        public static BattleTankPlate Create(
            RectTransform parent,
            BattleTank tank,
            BattleTank player,
            BattleCamera view,
            AtlasRegionTable atlas,
            Font font,
            string displayName)
        {
            if (parent == null || tank == null || tank == player)
            {
                return null;
            }

            var root = UiFactory.Node("tank_plate_" + tank.name, parent);
            var plate = root.gameObject.AddComponent<BattleTankPlate>();
            plate._tank = tank;
            plate._player = player;
            plate._view = view;
            plate._canvasRoot = parent;
            plate._root = root;
            plate.Build(atlas, font, displayName);
            return plate;
        }

        /// <summary>
        /// <c>VehicleUIObject/EnemyDamagedModules</c>: one white pictogram per
        /// module of this tank that is no longer intact, stacked over the
        /// marker. The client keeps the whole row built and only toggles the
        /// icons, so the port does the same.
        /// </summary>
        private void BuildDamagedModules(AtlasRegionTable atlas)
        {
            _damagedModules = UiFactory.Node("damaged_modules", _root);
            _damagedModules.anchorMin = new Vector2(0.5f, 0.5f);
            _damagedModules.anchorMax = new Vector2(0.5f, 0.5f);
            _damagedModules.pivot = new Vector2(0.5f, 0.5f);
            _damagedModules.anchoredPosition =
                new Vector2(0f, DamagePanelTuning.EnemyRowOffsetY);
            _damagedModules.localScale = new Vector3(
                DamagePanelTuning.EnemyIconScale,
                DamagePanelTuning.EnemyIconScale,
                1f);

            for (var i = 0; i < TankModuleIds.All.Length; i++)
            {
                var id = TankModuleIds.All[i];
                var image = UiFactory.Picture(
                    id, _damagedModules, atlas.Get("damage_enemy_" + id));
                image.raycastTarget = false;
                var rect = image.rectTransform;
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(
                    DamagePanelTuning.EnemyIconWidth,
                    DamagePanelTuning.EnemyIconHeight);
                image.gameObject.SetActive(false);
                _damagedModuleIcons.Add(image);
            }
        }

        /// <summary>Lays the visible pictograms out around the marker centre.</summary>
        private void RefreshDamagedModules()
        {
            if (_damagedModules == null) { return; }

            var step = DamagePanelTuning.EnemyIconWidth
                + DamagePanelTuning.EnemyIconSpacing;
            var shown = 0;
            for (var i = 0; i < _damagedModuleIcons.Count; i++)
            {
                var on = _tank != null
                    && _tank.ModuleStatus(TankModuleIds.All[i])
                        != TankModule.Status.Normal;
                var icon = _damagedModuleIcons[i];
                if (icon.gameObject.activeSelf != on) { icon.gameObject.SetActive(on); }

                if (on) { shown++; }
            }

            var left = -0.5f * (step * shown) + 0.5f * step;
            var slot = 0;
            for (var i = 0; i < _damagedModuleIcons.Count; i++)
            {
                var icon = _damagedModuleIcons[i];
                if (!icon.gameObject.activeSelf) { continue; }

                icon.rectTransform.anchoredPosition =
                    new Vector2(left + slot * step, 0f);
                slot++;
            }
        }

        private void Build(AtlasRegionTable atlas, Font font, string displayName)
        {
            GodotRect.Centered(_root, 0f, 0f, 0f, 0f);
            _root.localScale = new Vector3(
                TankPlateTuning.RootScale,
                TankPlateTuning.RootScale,
                1f);

            _group = gameObject.AddComponent<CanvasGroup>();
            _group.interactable = false;
            _group.blocksRaycasts = false;

            Picture(
                "hp_back", atlas.Get("battle_otm_hp_back"),
                TankPlateTuning.BackLeft,
                TankPlateTuning.BackTop,
                TankPlateTuning.BackRight,
                TankPlateTuning.BackBottom);

            _underBar = Picture(
                "hp_bar_under", atlas.Get("battle_otm_hp_under"),
                TankPlateTuning.BarUnderLeft,
                TankPlateTuning.BarUnderTop,
                TankPlateTuning.BarUnderRight,
                TankPlateTuning.BarUnderBottom);
            ConfigureBar(_underBar);

            _topBar = Picture(
                "hp_bar_top", atlas.Get("battle_otm_hp_top"),
                TankPlateTuning.BarTopLeft,
                TankPlateTuning.BarTopTop,
                TankPlateTuning.BarTopRight,
                TankPlateTuning.BarTopBottom);
            ConfigureBar(_topBar);

            var classShadow = Picture(
                "class_shadow", atlas.Get("battle_otm_class_shadow"),
                TankPlateTuning.ClassShadowLeft,
                TankPlateTuning.ClassShadowTop,
                TankPlateTuning.ClassShadowRight,
                TankPlateTuning.ClassShadowBottom);
            classShadow.color = ClassShadowColour;
            classShadow.preserveAspect = true;
            classShadow.rectTransform.localScale = new Vector3(
                TankPlateTuning.ClassShadowScale,
                TankPlateTuning.ClassShadowScale,
                1f);

            _classIcon = Picture(
                "class", atlas.Get(ClassRegion(_tank.Card)),
                TankPlateTuning.ClassLeft,
                TankPlateTuning.ClassTop,
                TankPlateTuning.ClassRight,
                TankPlateTuning.ClassBottom);
            _classIcon.preserveAspect = true;
            _classIcon.rectTransform.localScale = new Vector3(
                TankPlateTuning.ClassScale,
                TankPlateTuning.ClassScale,
                1f);

            // level: expand_mode 1 + stretch_mode 3 (KEEP_CENTERED) draws the
            // numeral at its atlas size in the middle of the box, and the 0.9
            // scale pivots on the box's top-left corner. Bake both into the rect.
            var level = _tank.Card != null ? Mathf.Clamp(_tank.Card.Level, 1, 10) : 1;
            var levelSprite = atlas.Get("battle_otm_level_" + level);
            var boxWidth = TankPlateTuning.LevelRight - TankPlateTuning.LevelLeft;
            var boxHeight = TankPlateTuning.LevelBottom - TankPlateTuning.LevelTop;
            var levelSize = levelSprite != null
                ? levelSprite.rect.size : new Vector2(boxWidth, boxHeight);
            var scale = TankPlateTuning.LevelScale;
            var levelLeft = TankPlateTuning.LevelLeft + (scale * (boxWidth - levelSize.x) / 2f);
            var levelTop = TankPlateTuning.LevelTop + (scale * (boxHeight - levelSize.y) / 2f);
            _levelIcon = Picture(
                "level", levelSprite,
                levelLeft,
                levelTop,
                levelLeft + (scale * levelSize.x),
                levelTop + (scale * levelSize.y));

            _healthLabel = Label(
                "hp", font, TankPlateTuning.HealthTextFontSize, Color.white,
                TankPlateTuning.HealthTextLeft,
                TankPlateTuning.HealthTextTop,
                TankPlateTuning.HealthTextRight,
                TankPlateTuning.HealthTextBottom);

            _damageLabel = Label(
                "dmg", font, TankPlateTuning.DamageFontSize, DamageColour,
                TankPlateTuning.DamageLeft,
                TankPlateTuning.DamageTop,
                TankPlateTuning.DamageRight,
                TankPlateTuning.DamageBottom);
            var damageShadow = _damageLabel.gameObject.AddComponent<Shadow>();
            damageShadow.effectColor = Color.white;
            damageShadow.effectDistance = new Vector2(
                0f, -TankPlateTuning.DamageShadowY);

            var nameShadow = Picture(
                "name_shadow", atlas.Get("battle_otm_name_shadow"),
                TankPlateTuning.NameShadowLeft,
                TankPlateTuning.NameShadowTop,
                TankPlateTuning.NameShadowRight,
                TankPlateTuning.NameShadowBottom);
            nameShadow.color = new Color(1f, 1f, 1f, 0.5686275f);
            nameShadow.rectTransform.localScale = new Vector3(
                TankPlateTuning.NameShadowScaleX, 1f, 1f);

            _nameLabel = Label(
                "tank_name", font, TankPlateTuning.NameFontSize, TeamColour(),
                TankPlateTuning.NameLeft,
                TankPlateTuning.NameTop,
                TankPlateTuning.NameRight,
                TankPlateTuning.NameBottom);
            _nameLabel.text = displayName ?? string.Empty;

            _maxHealth = _tank.Health != null
                ? Mathf.Max(1, _tank.Health.MaxHealth)
                : Mathf.Max(1,
                    _tank.Card != null
                        ? _tank.Card.TotalHealthWith(_tank.TurretModule)
                        : 1);
            _healthPrevious = DisplayHealth();
            _underHealth = _healthPrevious;
            ApplyHealth(_healthPrevious, false);

            if (_tank.Health != null)
            {
                _tank.Health.HealthChanged += OnHealthChanged;
                _tank.Health.OnDamageBlock += OnDamageBlocked;
                _tank.Health.OnDeath += OnDeath;
            }

            BuildDamagedModules(atlas);

            SetShown(false);
        }

        private Image Picture(
            string name, Sprite sprite, float left, float top, float right, float bottom)
        {
            var image = UiFactory.Picture(name, _root, sprite);
            GodotRect.Centered(image.rectTransform, left, top, right, bottom);
            image.raycastTarget = false;
            return image;
        }

        private Text Label(
            string name,
            Font font,
            int fontSize,
            Color colour,
            float left,
            float top,
            float right,
            float bottom)
        {
            var label = UiFactory.Label(
                name, _root, font, fontSize, colour,
                TextAnchor.MiddleCenter, string.Empty);
            GodotRect.Centered(label.rectTransform, left, top, right, bottom);
            return label;
        }

        private void ConfigureBar(Image bar)
        {
            bar.type = Image.Type.Filled;
            bar.fillMethod = Image.FillMethod.Horizontal;
            bar.fillOrigin = 0;
            bar.fillClockwise = true;
            bar.color = TeamColour();
            bar.fillAmount = 1f;
        }

        private Color TeamColour()
        {
            return TeamColour(_tank != null && _tank.Dead);
        }

        /// <summary>
        /// <c>get_team_modulate(dead)</c>: a wreck keeps its plate, with the
        /// team tint halved and the alpha put straight back to one.
        /// </summary>
        private Color TeamColour(bool dead)
        {
            var enemy = _player != null && _tank.Team != _player.Team;
            var colour = enemy ? EnemyColour : _tank.TeamKiller ? TeamKillerColour : AllyColour;
            if (!dead)
            {
                return colour;
            }

            colour *= 0.5f;
            colour.a = 1f;
            return colour;
        }

        private static string ClassRegion(TankInfo card)
        {
            if (card == null)
            {
                return "battle_otm_class_light";
            }

            switch (card.Class)
            {
                case TankClass.Medium: return "battle_otm_class_medium";
                case TankClass.Heavy: return "battle_otm_class_heavy";
                case TankClass.TankDestroyer: return "battle_otm_class_td";
                default: return "battle_otm_class_light";
            }
        }

        private int DisplayHealth()
        {
            return _tank != null ? _tank.ExternalDisplayHealth(_player) : 0;
        }

        private void OnHealthChanged(int health)
        {
            ApplyHealth(DisplayHealth(), true);
        }

        private void ApplyHealth(int health, bool animateDamage)
        {
            // Modes resize the pool after the plate exists (Big Boss roles and
            // levels), so the cap follows the live component.
            if (_tank != null && _tank.Health != null)
            {
                _maxHealth = Mathf.Max(1, _tank.Health.MaxHealth);
            }

            var safe = Mathf.Clamp(health, 0, _maxHealth);
            _topBar.fillAmount = safe / (float)_maxHealth;
            _healthLabel.text = safe + "/" + _maxHealth;

            if (animateDamage && safe < _healthPrevious)
            {
                var now = Time.unscaledTime;
                if (now > _damageAccumulatorDue)
                {
                    _damageAccumulated = 0;
                }

                _damageAccumulatorDue = now + TankPlateTuning.DamageAccumulationWindow;
                _damageAccumulated += _healthPrevious - safe;
                _damageLabel.text = "-" + _damageAccumulated;
                _damageShownAt = now;
                StartClassFlash(Color.white);
            }
            else if (safe > _underHealth)
            {
                _underHealth = safe;
            }

            _healthPrevious = safe;
        }

        private void OnDamageBlocked(int ignored)
        {
            StartClassFlash(Color.black);
        }

        private void StartClassFlash(Color colour)
        {
            _classFlashFrom = colour;
            _classFlashAt = Time.unscaledTime;
            _classIcon.color = colour;
        }

        public void RefreshLife()
        {
            _fakeDeathShown = false;
            var tint = TeamColour(false);
            if (_classIcon != null) _classIcon.color = tint;
            if (_nameLabel != null) _nameLabel.color = tint;
        }

        private void OnDeath()
        {
            // ui_tank_external.on_death: the plate stays up on a wreck. Only
            // the class icon and the nick go to the halved team tint, while
            // health_changed has already driven the bar to a black 0/max.
            var dim = TeamColour(true);
            if (_classIcon != null)
            {
                _classIcon.color = dim;
            }

            if (_nameLabel != null)
            {
                _nameLabel.color = dim;
            }
        }

        /// <summary>The blue status can start mid-battle: repaint the bars and the nick.</summary>
        private void RefreshTeamKiller()
        {
            var blue = _tank != null && _tank.TeamKiller;
            if (blue == _teamKillerShown) { return; }
            _teamKillerShown = blue;
            var tint = TeamColour();
            if (_topBar != null) { _topBar.color = tint; }
            if (_underBar != null) { _underBar.color = tint; }
            if (_nameLabel != null) { _nameLabel.color = tint; }
        }

        /// <summary>
        /// Uprising Play Dead: the plate of an enemy acting dead dims exactly
        /// like a wreck's (see <see cref="OnDeath"/>) and lights up again when
        /// the act ends.
        /// </summary>
        private void RefreshFakeDeath()
        {
            if (_tank == null || _tank.Dead) { return; }
            var fake = _tank.LooksDeadTo(_player);
            if (fake == _fakeDeathShown) { return; }
            _fakeDeathShown = fake;
            if (fake) { OnDeath(); } else { RefreshLife(); }
        }

        private bool _fakeDeathShown;

        private void Update()
        {
            RefreshTeamKiller();
            RefreshFakeDeath();
            var shownHealth = DisplayHealth();
            if (shownHealth != _healthPrevious)
            {
                ApplyHealth(shownHealth, false);
            }

            AdvanceAnimations(Time.unscaledDeltaTime, Time.unscaledTime);
            RefreshDamagedModules();
        }

        private void LateUpdate()
        {
            // BattleCamera follows the interpolated player hull in LateUpdate.
            // Project only afterwards (this component's execution order is 3),
            // otherwise the plate contains last frame's camera pose while the
            // world is rendered with this frame's pose and shakes on slopes.
            UpdateProjection();
        }

        /// <summary>
        /// Advances the source delayed-HP, damage-fade and class-flash timers.
        /// Keeping the clock explicit also makes these visual transitions
        /// deterministic in render and EditMode verification.
        /// </summary>
        public void AdvanceAnimations(float deltaTime, float now)
        {
            UpdateUnderBar(deltaTime);
            UpdateDamageLabel(now);
            UpdateClassFlash(now);
        }

        private void UpdateUnderBar(float deltaTime)
        {
            if (_tank != null && _tank.Health != null
                && Mathf.Max(1, _tank.Health.MaxHealth) != _maxHealth)
            {
                ApplyHealth(DisplayHealth(), false);
            }

            var target = _healthPrevious;
            if (_underHealth > target)
            {
                _underHealth -= Mathf.Max(
                    (_underHealth - target) * deltaTime
                        * TankPlateTuning.UnderBarCatchupRate,
                    TankPlateTuning.UnderBarMinimumStep);
                if (_underHealth < target)
                {
                    _underHealth = target;
                }
            }
            else if (_underHealth < target)
            {
                _underHealth = target;
            }

            _underBar.fillAmount = Mathf.Clamp01(_underHealth / _maxHealth);
        }

        private void UpdateDamageLabel(float now)
        {
            var alpha = 0f;
            if (_damageShownAt >= 0f)
            {
                var age = now - _damageShownAt;
                if (age <= TankPlateTuning.DamageVisibleTime)
                {
                    alpha = 1f;
                }
                else if (age <= TankPlateTuning.DamageVisibleTime
                    + TankPlateTuning.DamageFadeTime)
                {
                    alpha = 1f - ((age - TankPlateTuning.DamageVisibleTime)
                        / TankPlateTuning.DamageFadeTime);
                }
            }

            var colour = _damageLabel.color;
            colour.a = Mathf.Clamp01(alpha);
            _damageLabel.color = colour;
        }

        private void UpdateClassFlash(float now)
        {
            if (_classFlashAt < 0f)
            {
                _classIcon.color = TeamColour();
                return;
            }

            var t = Mathf.Clamp01((now - _classFlashAt) / 0.5f);
            _classIcon.color = Color.Lerp(_classFlashFrom, TeamColour(), t);
            if (t >= 1f)
            {
                _classFlashAt = -1f;
            }
        }

        /// <summary>
        /// Godot's <c>Camera3D.unproject_position</c>: map directly from camera
        /// viewport space into the overlay canvas. This deliberately ignores
        /// the HUD's world transform, which may follow and rotate with the
        /// observed hull for lifetime ownership.
        /// </summary>
        public static bool TryProjectToCanvas(
            Camera camera, RectTransform canvasRoot, Vector3 world, out Vector2 local)
        {
            local = Vector2.zero;
            if (camera == null || canvasRoot == null) { return false; }

            var viewport = camera.WorldToViewportPoint(world);
            if (viewport.z <= 0f) { return false; }

            var rect = canvasRoot.rect;
            local = new Vector2(
                Mathf.LerpUnclamped(rect.xMin, rect.xMax, viewport.x),
                Mathf.LerpUnclamped(rect.yMin, rect.yMax, viewport.y));
            return true;
        }

        private void UpdateProjection()
        {
            var camera = _view != null ? _view.Active : Camera.main;
            if (_tank == null || _tank == _player || camera == null)
            {
                SetShown(false);
                return;
            }

            // set_spotted(): if is_enemy, root_2d follows the hull out of
            // sight, so an unspotted enemy shows no marker, bar or name.
            if (!_tank.Spotted && _player != null && _tank.Team != _player.Team)
            {
                SetShown(false);
                return;
            }

            var world = _tank.Pos()
                + (_tank.transform.up * (_tank.Hull.Height + TankPlateTuning.HeightOffset));
            Vector2 local;
            if (!TryProjectToCanvas(camera, _canvasRoot, world, out local))
            {
                SetShown(false);
                return;
            }

            _root.anchoredPosition = local;
            SetShown(true);
        }

        private void SetShown(bool shown)
        {
            if (_group != null)
            {
                _group.alpha = shown ? 1f : 0f;
            }
        }

        private void OnDestroy()
        {
            if (_tank == null || _tank.Health == null)
            {
                return;
            }

            _tank.Health.HealthChanged -= OnHealthChanged;
            _tank.Health.OnDamageBlock -= OnDamageBlocked;
            _tank.Health.OnDeath -= OnDeath;
        }
    }
}
