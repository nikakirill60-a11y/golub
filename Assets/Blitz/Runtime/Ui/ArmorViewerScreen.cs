using System;
using System.Globalization;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Hangar;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>Native uGUI armour viewer shown over the live hangar camera.</summary>
    public sealed class ArmorViewerScreen
    {
        private static readonly Color PanelFill = new Color(0.035f, 0.055f, 0.07f, 0.92f);
        private readonly HangarUiKit _kit;
        private readonly HangarWorld _world;
        private HangarArmorViewer _viewer;
        private HangarArmorInput _input;
        private Text _tankName;
        private Text _modeHint;
        private Text _caliberValue;
        private Text _penetrationValue;
        private Text _result;
        private readonly Button[] _modeButtons = new Button[3];
        private readonly Button[] _shellButtons = new Button[4];
        private readonly ShellType[] _shellTypes =
            { ShellType.Ap, ShellType.Apcr, ShellType.Heat, ShellType.He };
        private HangarArmorViewer.ViewMode _mode;
        private ShellType _shell = ShellType.Ap;
        private int _caliber = 100;
        private int _penetration = 175;

        public ArmorViewerScreen(HangarUiKit kit, HangarWorld world)
        {
            _kit = kit;
            _world = world;
        }

        public RectTransform Root { get; private set; }
        public HangarArmorViewer Viewer { get { return _viewer; } }
        public Action HomePressed;

        public void Build(RectTransform parent)
        {
            Root = _kit.Node("armor_viewer", parent, 0f, 0f,
                UiLayout.ReferenceWidth, UiLayout.ReferenceHeight);
            HangarUiKit.Stretch(Root, 0f, 0f, 0f, 0f);

            var tint = _kit.Picture("blur", Root, null, 0f, 0f,
                UiLayout.ReferenceWidth, UiLayout.ReferenceHeight);
            tint.color = new Color(
                HangarUiKit.ScreenTint.r,
                HangarUiKit.ScreenTint.g,
                HangarUiKit.ScreenTint.b,
                0.38f);
            tint.raycastTarget = false;
            HangarUiKit.Stretch(tint.rectTransform, 0f, 0f, 0f, 0f);

            var surface = _kit.Picture("viewport", Root, null, 0f, 0f,
                UiLayout.ReferenceWidth, UiLayout.ReferenceHeight);
            surface.color = new Color(0f, 0f, 0f, 0.001f);
            surface.raycastTarget = true;
            HangarUiKit.Stretch(surface.rectTransform, 0f, 0f, 0f, 0f);
            _input = surface.gameObject.AddComponent<HangarArmorInput>();
            _input.Hit = ShowHit;
            _input.Miss = () => SetResult(_kit.Str("ARMOR_VIEWER_MISS"), UiTheme.Label);

            var back = _kit.StyleButton("btn_back", Root, "button_small_generic", 5f, 4f, 94f, 77f);
            _kit.AddIcon(back, "icon_back", 50f);
            _kit.Bind(back, Close, AudioService.SoundEnter);

            _tankName = _kit.Label("tank_name", Root, 30, TextAnchor.MiddleLeft,
                string.Empty, _kit.Bold, UiTheme.Label, 116f, 10f, 420f, 50f);

            BuildModes();
            BuildPanel();
            Root.gameObject.SetActive(false);
        }

        public bool Init(TankInfo card, TankSaveData save)
        {
            if (card == null || _world == null) { return false; }
            _viewer = HangarArmorViewer.Create(_world);
            if (_viewer == null || !_viewer.Open(card, save)) { return false; }
            _input.Configure(_world.Orbit, _viewer);
            _tankName.text = _kit.Lang.GetTankName(card);
            _shell = _viewer.ShellType;
            _caliber = _viewer.Caliber;
            _penetration = Mathf.RoundToInt(_viewer.Penetration);
            Root.gameObject.SetActive(true);
            SetMode(HangarArmorViewer.ViewMode.Sight);
            RefreshShellControls();
            SetResult(_kit.Str("ARMOR_VIEWER_READY"), UiTheme.Label);
            return true;
        }

        public void Close()
        {
            if (_viewer != null) { _viewer.Close(); }
            if (Root != null) { Root.gameObject.SetActive(false); }
            if (HomePressed != null) { HomePressed(); }
        }

        private void BuildModes()
        {
            var keys = new[]
            {
                "ARMOR_VIEWER_SIGHT",
                "ARMOR_VIEWER_FILL",
                "ARMOR_VIEWER_TEST",
            };
            for (var i = 0; i < keys.Length; i++)
            {
                var index = i;
                var button = _kit.StyleButton("mode_" + i, Root, "button_small_generic",
                    330f + i * 205f, 4f, 195f, 77f);
                _kit.Label("label", (RectTransform)button.transform, 20,
                    TextAnchor.MiddleCenter, _kit.Str(keys[i]), _kit.Bold,
                    UiTheme.ButtonFont, 8f, 8f, 179f, 61f);
                _kit.Bind(button, () => SetMode((HangarArmorViewer.ViewMode)index),
                    AudioService.SoundEnter);
                _modeButtons[i] = button;
            }
        }

        private void BuildPanel()
        {
            var panel = _kit.Node("controls", Root, 1035f, 91f, 331f, 642f);
            HangarUiKit.RightStretch(panel, -331f, 91f, 0f, 0f);
            var bg = _kit.Picture("bg", panel, null, 0f, 0f, 331f, 642f);
            bg.color = PanelFill;
            HangarUiKit.Stretch(bg.rectTransform, 0f, 0f, 0f, 0f);

            _kit.Label("shell_title", panel, 20, TextAnchor.MiddleLeft,
                _kit.Str("ARMOR_VIEWER_SHELL"), _kit.Bold, UiTheme.Label,
                16f, 16f, 299f, 30f);
            for (var i = 0; i < _shellTypes.Length; i++)
            {
                var index = i;
                var button = _kit.StyleButton("shell_" + i, panel,
                    "button_small_generic", 16f + (i % 2) * 150f,
                    54f + (i / 2) * 66f, 142f, 58f);
                _kit.Label("label", (RectTransform)button.transform, 18,
                    TextAnchor.MiddleCenter, ShellName(_shellTypes[i]), _kit.Bold,
                    UiTheme.ButtonFont, 4f, 4f, 134f, 50f);
                _kit.Bind(button, () => SetShellType(_shellTypes[index]),
                    AudioService.SoundEnter);
                _shellButtons[i] = button;
            }

            BuildStepper(panel, "caliber", "ARMOR_VIEWER_CALIBER", 196f,
                -5, 5, value => _caliber = Mathf.Clamp(_caliber + value, 1, 400),
                out _caliberValue);
            BuildStepper(panel, "penetration", "ARMOR_VIEWER_PENETRATION", 292f,
                -5, 5, value => _penetration = Mathf.Clamp(_penetration + value, 0, 1000),
                out _penetrationValue);

            _modeHint = _kit.Label("hint", panel, 18, TextAnchor.UpperLeft,
                string.Empty, _kit.Regular, HangarUiKit.Caption,
                16f, 394f, 299f, 72f);
            _modeHint.horizontalOverflow = HorizontalWrapMode.Wrap;
            _modeHint.verticalOverflow = VerticalWrapMode.Overflow;

            _result = _kit.Label("result", panel, 20, TextAnchor.UpperLeft,
                string.Empty, _kit.Bold, UiTheme.Label,
                16f, 478f, 299f, 145f);
            _result.horizontalOverflow = HorizontalWrapMode.Wrap;
            _result.verticalOverflow = VerticalWrapMode.Overflow;
        }

        private void BuildStepper(
            RectTransform panel, string name, string key, float top,
            int minus, int plus, Action<int> change, out Text value)
        {
            _kit.Label(name + "_title", panel, 18, TextAnchor.MiddleLeft,
                _kit.Str(key), _kit.Regular, HangarUiKit.Caption,
                16f, top, 299f, 25f);
            var dec = _kit.StyleButton(name + "_minus", panel, "button_small_generic",
                16f, top + 30f, 64f, 50f);
            _kit.Label("label", (RectTransform)dec.transform, 28,
                TextAnchor.MiddleCenter, "−", _kit.Bold, UiTheme.ButtonFont,
                0f, 0f, 64f, 50f);
            value = _kit.Label(name + "_value", panel, 24, TextAnchor.MiddleCenter,
                string.Empty, _kit.Bold, UiTheme.Label, 88f, top + 30f, 139f, 50f);
            var inc = _kit.StyleButton(name + "_plus", panel, "button_small_generic",
                235f, top + 30f, 64f, 50f);
            _kit.Label("label", (RectTransform)inc.transform, 28,
                TextAnchor.MiddleCenter, "+", _kit.Bold, UiTheme.ButtonFont,
                0f, 0f, 64f, 50f);
            _kit.Bind(dec, () => { change(minus); ApplyShell(); }, AudioService.SoundEnter);
            _kit.Bind(inc, () => { change(plus); ApplyShell(); }, AudioService.SoundEnter);
        }

        private void SetMode(HangarArmorViewer.ViewMode mode)
        {
            _mode = mode;
            if (_viewer != null) { _viewer.SetMode(mode); }
            var hint = mode == HangarArmorViewer.ViewMode.Sight
                ? "ARMOR_VIEWER_SIGHT_HINT"
                : mode == HangarArmorViewer.ViewMode.Fill
                    ? "ARMOR_VIEWER_FILL_HINT"
                    : "ARMOR_VIEWER_TEST_HINT";
            _modeHint.text = _kit.Str(hint);
            for (var i = 0; i < _modeButtons.Length; i++)
            {
                SetSelected(_modeButtons[i], i == (int)mode);
            }
        }

        private void SetShellType(ShellType type)
        {
            _shell = type;
            ApplyShell();
        }

        private void ApplyShell()
        {
            if (_viewer != null) { _viewer.SetShell(_shell, _caliber, _penetration); }
            RefreshShellControls();
        }

        private void RefreshShellControls()
        {
            _caliberValue.text = _caliber.ToString(CultureInfo.InvariantCulture) + " mm";
            _penetrationValue.text = _penetration.ToString(CultureInfo.InvariantCulture) + " mm";
            for (var i = 0; i < _shellButtons.Length; i++)
            {
                SetSelected(_shellButtons[i], _shellTypes[i] == _shell);
            }
        }

        private void ShowHit(HangarArmorViewer.HitInfo hit)
        {
            var outcome = _kit.Str(OutcomeKey(hit.Outcome));
            var text = outcome + "\n" +
                _kit.Str("ARMOR_VIEWER_NOMINAL") + ": " + hit.NominalArmor + " mm\n" +
                _kit.Str("ARMOR_VIEWER_EFFECTIVE") + ": " +
                hit.EffectiveArmor.ToString("0.0", CultureInfo.InvariantCulture) + " mm\n" +
                _kit.Str("ARMOR_VIEWER_ANGLE") + ": " +
                hit.AngleDegrees.ToString("0.0", CultureInfo.InvariantCulture) + "°";
            SetResult(text, HangarArmorViewer.OutcomeColor(hit.Outcome));
        }

        private void SetResult(string text, Color color)
        {
            _result.text = text;
            _result.color = color;
        }

        private string ShellName(ShellType type)
        {
            switch (type)
            {
                case ShellType.Apcr: return _kit.Str("Abbr_ARMOR_PIERCING_CR");
                case ShellType.Heat: return _kit.Str("Abbr_HOLLOW_CHARGE");
                case ShellType.He: return _kit.Str("Abbr_HIGH_EXPLOSIVE");
                default: return _kit.Str("Abbr_ARMOR_PIERCING");
            }
        }

        private static string OutcomeKey(HitOutcome outcome)
        {
            switch (outcome)
            {
                case HitOutcome.Penetration: return "ARMOR_VIEWER_PENETRATED";
                case HitOutcome.Overmatch: return "ARMOR_VIEWER_OVERMATCH";
                case HitOutcome.Ricochet: return "ARMOR_VIEWER_RICOCHET";
                default: return "ARMOR_VIEWER_BOUNCE";
            }
        }

        private static void SetSelected(Button button, bool selected)
        {
            if (button == null) { return; }
            var image = button.GetComponent<Image>();
            if (image != null)
            {
                image.color = selected ? new Color(0.72f, 0.88f, 1f, 1f) : Color.white;
            }
        }
    }
}
