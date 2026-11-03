using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// The Modern style of the shop. Godot has no such screen, so it follows
    /// the other Modern sub-screens instead: the glass back button and title
    /// in the header band, glass tabs with the accent outline on the open
    /// one, the hangar's currency pills left on screen, and rounded glass
    /// cards with the original artwork in a dark well.
    /// </summary>
    public sealed partial class ShopScreen
    {
        private const int ModernPadding = 16;
        private const float ModernSpacing = 16f;
        private const float ModernCardWidth = 280f;
        private const float ModernCardTop = 18f;
        private const float ModernCardHeight = 440f;
        private const float ModernInset = 12f;
        private const float ModernWellHeight = 200f;

        /// <summary>A chest picture may grow this much to fill its well.</summary>
        private const float ModernChestGrowth = 1.35f;

        private static readonly Color ModernTitleColor = ModernTheme.TextPrimary;
        private static readonly Color ModernWell = new Color(0f, 0f, 0f, 0.3f);

        /// <summary>Modern top bar height (HangarScreen.Modern); the title is centred in it.</summary>
        public const float ModernTopBarHeight = 76f;
        /// <summary>Left aligned like the hangar nickname, right of the back button.</summary>
        public const float ModernTitleX = 88f;
        public const float ModernTitleHeight = 40f;
        public const float ModernTitleY = (ModernTopBarHeight - ModernTitleHeight) * 0.5f;

        /// <summary>The title spot the Modern loadout uses for its tank name.</summary>
        private void BuildModernTitle(RectTransform top)
        {
            _kit.Label(
                "title", top, 30, TextAnchor.MiddleLeft, _kit.Str("shop:title"),
                _kit.Bold, UiTheme.Label, ModernTitleX, ModernTitleY, 329f, ModernTitleHeight);
        }

        private Card ModernCard(Page page, CardSpec spec)
        {
            const float w = ModernCardWidth;
            const float inner = w - (ModernInset * 2f);
            var root = EntryNode(page, spec.Name, w);
            var card = new Card { Root = root };

            var panel = ModernUi.Panel("card", root, ModernTheme.PanelRadius, ModernTheme.Glass, true);
            ModernUi.TopLeft(panel.rectTransform, 0f, ModernCardTop, w, ModernCardHeight);
            var body = panel.rectTransform;

            // The well fades into the vehicle kind colour at the bottom.
            var wellBottom = spec.Tank != null
                ? new Color(spec.TagColor.r * 0.35f, spec.TagColor.g * 0.35f, spec.TagColor.b * 0.35f, 0.45f)
                : ModernWell;
            var well = UiFactory.Picture(
                "art", body, ModernSprites.RoundedRect(ModernTheme.Radius, ModernWell, wellBottom, Color.clear, 0f));
            well.type = Image.Type.Sliced;
            ModernUi.TopLeft(well.rectTransform, ModernInset, ModernInset, inner, ModernWellHeight);

            var sprite = _kit.Art(spec.Image);
            var icon = UiFactory.Picture("icon", well.rectTransform, sprite);
            icon.preserveAspect = true;
            icon.enabled = sprite != null;
            var size = FitInto(
                sprite, inner - 20f, ModernWellHeight - 20f,
                spec.Kind == CardKind.Container ? ModernChestGrowth : 1f);
            ModernUi.Centered(icon.rectTransform, 0f, 0f, size.x, size.y);

            if (spec.Tank != null)
            {
                var tint = HangarUiKit.TankGlyphTint(spec.Tank);
                var classGlyph = UiFactory.Picture(
                    "class", well.rectTransform, _kit.Art(HangarUiKit.TankClassRegion(spec.Tank)));
                classGlyph.preserveAspect = true;
                classGlyph.color = tint;
                ModernUi.TopLeft(classGlyph.rectTransform, 10f, 10f, 22f, 25f);
                var levelGlyph = UiFactory.Picture(
                    "lvl", well.rectTransform, _kit.Art(HangarUiKit.TankLevelRegion(spec.Tank)));
                levelGlyph.preserveAspect = true;
                levelGlyph.color = tint;
                ModernUi.TopLeft(levelGlyph.rectTransform, 36f, 10f, 34f, 25f);
            }

            card.Title = ModernUi.Label(
                "title", body, _kit.Bold, 22, spec.TitleColor, TextAnchor.MiddleLeft, spec.Title);
            ModernUi.TopLeft(card.Title.rectTransform, ModernInset + 4f, 220f, inner - 8f, 30f);
            FitOneLine(card.Title, 16);

            var subtitle = ModernUi.Label(
                "desc", body, _kit.Regular, 15,
                spec.Tank != null ? spec.TagColor : ModernTheme.TextSecondary,
                TextAnchor.UpperLeft, spec.Tank != null ? spec.Tag : spec.Description);
            subtitle.horizontalOverflow = HorizontalWrapMode.Wrap;
            ModernUi.TopLeft(subtitle.rectTransform, ModernInset + 4f, 254f, inner - 8f, 54f);

            if (spec.Kind == CardKind.Container)
            {
                card.Note = ModernUi.Label(
                    "guaranteed", body, _kit.Bold, 14, ModernTheme.Accent, TextAnchor.MiddleLeft, string.Empty);
                ModernUi.TopLeft(card.Note.rectTransform, ModernInset + 4f, 306f, inner - 8f, 22f);
            }

            var divider = ModernUi.Strip("divider", body, ModernTheme.Stroke);
            ModernUi.TopLeft(divider.rectTransform, ModernInset, 333f, inner, 1f);

            var currencySprite = _kit.Art(CurrencyRegion(spec.Currency));
            var currency = UiFactory.Picture("currency", body, currencySprite);
            currency.preserveAspect = true;
            ModernUi.TopLeft(currency.rectTransform, ModernInset + 4f, 342f, 26f, 26f);
            card.Cost = ModernUi.Label(
                "cost", body, _kit.Bold, 24, ModernTheme.TextPrimary, TextAnchor.MiddleLeft,
                HangarUiKit.FormatNumber(spec.Cost));
            ModernUi.TopLeft(card.Cost.rectTransform, ModernInset + 38f, 340f, inner - 42f, 30f);

            card.Buy = ModernUi.Button(
                "ButtonCustom", body, ModernUi.Tone.Green, _kit.Bold, 20, _kit.Str("BUY"));
            ModernUi.TopLeft(
                (RectTransform)card.Buy.transform, ModernInset,
                ModernCardHeight - ModernInset - 50f, inner, 50f);
            card.BuyImage = card.Buy.GetComponent<Image>();
            card.BuyLabel = card.Buy.transform.Find("label").GetComponent<Text>();
            return card;
        }

        private static void ModernOwned(Card card, bool owned)
        {
            card.BuyImage.color = owned ? new Color(1f, 1f, 1f, 0.35f) : Color.white;
            card.BuyLabel.color = owned ? ModernTheme.TextSecondary : ModernTheme.TextPrimary;
        }

        /// <summary>The countdown to the next tank week in a glass pill.</summary>
        private void BuildModernRotation(RectTransform page)
        {
            var pill = ModernUi.Panel("rotation", page, ModernTheme.Radius, ModernTheme.Glass, true);
            ModernUi.BottomLeft(pill.rectTransform, ModernPadding, 14f, 660f, 58f);
            var accent = ModernUi.Strip("accent", pill.rectTransform, ModernTheme.Accent);
            ModernUi.TopLeft(accent.rectTransform, 10f, 12f, 3f, 34f);

            _rotation = ModernUi.Label(
                "rotation", pill.rectTransform, _kit.Bold, 20, ModernTheme.TextPrimary,
                TextAnchor.MiddleLeft, string.Empty);
            ModernUi.TopLeft(_rotation.rectTransform, 24f, 6f, 624f, 26f);
            _rotationHint = ModernUi.Label(
                "rotation_hint", pill.rectTransform, _kit.Regular, 15, ModernTheme.TextSecondary,
                TextAnchor.MiddleLeft, _kit.Str("shop:rotation/hint"));
            ModernUi.TopLeft(_rotationHint.rectTransform, 24f, 32f, 624f, 20f);
        }

        /// <summary>Fits a sprite into a box, growing it by at most <paramref name="growth"/>.</summary>
        private static Vector2 FitInto(Sprite sprite, float width, float height, float growth)
        {
            if (sprite == null)
            {
                return Vector2.zero;
            }

            var w = Mathf.Max(1f, sprite.rect.width);
            var h = Mathf.Max(1f, sprite.rect.height);
            var scale = Mathf.Min(growth, Mathf.Min(width / w, height / h));
            return new Vector2(w * scale, h * scale);
        }
    }
}
