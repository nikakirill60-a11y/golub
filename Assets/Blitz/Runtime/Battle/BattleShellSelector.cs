using System;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// The "shells_equipment/shells" half of ui_player.tscn, driven by the
    /// shell block of ui_player_battle.gd: three ui_shell_icon instances that
    /// show the rack of the mounted gun, highlight the selected slot and count
    /// down as projectile_fired lands.
    ///
    /// The source keeps the rack inside the player HUD tree, after the touch
    /// surface, so a tap on an icon reaches the icon and not the look input.
    /// The port keeps that order by living on the controls canvas, and
    /// <see cref="BattleHud"/> only forwards its own visibility.
    ///
    /// The consumables column next to it (cons1..cons3, cons_jump) is a
    /// separate source feature and is not part of this slice.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleShellSelector : MonoBehaviour
    {
        /// <summary>
        /// ui_shell_icon.BgType, with the same ordinals: NONE is AmmoKeys03,
        /// GENERIC the inventory/BulletButton frame every hangar instance of
        /// shell_icon.tscn keeps by default, and BATTLE the AmmoKeys04 key of
        /// the rack in ui_player.tscn.
        /// </summary>
        public enum Background
        {
            None = 0,
            Generic = 1,
            Battle = 2,
        }

        public const int SlotCount = 3;

        /// <summary>custom_minimum_size of shell1..shell3 in ui_player.tscn.</summary>
        public const float IconSize = 186f;

        /// <summary>The two 32x32 spacers between the icons.</summary>
        public const float IconSpacing = 32f;

        /// <summary>
        /// Godot's default BoxContainer separation, which the source row pays
        /// on both sides of every spacer: 3 x 186 + 2 x (32 + 4 + 4) is
        /// exactly the 638 px the HBox is authored at.
        /// </summary>
        public const float Separation = 4f;

        /// <summary>One icon plus the spacer and separations after it.</summary>
        public const float SlotStep = IconSize + IconSpacing + (Separation * 2f);

        /// <summary>shells_equipment.scale.</summary>
        public const float RackScale = 0.33f;

        /// <summary>HBoxContainer alignment 2 (END) inside the 638 px row.</summary>
        public const float RowWidth = 638f;

        private BattleTank _tank;
        private AtlasRegionTable _atlas;
        private LocalizationTable _lang;
        private readonly ShellIcon[] _icons = new ShellIcon[SlotCount];
        private Action<bool, int> _firedHandler;
        private int _lastShellId = -1;

        public RectTransform Root { get; private set; }

        public int SelectedSlot { get { return _lastShellId; } }

        public static BattleShellSelector Create(
            RectTransform parent,
            BattleTank tank,
            AtlasRegionTable atlas,
            LocalizationTable lang,
            Font bold)
        {
            if (parent == null) { return null; }

            var rootGo = new GameObject("shells_equipment", typeof(RectTransform));
            rootGo.transform.SetParent(parent, false);
            var root = (RectTransform)rootGo.transform;

            // anchors_preset 6 (centre right) with grow_horizontal BEGIN.
            GodotRect.Apply(root, 1f, 0.5f, 1f, 0.5f, -61f, -60f, -21.000061f, -20.000015f);
            root.localScale = new Vector3(RackScale, RackScale, 1f);

            var selector = rootGo.AddComponent<BattleShellSelector>();
            selector.Root = root;
            selector._tank = tank;
            selector._atlas = atlas;
            selector._lang = lang;

            var rowGo = new GameObject("shells", typeof(RectTransform));
            rowGo.transform.SetParent(root, false);
            var row = (RectTransform)rowGo.transform;
            GodotRect.TopLeft(
                row, -484.84888f, -24.24231f, 153.15112f, 161.75769f);

            // The HBoxContainer packs its children to the END; icons, spacers
            // and separations together fill the authored 638 px exactly, so
            // the first icon starts flush at the left edge of the row.
            var left = RowWidth
                - ((IconSize * SlotCount) + (SlotStep - IconSize) * (SlotCount - 1));
            for (var slot = 0; slot < SlotCount; slot++)
            {
                var x = left + (slot * SlotStep);
                selector._icons[slot] = ShellIcon.Create(
                    row, "shell" + (slot + 1), x, atlas, lang, bold);
                var captured = slot;
                selector._icons[slot].Pressed = () => selector.Select(captured);
            }

            selector.Refresh();
            return selector;
        }

        /// <summary>try_setup_shell for all three slots.</summary>
        public void Refresh()
        {
            for (var slot = 0; slot < _icons.Length; slot++)
            {
                TrySetupShell(slot);
            }

            _lastShellId = -1;
            SyncSelection();
        }

        /// <summary>
        /// try_setup_shell: a slot the gun does not carry, or one the save has
        /// no counter for, falls back to the empty BgType.NONE frame.
        /// </summary>
        public void TrySetupShell(int slot)
        {
            if (slot < 0 || slot >= _icons.Length) { return; }

            var gun = _tank != null ? _tank.GunInfo : null;
            var amounts = _tank != null ? _tank.ShellAmounts : null;
            var hasShell = gun != null && slot < gun.Shells.Count;
            var hasAmount = amounts != null && slot < amounts.Count;
            if (hasShell && hasAmount)
            {
                _icons[slot].Init(Background.Battle, gun.Shells[slot], amounts[slot]);
                return;
            }

            _icons[slot].Init(Background.None, null, 0);
        }

        /// <summary>base_tank.select_shell, reached from a key or a tap.</summary>
        public void Select(int slot)
        {
            if (_tank == null) { return; }

            var before = _tank.ShellId;
            _tank.SelectShell(slot);
            if (_tank.ShellId != before)
            {
                PlaySelectCue();
            }

            SyncSelection();
        }

        public ShellIcon GetIcon(int slot)
        {
            return slot >= 0 && slot < _icons.Length ? _icons[slot] : null;
        }

        public void SetVisible(bool visible)
        {
            if (Root != null)
            {
                Root.gameObject.SetActive(visible);
            }
        }

        /// <summary>
        /// select_shell in ui_player_battle.gd clamps to the three widgets the
        /// HUD owns even when the gun carries fewer shells.
        /// </summary>
        private void SyncSelection()
        {
            var id = _tank != null ? Mathf.Clamp(_tank.ShellId, 0, SlotCount - 1) : 0;
            if (id == _lastShellId) { return; }

            _lastShellId = id;
            for (var slot = 0; slot < _icons.Length; slot++)
            {
                _icons[slot].SetSelected(slot == id);
            }
        }

        private void PlaySelectCue()
        {
            var audio = BlitzBootstrap.Current != null
                ? BlitzBootstrap.Current.Audio : null;
            if (audio == null) { return; }

            audio.PlayPool2D(audio.Sounds.Sound("ui_shell_select"));
        }

        private void OnEnable()
        {
            if (_tank == null) { return; }

            _firedHandler = OnProjectileFired;
            _tank.ProjectileFired += _firedHandler;
        }

        private void OnDisable()
        {
            if (_tank != null && _firedHandler != null)
            {
                _tank.ProjectileFired -= _firedHandler;
            }

            _firedHandler = null;
        }

        /// <summary>_on_projectile_fired refreshes every counter, not just the fired one.</summary>
        private void OnProjectileFired(bool firstShot, int shellId)
        {
            var amounts = _tank != null ? _tank.ShellAmounts : null;
            for (var slot = 0; slot < _icons.Length; slot++)
            {
                if (amounts != null && slot < amounts.Count)
                {
                    _icons[slot].SetAmount(amounts[slot]);
                }
            }
        }

        private void Update()
        {
            // The source compares tank.shell_id against last_shell_id every
            // frame, so the AI-free paths that change it (a reload onto an
            // empty rack, a cheat) still move the highlight.
            SyncSelection();
        }

        /// <summary>One ui_shell_icon instance.</summary>
        public sealed class ShellIcon : MonoBehaviour, IPointerClickHandler
        {
            private AtlasRegionTable _atlas;
            private LocalizationTable _lang;

            public Action Pressed;

            public Image BackgroundImage { get; private set; }

            public Image IconImage { get; private set; }

            public Image SelectedImage { get; private set; }

            public Text AmountLabel { get; private set; }

            public Text TypeLabel { get; private set; }

            public Background Frame { get; private set; }

            public bool Disabled { get; private set; }

            public int Amount { get; private set; }

            public static ShellIcon Create(
                RectTransform parent,
                string name,
                float x,
                AtlasRegionTable atlas,
                LocalizationTable lang,
                Font bold)
            {
                var go = new GameObject(name, typeof(RectTransform));
                go.transform.SetParent(parent, false);
                var rect = (RectTransform)go.transform;
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = GodotRect.TopLeftPivot;
                rect.sizeDelta = new Vector2(IconSize, IconSize);
                rect.anchoredPosition = new Vector2(x, 0f);

                var icon = go.AddComponent<ShellIcon>();
                icon._atlas = atlas;
                icon._lang = lang;

                var background = go.AddComponent<Image>();
                background.raycastTarget = true;
                icon.BackgroundImage = background;

                // shell_ico: anchors_preset 8, offsets -90/-65/90/65.
                icon.IconImage = UiFactory.Picture("shell_ico", rect, null);
                GodotRect.Centered(icon.IconImage.rectTransform, -90f, -65f, 90f, 65f);
                icon.IconImage.preserveAspect = true;

                // "selected" is anchors_preset 15 above the icon.
                icon.SelectedImage = UiFactory.Picture(
                    "selected", rect, atlas != null ? atlas.Get("shell_bg_selected") : null);
                GodotRect.FullRect(icon.SelectedImage.rectTransform);
                icon.SelectedImage.gameObject.SetActive(false);

                // "type": anchors_preset 3, font 40, shadow 0/3.
                icon.TypeLabel = UiFactory.Label(
                    "type", rect, bold, 40, Color.white, TextAnchor.UpperLeft, string.Empty);
                GodotRect.Apply(
                    icon.TypeLabel.rectTransform, 1f, 1f, 1f, 1f, -175f, -175f, -86f, -114f);
                UiFactory.AddShadow(icon.TypeLabel, Color.black, new Vector2(0f, 3f));

                // "amount": anchors_preset 3, font 60, shadow 4/4, right aligned.
                icon.AmountLabel = UiFactory.Label(
                    "amount", rect, bold, 60, Color.white, TextAnchor.UpperRight, string.Empty);
                GodotRect.Apply(
                    icon.AmountLabel.rectTransform, 1f, 1f, 1f, 1f,
                    -104.15152f, -72.272766f, -15.15152f, -11.272766f);
                UiFactory.AddShadow(icon.AmountLabel, Color.black, new Vector2(4f, 4f));

                return icon;
            }

            /// <summary>
            /// ui_shell_icon.init: null info disables the slot and hides the
            /// icon, the counter and the abbreviation. BgType.BATTLE also
            /// shrinks the counter to font 50.
            /// </summary>
            public void Init(Background frame, ShellInfo info, int amount)
            {
                Frame = frame;
                Disabled = info == null;
                BackgroundImage.sprite = Art(BackgroundRegion(frame));
                BackgroundImage.enabled = BackgroundImage.sprite != null;

                if (frame == Background.Battle)
                {
                    AmountLabel.fontSize = 50;
                }

                if (Disabled)
                {
                    IconImage.gameObject.SetActive(false);
                    AmountLabel.gameObject.SetActive(false);
                    TypeLabel.gameObject.SetActive(false);
                    Amount = 0;
                    return;
                }

                IconImage.gameObject.SetActive(true);
                // The battle rack shows the counter; only the hangar slider
                // swaps it for the type abbreviation.
                AmountLabel.gameObject.SetActive(true);
                TypeLabel.gameObject.SetActive(false);
                TypeLabel.text = Str(HangarUiKit.ShellAbbreviationKey(info.Type));
                _type = info.Type;
                _premium = info.Premium;
                SetAmount(amount);
            }

            /// <summary>set_amount: an empty rack swaps in the greyed artwork.</summary>
            public void SetAmount(int amount)
            {
                Amount = Mathf.Max(0, amount);
                if (Disabled) { return; }

                // set_amount prints Util.format_number, the grouped form.
                AmountLabel.text = HangarUiKit.FormatNumber(Amount);
                var sprite = Art(
                    HangarUiKit.ShellIconRegion(_type, _premium, Amount == 0));
                IconImage.sprite = sprite;
                IconImage.enabled = sprite != null;

                // shell_ico is stretch_mode 3 (KEEP_CENTERED): the artwork
                // keeps its authored size and is centred on the -90/-65/90/65
                // rect, so a 151 px tall shell overhangs it exactly like in
                // the source instead of being squeezed into 180 x 130.
                var rt = IconImage.rectTransform;
                GodotRect.Centered(rt, -90f, -65f, 90f, 65f);
                if (sprite != null)
                {
                    rt.anchorMin = new Vector2(0.5f, 0.5f);
                    rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.anchoredPosition = Vector2.zero;
                    rt.sizeDelta = sprite.rect.size;
                }
            }

            public void SetSelected(bool selected)
            {
                SelectedImage.gameObject.SetActive(selected && !Disabled);
            }

            public void OnPointerClick(PointerEventData eventData)
            {
                if (Pressed != null)
                {
                    Pressed();
                }
            }

            private ShellType _type;
            private bool _premium;

            private static string BackgroundRegion(Background frame)
            {
                switch (frame)
                {
                    case Background.Battle:
                        return "shell_bg_battle";
                    case Background.Generic:
                        return "shell_bg_generic";
                    default:
                        return "shell_bg_none";
                }
            }

            private Sprite Art(string region)
            {
                return _atlas == null ? null : _atlas.Get(region);
            }

            private string Str(string key)
            {
                return _lang == null ? key : _lang.Get(key);
            }
        }
    }
}
