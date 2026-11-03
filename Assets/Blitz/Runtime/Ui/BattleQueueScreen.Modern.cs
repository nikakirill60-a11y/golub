using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// The Modern battle queue: a glass panel over the dimmed hangar with a
    /// spinning ring timer, the player counts as bars and the cancel button
    /// where the battle button was. The bottom edge stays transparent so the
    /// locked carousel of the Modern hangar shows through. All counting,
    /// matching and online plumbing is the classic screen's.
    /// </summary>
    public sealed partial class BattleQueueScreen
    {
        private Image _ringArc;
        private readonly Image[] _tierBars = new Image[10];
        private readonly Image[] _typeBars = new Image[4];
        private bool _modern;

        public bool IsModern { get { return _modern; } }

        private void BuildModern(AtlasRegionTable atlas, LocalizationTable lang, Font bold, Font regular)
        {
            _modern = true;
            var canvas = BattleFlowUi.CreateCanvas(transform, false, false, 80);
            var root = UiFactory.Node("battle_queue", canvas);
            GodotRect.FullRect(root);

            var settings = BlitzBootstrap.Current == null ? null : BlitzBootstrap.Current.Settings;
            var aspect = MenuStyle.ScreenAspect;
            var bar = MenuStyleRules.Metrics(
                settings == null ? Core.Save.SettingsData.CarouselSizeNormal : settings.UiCarouselSize,
                aspect).BarHeight;

            // The scrim swallows input over the hangar but stops at the
            // carousel, which the hangar itself dims and locks.
            var scrim = UiFactory.Picture("scrim", root, ModernSprites.VerticalGradient(
                new Color(0.01f, 0.015f, 0.02f, 0.86f), new Color(0.01f, 0.015f, 0.02f, 0.55f)));
            scrim.raycastTarget = true;
            GodotRect.Apply(scrim.rectTransform, 0f, 0f, 1f, 1f, 0f, 0f, 0f, -bar);

            // Centre: mode, status, ring timer and the total.
            var panel = ModernUi.Panel("queue_panel", root, ModernTheme.PanelRadius, ModernTheme.GlassStrong, true);
            ModernUi.Centered(panel.rectTransform, 0f, -bar * 0.5f, 420f, 400f);
            var p = panel.rectTransform;

            var modeIcon = UiFactory.Picture("mode_icon", p, atlas.Get("flow_queue_mode"));
            modeIcon.preserveAspect = true;
            GodotRect.Apply(modeIcon.rectTransform, 0.5f, 0f, 0.5f, 0f, -26f, 16f, 26f, 60f);
            _modeLabel = ModernUi.Label("mode", p, bold, 28, ModernTheme.Accent, TextAnchor.MiddleCenter,
                lang.Get(ModeLabelKey(_mode, _online)));
            GodotRect.Apply(_modeLabel.rectTransform, 0f, 0f, 1f, 0f, 16f, 62f, -16f, 98f);
            _caption = ModernUi.Label("caption", p, regular, 18, ModernTheme.TextSecondary,
                TextAnchor.MiddleCenter, lang.Get("queue.please_wait"));
            GodotRect.Apply(_caption.rectTransform, 0f, 0f, 1f, 0f, 16f, 98f, -16f, 124f);

            var ringBack = UiFactory.Picture("ring_bg", p, ModernSprites.Ring(192, 10f, new Color(1f, 1f, 1f, 0.1f)));
            GodotRect.Apply(ringBack.rectTransform, 0.5f, 0f, 0.5f, 0f, -80f, 136f, 80f, 296f);
            _ringArc = UiFactory.Picture("ring", ringBack.rectTransform, ModernSprites.Ring(192, 10f, ModernTheme.Accent));
            GodotRect.FullRect(_ringArc.rectTransform);
            _ringArc.type = Image.Type.Filled;
            _ringArc.fillMethod = Image.FillMethod.Radial360;
            _ringArc.fillOrigin = (int)Image.Origin360.Top;
            _ringArc.fillAmount = 0.28f;

            _waitValue = ModernUi.Label("wait", ringBack.rectTransform, bold, 36, ModernTheme.TextPrimary,
                TextAnchor.MiddleCenter, "00:00");
            GodotRect.Apply(_waitValue.rectTransform, 0f, 0f, 1f, 1f, 0f, 44f, 0f, -60f);
            var waitCaption = ModernUi.Label("wait_caption", ringBack.rectTransform, regular, 14,
                ModernTheme.TextSecondary, TextAnchor.MiddleCenter, lang.Get("queue.wait_time"));
            GodotRect.Apply(waitCaption.rectTransform, 0f, 0f, 1f, 1f, 10f, 98f, -10f, -40f);

            var players = UiFactory.Picture("players_icon", p, atlas.Get("flow_queue_players"));
            players.preserveAspect = true;
            GodotRect.Apply(players.rectTransform, 0.5f, 1f, 0.5f, 1f, -150f, -78f, -122f, -50f);
            var playersCaption = ModernUi.Label("players_caption", p, regular, 18, ModernTheme.TextSecondary,
                TextAnchor.MiddleLeft, lang.Get("queue.players"));
            GodotRect.Apply(playersCaption.rectTransform, 0.5f, 1f, 0.5f, 1f, -114f, -80f, 60f, -48f);
            _totalValue = ModernUi.Label("players", p, bold, 22, ModernTheme.TextPrimary,
                TextAnchor.MiddleRight, "0");
            GodotRect.Apply(_totalValue.rectTransform, 0.5f, 1f, 0.5f, 1f, 40f, -80f, 160f, -48f);

            // Left: tanks by tier; right: tanks by class. Original glyphs.
            var tiers = ModernUi.Panel("tank_by_lvl", root, ModernTheme.PanelRadius, ModernTheme.Glass, true);
            ModernUi.Centered(tiers.rectTransform, -420f, -bar * 0.5f, 340f, 400f);
            Heading(tiers.rectTransform, bold, lang.Get("queue.tanks_by_lvl"));
            for (var i = 0; i < _tierValues.Length; i++)
            {
                _tierValues[i] = CountRow(tiers.rectTransform, atlas, regular, bold, i, 48f + i * 34f,
                    HangarUiKit.LevelRegion(10 - i), null, 0f, out _tierBars[i]);
            }

            var types = ModernUi.Panel("tank_by_type", root, ModernTheme.PanelRadius, ModernTheme.Glass, true);
            ModernUi.Centered(types.rectTransform, 420f, -bar * 0.5f, 340f, 400f);
            Heading(types.rectTransform, bold, lang.Get("queue.tanks_by_lvl"));
            var classes = new[] { TankClass.Heavy, TankClass.Medium, TankClass.Light, TankClass.TankDestroyer };
            for (var i = 0; i < _typeValues.Length; i++)
            {
                _typeValues[i] = CountRow(types.rectTransform, atlas, regular, bold, i, 56f + i * 52f,
                    HangarUiKit.ClassRegion(classes[i]), lang.Get(TypeKeys[i]), 120f, out _typeBars[i]);
            }

            // Cancel sits exactly where the battle button was.
            var cancel = ModernUi.Button("btn_cancel", root, ModernUi.Tone.Red, bold, 30, lang.Get("CANCEL"));
            ModernUi.BottomRight((RectTransform)cancel.transform, 16f, bar + ModernTheme.Gap, 222f, 77f);
            cancel.onClick.AddListener(Cancel);
        }

        private static void Heading(RectTransform panel, Font bold, string text)
        {
            var title = ModernUi.Label("title", panel, bold, 18, ModernTheme.TextSecondary,
                TextAnchor.MiddleLeft, text);
            GodotRect.Apply(title.rectTransform, 0f, 0f, 1f, 0f, 18f, 10f, -18f, 40f);
        }

        private static Text CountRow(
            RectTransform panel, AtlasRegionTable atlas, Font regular, Font bold, int index, float y,
            string glyphRegion, string caption, float captionWidth, out Image fill)
        {
            var row = UiFactory.Node("row" + index, panel);
            GodotRect.Apply(row, 0f, 0f, 1f, 0f, 18f, y, -18f, y + 28f);

            var glyph = UiFactory.Picture("glyph", row, atlas.Get(glyphRegion));
            glyph.preserveAspect = true;
            GodotRect.Apply(glyph.rectTransform, 0f, 0f, 0f, 1f, 0f, 2f, 28f, -2f);
            var trackStart = 40f;
            if (!string.IsNullOrEmpty(caption))
            {
                var label = ModernUi.Label("caption", row, regular, 15, ModernTheme.TextSecondary,
                    TextAnchor.MiddleLeft, caption);
                label.resizeTextForBestFit = true;
                label.resizeTextMinSize = 10;
                label.resizeTextMaxSize = 15;
                GodotRect.Apply(label.rectTransform, 0f, 0f, 0f, 1f, 34f, 0f, 34f + captionWidth, 0f);
                trackStart = 40f + captionWidth;
            }

            var track = ModernUi.Strip("track", row, new Color(1f, 1f, 1f, 0.07f));
            GodotRect.Apply(track.rectTransform, 0f, 0.5f, 1f, 0.5f, trackStart, -4f, -48f, 4f);
            fill = ModernUi.Strip("fill", track.rectTransform, ModernTheme.Accent);
            fill.rectTransform.anchorMin = Vector2.zero;
            fill.rectTransform.anchorMax = new Vector2(0f, 1f);
            fill.rectTransform.pivot = new Vector2(0f, 0.5f);
            fill.rectTransform.offsetMin = Vector2.zero;
            fill.rectTransform.offsetMax = Vector2.zero;

            var value = ModernUi.Label("value", row, bold, 17, ModernTheme.TextPrimary,
                TextAnchor.MiddleRight, "0");
            GodotRect.Apply(value.rectTransform, 1f, 0f, 1f, 1f, -44f, 0f, 0f, 0f);
            return value;
        }

        /// <summary>Spins the ring and sizes the bars from the counters the classic logic writes.</summary>
        private void UpdateModern()
        {
            if (_ringArc != null)
            {
                _ringArc.rectTransform.localEulerAngles = new Vector3(0f, 0f, -Time.unscaledTime * 220f);
            }

            ScaleBars(_tierValues, _tierBars);
            ScaleBars(_typeValues, _typeBars);
        }

        private static void ScaleBars(Text[] values, Image[] bars)
        {
            var max = 1;
            var counts = new int[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                int count;
                counts[i] = values[i] != null && int.TryParse(values[i].text, out count) ? count : 0;
                max = Mathf.Max(max, counts[i]);
            }

            for (var i = 0; i < bars.Length; i++)
            {
                if (bars[i] == null) { continue; }
                var target = counts[i] / (float)max;
                var current = bars[i].rectTransform.anchorMax.x;
                var next = Mathf.MoveTowards(current, target, Time.unscaledDeltaTime * 2f);
                bars[i].rectTransform.anchorMax = new Vector2(next, 1f);
            }
        }
    }
}
