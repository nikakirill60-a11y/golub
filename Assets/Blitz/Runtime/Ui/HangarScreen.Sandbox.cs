using System;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Platform;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// The hangar side of the Sandbox (ui_hangar.tscn: open_battle_mode_menu,
    /// refresh_battle_mode, refresh_training_button, open_training_mode,
    /// _launch_training_map), shared by the classic and Modern layouts.
    ///
    /// The Sandbox is offline only: picking it switches the online toggle
    /// off, and switching online on while it is selected falls back to the
    /// standard mode, so the hangar never queues an online Sandbox battle.
    /// </summary>
    public sealed partial class HangarScreen
    {
        // ui_hangar.tscn btn_crew offsets: the Sandbox settings button takes
        // the crew slot and crew moves one slot left.
        private const float CrewLeftDefault = -247f;
        private const float CrewRightDefault = -146f;
        private const float CrewLeftSandbox = -353f;
        private const float CrewRightSandbox = -252f;

        private Text _modeName;
        private Text _modeCaption;
        private Button _crewButton;

        /// <summary>The open Sandbox editor, or null.</summary>
        public SandboxScreen SandboxUi { get; private set; }

        /// <summary>btn_training: visible only while the Sandbox is selected.</summary>
        public Button SandboxButton { get; private set; }

        /// <summary>btn_battle_mode -> battle_mode_menu.tscn.</summary>
        public BattleModeScreen OpenModePicker()
        {
            return BattleModeScreen.Create(transform, _bootstrap.SelectedMode, PickMode,
                _bootstrap.OnlineSelected,
                BattleOnline.Available ? (Action<bool>)SetOnline : null,
                _kit);
        }

        /// <summary>mode_picked (+ training_requested for the Sandbox card).</summary>
        public void PickMode(BattleMode mode)
        {
            _bootstrap.SelectedMode = mode;
            if (!BattleModeCatalog.AllowsOnline(mode))
            {
                _bootstrap.OnlineSelected = false;
            }
            RefreshModeButton();
            if (mode == BattleMode.Sandbox)
            {
                OpenSandbox();
            }
        }

        private void SetOnline(bool online)
        {
            _bootstrap.OnlineSelected = online;
            if (online && !BattleModeCatalog.AllowsOnline(_bootstrap.SelectedMode))
            {
                _bootstrap.SelectedMode = BattleMode.Normal;
            }
            RefreshModeButton();
        }

        /// <summary>open_training_mode: the full-screen editor over the hangar.</summary>
        public SandboxScreen OpenSandbox()
        {
            if (SandboxUi != null)
            {
                return SandboxUi;
            }

            var presets = _bootstrap.SandboxPresets;
            if (presets == null)
            {
                Debug.LogWarning("[Blitz] sandbox screen unavailable: no save storage");
                return null;
            }

            // Like the settings screen, the editor replaces the hangar UI; the
            // hangar scene stays behind its tinted backdrop.
            if (_uiRoot != null)
            {
                _uiRoot.gameObject.SetActive(false);
            }

            SandboxUi = SandboxScreen.Open(
                transform,
                presets,
                SettingsSortingOrder,
                MouseLock.IsMobile,
                () =>
                {
                    _bootstrap.SelectedMode = BattleMode.Sandbox;
                    _bootstrap.OnlineSelected = false;
                    RefreshModeButton();
                },
                LaunchSandbox,
                () =>
                {
                    SandboxUi = null;
                    if (_uiRoot != null)
                    {
                        _uiRoot.gameObject.SetActive(true);
                    }
                });
            return SandboxUi;
        }

        /// <summary>_launch_training_map: straight into the offline battle flow.</summary>
        private void LaunchSandbox(string mapId)
        {
            RefreshModeButton();
            if (!_bootstrap.EnterSandbox(this, mapId))
            {
                Debug.LogWarning("[Blitz] sandbox launch rejected by the bootstrap");
            }
        }

        /// <summary>refresh_battle_mode + refresh_training_button.</summary>
        private void RefreshModeButton()
        {
            if (_bootstrap == null || _lang == null)
            {
                return;
            }

            if (_modeName != null)
            {
                _modeName.text = _lang.Get(BattleModeCatalog.NameKey(_bootstrap.SelectedMode));
            }
            if (_modeCaption != null)
            {
                _modeCaption.text = _lang.Get(_bootstrap.OnlineSelected ? "online:toggle/on" : "MODE");
            }

            var sandbox = _bootstrap.SelectedMode == BattleMode.Sandbox;
            if (SandboxButton != null)
            {
                var crewShown = _crewButton == null || _crewButton.gameObject.activeSelf;
                SandboxButton.gameObject.SetActive(sandbox && crewShown);
            }
            if (_crewButton != null)
            {
                GodotRect.CenterTop((RectTransform)_crewButton.transform,
                    sandbox ? CrewLeftSandbox : CrewLeftDefault, 96f,
                    sandbox ? CrewRightSandbox : CrewRightDefault, 173f);
            }
        }

        /// <summary>Classic btn_training (ButtonGreen, title + caption).</summary>
        private void BuildClassicSandboxButton(RectTransform main)
        {
            var button = StyleButton("btn_training", main, "button_green");
            GodotRect.CenterTop((RectTransform)button.transform, -247f, 96f, -153f, 173f);
            var title = Label("title", button.transform, 15, TextAnchor.MiddleCenter,
                _lang.Get("training:settings_button"), _bold, UiTheme.ButtonFont);
            title.horizontalOverflow = HorizontalWrapMode.Wrap;
            TopLeft(title.rectTransform, 4f, 8f, 93f, 36f);
            var caption = Label("caption", button.transform, 13, TextAnchor.UpperCenter,
                _lang.Get("training:settings_caption"), _regular, CaptionColor);
            TopLeft(caption.rectTransform, 4f, 48f, 93f, 21f);
            Bind(button, () => OpenSandbox(), AudioService.SoundEnter);
            SandboxButton = button;
        }

        /// <summary>Modern: the same green settings button, left of the mode button.</summary>
        private void BuildModernSandboxButton(RectTransform main, float right, float bottom)
        {
            var button = StyleButton("btn_training", main, "button_green");
            ModernUi.BottomRight((RectTransform)button.transform, right, bottom, 150f, 77f);
            button.gameObject.AddComponent<PressFeedback>();
            var title = ModernUi.Label("title", button.transform, _bold, 16, ModernTheme.TextPrimary,
                TextAnchor.MiddleCenter, _lang.Get("training:settings_button"));
            title.horizontalOverflow = HorizontalWrapMode.Wrap;
            ModernUi.TopLeft(title.rectTransform, 6f, 8f, 138f, 36f);
            var caption = ModernUi.Label("caption", button.transform, _regular, 14, ModernTheme.TextSecondary,
                TextAnchor.MiddleCenter, _lang.Get("training:settings_caption"));
            ModernUi.TopLeft(caption.rectTransform, 6f, 46f, 138f, 22f);
            Bind(button, () => OpenSandbox(), AudioService.SoundEnter);
            SandboxButton = button;
        }
    }
}
