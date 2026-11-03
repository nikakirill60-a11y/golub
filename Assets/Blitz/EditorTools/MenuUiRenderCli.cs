using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Hangar;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Shop;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Tanks;
using BlitzLegacy.Runtime.Ui;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Play Mode render of every redesigned menu screen in both styles:
    /// hangar, filter, tech tree (folded and pulled-up carousel), modules,
    /// loadout, armour viewer, camouflage shop, settings, mods, mode picker, queue, loading
    /// screen and the first-run picker. Frames land in Logs/menu-ui-render as
    /// classic_*.png and modern_*.png, so the classic set doubles as a
    /// regression reference for the untouched style.
    /// </summary>
    [InitializeOnLoad]
    public static class MenuUiRenderCli
    {
        private const string ActiveKey = "Blitz.MenuUiRender.Active";
        private const string ExitCodeKey = "Blitz.MenuUiRender.ExitCode";
        private const string OutputDirectory = "Logs/menu-ui-render";
        public const int Width = 1560;
        public const int Height = 720;

        private static bool _hooked;
        private static readonly List<Step> Steps = new List<Step>();

        /// <summary>The equalizer's curve in the render: the bass and the treble raised, a V that moves every fader.</summary>
        private static readonly int[] RenderCurve = { 5, 4, 2, 0, -2, -2, 0, 2, 4, 5 };
        private static int _step;
        private static double _nextAt;
        private static int _frames;
        private static AccountData _account;
        private static HangarScreen _hangar;
        private static Camera _uiCamera;
        private static GameObject _rosterRoot;

        private struct Step
        {
            public string Name;
            public Action Run;
            public float Wait;
        }

        static MenuUiRenderCli()
        {
            if (SessionState.GetBool(ActiveKey, false)) { Hook(); }
        }

        public static void Render()
        {
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetInt(ExitCodeKey, -1);
            Hook();
            try
            {
                EditorSceneManager.OpenScene(SceneGen.BootstrapScenePath, OpenSceneMode.Single);
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
            EditorApplication.update += OnUpdate;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(ActiveKey, false)) { return; }
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                Plan();
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                var code = SessionState.GetInt(ExitCodeKey, -1);
                if (code >= 0) { Quit(code); }
            }
        }

        private static void Plan()
        {
            Steps.Clear();
            _step = 0;
            _frames = 0;
            _nextAt = EditorApplication.timeSinceStartup + 1.0;
            MenuStyle.AspectOverride = Width / (float)Height;
            Directory.CreateDirectory(OutputDirectory);

            Add("setup", Setup, 0.2f);
            PlanStyle("classic");
            PlanStyle("modern");
            Add("picker", () =>
            {
                Environment.SetEnvironmentVariable(MenuStyleRules.OverrideVariable, "modern");
                var picker = StylePickerScreen.Create(Bootstrap().transform, "ru", null);
                picker.Choose(SettingsData.UiStyleModern);
            }, 0.4f);
            Add("picker_capture", () =>
            {
                Capture("modern_style_picker.png");
                Destroy<StylePickerScreen>();
            }, 0.1f);
            Add("done", () => Finish(0), 0f);
        }

        private static void PlanStyle(string style)
        {
            Add(style + "_hangar", () =>
            {
                Environment.SetEnvironmentVariable(MenuStyleRules.OverrideVariable, style);
                if (_hangar != null) { _hangar.Discard(); }
                // Every style meets the camouflage shop of the selected tank
                // unvisited and with nothing bought, so its hangar slot wears
                // the red badge again and the shop opens on a purchase.
                var selected = _account.GetTank(_account.SelectedTankName);
                if (selected != null)
                {
                    selected.CamouflageSeen = false;
                    selected.OwnedCamouflages.Clear();
                    for (var i = 0; i < selected.Camouflages.Count; i++) { selected.Camouflages[i] = string.Empty; }
                }
                _hangar = HangarScreen.Create(Bootstrap(), _account);
                Require(_hangar.IsModern == (style == "modern"), "hangar style mismatch for " + style);
            }, 2.5f);
            Add(style + "_hangar_capture", () => Capture(style + "_hangar.png"), 0.1f);

            // The Miku hangar puts the music player up out of the box: the
            // panel open, folded, on the playlist and on the equalizer with a
            // curve, where the hangar puts it. The fold, the place, the view
            // and the curve are set on the settings in memory only, and the
            // view and the curve are put back after, so the render leaves the
            // saved settings alone.
            var savedGains = new int[HangarMusicEqualizer.BandCount];
            var savedEqOn = false;
            Add(style + "_hangar_music", () =>
            {
                Bootstrap().Settings.SetString("hangar", HangarCatalog.MikuId);
                _hangar.RefreshHangarKind();
                Require(_hangar.World.Kind == HangarKind.Miku, "the Miku hangar did not come up for " + style);
                Require(_hangar.MusicPlayerOn && _hangar.MusicPanel != null, "the Miku hangar has no music player");
                Bootstrap().Settings.HangarMusic.Collapsed = false;
                Bootstrap().Settings.HangarMusic.Placed = false;
            }, 2.5f);
            Add(style + "_hangar_music_capture", () =>
            {
                Require(_hangar.MusicPanel.Visible, "the music panel is not up in the Miku hangar");
                Capture(style + "_hangar_music.png");
                Bootstrap().Settings.HangarMusic.Collapsed = true;
            }, 0.3f);
            Add(style + "_hangar_music_collapsed_capture", () =>
            {
                Capture(style + "_hangar_music_collapsed.png");
                Bootstrap().Settings.HangarMusic.Collapsed = false;
                Bootstrap().Settings.HangarMusic.View = HangarMusicView.Playlist;
            }, 0.3f);
            Add(style + "_hangar_music_playlist_capture", () =>
            {
                Capture(style + "_hangar_music_playlist.png");
                var music = Bootstrap().Settings.HangarMusic;
                savedEqOn = music.EqOn;
                for (var band = 0; band < savedGains.Length; band++)
                {
                    savedGains[band] = music.EqGain(band);
                    music.SetEqGain(band, RenderCurve[band]);
                }

                music.EqOn = true;
                music.View = HangarMusicView.Equalizer;
            }, 0.3f);
            Add(style + "_hangar_music_eq_capture", () =>
            {
                Capture(style + "_hangar_music_eq.png");
                var music = Bootstrap().Settings.HangarMusic;
                for (var band = 0; band < savedGains.Length; band++)
                {
                    music.SetEqGain(band, savedGains[band]);
                }

                music.EqOn = savedEqOn;
                music.View = HangarMusicView.Player;
                Bootstrap().Settings.SetString("hangar", string.Empty);
                _hangar.RefreshHangarKind();
            }, 2.5f);

            // show_filter takes 0.4 s; a tier and a class are picked so the
            // outlines and the narrowed carousel are in the shot.
            Add(style + "_filter", () => Click("btn_filter"), 0.6f);
            Add(style + "_filter_pick", () => { Click("lvl5"); Click("mt"); }, 0.2f);
            Add(style + "_filter_capture", () =>
            {
                Capture(style + "_filter.png");
                Click("reset_filter");
                Click("btn_filter");
            }, 0.6f);

            if (style == "modern")
            {
                // The burger drawer folds the carousel out of its way; pulling
                // the handle or pressing btn_menu again brings the bar back.
                Add("modern_menu", () => Click("btn_menu"), 0.6f);
                Add("modern_menu_capture", () =>
                {
                    Require(_hangar.IsMenuOpen, "btn_menu did not open the drawer");
                    Require(_hangar.CarouselState == CarouselMode.Collapsed, "the open drawer did not fold the carousel");
                    Capture("modern_menu.png");
                    Click("carousel_handle");
                }, 0.6f);
                Add("modern_menu_handle_closed", () =>
                {
                    Require(!_hangar.IsMenuOpen, "the carousel handle did not close the drawer");
                    Require(_hangar.CarouselState == CarouselMode.Normal, "the handle did not restore the carousel");
                    Click("btn_menu");
                }, 0.4f);
                Add("modern_menu_reopened", () =>
                {
                    Require(_hangar.CarouselState == CarouselMode.Collapsed, "the reopened drawer did not fold the carousel");
                    Click("btn_menu");
                }, 0.6f);
                Add("modern_menu_button_closed", () =>
                {
                    Require(!_hangar.IsMenuOpen, "btn_menu did not close the drawer");
                    Require(_hangar.CarouselState == CarouselMode.Normal, "btn_menu did not restore the carousel");
                }, 0.1f);
            }

            Add(style + "_techtree", () => Click("btn_techTree"), 0.6f);
            Add(style + "_techtree_capture", () => Capture(style + "_techtree.png"), 0.1f);
            if (style == "classic")
            {
                // The re-laid hybrid trees, as four quarters per nation.
                foreach (var nation in new[] { Nation.Usa, Nation.Germany })
                {
                    var tag = nation.ToString().ToLowerInvariant();
                    foreach (var level in new[] { 1, 6 })
                    {
                        foreach (var top in new[] { true, false })
                        {
                            var shot = "classic_techtree_" + tag + "_" + level + (top ? "_top" : "_bottom");
                            var selected = nation; var column = level; var rows = top ? 0 : 4;
                            Add(shot, () =>
                            {
                                if (_hangar.TechTree.CurrentNation != selected) { _hangar.TechTree.Update(selected); }
                                _hangar.TechTree.ScrollToLevel(column);
                                _hangar.TechTree.ScrollToRow(rows);
                            }, 0.3f);
                            Add(shot + "_capture", () => Capture(shot + ".png"), 0.1f);
                        }
                    }
                }

                Add("classic_techtree_reset", () => _hangar.TechTree.Update(Nation.Ussr), 0.2f);
            }
            if (style == "modern")
            {
                Add("modern_handle", () =>
                {
                    // Folding only stops the bar's canvas from drawing; a
                    // deactivated bar re-enabled every card on the pull-up.
                    Require(_hangar.CarouselBar.gameObject.activeSelf && !_hangar.CarouselBarDrawn,
                        "the folded carousel bar must stay active with its canvas off");
                    Click("carousel_handle");
                    Require(_hangar.CarouselBarDrawn, "the rising carousel bar is not drawn from its first frame");
                }, 0.5f);
                Add("modern_handle_capture", () =>
                {
                    Require(_hangar.CarouselState == CarouselMode.Expanded, "handle did not expand the carousel");
                    Require(_hangar.CarouselBarDrawn, "the expanded carousel bar is not drawn");
                    Capture("modern_techtree_expanded.png");
                    Click("carousel_handle");
                }, 0.4f);
            }

            Add(style + "_home1", () => _hangar.TechTree.HomePressed(), 0.4f);
            Add(style + "_modules", () => Click("btn_research"), 0.6f);
            Add(style + "_modules_capture", () => { Capture(style + "_modules.png"); _hangar.Modules.HomePressed(); }, 0.4f);
            Add(style + "_loadout", () => Click("btn_shells"), 0.6f);
            Add(style + "_loadout_capture", () => { Capture(style + "_loadout.png"); _hangar.Loadout.HomePressed(); }, 0.4f);
            Add(style + "_shop", () =>
            {
                Bootstrap().NetworkClock.SetSynced(1750032000d);
                Click("btn_shop");
                Require(_hangar.CurrentScreen == HangarScreen.HangarScreenId.Shop, "Shop navigation failed");
            }, 0.3f);
            Add(style + "_shop_premium_capture", () =>
            {
                // Both styles keep the hangar header in the shop: the
                // currencies, a title next to the back arrow, no cart.
                foreach (var amount in new[] { "gold_amount", "credits_amount", "freexp_amount" })
                {
                    Require(_hangar.CurrencyShown(amount), "the shop header lost " + amount);
                }
                Require(FindButton(_hangar.Shop.Overlay, "btn_menu") != null, "the shop has no back arrow");
                var title = FindText(_hangar.Shop.Overlay, "title");
                Require(title != null && title.isActiveAndEnabled && title.text.Length > 0 && !title.text.StartsWith("shop:"),
                    "the shop header has no title");
                Require(!_hangar.IsModern || (_hangar.CarouselBar.gameObject.activeSelf && !_hangar.CarouselBarDrawn),
                    "the hidden carousel bar must stay active with its canvas off");
                Capture(style + "_shop_premium.png");
            }, 0.1f);
            Add(style + "_shop_tanks", () => _hangar.Shop.ShowTab(ShopScreen.TabTanks), 0.3f);
            Add(style + "_shop_tanks_capture", () => Capture(style + "_shop_tanks.png"), 0.1f);
            Add(style + "_shop_containers", () => _hangar.Shop.ShowTab(ShopScreen.TabContainers), 0.3f);
            Add(style + "_shop_containers_capture", () => Capture(style + "_shop_containers.png"), 0.1f);
            Add(style + "_container_open", () =>
            {
                var offer = ShopCatalog.ContainerOffers()[2];
                _account.ShopGuaranteedRewards[offer.Loot.GuaranteedRewardId] = offer.GuaranteedRewardPoints - 1;
                _hangar.Shop.PressBuy(ShopScreen.TabContainers, 2);
                Require(_hangar.Shop.ActivePopup != null, "Container confirmation is missing");
                _hangar.Shop.ActivePopup.Accept();
                Require(_hangar.Shop.OpenedContainer != null, "Container results did not open");
            }, 0.1f);
            Add(style + "_container_intro_capture", () => Capture(style + "_container_intro.png"), 0.1f);
            Add(style + "_container_glow_capture", () =>
            {
                _hangar.Shop.OpenedContainer.AdvanceAnimation(1.9f);
                Capture(style + "_container_glow.png");
            }, 0.1f);
            Add(style + "_container_rewards_capture", () =>
            {
                var window = _hangar.Shop.OpenedContainer;
                window.SkipToRewards();
                Capture(style + "_container_rewards.png");
                Require(FindButton(window.transform, "btn_close") == null, "Container results still have a CLOSE button");
                Require(window.BackButton != null && window.BackButton.gameObject.activeInHierarchy,
                    "Container results have no back arrow");
                window.BackButton.onClick.Invoke();
                Require(_hangar.CurrentScreen == HangarScreen.HangarScreenId.Shop &&
                        _hangar.Shop.Tab == ShopScreen.TabContainers && _hangar.Shop.OpenedContainer == null,
                    "The container back arrow did not return to the containers tab");
                Bootstrap().NetworkClock.SetOffline();
                _hangar.Shop.Refresh(true);
            }, 0.2f);
            Add(style + "_shop_offline_capture", () =>
            {
                Capture(style + "_shop_offline.png");
                _hangar.Shop.HomePressed();
                Bootstrap().NetworkClock.SetSynced(1750032000d);
            }, 0.3f);
            Add(style + "_armor", () => Click("btn_armor_viewer"), 1.2f);
            Add(style + "_armor_capture", () => { Capture(style + "_armor.png"); _hangar.ArmorViewer.HomePressed(); }, 0.4f);
            PlanCamouflage(style);

            Add(style + "_settings", () => Invoke(_hangar, "OpenSettings"), 0.6f);
            Add(style + "_settings_capture", () =>
            {
                Capture(style + "_settings.png");
                // The hangar rows close the main page's right column.
                foreach (var scroll in _hangar.SettingsUi.GetComponentsInChildren<ScrollRect>())
                    scroll.verticalNormalizedPosition = 0f;
            }, 0.4f);
            Add(style + "_settings_hangar_capture", () =>
            {
                Capture(style + "_settings_hangar.png");
                _hangar.SettingsUi.OpenHangarPicker();
                Require(_hangar.SettingsUi.HangarPicker != null && !_hangar.SettingsUi.HangarPicker.IsClosed,
                    "the hangar row did not open the hangar picker");
            }, 0.4f);
            Add(style + "_hangar_picker_capture", () =>
            {
                Capture(style + "_hangar_picker.png");
                _hangar.SettingsUi.HangarPicker.Close();
                _hangar.SettingsUi.Close();
            }, 0.4f);
            Add(style + "_mods", () => Invoke(_hangar, "OpenMods"), 0.6f);
            Add(style + "_mods_capture", () => { Capture(style + "_mods.png"); _hangar.ModsUi.Close(); }, 0.4f);

            Add(style + "_mode", () => Click("btn_battle_mode"), 0.5f);
            Add(style + "_mode_capture", () =>
            {
                var picker = Find<BattleModeScreen>();
                Require(picker != null && picker.transform.Find("close") == null,
                    "the mode picker still has its CLOSE text button");
                var back = FindButton(picker.transform, "btn_menu");
                Require(back != null && back.isActiveAndEnabled, "the mode picker has no back arrow");
                Capture(style + "_mode_picker.png");
                back.onClick.Invoke();
            }, 0.3f);
            Add(style + "_mode_closed", () =>
                Require(Find<BattleModeScreen>() == null, "the mode picker back arrow did not close it"), 0.1f);

            // Sandbox: the mode card opens the editor; every tab and overlay
            // is captured, then APPLY returns to the hangar with the green
            // settings button in the crew slot.
            Add(style + "_sandbox", () => _hangar.PickMode(BlitzLegacy.Core.Battle.BattleMode.Sandbox), 0.8f);
            Add(style + "_sandbox_capture", () =>
            {
                Require(_hangar.SandboxUi != null, "picking the Sandbox did not open its editor");
                Require(!Bootstrap().OnlineSelected, "the Sandbox left the online toggle on");
                Capture(style + "_sandbox_scenario.png");
                ClickIn(_hangar.SandboxUi.Root, "sub_1");
            }, 0.3f);
            Add(style + "_sandbox_match_capture", () =>
            {
                Capture(style + "_sandbox_match.png");
                ClickIn(_hangar.SandboxUi.Root, "sub_2");
            }, 0.3f);
            Add(style + "_sandbox_rules_capture", () =>
            {
                Capture(style + "_sandbox_rules.png");
                ClickIn(_hangar.SandboxUi.Root, "sub_3");
            }, 0.3f);
            Add(style + "_sandbox_bots_capture", () =>
            {
                Capture(style + "_sandbox_bots.png");
                _hangar.SandboxUi.SelectPage(SandboxScreen.Page.Allies);
            }, 0.3f);
            Add(style + "_sandbox_allies_capture", () =>
            {
                Capture(style + "_sandbox_allies.png");
                _hangar.SandboxUi.SelectPage(SandboxScreen.Page.Modifiers);
            }, 0.3f);
            Add(style + "_sandbox_modifiers_capture", () =>
            {
                Capture(style + "_sandbox_modifiers.png");
                _hangar.SandboxUi.SelectPage(SandboxScreen.Page.Cheats);
            }, 0.3f);
            Add(style + "_sandbox_cheats_capture", () =>
            {
                Capture(style + "_sandbox_cheats.png");
                _hangar.SandboxUi.OpenTankPicker(false, 0);
            }, 0.8f);
            Add(style + "_sandbox_picker_capture", () =>
            {
                Require(_hangar.SandboxUi.TankPicker != null, "the tank picker did not open");
                Capture(style + "_sandbox_picker.png");
                _hangar.SandboxUi.CloseTankPicker();
                _hangar.SandboxUi.OpenPresetManager();
            }, 0.4f);
            Add(style + "_sandbox_presets_capture", () =>
            {
                Require(_hangar.SandboxUi.PresetManager != null, "the preset manager did not open");
                Capture(style + "_sandbox_presets.png");
                _hangar.SandboxUi.ClosePresetManager();
                _hangar.SandboxUi.Apply();
            }, 0.4f);
            Add(style + "_sandbox_hangar_capture", () =>
            {
                Require(_hangar.SandboxUi == null, "APPLY did not close the Sandbox editor");
                Require(_hangar.SandboxButton != null && _hangar.SandboxButton.gameObject.activeInHierarchy,
                    "the hangar has no Sandbox settings button in the Sandbox mode");
                Capture(style + "_hangar_sandbox.png");
                _hangar.PickMode(BlitzLegacy.Core.Battle.BattleMode.Normal);
                Require(!_hangar.SandboxButton.gameObject.activeSelf,
                    "the Sandbox settings button stayed in the standard mode");
            }, 0.4f);

            Add(style + "_queue", () =>
            {
                BattleQueueScreen.Create(Bootstrap().transform, _account, null, null);
                _hangar.SetQueueMode(true);
            }, 1.4f);
            Add(style + "_queue_capture", () =>
            {
                Capture(style + "_queue.png");
                Destroy<BattleQueueScreen>();
                _hangar.SetQueueMode(false);
            }, 0.4f);

            Add(style + "_loading", () =>
            {
                BattleLoadingScreen.Create(Bootstrap().transform, Roster(), BattleWorld.LoadDefinition("rudniki"),
                    false, null);
            }, 1.5f);
            Add(style + "_loading_capture", () =>
            {
                Capture(style + "_loading.png");
                var loading = Find<BattleLoadingScreen>();
                if (loading != null) { loading.Cancel(); UnityEngine.Object.Destroy(loading.gameObject); }
            }, 0.4f);

            Add(style + "_login", () => LoginScreen.Create(Bootstrap()), 0.8f);
            Add(style + "_login_capture", () => { Capture(style + "_login.png"); Destroy<LoginScreen>(); }, 0.3f);
        }

        /// <summary>
        /// The 4.8 camouflage shop of the selected tank: the bare slot with
        /// its badge, the shop, the purchase confirmation, the installed
        /// camouflage, the preview and the filled hangar slot. The removal
        /// at the end is free and keeps the camouflage the tank's, framed in
        /// green without a price; the later frames show the plain tank.
        /// </summary>
        private static void PlanCamouflage(string style)
        {
            Add(style + "_camouflage", () =>
            {
                Require(!_hangar.CamouflageSlotFilled && _hangar.CamouflageBadgeShown,
                    "the hangar camouflage slot of a bare, unvisited tank has no badge");
                Click("btn_camouflage");
                Require(_hangar.CurrentScreen == HangarScreen.HangarScreenId.Camouflage, "Camouflage navigation failed");
                Require(_hangar.Camouflage.Supported && _hangar.Camouflage.CurrentAction == CamouflageAction.Buy,
                    "the camouflage shop offers no purchase for " + _account.SelectedTankName);
            }, 0.6f);
            Add(style + "_camouflage_capture", () =>
            {
                Require(!_hangar.CamouflageBadgeShown, "opening the camouflage shop kept the hangar badge");
                Capture(style + "_camouflage.png");
                _account.Credits = Math.Max(_account.Credits, 100000L);
                _hangar.Camouflage.PressApply();
                Require(_hangar.Camouflage.ActivePopup != null, "the camouflage purchase does not ask first");
            }, 0.3f);
            Add(style + "_camouflage_confirm_capture", () =>
            {
                Capture(style + "_camouflage_confirm.png");
                _hangar.Camouflage.ActivePopup.Accept();
                Require(_hangar.Camouflage.CurrentAction == CamouflageAction.Remove,
                    "the camouflage purchase did not install it");
            }, 0.3f);
            Add(style + "_camouflage_installed_capture", () =>
            {
                Capture(style + "_camouflage_installed.png");
                Click("previewButton");
            }, 0.5f);
            Add(style + "_camouflage_preview_capture", () =>
            {
                Require(_hangar.Camouflage.IsPreview && _hangar.Camouflage.PreviewProgress >= 1f,
                    "the camouflage preview did not slide the panels away");
                Capture(style + "_camouflage_preview.png");
                _hangar.Camouflage.SetPreview(false);
            }, 0.4f);
            Add(style + "_camouflage_home", () =>
            {
                _hangar.Camouflage.HomePressed();
                Require(_hangar.CurrentScreen == HangarScreen.HangarScreenId.Main,
                    "the camouflage back arrow did not return to the hangar");
                Require(_hangar.CamouflageSlotFilled, "the hangar camouflage slot does not show the installed camouflage");
            }, 0.8f);
            Add(style + "_camouflage_hangar_capture", () =>
            {
                Capture(style + "_camouflage_hangar.png");
                Click("btn_camouflage");
            }, 0.5f);
            Add(style + "_camouflage_remove", () =>
            {
                var shop = _hangar.Camouflage;
                shop.PressApply();
                Require(shop.ActivePopup == null && shop.CurrentAction == CamouflageAction.Install,
                    "REMOVE did not take the camouflage off at once, or the tank lost it");
                Require(!shop.PriceShown && shop.ElementOwnedShown(shop.SelectedCamouflage) &&
                        !shop.ElementPriceShown(shop.SelectedCamouflage),
                    "the owned camouflage shows a price or lacks its green frame");
            }, 0.4f);
            Add(style + "_camouflage_owned_capture", () =>
            {
                Capture(style + "_camouflage_owned.png");
                _hangar.Camouflage.HomePressed();
                Require(!_hangar.CamouflageSlotFilled, "the removed camouflage stayed in the hangar slot");
            }, 0.6f);
        }

        private static void Add(string name, Action run, float wait)
        {
            Steps.Add(new Step { Name = name, Run = run, Wait = wait });
        }

        private static void Setup()
        {
            Destroy<LoginScreen>();
            Destroy<HangarScreen>();
            Destroy<StylePickerScreen>();

            var tanks = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            _account = new AccountData
            {
                Name = "MenuRender",
                Credits = 1234567,
                Gold = 15400,
                FreeXp = 48210,
                SelectedTankName = "t-34",
            };

            var levels = new HashSet<int>();
            foreach (var tank in tanks.Tanks)
            {
                if (tank.Hidden || !TankAssetSource.HasModel(tank)) { continue; }
                var key = tank.Level * 10 + (int)tank.Class;
                if (levels.Contains(key) && tank.Name != "t-34") { continue; }
                levels.Add(key);
                var save = _account.AddTank(tank.Name, true);
                save.Exp = 3200 + tank.Level * 410;
                if (_account.Tanks.Count >= 18) { break; }
            }

            if (_account.GetTank("t-34") == null) { _account.AddTank("t-34", true); }
            if (_account.GetTank("ms-1") == null) { _account.AddTank("ms-1", true); }

            var cameraObject = new GameObject("menu_render_ui_camera");
            cameraObject.transform.position = new Vector3(0f, -5000f, 0f);
            _uiCamera = cameraObject.AddComponent<Camera>();
            _uiCamera.clearFlags = CameraClearFlags.Nothing;
            _uiCamera.nearClipPlane = 0.1f;
            _uiCamera.farClipPlane = 50f;
            _uiCamera.allowHDR = false;
            _uiCamera.enabled = false;
            var data = cameraObject.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = false;
        }

        private static void OnUpdate()
        {
            if (!SessionState.GetBool(ActiveKey, false) || !EditorApplication.isPlaying) { return; }
            if (_step >= Steps.Count) { return; }
            if (EditorApplication.timeSinceStartup < _nextAt || _frames++ < 3) { return; }
            _frames = 0;

            var step = Steps[_step++];
            try
            {
                Debug.Log("[Blitz] menu render step: " + step.Name);
                step.Run();
            }
            catch (Exception exception)
            {
                Debug.LogError("[Blitz] menu render step failed: " + step.Name);
                Debug.LogException(exception);
                Finish(1);
                return;
            }

            _nextAt = EditorApplication.timeSinceStartup + step.Wait;
        }

        private static BlitzBootstrap Bootstrap()
        {
            var bootstrap = Find<BlitzBootstrap>();
            Require(bootstrap != null, "BlitzBootstrap missing");
            return bootstrap;
        }

        private static void Click(string name)
        {
            Require(_hangar != null, "no hangar");
            foreach (var button in _hangar.GetComponentsInChildren<Button>(false))
            {
                if (button.name == name && button.isActiveAndEnabled)
                {
                    button.onClick.Invoke();
                    return;
                }
            }

            throw new InvalidOperationException("button not found: " + name);
        }

        /// <summary>Clicks the first active button called <paramref name="name"/> under the root.</summary>
        private static void ClickIn(Transform root, string name)
        {
            Require(root != null, "no root to click " + name + " in");
            foreach (var button in root.GetComponentsInChildren<Button>(false))
            {
                if (button.name == name && button.isActiveAndEnabled)
                {
                    button.onClick.Invoke();
                    return;
                }
            }

            throw new InvalidOperationException("button not found: " + name);
        }

        private static Text FindText(Transform root, string name)
        {
            if (root == null) { return null; }
            foreach (var text in root.GetComponentsInChildren<Text>(true))
            {
                if (text.name == name) { return text; }
            }

            return null;
        }

        /// <summary>A button under <paramref name="root"/>, active or not; null when absent.</summary>
        private static Button FindButton(Transform root, string name)
        {
            if (root == null) { return null; }
            foreach (var button in root.GetComponentsInChildren<Button>(true))
            {
                if (button.name == name) { return button; }
            }

            return null;
        }

        private static void Invoke(object target, string method)
        {
            var info = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Require(info != null, "method missing: " + method);
            info.Invoke(target, null);
        }

        private static List<BattleParticipantStats> Roster()
        {
            if (_rosterRoot != null) { UnityEngine.Object.Destroy(_rosterRoot); }
            _rosterRoot = new GameObject("menu_render_roster");
            _rosterRoot.transform.position = new Vector3(0f, -3000f, 0f);
            var database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            var ids = new[] { "t-34", "is-7", "su-100", "ms-1", "kv-1", "t-34", "is-7" };
            var roster = new List<BattleParticipantStats>();
            var type = typeof(BattleParticipantStats);
            var names = new[] { "MenuRender", "Amway921", "Jove", "LeBwa", "Yusha", "Nechaev", "EviL" };
            for (var team = 0; team < 2; team++)
            {
                for (var i = 0; i < ids.Length; i++)
                {
                    var card = database.Get(ids[i]) ?? database.Get("ms-1");
                    var tank = BattleTank.Spawn(_rosterRoot.transform, "t" + team + "_" + i, card,
                        _rosterRoot.transform, new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f)), team);
                    var stats = new BattleParticipantStats();
                    type.GetProperty("Team").SetValue(stats, team);
                    type.GetProperty("Nickname").SetValue(stats, team == 0 ? names[i] : "Enemy_" + i);
                    type.GetProperty("Tank").SetValue(stats, tank);
                    type.GetProperty("IsPlayer").SetValue(stats, team == 0 && i == 0);
                    roster.Add(stats);
                }
            }

            _rosterRoot.SetActive(false);
            return roster;
        }

        private static void Capture(string fileName)
        {
            var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var texture = new Texture2D(Width, Height, TextureFormat.RGB24, false, false);
            var previousActive = RenderTexture.active;
            var switched = new List<Canvas>();
            try
            {
                RenderTexture.active = target;
                GL.Clear(true, true, Color.black);
                RenderTexture.active = previousActive;

                var world = _hangar != null && _hangar.World != null ? _hangar.World.HangarCamera : null;
                if (world != null && world.isActiveAndEnabled)
                {
                    var previous = world.targetTexture;
                    world.targetTexture = target;
                    world.Render();
                    world.Render();
                    world.targetTexture = previous;
                }

                foreach (var canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
                {
                    if (!canvas.isRootCanvas || !canvas.isActiveAndEnabled ||
                        canvas.renderMode != RenderMode.ScreenSpaceOverlay) { continue; }
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = _uiCamera;
                    canvas.planeDistance = 10f;
                    switched.Add(canvas);
                }

                _uiCamera.targetTexture = target;
                Canvas.ForceUpdateCanvases();

                // The music panel holds itself inside the screen it is on every
                // frame, and the capture's canvas has the render's size, not the
                // batch editor's: it is placed again for this one.
                if (_hangar != null && _hangar.MusicPanel != null)
                {
                    _hangar.MusicPanel.Refresh();
                }

                _uiCamera.Render();
                RenderTexture.active = target;
                GL.Clear(true, true, Color.black);
                RenderTexture.active = previousActive;
                if (world != null && world.isActiveAndEnabled)
                {
                    var previous = world.targetTexture;
                    world.targetTexture = target;
                    world.Render();
                    world.targetTexture = previous;
                }

                Canvas.ForceUpdateCanvases();
                _uiCamera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0f, 0f, Width, Height), 0, 0);
                texture.Apply(false, false);
                File.WriteAllBytes(Path.Combine(OutputDirectory, fileName), texture.EncodeToPNG());
                Debug.Log("[Blitz] menu render frame: " + fileName + " canvases=" + switched.Count);
            }
            finally
            {
                RenderTexture.active = previousActive;
                _uiCamera.targetTexture = null;
                foreach (var canvas in switched)
                {
                    if (canvas == null) { continue; }
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    canvas.worldCamera = null;
                }

                UnityEngine.Object.DestroyImmediate(texture);
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private static T Find<T>() where T : Component
        {
            var all = Resources.FindObjectsOfTypeAll<T>();
            foreach (var item in all)
            {
                if (item != null && item.gameObject.scene.IsValid()) { return item; }
            }

            return null;
        }

        private static void Destroy<T>() where T : Component
        {
            foreach (var item in Resources.FindObjectsOfTypeAll<T>())
            {
                if (item != null && item.gameObject.scene.IsValid())
                {
                    UnityEngine.Object.Destroy(item.gameObject);
                }
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
        }

        private static void Finish(int code)
        {
            SessionState.SetInt(ExitCodeKey, code);
            _step = Steps.Count;
            Debug.Log("[Blitz] menu render " + (code == 0 ? "ok" : "failed") + ": out=" + Path.GetFullPath(OutputDirectory));
            if (EditorApplication.isPlaying) { EditorApplication.isPlaying = false; }
            else { Quit(code); }
        }

        private static void Quit(int code)
        {
            SessionState.SetBool(ActiveKey, false);
            if (_hooked)
            {
                _hooked = false;
                EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
                EditorApplication.update -= OnUpdate;
            }

            EditorApplication.delayCall += () => EditorApplication.Exit(code);
        }
    }
}
