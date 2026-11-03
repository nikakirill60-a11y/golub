using System;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Runtime;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Platform;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// menu_bg from assets/gfx/ui/ui_player.tscn: the in-battle escape menu
    /// with TO HANGAR, SETTINGS and RESUME.
    ///
    /// The source keeps the window in the HUD tree and only toggles its
    /// visibility. player_controller (base_tank.tscn, _unhandled_input) owns
    /// the toggle:
    ///
    ///   Util.mouse_lock(menu.visible)
    ///   menu.visible = !menu.visible
    ///   if is_instance_valid(player_ui.popup): player_ui.popup.queue_free()
    ///
    /// so the pointer is released *before* the flip - it is free while the
    /// menu is up and captured again when it closes - and an open confirmation
    /// dies with the menu. The scene tree is never paused: the battle keeps
    /// running behind the window, and what stops the player from driving is
    /// only the get_mouse_lock() gate at the top of the controller's physics
    /// tick, which is always open on a phone.
    ///
    /// btn_resume hides the menu and re-captures the pointer, btn_hangar opens
    /// Global.create_popup("CONFIRM", "popup:to_hangar", false, Global.to_hangar)
    /// and btn_settings opens ui_settings.tscn, which is ported separately.
    ///
    /// Geometry, styles and captions come from
    /// Resources/Data/battle_menu_source_manifest.json, dumped headlessly by
    /// Tools/godot_battle_menu_dump.gd; BattlePauseMenuTests re-reads that
    /// manifest and fails if these constants drift from it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattlePauseMenu : MonoBehaviour
    {
        /// <summary>Above the HUD and above the controls layer.</summary>
        public const int SortingOrder = BattleHud.SortingOrder + 4;

        /// <summary>grey_panel keeps its authored 0.5 scale.</summary>
        public const float PanelScale = 0.5f;

        public const int TitleFontSize = 45;
        public const int ButtonFontSize = 50;

        private LocalizationTable _lang;
        private AtlasRegionTable _atlas;
        private Font _bold;
        private Font _regular;

        /// <summary>menu_bg, the control whose visibility the action flips.</summary>
        public RectTransform MenuRoot { get; private set; }

        public Image Panel { get; private set; }

        public Text Title { get; private set; }

        public Button Hangar { get; private set; }

        public Button Settings { get; private set; }

        public Button Resume { get; private set; }

        /// <summary>ui_player_battle.popup, the live confirmation window.</summary>
        public GenericPopup Popup { get; private set; }

        /// <summary>
        /// btn_settings. ui_settings.tscn is stage E of the port, so the menu
        /// raises the request instead of guessing at that screen.
        /// </summary>
        public Action SettingsPressed;

        /// <summary>Global.to_hangar, once the confirmation is accepted.</summary>
        public Action HangarConfirmed;

        public bool Visible
        {
            get { return MenuRoot != null && MenuRoot.gameObject.activeSelf; }
        }

        public static BattlePauseMenu Create(Transform parent, bool mobile)
        {
            var go = new GameObject("BattlePauseMenu");
            go.transform.SetParent(parent, false);
            var menu = go.AddComponent<BattlePauseMenu>();
            menu.Build(mobile);
            return menu;
        }

        private void Build(bool mobile)
        {
            _atlas = BattleFlowUi.LoadAtlas();
            _lang = BattleFlowUi.LoadLanguage();
            _bold = BlitzResources.LoadFont(BlitzResources.BoldFontName);
            _regular = BlitzResources.LoadFont(BlitzResources.RegularFontName);

            var canvas = BattleFlowUi.CreateCanvas(
                transform, true, mobile, SortingOrder);

            // menu_bg: preset 15 with mouse_filter IGNORE, so the window itself
            // never eats a click - only its buttons do, and a drag that misses
            // them still turns the turret underneath.
            MenuRoot = UiFactory.Node("menu_bg", canvas);
            GodotRect.FullRect(MenuRoot);

            Panel = Sliced("menu", MenuRoot, "panel_body");
            GodotRect.Apply(
                Panel.rectTransform, 0.5f, 0.5f, 0.5f, 0.5f,
                -119.0001f, -132.0001f, 353.0001f, 414.0001f,
                GodotRect.TopLeftPivot);
            Panel.rectTransform.localScale = new Vector3(PanelScale, PanelScale, 1f);

            var strip = Sliced("header", Panel.transform, "panel_header");
            GodotRect.Apply(
                strip.rectTransform, 0f, 0f, 1f, 0f,
                0f, -84f, 0f, 2f, GodotRect.TopLeftPivot);

            Title = UiFactory.Label(
                "header2", Panel.transform, _bold, TitleFontSize, UiTheme.BoldLabel,
                TextAnchor.UpperCenter, _lang.Get("MENU"));
            UiFactory.AddShadow(Title, UiTheme.BoldLabelShadow);
            GodotRect.Apply(
                Title.rectTransform, 0.5f, 0f, 0.5f, 0f,
                -40.4999f, -52f, 40.5001f, -16f);

            Hangar = MakeButton(
                "btn_hangar", "button_red", "TO HANGAR",
                20.0001f, 26.0001f, 451.0001f, 154.0001f, OnHangar);
            Settings = MakeButton(
                "btn_settings", "button_generic", "SETTINGS",
                20.0001f, 166.0001f, 451.0001f, 294.0001f, OnSettings);
            Resume = MakeButton(
                "btn_resume", "button_orange", "RESUME",
                20.0001f, 382.0001f, 451.0001f, 510.0001f, OnResume);

            // menu.hide() in ui_player_battle's setup, and visible = false in
            // the scene itself.
            MenuRoot.gameObject.SetActive(false);
        }

        private Image Sliced(string name, Transform parent, string region)
        {
            var image = UiFactory.Picture(name, parent, _atlas.Get(region));
            image.type = Image.Type.Sliced;
            return image;
        }

        private Button MakeButton(
            string name,
            string region,
            string key,
            float left,
            float top,
            float right,
            float bottom,
            Action pressed)
        {
            var button = UiFactory.TextButton(
                name, Panel.transform, _atlas.Get(region), _bold,
                ButtonFontSize, _lang.Get(key));
            var image = button.GetComponent<Image>();
            image.type = Image.Type.Sliced;
            GodotRect.TopLeft((RectTransform)button.transform, left, top, right, bottom);
            button.onClick.AddListener(() =>
            {
                // button_generic.gd plays Audio.ui_enter at 0.7 on every press.
                var audio = BlitzBootstrap.Current != null
                    ? BlitzBootstrap.Current.Audio : null;
                if (audio != null)
                {
                    audio.PlayButtonClick(AudioService.SoundEnter);
                }

                pressed();
            });
            return button;
        }

        /// <summary>
        /// The "menu" action (Escape or P, and btn_menu on a phone) exactly as
        /// player_controller._unhandled_input runs it.
        /// </summary>
        public void Toggle()
        {
            if (MenuRoot == null)
            {
                return;
            }

            // The lock follows the state the menu is leaving, not the one it
            // is entering: the pointer is released while the window is up.
            MouseLock.Set(Visible);
            MenuRoot.gameObject.SetActive(!Visible);
            ClosePopup();
        }

        /// <summary>menu.show(), which btn_menu is wired straight to.</summary>
        public void Show()
        {
            if (MenuRoot != null)
            {
                MenuRoot.gameObject.SetActive(true);
            }
        }

        /// <summary>menu.hide().</summary>
        public void Hide()
        {
            if (MenuRoot != null)
            {
                MenuRoot.gameObject.SetActive(false);
            }
        }

        /// <summary>b_resume: hide the window and take the pointer back.</summary>
        private void OnResume()
        {
            Hide();
            if (!MouseLock.Locked)
            {
                MouseLock.Set(true);
            }
        }

        /// <summary>
        /// b_hangar: Global.create_popup("CONFIRM", "popup:to_hangar", false,
        /// Global.to_hangar).
        /// </summary>
        private void OnHangar()
        {
            ClosePopup();
            Popup = GenericPopup.Create(
                (RectTransform)MenuRoot.parent, _atlas, _lang, _bold, _regular,
                "CONFIRM", "popup:to_hangar", false, ToHangar);
            Popup.Dismissed = () => Popup = null;
        }

        private void OnSettings()
        {
            if (SettingsPressed != null)
            {
                SettingsPressed();
            }
        }

        private void ToHangar()
        {
            if (HangarConfirmed != null)
            {
                HangarConfirmed();
                return;
            }

            if (BlitzBootstrap.Current != null)
            {
                BlitzBootstrap.Current.ToHangar();
            }
        }

        /// <summary>popup.queue_free(), which the menu action also performs.</summary>
        public void ClosePopup()
        {
            if (Popup != null)
            {
                Popup.Close();
                Popup = null;
            }
        }
    }
}
