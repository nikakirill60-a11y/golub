using System;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// The "shells_equipment/equipment" column of ui_player.tscn: the three
    /// ui_consumable_icon.tscn instances ui_player_battle.try_setup_consumable
    /// fills from tank.active_consumables, plus the press that reaches
    /// base_tank.use_consumable.
    ///
    /// The fourth slot of the source column, cons_jump, is authored hidden and
    /// only built when the crew's battle mode has an ability for it
    /// (<see cref="BattleHudMode.Ability"/>). The key and Digit7 press it
    /// behind the same gate as the three hangar slots.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleConsumableBar : MonoBehaviour
    {
        /// <summary>Global.MAX_CONSUMABLES.</summary>
        public const int SlotCount = ConsumableCatalog.MaxActive;

        /// <summary>custom_minimum_size of ui_consumable_icon.tscn.</summary>
        public const float IconSize = 186f;

        /// <summary>The 32 px spacers between the icons.</summary>
        public const float IconSpacing = 32f;

        /// <summary>Godot's default BoxContainer separation, once per gap.</summary>
        public const float Separation = 4f;

        /// <summary>A spacer plus the two separations around it.</summary>
        public const float SlotStep = IconSize + IconSpacing + (Separation * 2f);

        private BattleTank _tank;
        private readonly ConsumableIcon[] _icons = new ConsumableIcon[SlotCount];
        private ConsumableIcon _modeIcon;
        private IBattleModeAbility _modeAbility;

        public RectTransform Root { get; private set; }

        public ConsumableIcon GetIcon(int slot)
        {
            return slot >= 0 && slot < _icons.Length ? _icons[slot] : null;
        }

        /// <summary>cons_jump, null when the battle mode has no ability for it.</summary>
        public ConsumableIcon ModeIcon { get { return _modeIcon; } }

        /// <summary>
        /// Builds the column under the shells_equipment root the shell rack
        /// already owns, because the source keeps both inside that one scaled
        /// Control.
        /// </summary>
        public static BattleConsumableBar Create(
            RectTransform shellsEquipment,
            BattleTank tank,
            AtlasRegionTable atlas,
            Font bold)
        {
            if (shellsEquipment == null) { return null; }

            var go = new GameObject("equipment", typeof(RectTransform));
            go.transform.SetParent(shellsEquipment, false);
            var root = (RectTransform)go.transform;

            // anchors_preset 6 with grow_horizontal BEGIN: 186 x 638.
            GodotRect.Apply(root, 1f, 0.5f, 1f, 0.5f, -74f, 235.63611f, 112f, 873.6361f);

            var bar = go.AddComponent<BattleConsumableBar>();
            bar.Root = root;
            bar._tank = tank;

            for (var slot = 0; slot < SlotCount; slot++)
            {
                var icon = ConsumableIcon.Create(
                    root, "cons" + (slot + 1), slot * SlotStep, atlas, bold);
                var captured = slot;
                icon.Pressed = () => bar.Use(captured);
                bar._icons[slot] = icon;
            }

            var ability = BattleHudMode.Of(tank).Ability;
            if (ability != null)
            {
                var mode = ConsumableIcon.Create(
                    root, "cons_jump", 3 * SlotStep, atlas, bold);
                mode.InitAbility(ability);
                mode.Pressed = bar.UseModeAbility;
                bar._modeIcon = mode;
                bar._modeAbility = ability;
            }
            bar.Refresh();
            return bar;
        }

        /// <summary>try_setup_consumable for all three slots.</summary>
        public void Refresh()
        {
            for (var slot = 0; slot < _icons.Length; slot++)
            {
                TrySetupConsumable(slot);
            }
        }

        /// <summary>
        /// try_setup_consumable(id): a slot the tank has no mounted item for
        /// is cleared back to the empty EquipmentKeys frame.
        /// </summary>
        public void TrySetupConsumable(int slot)
        {
            if (slot < 0 || slot >= _icons.Length) { return; }

            var mounted = _tank != null ? _tank.ActiveConsumables : null;
            _icons[slot].Init(
                mounted != null && slot < mounted.Count ? mounted[slot] : null);
        }

        /// <summary>
        /// The one press gate of every mode, for the three slots and the mode slot
        /// alike, and the host's gate for a remote crew's press: a live, unblocked
        /// crew in a running, unpaused battle.
        /// </summary>
        public static bool CanUse(BattleTank tank)
        {
            if (tank == null || tank.Dead || tank.Blocked || Time.timeScale <= 0f) { return false; }

            var session = tank.GetComponentInParent<BattleSession>();
            return session == null || session.Started;
        }

        /// <summary>consN.pressed -> tank.use_consumable(N).</summary>
        public void Use(int slot)
        {
            if (!CanUse(_tank)) { return; }

            // A consumable changes the simulated hull (repair, reload, engine): online
            // the host spends the press for this crew in every mode, while the local use
            // keeps the slot's cooldown on screen and books its credits.
            if (BattleNetHooks.PuppetWorld && slot >= 0 && slot < BattleRemoteDriver.ConsumableSlots)
            {
                BattleNetHooks.RequestUprisingAction((byte)(BattleRemoteDriver.ConsumableAction + slot));
            }
            _tank.UseConsumable(slot);
        }

        /// <summary>cons_jump.pressed and Digit7: the mode's ability behind the press gate.</summary>
        public void UseModeAbility()
        {
            if (_modeAbility != null && CanUse(_tank)) { _modeAbility.Use(); }
        }

        /// <summary>One ui_consumable_icon.tscn instance.</summary>
        public sealed class ConsumableIcon : MonoBehaviour, IPointerClickHandler
        {
            private AtlasRegionTable _atlas;
            private ConsumableRuntime _cons;
            private bool _passive;
            private IBattleModeAbility _ability;
            private Sprite _abilitySprite;
            private string _abilityArt;
            private int _shownSeconds = -1;

            public Action Pressed;

            public Image BackgroundImage { get; private set; }

            public Image IconImage { get; private set; }

            public Text TimerLabel { get; private set; }

            /// <summary>The runtime this widget is bound to, null when empty.</summary>
            public ConsumableRuntime Consumable { get { return _cons; } }

            public static ConsumableIcon Create(
                RectTransform parent,
                string name,
                float y,
                AtlasRegionTable atlas,
                Font bold)
            {
                var go = new GameObject(name, typeof(RectTransform));
                go.transform.SetParent(parent, false);
                var rect = (RectTransform)go.transform;
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = GodotRect.TopLeftPivot;
                rect.sizeDelta = new Vector2(IconSize, IconSize);
                rect.anchoredPosition = new Vector2(0f, -y);

                var icon = go.AddComponent<ConsumableIcon>();
                icon._atlas = atlas;

                // The root TextureRect is the key frame and swallows the tap
                // through the transparent ButtonCustom on top of it.
                var background = go.AddComponent<Image>();
                background.raycastTarget = true;
                icon.BackgroundImage = background;

                // icon: anchors_preset 8, offsets -20/-20/20/20, stretch_mode 3
                // (KEEP_CENTERED), so the artwork keeps its authored size.
                icon.IconImage = UiFactory.Picture("icon", rect, null);
                GodotRect.Centered(icon.IconImage.rectTransform, -20f, -20f, 20f, 20f);

                // timer: anchors_preset 8, offsets -20/-8/20/8, font 60,
                // shadow 3/0, centred horizontally and drawn from the top.
                icon.TimerLabel = UiFactory.Label(
                    "timer", rect, bold, 60, Color.white,
                    TextAnchor.UpperCenter, string.Empty);
                GodotRect.Centered(icon.TimerLabel.rectTransform, -20f, -8f, 20f, 8f);
                icon.TimerLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
                icon.TimerLabel.verticalOverflow = VerticalWrapMode.Overflow;
                UiFactory.AddShadow(icon.TimerLabel, Color.black, new Vector2(3f, 0f));
                icon.TimerLabel.gameObject.SetActive(false);

                icon.ClearSlot();
                return icon;
            }

            /// <summary>clear_slot().</summary>
            public void ClearSlot()
            {
                _cons = null;
                _passive = false;
                IconImage.sprite = null;
                IconImage.enabled = false;
                IconImage.color = Color.white;
                TimerLabel.gameObject.SetActive(false);
                SetFrame("consumable_key_empty");
            }

            /// <summary>init(cons_active).</summary>
            public void Init(ConsumableRuntime cons)
            {
                ClearSlot();
                if (cons == null || cons.Info == null) { return; }

                _cons = cons;
                _passive = cons.Info.Type == ConsumableType.Passive;
                SetFrame("consumable_key_ready");

                var sprite = _atlas == null ? null : _atlas.Get(cons.Info.IconRegion);
                IconImage.sprite = sprite;
                IconImage.enabled = sprite != null;
                if (sprite != null)
                {
                    var rt = IconImage.rectTransform;
                    rt.sizeDelta = sprite.rect.size;
                }

                var bound = cons;
                cons.Used = () =>
                {
                    if (_cons != bound) { return; }

                    SetFrame("consumable_key_active");
                    if (_passive) { return; }

                    SetIconAlpha(0.5f);
                    TimerLabel.gameObject.SetActive(true);
                };
                cons.StoppedUse = () =>
                {
                    if (_cons != bound) { return; }

                    SetIconAlpha(0.5f);
                    SetFrame("consumable_key_cooling");
                    if (!_passive) { TimerLabel.gameObject.SetActive(true); }
                };
                cons.CooldownEnd = () =>
                {
                    if (_cons != bound) { return; }

                    SetIconAlpha(1f);
                    SetFrame("consumable_key_ready");
                    if (!_passive) { TimerLabel.gameObject.SetActive(false); }
                };
            }

            /// <summary>
            /// cons_jump: the battle mode's ability, read every physics tick so
            /// its art follows role and level changes.
            /// </summary>
            public void InitAbility(IBattleModeAbility ability)
            {
                ClearSlot();
                _ability = ability;
                _abilityArt = null;
                _shownSeconds = -1;
                if (ability != null) { RefreshAbility(); }
            }

            private void RefreshAbility()
            {
                var state = _ability.Read(Time.time);
                if (state.Phase == BattleModeAbilityPhase.Hidden)
                {
                    if (_abilityArt != null || IconImage.enabled)
                    {
                        ClearSlot();
                        _abilityArt = null;
                    }
                    return;
                }

                if (state.Icon != _abilityArt)
                {
                    _abilityArt = state.Icon;
                    ApplyCustomIcon(state.Icon);
                    if (state.IconSize != Vector2.zero) { IconImage.rectTransform.sizeDelta = state.IconSize; }
                }

                var phase = state.Phase;
                SetFrame(phase == BattleModeAbilityPhase.Active ? "consumable_key_active"
                    : phase == BattleModeAbilityPhase.Ready ? "consumable_key_ready"
                    : "consumable_key_cooling");
                IconImage.color = phase == BattleModeAbilityPhase.Active
                    ? new Color(1f, 0.75f, 0.35f)
                    : new Color(1f, 1f, 1f, phase == BattleModeAbilityPhase.Cooling ? 0.5f
                        : phase == BattleModeAbilityPhase.NoTarget ? 0.35f : 1f);
                var seconds = state.Seconds > 0f ? Mathf.CeilToInt(state.Seconds) : 0;
                TimerLabel.gameObject.SetActive(seconds > 0);
                if (seconds != _shownSeconds)
                {
                    _shownSeconds = seconds;
                    TimerLabel.text = seconds > 0
                        ? seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : string.Empty;
                }
            }

            private void ApplyCustomIcon(string resource)
            {
                var texture=Resources.Load<Texture2D>(resource);
                if(texture==null)return;
                if(_abilitySprite!=null){if(Application.isPlaying)Destroy(_abilitySprite);else DestroyImmediate(_abilitySprite);}
                _abilitySprite=Sprite.Create(texture,new Rect(0,0,texture.width,texture.height),new Vector2(.5f,.5f),100);
                IconImage.sprite=_abilitySprite;IconImage.rectTransform.sizeDelta=new Vector2(texture.width,texture.height);IconImage.enabled=true;
            }
            private void OnDestroy(){if(_abilitySprite!=null){if(Application.isPlaying)Destroy(_abilitySprite);else DestroyImmediate(_abilitySprite);}}
            public void OnPointerClick(PointerEventData eventData)
            {
                if (Pressed != null) { Pressed(); }
            }

            /// <summary>_physics_process: the label counts the live deadline down.</summary>
            private void FixedUpdate()
            {
                if (_ability != null) { RefreshAbility(); return; }
                if (_cons == null || !TimerLabel.gameObject.activeSelf) { return; }

                TimerLabel.text = _cons.TimerSeconds(Time.time)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            private void SetFrame(string region)
            {
                var sprite = _atlas == null ? null : _atlas.Get(region);
                BackgroundImage.sprite = sprite;
                BackgroundImage.enabled = sprite != null;
            }

            private void SetIconAlpha(float alpha)
            {
                var colour = IconImage.color;
                colour.a = alpha;
                IconImage.color = colour;
            }
        }
    }
}
