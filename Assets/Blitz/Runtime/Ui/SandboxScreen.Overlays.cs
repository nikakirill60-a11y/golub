using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Sandbox;
using BlitzLegacy.Core.Tanks;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// The four layers training_mode_screen.gd opens over itself: the tank
    /// picker (z 30), the preset manager (z 40), the export / import dialog
    /// (z 60) and the OptionButton popup list. uGUI draws by sibling order,
    /// so every layer is appended to the screen root when it opens.
    /// </summary>
    public sealed partial class SandboxScreen
    {
        private sealed class PickerCard
        {
            public TankInfo Info;
            public string Display;
            public GameObject Root;
            public GameObject Frame;
        }

        private RectTransform _choiceOverlay;
        private RectTransform _pickerOverlay;
        private RectTransform _presetOverlay;
        private RectTransform _exchangeOverlay;

        private bool _pickerEnemy;
        private int _pickerNext = -1;
        private RectTransform _pickerSelected;
        private InputField _pickerSearch;
        private readonly List<PickerCard> _pickerCards = new List<PickerCard>();

        private Button _presetOption;
        private InputField _presetName;
        private Button _presetNew;
        private Button _presetOverwrite;
        private Button _presetDelete;
        private Text _presetPreview;
        private bool _deleteArmed;

        private InputField _exchangeCode;
        private Text _exchangeStatus;

        public RectTransform TankPicker { get { return _pickerOverlay; } }

        public RectTransform PresetManager { get { return _presetOverlay; } }

        public RectTransform ExchangeDialog { get { return _exchangeOverlay; } }

        public RectTransform ChoicePopup { get { return _choiceOverlay; } }

        /// <summary>The slot the next picked vehicle goes into, or -1.</summary>
        public int PickerNextSlot { get { return _pickerNext; } }

        /// <summary>Catalogue cards the current search leaves visible.</summary>
        public int PickerVisibleCount
        {
            get
            {
                var count = 0;
                foreach (var card in _pickerCards)
                {
                    if (card.Root != null && card.Root.activeSelf) { count++; }
                }

                return count;
            }
        }

        public string ExchangeText { get { return _exchangeCode == null ? string.Empty : _exchangeCode.text; } }

        public string ExchangeStatus { get { return _exchangeStatus == null ? string.Empty : _exchangeStatus.text; } }

        private void ResetOverlayFields()
        {
            _choiceOverlay = null;
            _pickerOverlay = null;
            _presetOverlay = null;
            _exchangeOverlay = null;
            _pickerSelected = null;
            _pickerSearch = null;
            _pickerCards.Clear();
            _presetOption = null;
            _presetName = null;
            _presetNew = null;
            _presetOverwrite = null;
            _presetDelete = null;
            _presetPreview = null;
            _exchangeCode = null;
            _exchangeStatus = null;
        }

        private RectTransform Overlay(string name, float dim)
        {
            var overlay = UiFactory.Node(name, Root);
            GodotRect.FullRect(overlay);
            var shade = UiFactory.Picture("dim", overlay, null);
            GodotRect.FullRect(shade.rectTransform);
            shade.color = DimColor(dim);
            shade.raycastTarget = true;
            return overlay;
        }

        /// <summary>A MarginContainer around _native_panel with a VBox inside.</summary>
        private RectTransform OverlayPanel(RectTransform overlay, float marginX, float marginY, int padding,
            float spacing)
        {
            var panel = _kit.Picture("panel", overlay, RowRegion, 0f, 0f, 0f, 0f);
            GodotRect.Apply(panel.rectTransform, 0f, 0f, 1f, 1f, marginX, marginY, -marginX, -marginY,
                GodotRect.TopLeftPivot);
            panel.raycastTarget = true;
            panel.color = OverlayPanelColor;
            var box = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            box.padding = new RectOffset(padding, padding, padding, padding);
            box.spacing = spacing;
            box.childControlWidth = true;
            box.childControlHeight = true;
            box.childForceExpandWidth = true;
            box.childForceExpandHeight = false;
            return panel.rectTransform;
        }

        /// <summary>Title, an expanding spacer and CLOSE.</summary>
        private void OverlayHeader(RectTransform panel, string title, int size, float height, Action close)
        {
            var header = HBox("header", panel, 9f, height);
            header.GetComponent<HorizontalLayoutGroup>().childForceExpandHeight = false;
            var label = Bold(header, title, size);
            Elem(label).flexibleWidth = 1f;
            var button = Btn("btn_close", header, S("training:close"), false, 132f, ControlHeight, 20);
            Bind(button, close);
        }

        // ------------------------------------------------------------------
        // OptionButton popup.
        // ------------------------------------------------------------------

        /// <summary>The OptionButton's PopupMenu: one row per item, the current one green.</summary>
        public void OpenChoice(string titleKey, IList<string> labels, int selected, Action<int> picked)
        {
            CloseChoice();
            _choiceOverlay = Overlay("sandbox_choice", 0.6f);
            var dim = _choiceOverlay.Find("dim").gameObject.AddComponent<Button>();
            dim.transition = Selectable.Transition.None;
            dim.onClick.AddListener(CloseChoice);

            var height = Mathf.Min(64f + (labels.Count * (ControlHeight + 6f)) + 32f, 600f);
            var panel = _kit.Picture("panel", _choiceOverlay, RowRegion, 0f, 0f, 0f, 0f);
            GodotRect.Centered(panel.rectTransform, -240f, -height * 0.5f, 240f, height * 0.5f);
            panel.raycastTarget = true;
            panel.color = OverlayPanelColor;

            var title = Bold(panel.transform, S(titleKey), 22);
            GodotRect.Apply(title.rectTransform, 0f, 0f, 1f, 0f, 16f, 10f, -16f, 54f, GodotRect.TopLeftPivot);
            var view = UiFactory.Node("list", panel.transform);
            GodotRect.Apply(view, 0f, 0f, 1f, 1f, 12f, 60f, -12f, -12f, GodotRect.TopLeftPivot);
            var content = ScrollContent(view);
            content.GetComponent<VerticalLayoutGroup>().spacing = 6f;
            for (var i = 0; i < labels.Count; i++)
            {
                var index = i;
                var item = Btn("item_" + i, content, labels[i], i == selected, 132f, ControlHeight, 19);
                Bind(item, () =>
                {
                    CloseChoice();
                    picked(index);
                });
            }
        }

        public void CloseChoice()
        {
            if (_choiceOverlay != null)
            {
                Kill(_choiceOverlay.gameObject);
            }

            _choiceOverlay = null;
        }

        // ------------------------------------------------------------------
        // Tank picker.
        // ------------------------------------------------------------------

        /// <summary>
        /// _open_tank_picker: <paramref name="index"/> &lt; 0 fills the RANDOM
        /// slots in order; otherwise the first pick replaces that slot.
        /// </summary>
        public void OpenTankPicker(bool enemy, int index)
        {
            CloseTankPicker();
            _pickerEnemy = enemy;
            _pickerNext = index >= 0 ? index : SandboxEditor.NextRandomSlot(_config, enemy, 0);
            _pickerOverlay = Overlay("training_tank_picker", 0.78f);
            var panel = OverlayPanel(_pickerOverlay, 22f, 22f, 16, 9f);
            OverlayHeader(panel, S("training:select_tanks"), 27, 58f, CloseTankPicker);

            Bold(panel, S("training:selected_tanks"), 20);
            _pickerSelected = Strip("training_selected_tanks", panel, 142f, 8f);

            _pickerSearch = UiFactory.Field("training_tank_search", panel, _kit.Regular, S("training:tank_search"),
                false, 48);
            var searchSize = Elem(_pickerSearch);
            searchSize.minHeight = ControlHeight;
            searchSize.preferredHeight = ControlHeight;
            _pickerSearch.onValueChanged.AddListener(_ => FilterTankPicker());

            var view = UiFactory.Node("training_tank_catalogue", panel);
            var viewSize = Elem(view);
            viewSize.minHeight = 130f;
            viewSize.flexibleHeight = 1f;
            var flow = FlowContent(view, new Vector2(TankCardWidth, TankCardHeight), 8f);

            _pickerCards.Clear();
            var infos = SandboxEditor.PickerTanks(_presets.Tanks, TankName);
            var selected = SandboxEditor.SelectedNames(_config, enemy);
            foreach (var info in infos)
            {
                var name = info.Name;
                var root = TankCard("tank_" + name, flow, info, selected.Contains(name), () => ChooseTank(name));
                var frame = root.Find("selected");
                _pickerCards.Add(new PickerCard
                {
                    Info = info,
                    Display = TankName(info),
                    Root = root.gameObject,
                    Frame = frame == null ? null : frame.gameObject,
                });
            }

            RefreshTankPicker();
        }

        public void CloseTankPicker()
        {
            if (_pickerOverlay != null)
            {
                Kill(_pickerOverlay.gameObject);
            }

            _pickerOverlay = null;
            _pickerSelected = null;
            _pickerSearch = null;
            _pickerCards.Clear();
        }

        /// <summary>The search box's text; tests and keyboards both go through it.</summary>
        public void SetTankSearch(string query)
        {
            if (_pickerSearch != null)
            {
                _pickerSearch.text = query ?? string.Empty;
                FilterTankPicker();
            }
        }

        /// <summary>_choose_tank.</summary>
        public void ChooseTank(string tankName)
        {
            if (_pickerOverlay == null)
            {
                return;
            }

            var index = SandboxEditor.ChooseTank(_config, _pickerEnemy, _pickerNext, tankName);
            if (index < 0)
            {
                return;
            }

            _selectedSlots[Team(_pickerEnemy)] = index;
            _pickerNext = SandboxEditor.NextRandomSlot(_config, _pickerEnemy, index + 1);
            RefreshTeam(_pickerEnemy);
            RefreshTankPicker();
            RefreshSummary();
        }

        /// <summary>_remove_picker_tank.</summary>
        public void RemovePickerTank(int index)
        {
            if (_pickerOverlay == null || !SandboxEditor.MakeSlotRandom(_config, _pickerEnemy, index))
            {
                return;
            }

            _selectedSlots[Team(_pickerEnemy)] = index;
            _pickerNext = index;
            RefreshTeam(_pickerEnemy);
            RefreshTankPicker();
        }

        private void RefreshTankPicker()
        {
            if (_pickerOverlay == null)
            {
                return;
            }

            FillPickerSelected();
            var selected = SandboxEditor.SelectedNames(_config, _pickerEnemy);
            foreach (var card in _pickerCards)
            {
                if (card.Frame != null)
                {
                    card.Frame.SetActive(selected.Contains(card.Info.Name));
                }
            }

            FilterTankPicker();
        }

        /// <summary>_fill_picker_selected: tapping a chosen vehicle removes it.</summary>
        private void FillPickerSelected()
        {
            ClearChildren(_pickerSelected);
            var found = false;
            var slots = _config.Slots(_pickerEnemy);
            var count = SandboxEditor.ActiveSlotCount(_config, _pickerEnemy);
            for (var i = 0; i < count; i++)
            {
                var slot = slots[i];
                if (!SandboxEditor.IsExact(slot, _presets.Tanks))
                {
                    continue;
                }

                found = true;
                var index = i;
                TankCard("picked_" + i, _pickerSelected, _presets.Tanks.Get(slot.TankName),
                    i == _selectedSlots[Team(_pickerEnemy)], () => RemovePickerTank(index));
            }

            if (!found)
            {
                var hint = Hint(_pickerSelected, S("training:no_selected_tanks"));
                hint.horizontalOverflow = HorizontalWrapMode.Overflow;
                hint.alignment = TextAnchor.MiddleLeft;
            }
        }

        /// <summary>_fill_tank_picker's filter over the already built catalogue.</summary>
        private void FilterTankPicker()
        {
            var query = _pickerSearch == null ? string.Empty : _pickerSearch.text;
            foreach (var card in _pickerCards)
            {
                if (card.Root != null)
                {
                    card.Root.SetActive(SandboxEditor.MatchesSearch(card.Info, card.Display, query));
                }
            }
        }

        /// <summary>An HFlowContainer in a vertical ScrollContainer.</summary>
        private static RectTransform FlowContent(RectTransform view, Vector2 cell, float spacing)
        {
            view.gameObject.AddComponent<RectMask2D>();
            var catcher = view.gameObject.AddComponent<Image>();
            catcher.color = Color.clear;
            catcher.raycastTarget = true;

            var content = UiFactory.Node("flow", view);
            content.pivot = GodotRect.TopLeftPivot;
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;
            var grid = content.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = cell;
            grid.spacing = new Vector2(spacing, spacing);
            grid.constraint = GridLayoutGroup.Constraint.Flexible;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.childAlignment = TextAnchor.UpperLeft;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;

            var scroll = view.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = view;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;
            return content;
        }

        // ------------------------------------------------------------------
        // Preset manager.
        // ------------------------------------------------------------------

        /// <summary>_open_preset_manager.</summary>
        public void OpenPresetManager()
        {
            if (_presetOverlay != null)
            {
                return;
            }

            _presetOverlay = Overlay("training_preset_manager", 0.78f);
            var panel = OverlayPanel(_presetOverlay, 70f, 24f, 16, 8f);
            OverlayHeader(panel, S("training:preset/manager"), 28, ControlHeight, ClosePresetManager);

            var selector = FormRow(panel, "preset_selector_row", "training:preset/current");
            _presetOption = Btn("training_preset_selector", selector, string.Empty, false, 380f, ControlHeight, 19);
            Elem(_presetOption).flexibleWidth = 1f;
            Bind(_presetOption, OpenPresetChoice);

            var nameRow = FormRow(panel, "preset_name_row", "training:preset/name_label");
            _presetName = UiFactory.Field("training_preset_name", nameRow, _kit.Regular, S("training:preset/name"),
                false, SandboxPresetLibrary.MaxNameLength);
            var nameSize = Elem(_presetName);
            nameSize.minWidth = 380f;
            nameSize.minHeight = ControlHeight;
            nameSize.preferredHeight = ControlHeight;
            nameSize.flexibleWidth = 1f;
            _presetName.onEndEdit.AddListener(_ => CommitPresetName());

            var previewView = UiFactory.Node("preview", panel);
            var previewSize = Elem(previewView);
            previewSize.minHeight = 116f;
            previewSize.flexibleHeight = 1f;
            var preview = ScrollContent(previewView);
            _presetPreview = Hint(preview, string.Empty);
            _presetPreview.name = "training_preset_preview";

            var actions = HBox("actions", panel, 9f, 58f);
            _presetNew = Btn("training_preset_new", actions, S("training:preset/new"), false, 132f, ControlHeight, 20);
            Elem(_presetNew).flexibleWidth = 1f;
            Bind(_presetNew, CreatePreset);
            _presetOverwrite = Btn("training_preset_overwrite", actions, S("training:preset/overwrite"), true, 132f,
                ControlHeight, 20);
            Elem(_presetOverwrite).flexibleWidth = 1f;
            Bind(_presetOverwrite, OverwritePreset);
            _presetDelete = Btn("training_preset_delete", actions, S("training:preset/delete"), false, 132f,
                ControlHeight, 20);
            Elem(_presetDelete).flexibleWidth = 1f;
            Bind(_presetDelete, DeletePreset);

            var exchange = HBox("exchange_actions", panel, 9f, 58f);
            var export = Btn("training_preset_export", exchange, S("training:preset/export"), false, 132f,
                ControlHeight, 20);
            Elem(export).flexibleWidth = 1f;
            Bind(export, () => OpenExchange(false));
            var import = Btn("training_preset_import", exchange, S("training:preset/import"), false, 132f,
                ControlHeight, 20);
            Elem(import).flexibleWidth = 1f;
            Bind(import, () => OpenExchange(true));
            RefreshPresetBar();
        }

        /// <summary>_close_preset_manager: commit the name and rebuild the editor.</summary>
        public void ClosePresetManager()
        {
            CloseChoice();
            CloseExchange();
            CommitPresetName();
            if (_presetOverlay != null)
            {
                Kill(_presetOverlay.gameObject);
            }

            _presetOverlay = null;
            _presetOption = null;
            _presetName = null;
            _presetNew = null;
            _presetOverwrite = null;
            _presetDelete = null;
            _presetPreview = null;
            RebuildScreen();
        }

        private void OpenPresetChoice()
        {
            var labels = new List<string>();
            foreach (var preset in _presets.Library.Presets)
            {
                labels.Add(preset.DisplayName);
            }

            OpenChoice("training:preset/current", labels, _presets.Library.IndexOf(_presets.Library.ActiveId),
                SelectPreset);
        }

        /// <summary>_on_preset_selected.</summary>
        public void SelectPreset(int index)
        {
            var presets = _presets.Library.Presets;
            if (index < 0 || index >= presets.Count)
            {
                return;
            }

            CommitPresetName();
            if (_presets.Library.Select(presets[index].Id))
            {
                _config = _presets.Active;
                _presets.Save();
                RefreshPresetBar();
            }
        }

        /// <summary>_commit_preset_name.</summary>
        private void CommitPresetName()
        {
            if (_presetName == null)
            {
                return;
            }

            var active = _presets.Library.Active;
            if (active == null)
            {
                return;
            }

            if (_presets.Library.Rename(active.Id, _presetName.text, active.DisplayName))
            {
                _presets.Save();
            }

            RefreshPresetBar();
        }

        /// <summary>_create_preset: a copy of the active preset named "Config N".</summary>
        public void CreatePreset()
        {
            CommitPresetName();
            var number = _presets.Library.Count + 1;
            var preset = _presets.Library.Add(SandboxEditor.Percent(S("training:preset/new_name"), number), _config);
            if (preset != null)
            {
                _config = _presets.Active;
                _presets.Save();
                RefreshPresetBar();
            }
        }

        /// <summary>_overwrite_preset.</summary>
        public void OverwritePreset()
        {
            CommitPresetName();
            if (_presets.Library.OverwriteActive(_config.Copy(), _presets.MapIds))
            {
                _config = _presets.Active;
            }

            _presets.Save();
            RefreshPresetBar();
        }

        /// <summary>_delete_preset: the first press arms the button, the second deletes.</summary>
        public void DeletePreset()
        {
            if (_presetDelete == null || !_presetDelete.interactable)
            {
                return;
            }

            if (!_deleteArmed)
            {
                _deleteArmed = true;
                Caption(_presetDelete).text = S("training:preset/delete_confirm");
                SetVariant(_presetDelete, RedRegion);
                return;
            }

            var active = _presets.Library.Active;
            if (active != null && _presets.Library.Remove(active.Id))
            {
                _config = _presets.Active;
                _presets.Save();
            }

            RefreshPresetBar();
        }

        /// <summary>_refresh_preset_bar.</summary>
        private void RefreshPresetBar()
        {
            var library = _presets.Library;
            var active = library.Active;
            if (_presetOption != null)
            {
                Caption(_presetOption).text = active == null ? string.Empty : active.DisplayName;
            }

            if (_presetName != null)
            {
                _presetName.SetTextWithoutNotify(active == null ? string.Empty : active.DisplayName);
            }

            if (_presetNew != null)
            {
                _presetNew.interactable = !library.IsFull;
            }

            if (_presetDelete != null)
            {
                _presetDelete.interactable = library.Count > 1;
                Caption(_presetDelete).text = S("training:preset/delete");
                SetVariant(_presetDelete, GenericRegion);
            }

            _deleteArmed = false;
            if (_presetPreview != null)
            {
                _presetPreview.text = active == null ? string.Empty : PresetPreview(active.Config);
            }
        }

        /// <summary>_preset_preview_text.</summary>
        public string PresetPreview(SandboxConfig config)
        {
            if (config == null)
            {
                return string.Empty;
            }

            var time = config.BattleTimeMinutes <= 0.00001f
                ? S("training:value/unlimited")
                : SandboxEditor.Number(config.BattleTimeMinutes, 1) + " " + S("training:minutes_short");
            var lines = new[]
            {
                SandboxEditor.Percent("%s: %s", S("training:preset/preview_map"), MapName(config)),
                SandboxEditor.Percent("%s: %s", S("training:preset/preview_mode"), ModeName(config)),
                SandboxEditor.Percent("%s: %d : %d", S("training:preset/preview_teams"),
                    config.AlliedTeamSize, config.EnemyTeamSize),
                SandboxEditor.Percent("%s: %s", S("training:preset/preview_allies"), PresetTankNames(config, false)),
                SandboxEditor.Percent("%s: %s", S("training:preset/preview_enemies"), PresetTankNames(config, true)),
                SandboxEditor.Percent("%s: %s", S("training:preset/preview_time"), time),
                SandboxEditor.Percent("%s: %s", S("training:preset/preview_friendly_fire"),
                    S(config.FriendlyFire ? OnKey : OffKey)),
                SandboxEditor.Percent("%s: %s", S("training:preset/preview_global"),
                    SandboxEditor.Percent(S("training:preset/preview_global_values"),
                        SandboxEditor.Number(config.Global.Hp), SandboxEditor.Number(config.Global.Damage),
                        SandboxEditor.Number(config.Global.Reload), SandboxEditor.Number(config.Global.Speed))),
            };
            return string.Join("\n", lines);
        }

        private string PresetTankNames(SandboxConfig config, bool enemy)
        {
            var names = new List<string>();
            foreach (var info in SandboxEditor.ExactTanks(config, enemy, _presets.Tanks))
            {
                names.Add(TankName(info));
            }

            return names.Count == 0 ? S(RandomKey) : string.Join(", ", names.ToArray());
        }

        // ------------------------------------------------------------------
        // Export / import.
        // ------------------------------------------------------------------

        /// <summary>_open_exchange_dialog.</summary>
        public void OpenExchange(bool import)
        {
            if (_exchangeOverlay != null)
            {
                return;
            }

            _exchangeOverlay = Overlay(import ? "training_import_dialog" : "training_export_dialog", 0.84f);
            var panel = OverlayPanel(_exchangeOverlay, 70f, 45f, 16, 9f);
            OverlayHeader(panel, S(import ? "training:preset/import_title" : "training:preset/export_title"), 27,
                56f, CloseExchange);
            Hint(panel, S(import ? "training:preset/import_hint" : "training:preset/export_hint"));

            _exchangeCode = UiFactory.Field("training_preset_code", panel, _kit.Regular, string.Empty, false, 0);
            _exchangeCode.lineType = InputField.LineType.MultiLineNewline;
            _exchangeCode.textComponent.fontSize = 16;
            _exchangeCode.textComponent.alignment = TextAnchor.UpperLeft;
            _exchangeCode.textComponent.horizontalOverflow = HorizontalWrapMode.Wrap;
            _exchangeCode.textComponent.verticalOverflow = VerticalWrapMode.Truncate;
            var codeSize = Elem(_exchangeCode);
            codeSize.minHeight = 120f;
            codeSize.flexibleHeight = 1f;
            if (import)
            {
                var clipboard = GUIUtility.systemCopyBuffer ?? string.Empty;
                if (clipboard.Contains(SandboxPresetLibrary.ExportFormat))
                {
                    _exchangeCode.text = clipboard;
                }
            }
            else
            {
                _exchangeCode.text = _presets.Library.ExportActive();
                GUIUtility.systemCopyBuffer = _exchangeCode.text;
            }

            _exchangeStatus = Hint(panel, import ? string.Empty : S("training:preset/copied"));
            _exchangeStatus.name = "training_exchange_status";

            var actions = HBox("actions", panel, 9f, 58f);
            var clipboardButton = Btn("training_preset_clipboard", actions,
                S(import ? "training:preset/paste" : "training:preset/copy"), false, 132f, ControlHeight, 20);
            Elem(clipboardButton).flexibleWidth = 1f;
            if (import)
            {
                Bind(clipboardButton, () => _exchangeCode.text = GUIUtility.systemCopyBuffer ?? string.Empty);
                var importButton = Btn("training_preset_import_confirm", actions, S("training:preset/import"), true,
                    132f, ControlHeight, 20);
                Elem(importButton).flexibleWidth = 1f;
                Bind(importButton, ImportExchange);
            }
            else
            {
                Bind(clipboardButton, () =>
                {
                    GUIUtility.systemCopyBuffer = _exchangeCode.text;
                    _exchangeStatus.text = S("training:preset/copied");
                });
            }
        }

        /// <summary>The import dialog's text, as typed or pasted.</summary>
        public void SetExchangeText(string text)
        {
            if (_exchangeCode != null)
            {
                _exchangeCode.text = text ?? string.Empty;
            }
        }

        /// <summary>_import_exchange.</summary>
        public void ImportExchange()
        {
            if (_exchangeCode == null)
            {
                return;
            }

            string id;
            var error = _presets.Library.Import(_exchangeCode.text, _presets.DefaultName, _presets.MapIds, out id);
            if (error == SandboxImportError.None)
            {
                _config = _presets.Active;
                _presets.Save();
                CloseExchange();
                RefreshPresetBar();
                return;
            }

            var key = "training:preset/import_error";
            if (error == SandboxImportError.Limit) { key = "training:preset/import_limit"; }
            else if (error == SandboxImportError.Version) { key = "training:preset/import_version"; }
            _exchangeStatus.text = S(key);
        }

        public void CloseExchange()
        {
            if (_exchangeOverlay != null)
            {
                Kill(_exchangeOverlay.gameObject);
            }

            _exchangeOverlay = null;
            _exchangeCode = null;
            _exchangeStatus = null;
        }
    }
}
