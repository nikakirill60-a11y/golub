using System;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Audio;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// First-run choice between the Classic and the Modern menus. Shown once
    /// after the update (and to new players) while no style is saved; the
    /// choice can be changed later in Settings. Continue stays disabled until
    /// one of the two cards is picked.
    /// </summary>
    public sealed class StylePickerScreen : MonoBehaviour
    {
        public const int SortingOrder = 250;

        private Action<int> _chosen;
        private int _selection = SettingsData.UiStyleUnset;
        private Button _continue;
        private CanvasGroup _continueGroup;
        private GameObject _classicMark;
        private GameObject _modernMark;

        public int Selection { get { return _selection; } }

        public static StylePickerScreen Create(Transform parent, string language, Action<int> chosen)
        {
            var go = new GameObject("StylePickerScreen");
            go.transform.SetParent(parent, false);
            var screen = go.AddComponent<StylePickerScreen>();
            screen._chosen = chosen;
            screen.Build(language);
            return screen;
        }

        private void Build(string language)
        {
            var atlas = BattleFlowUi.LoadAtlas();
            var lang = BattleFlowUi.LoadLanguage();
            if (!string.IsNullOrEmpty(language)) { lang.Language = language; }
            var bold = BlitzResources.LoadFont(BlitzResources.BoldFontName);
            var regular = BlitzResources.LoadFont(BlitzResources.RegularFontName);

            var canvas = BattleFlowUi.CreateCanvas(transform, false, false, SortingOrder);
            var root = UiFactory.Node("style_picker", canvas);
            GodotRect.FullRect(root);

            var scrim = UiFactory.Picture("scrim", root, ModernSprites.VerticalGradient(
                new Color(0.01f, 0.015f, 0.02f, 0.9f), new Color(0.01f, 0.015f, 0.02f, 0.78f)));
            scrim.raycastTarget = true;
            GodotRect.FullRect(scrim.rectTransform);

            var title = ModernUi.Label("title", root, bold, 36, ModernTheme.TextPrimary,
                TextAnchor.MiddleCenter, lang.Get("ui:style/title"));
            GodotRect.Apply(title.rectTransform, 0f, 0f, 1f, 0f, 40f, 40f, -40f, 88f);
            var subtitle = ModernUi.Label("subtitle", root, regular, 19, ModernTheme.TextSecondary,
                TextAnchor.MiddleCenter, lang.Get("ui:style/subtitle"));
            GodotRect.Apply(subtitle.rectTransform, 0f, 0f, 1f, 0f, 40f, 90f, -40f, 120f);

            _classicMark = Card(root, atlas, lang, bold, regular, -250f, SettingsData.UiStyleClassic,
                "ui:style/classic", "ui:style/classic_desc");
            _modernMark = Card(root, atlas, lang, bold, regular, 250f, SettingsData.UiStyleModern,
                "ui:style/modern", "ui:style/modern_desc");

            _continue = ModernUi.Button("btn_continue", root, ModernUi.Tone.Accent, bold, 24,
                lang.Get("ui:style/continue"));
            GodotRect.Apply((RectTransform)_continue.transform, 0.5f, 1f, 0.5f, 1f,
                -140f, -132f, 140f, -72f, GodotRect.TopLeftPivot);
            _continueGroup = _continue.gameObject.AddComponent<CanvasGroup>();
            _continue.onClick.AddListener(Confirm);

            var hint = ModernUi.Label("hint", root, regular, 16, ModernTheme.TextMuted,
                TextAnchor.MiddleCenter, lang.Get("ui:style/hint"));
            GodotRect.Apply(hint.rectTransform, 0f, 1f, 1f, 1f, 40f, -60f, -40f, -30f);

            Refresh();
        }

        private GameObject Card(
            RectTransform root, AtlasRegionTable atlas, LocalizationTable lang, Font bold, Font regular,
            float x, int style, string nameKey, string descriptionKey)
        {
            var card = UiFactory.TextButton("card_" + (style == SettingsData.UiStyleModern ? "modern" : "classic"),
                root, ModernSprites.RoundedRect(ModernTheme.PanelRadius, ModernTheme.GlassTop,
                    ModernTheme.GlassBottom, ModernTheme.Stroke, 1.5f), bold, 0, null);
            card.GetComponent<Image>().type = Image.Type.Sliced;
            card.gameObject.AddComponent<PressFeedback>().PressedScale = 0.98f;
            var rt = (RectTransform)card.transform;
            ModernUi.Centered(rt, x, -10f, 440f, 380f);

            var preview = UiFactory.Node("preview", rt);
            GodotRect.Apply(preview, 0f, 0f, 1f, 0f, 16f, 16f, -16f, 246f);
            preview.gameObject.AddComponent<RectMask2D>();
            var bg = UiFactory.Picture("bg", preview, ModernSprites.VerticalGradient(
                new Color(0.2f, 0.23f, 0.25f, 1f), new Color(0.09f, 0.1f, 0.11f, 1f)));
            GodotRect.FullRect(bg.rectTransform);
            if (style == SettingsData.UiStyleModern) { ModernPreview(preview, atlas); }
            else { ClassicPreview(preview, atlas); }

            var name = ModernUi.Label("name", rt, bold, 26, ModernTheme.TextPrimary,
                TextAnchor.MiddleLeft, lang.Get(nameKey));
            GodotRect.Apply(name.rectTransform, 0f, 0f, 1f, 0f, 20f, 256f, -20f, 292f);
            var description = ModernUi.Label("description", rt, regular, 16, ModernTheme.TextSecondary,
                TextAnchor.UpperLeft, lang.Get(descriptionKey));
            description.horizontalOverflow = HorizontalWrapMode.Wrap;
            GodotRect.Apply(description.rectTransform, 0f, 0f, 1f, 0f, 20f, 296f, -20f, 368f);

            var mark = UiFactory.Picture("selected", rt, ModernSprites.RoundedRect(ModernTheme.PanelRadius,
                new Color(1f, 0.69f, 0.125f, 0.04f), new Color(1f, 0.69f, 0.125f, 0.1f), ModernTheme.Accent, 3f));
            mark.type = Image.Type.Sliced;
            GodotRect.FullRect(mark.rectTransform);

            card.onClick.AddListener(() => Choose(style));
            return mark.gameObject;
        }

        /// <summary>A thumbnail of the classic hangar drawn from the original artwork.</summary>
        private static void ClassicPreview(RectTransform preview, AtlasRegionTable atlas)
        {
            var header = UiFactory.Picture("header", preview, atlas.GetOriginal("hangar_header"));
            header.type = Image.Type.Sliced;
            GodotRect.Apply(header.rectTransform, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 34f);
            var line = UiFactory.Picture("line", preview, atlas.GetOriginal("hangar_thin_red_line"));
            GodotRect.Apply(line.rectTransform, 0f, 0f, 1f, 0f, 0f, 33f, 0f, 35f);
            var play = UiFactory.Picture("play", preview, atlas.GetOriginal("hangar_play"));
            GodotRect.Apply(play.rectTransform, 0.5f, 0f, 0.5f, 0f, -44f, 40f, 44f, 70f);
            var footer = UiFactory.Picture("footer", preview, atlas.GetOriginal("hangar_footer"));
            footer.type = Image.Type.Sliced;
            GodotRect.Apply(footer.rectTransform, 0f, 1f, 1f, 1f, 0f, -50f, 0f, 0f);
            for (var i = 0; i < 5; i++)
            {
                var tile = UiFactory.Picture("card" + i, preview, atlas.GetOriginal("tank_nation_" + (i + 1)));
                GodotRect.Apply(tile.rectTransform, 0f, 1f, 0f, 1f, 36f + i * 68f, -46f, 100f + i * 68f, -4f);
            }
        }

        /// <summary>A thumbnail of the modern hangar drawn with the modern chrome.</summary>
        private static void ModernPreview(RectTransform preview, AtlasRegionTable atlas)
        {
            var top = UiFactory.Picture("top", preview, ModernSprites.RoundedRect(1, ModernTheme.GlassStrong));
            top.type = Image.Type.Sliced;
            GodotRect.Apply(top.rectTransform, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 24f);
            var line = ModernUi.Strip("line", preview, ModernTheme.Accent);
            GodotRect.Apply(line.rectTransform, 0f, 0f, 1f, 0f, 0f, 24f, 0f, 25f);
            for (var i = 0; i < 4; i++)
            {
                var rail = UiFactory.Picture("rail" + i, preview, ModernSprites.RoundedRect(6, ModernTheme.GlassTop,
                    ModernTheme.GlassBottom, ModernTheme.Stroke, 1f));
                rail.type = Image.Type.Sliced;
                GodotRect.Apply(rail.rectTransform, 0f, 0f, 0f, 0f, 8f, 32f + i * 22f, 70f, 50f + i * 22f);
            }

            var panel = UiFactory.Picture("panel", preview, ModernSprites.RoundedRect(8, ModernTheme.Glass,
                ModernTheme.Glass, ModernTheme.Stroke, 1f));
            panel.type = Image.Type.Sliced;
            GodotRect.Apply(panel.rectTransform, 1f, 0f, 1f, 0f, -110f, 32f, -8f, 110f);

            var play = UiFactory.Picture("play", preview, atlas.GetOriginal("hangar_play"));
            GodotRect.Apply(play.rectTransform, 1f, 1f, 1f, 1f, -86f, -78f, -8f, -52f);

            var bar = UiFactory.Picture("carousel", preview, ModernSprites.RoundedRect(1, ModernTheme.GlassStrong));
            bar.type = Image.Type.Sliced;
            GodotRect.Apply(bar.rectTransform, 0f, 1f, 1f, 1f, 0f, -44f, 0f, 0f);
            for (var i = 0; i < 6; i++)
            {
                var card = UiFactory.Picture("card" + i, preview, ModernSprites.RoundedRect(6,
                    new Color(0.13f, 0.15f, 0.18f, 1f), new Color(0.07f, 0.08f, 0.1f, 1f),
                    i == 0 ? ModernTheme.Accent : ModernTheme.Stroke, i == 0 ? 2f : 1f));
                card.type = Image.Type.Sliced;
                GodotRect.Apply(card.rectTransform, 0f, 1f, 0f, 1f, 34f + i * 58f, -38f, 88f + i * 58f, -6f);
            }
        }

        public void Choose(int style)
        {
            _selection = style;
            var bootstrap = BlitzBootstrap.Current;
            if (bootstrap != null && bootstrap.Audio != null)
            {
                bootstrap.Audio.PlayButtonClick(AudioService.SoundEnter);
            }

            Refresh();
        }

        private void Refresh()
        {
            _classicMark.SetActive(_selection == SettingsData.UiStyleClassic);
            _modernMark.SetActive(_selection == SettingsData.UiStyleModern);
            var ready = _selection != SettingsData.UiStyleUnset;
            _continue.interactable = ready;
            _continueGroup.alpha = ready ? 1f : 0.4f;
        }

        public void Confirm()
        {
            if (_selection == SettingsData.UiStyleUnset)
            {
                return;
            }

            var callback = _chosen;
            _chosen = null;
            Destroy(gameObject);
            if (callback != null)
            {
                callback(_selection);
            }
        }
    }
}
