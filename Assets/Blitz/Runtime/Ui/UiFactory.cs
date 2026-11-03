using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// Builders for the uGUI equivalents of the Godot control types this screen
    /// uses. TextMeshPro is deliberately not a dependency: the project ships
    /// com.unity.ugui only, so these are legacy Text/InputField widgets.
    /// </summary>
    public static class UiFactory
    {
        public static RectTransform Node(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.localScale = Vector3.one;
            return rt;
        }

        public static Image Picture(string name, Transform parent, Sprite sprite)
        {
            var rt = Node(name, parent);
            var image = rt.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>
        /// Godot TextureRect with stretch_mode KEEP_CENTERED: the texture is
        /// drawn at its own size, centred in the control, never scaled.
        /// </summary>
        public static Image KeepCentered(RectTransform parent, Sprite sprite)
        {
            var image = Picture("texture", parent, sprite);
            var rt = image.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = sprite == null
                ? Vector2.zero
                : new Vector2(sprite.rect.width, sprite.rect.height);
            return image;
        }

        public static Text Label(
            string name,
            Transform parent,
            Font font,
            int fontSize,
            Color color,
            TextAnchor alignment,
            string text)
        {
            var rt = Node(name, parent);
            var label = rt.gameObject.AddComponent<Text>();
            label.font = font;
            label.fontSize = fontSize;
            label.color = color;
            label.alignment = alignment;
            label.text = text ?? string.Empty;
            label.raycastTarget = false;
            label.supportRichText = false;
            // Godot labels do not wrap unless autowrap is on, and none here is.
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            return label;
        }

        /// <summary>Godot LabelSettings draw a 1 px shadow offset down-right.</summary>
        public static void AddShadow(Text label, Color color)
        {
            AddShadow(label, color, new Vector2(1f, 1f));
        }

        /// <summary>
        /// Shadow with the authored Godot shadow_offset_x/shadow_offset_y, which
        /// grow down-right while Unity's Y axis grows up.
        /// </summary>
        public static void AddShadow(Text label, Color color, Vector2 godotOffset)
        {
            var shadow = label.gameObject.AddComponent<Shadow>();
            shadow.effectColor = color;
            shadow.effectDistance = new Vector2(godotOffset.x, -godotOffset.y);
        }

        public static Button TextButton(
            string name,
            Transform parent,
            Sprite background,
            Font font,
            int fontSize,
            string text)
        {
            var rt = Node(name, parent);
            var image = rt.gameObject.AddComponent<Image>();
            image.sprite = background;
            image.type = Image.Type.Simple;

            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            // uGUI tints the target graphic per state on top of whatever the
            // style already says. Godot swaps the whole StyleBox instead, so the
            // built-in tinting is switched off and GodotButtonState draws the
            // states the source authored.
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = Color.white;
            colors.pressedColor = Color.white;
            colors.selectedColor = Color.white;
            colors.disabledColor = Color.white;
            button.colors = colors;
            rt.gameObject.AddComponent<GodotButtonState>();

            if (!string.IsNullOrEmpty(text))
            {
                var label = Label(
                    "label", rt, font, fontSize, UiTheme.ButtonFont,
                    TextAnchor.MiddleCenter, text);
                GodotRect.FullRect(label.rectTransform);
            }

            return button;
        }

        /// <summary>Button whose content is an icon at its native size.</summary>
        public static Button IconButton(
            string name, Transform parent, Sprite background, Sprite icon)
        {
            var button = TextButton(name, parent, background, null, 0, null);
            var image = Picture("icon", button.transform, icon);
            var rt = image.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = icon == null
                ? Vector2.zero
                : new Vector2(icon.rect.width, icon.rect.height);
            return button;
        }

        /// <summary>Godot LineEdit.</summary>
        public static InputField Field(
            string name,
            Transform parent,
            Font font,
            string placeholderText,
            bool secret,
            int characterLimit)
        {
            var rt = Node(name, parent);
            var background = rt.gameObject.AddComponent<Image>();
            background.color = UiTheme.LineEditBackground;

            var field = rt.gameObject.AddComponent<InputField>();
            field.targetGraphic = background;
            field.selectionColor = UiTheme.LineEditSelection;
            field.characterLimit = characterLimit;
            field.lineType = InputField.LineType.SingleLine;
            if (secret)
            {
                field.contentType = InputField.ContentType.Password;
                field.asteriskChar = '*';
            }

            var placeholder = Label(
                "placeholder", rt, font, UiTheme.LineEditFontSize,
                UiTheme.LineEditPlaceholder, TextAnchor.MiddleLeft, placeholderText);
            Inset(placeholder.rectTransform);

            var text = Label(
                "text", rt, font, UiTheme.LineEditFontSize,
                UiTheme.LineEditFont, TextAnchor.MiddleLeft, string.Empty);
            text.supportRichText = false;
            Inset(text.rectTransform);

            field.placeholder = placeholder;
            field.textComponent = text;
            return field;
        }

        private static void Inset(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(UiTheme.LineEditPadding, UiTheme.LineEditPadding);
            rt.offsetMax = new Vector2(-UiTheme.LineEditPadding, -UiTheme.LineEditPadding);
        }
    }
}
