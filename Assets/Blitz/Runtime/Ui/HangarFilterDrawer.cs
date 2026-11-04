using System;
using System.Collections.Generic;
using System.Globalization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Audio;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// screens/main/filter_handle/filter of ui_hangar.tscn: the classic
    /// carousel filter.
    ///
    /// The source has no filter pop-up. "filter" is the band that carries the
    /// hangar footer -- footer_lol, the carousel, btn_filter, btn_techTree,
    /// the loadout strip and btn_notifs -- and the filter itself hangs under
    /// that band, past the bottom edge of the screen. show_filter animates
    /// nothing but the band's position, from y = -135 to y = -399, so opening
    /// the filter lifts the whole footer by 264 px and pulls the filter rows
    /// up into the room it leaves.
    ///
    /// The port moves the same band the same way: the hangar builds its
    /// footer widgets into <see cref="Band"/>, and this class owns the slide
    /// and the filter rows. Every rect is the scene's own but where the port
    /// has more to filter by than 4.8: filter_nations carries all nine
    /// nations in toggles the size of a class toggle, which lengthens
    /// border3 and moves border4 and reset_filter over, and the vehicle
    /// status grows from premium alone to tech tree, premium, collector and
    /// mod: the tech tree toggle takes the premium one's place in border5,
    /// and border6 .. border8 frame the other three the same way.
    /// The rows and the frames are anchored to the band's bottom centre, and
    /// the frames and the selection outlines are authored three times too
    /// large and drawn at scale 0.33.
    /// </summary>
    public sealed class HangarFilterDrawer : MonoBehaviour
    {
        /// <summary>filter: offset_top = -135 against the zero-height handle.</summary>
        public const float BandHeight = 135f;

        /// <summary>show_filter key 0: the band ends on the screen's bottom edge.</summary>
        public const float ClosedY = -135f;

        /// <summary>show_filter key 1.</summary>
        public const float OpenY = -399f;

        /// <summary>show_filter length.</summary>
        public const float SlideSeconds = 0.4f;

        /// <summary>
        /// The transition both show_filter keys carry. Above 1, Godot's
        /// Math::ease() is pow(x, c): the band starts a little slow.
        /// </summary>
        public const float Transition = 1.1095695f;

        /// <summary>The scale of every frame and selection outline.</summary>
        public const float NodeScale = 0.33f;

        /// <summary>border.self_modulate and border2..8.modulate alpha.</summary>
        public const float FrameAlpha = 0.30400002f;

        /// <summary>border*.patch_margin_*: hangar_select_tile sliced at 20.</summary>
        public const float FramePatch = 20f;

        /// <summary>selected.patch_margin_* (tree_selector).</summary>
        public const float SelectorPatch = 9f;

        /// <summary>bg.patch_margin_* (hangar_bottom_layer).</summary>
        public const float LayerPatch = 5f;

        /// <summary>bg: offset_bottom = 266 below the band's bottom edge.</summary>
        public const float LayerHeight = 266f;

        /// <summary>custom_minimum_size of the level and class toggles.</summary>
        public const float ToggleSize = 60f;

        /// <summary>
        /// A nation toggle's icon_max_width. The scene's five flags had
        /// 100 x 65 toggles of their own; the nine of the port ride in class
        /// sized toggles, the flag inset like a tier numeral.
        /// </summary>
        public const float FlagWidth = 48f;

        /// <summary>Godot's default BoxContainer separation.</summary>
        public const float Separation = 4f;

        /// <summary>
        /// nation1 .. nation9, one per <see cref="Nation"/>. The scene stops
        /// at nation5, the five nations of its filter_vars_nations.
        /// </summary>
        public const int NationCount = 9;

        /// <summary>filter_nations: <see cref="NationCount"/> toggles, 4 px apart.</summary>
        public const float NationRowWidth = (NationCount * (ToggleSize + Separation)) - Separation;

        /// <summary>filter_nations in the scene: five 100 px flags, 4 px apart.</summary>
        public const float SceneNationRowWidth = 516f;

        /// <summary>filter_regulars, filter_premiums, filter_collectors and filter_mods.</summary>
        public const int StatusCount = 4;

        /// <summary>
        /// From one status frame to the next: border5 is 72 px wide and sits
        /// 19 px past border, so border6 .. border8 repeat both.
        /// </summary>
        public const float StatusPitch = 91f;

        /// <summary>The ButtonSmallGeneric texture of every filter button.</summary>
        private const string ButtonRegion = "button_small_generic";

        /// <summary>lvl1 .. lvl11 icon_max_width.</summary>
        private static readonly float[] LevelIconWidths =
        {
            10f, 16f, 24f, 26f, 20f, 26f, 34f, 40f, 28f, 21f, 30f,
        };

        /// <summary>filter_classes children, in filter_vars_classes order.</summary>
        private static readonly string[] ClassNodes = { "lt", "mt", "ht", "td" };

        private static readonly TankClass[] ClassValues =
        {
            TankClass.Light, TankClass.Medium, TankClass.Heavy, TankClass.TankDestroyer,
        };

        /// <summary>selected under lvl* and lt .. td, drawn at scale 0.33.</summary>
        private static readonly Vector4 ToggleSelector =
            new Vector4(-27.999996f, -27.999977f, 141.69695f, 141.69693f);

        /// <summary>selected under the status toggles, inside border's own scale.</summary>
        private static readonly Vector4 StatusSelector =
            new Vector4(-92.42319f, -81.82126f, 92.4252f, 84.84537f);

        private sealed class FilterToggle
        {
            public Button Button;
            public GameObject Selected;
            public Func<bool> IsOn;
        }

        private readonly List<FilterToggle> _toggles = new List<FilterToggle>();
        private HangarUiKit _kit;
        private CarouselFilter _filter;
        private RectTransform _band;
        private bool _open;
        private float _playhead;

        /// <summary>A toggle or btn_reset_filter changed the filter: update_carousel().</summary>
        public Action Changed;

        /// <summary>
        /// screens/main/filter_handle/filter. The hangar builds its footer
        /// widgets into it, so they ride up with the filter.
        /// </summary>
        public RectTransform Band
        {
            get { return _band; }
        }

        /// <summary>btn_filter's pressed state, i.e. ui_hangar.gd's filter_shown.</summary>
        public bool IsOpen
        {
            get { return _open; }
        }

        /// <summary>The show_filter playhead, 0 .. <see cref="SlideSeconds"/>.</summary>
        public float Playhead
        {
            get { return _playhead; }
        }

        /// <summary>The band's position.y right now.</summary>
        public float PositionY
        {
            get { return SlideY(_playhead); }
        }

        /// <summary>True while any part of the filter rows is above the screen edge.</summary>
        public bool IsVisible
        {
            get { return _playhead > 0f; }
        }

        public CarouselFilter Filter
        {
            get { return _filter; }
        }

        /// <summary>btn_reset_filter, once <see cref="BuildControls"/> ran.</summary>
        public Button ResetButton { get; private set; }

        /// <summary>Levels, classes, nations and the four status toggles.</summary>
        public int ToggleCount
        {
            get { return _toggles.Count; }
        }

        public static HangarFilterDrawer Create(
            RectTransform parent, HangarUiKit kit, CarouselFilter filter)
        {
            if (parent == null)
            {
                throw new ArgumentNullException("parent");
            }

            if (kit == null)
            {
                throw new ArgumentNullException("kit");
            }

            if (filter == null)
            {
                throw new ArgumentNullException("filter");
            }

            // filter_handle: a zero-height strip along the bottom edge that
            // the band is positioned against.
            var handle = UiFactory.Node("filter_handle", parent);
            GodotRect.Apply(
                handle, 0f, 1f, 1f, 1f, 0f, 0f, 0f, 0f, GodotRect.TopLeftPivot);
            var band = UiFactory.Node("filter", handle);

            var drawer = handle.gameObject.AddComponent<HangarFilterDrawer>();
            drawer._kit = kit;
            drawer._filter = filter;
            drawer._band = band;
            drawer.SetOpenImmediate(false);
            return drawer;
        }

        /// <summary>
        /// bg, the eight frames and the filter rows, in the scene's sibling
        /// order. The hangar calls this between btn_techTree and
        /// blackGradTile2, which keeps the footer's draw order around them.
        /// </summary>
        public void BuildControls()
        {
            if (ResetButton != null)
            {
                throw new InvalidOperationException("The filter rows are already built.");
            }

            // bg is mouse_filter = STOP: a tap on the open filter never falls
            // through to the hangar camera.
            var layer = UiFactory.Picture("bg", _band, Patch("hangar_bottom_layer", LayerPatch));
            layer.type = Image.Type.Sliced;
            layer.raycastTarget = true;
            GodotRect.Apply(
                layer.rectTransform, 0f, 1f, 1f, 1f,
                0f, 0f, 0f, LayerHeight, GodotRect.TopLeftPivot);

            // border frames the level row. Its alpha is self_modulate, so its
            // children -- the vehicle-status toggles, which sit past its
            // right edge -- stay fully opaque.
            var border = Frame("border", -463f, 32.999985f, 1530.9392f, 269.36365f);
            StatusToggle(
                border, "filter_regulars", HangarUiKit.RegularBadgeRegion, 0,
                () => _filter.Regular, _filter.ToggleRegular);
            StatusToggle(
                border, "filter_premiums", HangarUiKit.PremiumBadgeRegion, 1,
                () => _filter.Premium, _filter.TogglePremium);
            StatusToggle(
                border, "filter_collectors", HangarUiKit.CollectorBadgeRegion, 2,
                () => _filter.Collector, _filter.ToggleCollector);
            StatusToggle(
                border, "filter_mods", HangarUiKit.ModBadgeRegion, 3,
                () => _filter.Mods, _filter.ToggleMods);

            // The nine nations lengthen filter_nations: border3 grows by as
            // much, and border4 and reset_filter move over by it.
            var growth = NationRowWidth - SceneNationRowWidth;
            Frame("border2", -463f, 153f, 370.33325f, 392.39404f);
            Frame("border3", -140f, 153f, 1456.9696f + (growth / NodeScale), 392.39404f);
            Frame("border4", 414f + growth, 154.99902f, 632.1819f + growth, 373.18066f);

            // border5 frames the tech tree toggle, in the scene's premium
            // frame; border6 .. border8 frame the premium, collector and mod
            // toggles alike, a pitch further on each.
            for (var slot = 0; slot < StatusCount; slot++)
            {
                var shift = slot * StatusPitch;
                Frame(
                    "border" + (5 + slot).ToString(CultureInfo.InvariantCulture),
                    214.00006f + shift, 34.999023f, 432.1819f + shift, 253.18066f);
            }

            var levels = Row("filter_levels", -452f, 42f, 184f, 102f);
            for (var i = 0; i < LevelIconWidths.Length; i++)
            {
                var level = CarouselFilter.MinLevel + i;
                var button = AddToggle(
                    levels, "lvl" + level, "hangar_lvl" + level, LevelIconWidths[i],
                    ToggleSelector, true,
                    () => _filter.HasLevel(level), () => _filter.ToggleLevel(level));
                HangarUiKit.Place(
                    (RectTransform)button.transform,
                    i * (ToggleSize + Separation), 0f, ToggleSize, ToggleSize);
            }

            var classes = Row("filter_classes", -452f, 162f, -200f, 222f);
            for (var i = 0; i < ClassNodes.Length; i++)
            {
                var tankClass = ClassValues[i];
                var button = AddToggle(
                    classes, ClassNodes[i], "hangar_class_" + ClassNodes[i], 26f,
                    ToggleSelector, true,
                    () => _filter.HasClass(tankClass), () => _filter.ToggleClass(tankClass));
                HangarUiKit.Place(
                    (RectTransform)button.transform,
                    i * (ToggleSize + Separation), 0f, ToggleSize, ToggleSize);
            }

            // filter_vars_nations[info.nation - 1]: nation1 is Nation.Ussr.
            // The row starts where the scene's does, on the class row's line.
            var nations = Row("filter_nations", -135f, 162f, -135f + NationRowWidth, 222f);
            for (var i = 0; i < NationCount; i++)
            {
                var nation = (Nation)(i + 1);
                var button = AddToggle(
                    nations, "nation" + (i + 1), HangarUiKit.FilterNationRegion(nation), FlagWidth,
                    ToggleSelector, true,
                    () => _filter.HasNation(nation), () => _filter.ToggleNation(nation));
                HangarUiKit.Place(
                    (RectTransform)button.transform,
                    i * (ToggleSize + Separation), 0f, ToggleSize, ToggleSize);
            }

            var reset = SmallGenericButton("reset_filter", _band);
            BottomCentre(
                (RectTransform)reset.transform, 420f + growth, 160.99896f, 480f + growth, 220.99896f);
            _kit.AddIcon(reset, "hangar_red_button", 60f);
            _kit.Bind(reset, ResetFilter, AudioService.SoundSelect);
            ResetButton = reset;

            Refresh();
        }

        // ------------------------------------------------------------------
        // Filter state.
        // ------------------------------------------------------------------

        /// <summary>
        /// btn_reset_filter.pressed: every toggle is released. Godot only
        /// rebuilds the carousel for a toggle that actually flips, so an
        /// empty filter stays as it is. Not named Reset: the editor sends
        /// that message to a component as soon as AddComponent adds it.
        /// </summary>
        public void ResetFilter()
        {
            if (!_filter.IsActive)
            {
                return;
            }

            _filter.Clear();
            Refresh();
            Raise(Changed);
        }

        /// <summary>Redraws every toggle from the filter: latch and outline.</summary>
        public void Refresh()
        {
            for (var i = 0; i < _toggles.Count; i++)
            {
                var toggle = _toggles[i];
                var on = toggle.IsOn();
                var state = GodotButtonState.Of(toggle.Button);
                if (state != null)
                {
                    state.Latched = on;
                }

                if (toggle.Selected.activeSelf != on)
                {
                    toggle.Selected.SetActive(on);
                }
            }
        }

        /// <summary>The toggle node called <paramref name="name"/> (lvl3, mt, nation2, ...).</summary>
        public Button FindToggle(string name)
        {
            for (var i = 0; i < _toggles.Count; i++)
            {
                if (_toggles[i].Button.name == name)
                {
                    return _toggles[i].Button;
                }
            }

            return null;
        }

        /// <summary>True while the toggle's selection outline is drawn.</summary>
        public bool IsSelected(string name)
        {
            for (var i = 0; i < _toggles.Count; i++)
            {
                if (_toggles[i].Button.name == name)
                {
                    return _toggles[i].Selected.activeSelf;
                }
            }

            return false;
        }

        // ------------------------------------------------------------------
        // show_filter.
        // ------------------------------------------------------------------

        /// <summary>Godot's Math::ease(), which Animation applies per key transition.</summary>
        public static float Ease(float x, float curve)
        {
            x = Mathf.Clamp01(x);
            if (curve > 0f)
            {
                return curve < 1f
                    ? 1f - Mathf.Pow(1f - x, 1f / curve)
                    : Mathf.Pow(x, curve);
            }

            if (curve < 0f)
            {
                return x < 0.5f
                    ? Mathf.Pow(x * 2f, -curve) * 0.5f
                    : ((1f - Mathf.Pow(1f - ((x - 0.5f) * 2f), -curve)) * 0.5f) + 0.5f;
            }

            return 0f;
        }

        /// <summary>The show_filter position track sampled at <paramref name="seconds"/>.</summary>
        public static float SlideY(float seconds)
        {
            var t = Mathf.Clamp01(seconds / SlideSeconds);
            return Mathf.Lerp(ClosedY, OpenY, Ease(t, Transition));
        }

        /// <summary>btn_filter.toggled: play("show_filter") or play_backwards(it).</summary>
        public void SetOpen(bool open)
        {
            _open = open;
        }

        public void Toggle()
        {
            SetOpen(!_open);
        }

        /// <summary>
        /// Jumps to an end state, the way to_main_screen() parks the band
        /// with play("show_filter", -1, -1000, true).
        /// </summary>
        public void SetOpenImmediate(bool open)
        {
            _open = open;
            _playhead = open ? SlideSeconds : 0f;
            Apply();
        }

        /// <summary>
        /// One AnimationPlayer step. play() and play_backwards() on the
        /// running animation only turn it around, so a filter closed halfway
        /// up slides back from where it is.
        /// </summary>
        public void Advance(float deltaSeconds)
        {
            var target = _open ? SlideSeconds : 0f;
            if (Mathf.Approximately(_playhead, target) && _playhead == target)
            {
                return;
            }

            _playhead = Mathf.MoveTowards(_playhead, target, Mathf.Max(deltaSeconds, 0f));
            Apply();
        }

        private void Update()
        {
            Advance(Time.unscaledDeltaTime);
        }

        private void Apply()
        {
            if (_band == null)
            {
                return;
            }

            var y = SlideY(_playhead);
            GodotRect.Apply(
                _band, 0f, 1f, 1f, 1f, 0f, y, 0f, y + BandHeight, GodotRect.TopLeftPivot);
        }

        // ------------------------------------------------------------------
        // Widgets.
        // ------------------------------------------------------------------

        /// <summary>border .. border8: a faint hangar_select_tile frame.</summary>
        private RectTransform Frame(
            string name, float left, float top, float right, float bottom)
        {
            var image = UiFactory.Picture(name, _band, Patch("hangar_select_tile", FramePatch));
            image.type = Image.Type.Sliced;
            image.color = new Color(1f, 1f, 1f, FrameAlpha);
            var rt = image.rectTransform;
            BottomCentre(rt, left, top, right, bottom);
            rt.localScale = new Vector3(NodeScale, NodeScale, 1f);
            return rt;
        }

        /// <summary>filter_levels, filter_classes, filter_nations: plain HBoxes.</summary>
        private RectTransform Row(
            string name, float left, float top, float right, float bottom)
        {
            var row = UiFactory.Node(name, _band);
            BottomCentre(row, left, top, right, bottom);
            return row;
        }

        /// <summary>
        /// filter_regulars / filter_premiums / filter_collectors / filter_mods:
        /// children of border, anchored to its right-hand middle, so they
        /// inherit its scale. filter_regulars keeps the scene's
        /// filter_premiums rect; each later toggle sits
        /// <see cref="StatusPitch"/> further on, inside its own frame.
        /// </summary>
        private void StatusToggle(
            RectTransform border,
            string name,
            string iconRegion,
            int slot,
            Func<bool> isOn,
            Action flip)
        {
            var button = AddToggle(
                border, name, iconRegion, 183f, StatusSelector, false, isOn, flip);
            var left = 72.72632f + (slot * StatusPitch / NodeScale);
            GodotRect.Apply(
                (RectTransform)button.transform, 1f, 0.5f, 1f, 0.5f,
                left, -90.90765f, left + 184.84863f, 84.8471f, GodotRect.TopLeftPivot);
        }

        /// <summary>
        /// One ButtonCustom toggle: icon, toggle_rect outline and the
        /// snd_var = SOUND_SELECT click. toggle_sound is off, so only
        /// _pressed() makes a sound.
        /// </summary>
        private Button AddToggle(
            RectTransform parent,
            string name,
            string iconRegion,
            float iconMaxWidth,
            Vector4 selector,
            bool scaledSelector,
            Func<bool> isOn,
            Action flip)
        {
            var button = SmallGenericButton(name, parent);
            GodotButtonState.Of(button).ToggleMode = true;
            _kit.AddIcon(button, iconRegion, iconMaxWidth);

            var toggle = new FilterToggle
            {
                Button = button,
                Selected = Selector(button, selector, scaledSelector),
                IsOn = isOn,
            };
            _toggles.Add(toggle);

            _kit.Bind(button, () =>
            {
                flip();
                Refresh();
                Raise(Changed);
            }, AudioService.SoundSelect);
            return button;
        }

        /// <summary>
        /// ButtonSmallGeneric draws one texture, stretched, for normal,
        /// hover and pressed alike, so a held or latched button never darkens.
        /// </summary>
        private Button SmallGenericButton(string name, RectTransform parent)
        {
            var sprite = _kit.Art(ButtonRegion);
            var button = UiFactory.TextButton(name, parent, sprite, _kit.Bold, 0, null);
            button.GetComponent<Image>().type = Image.Type.Simple;
            GodotButtonState.Of(button).SetPressedStyle(sprite, Color.white);
            return button;
        }

        /// <summary>
        /// toggle_rect ("selected"): the tree_selector outline ButtonCustom
        /// shows only while its toggle stays pressed.
        /// </summary>
        private GameObject Selector(Button button, Vector4 rect, bool scaled)
        {
            var image = UiFactory.Picture(
                "selected", button.transform, Patch("tree_selector", SelectorPatch));
            image.type = Image.Type.Sliced;
            var rt = image.rectTransform;
            GodotRect.Apply(
                rt, 0.5f, 0.5f, 0.5f, 0.5f, rect.x, rect.y, rect.z, rect.w,
                GodotRect.TopLeftPivot);
            if (scaled)
            {
                rt.localScale = new Vector3(NodeScale, NodeScale, 1f);
            }

            image.gameObject.SetActive(false);
            return image.gameObject;
        }

        private Sprite Patch(string region, float margin)
        {
            return _kit.Atlas == null ? null : _kit.Atlas.GetNinePatch(region, margin);
        }

        /// <summary>anchors_preset 7 (center bottom) inside the band.</summary>
        private static void BottomCentre(
            RectTransform rt, float left, float top, float right, float bottom)
        {
            GodotRect.Apply(
                rt, 0.5f, 1f, 0.5f, 1f, left, top, right, bottom, GodotRect.TopLeftPivot);
        }

        private static void Raise(Action action)
        {
            if (action != null)
            {
                action();
            }
        }
    }
}
