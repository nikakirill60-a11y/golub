using System;
using System.Collections;
using BlitzLegacy.Core.Ui;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>Widget helpers of the Modern style, built on <see cref="UiFactory"/>.</summary>
    public static class ModernUi
    {
        public enum Tone
        {
            Glass,
            Green,
            Red,
            Accent,
            Ghost,
        }

        /// <summary>A rounded glass panel.</summary>
        public static Image Panel(string name, Transform parent, int radius, Color fill, bool stroke)
        {
            var image = UiFactory.Picture(name, parent, ModernSprites.RoundedRect(
                radius, fill, fill, stroke ? ModernTheme.Stroke : Color.clear, stroke ? 1.5f : 0f));
            image.type = Image.Type.Sliced;
            return image;
        }

        public static Image Panel(string name, Transform parent)
        {
            return Panel(name, parent, ModernTheme.PanelRadius, ModernTheme.Glass, true);
        }

        /// <summary>A solid strip (hairline, divider, bar fill).</summary>
        public static Image Strip(string name, Transform parent, Color color)
        {
            var image = UiFactory.Picture(name, parent, ModernSprites.RoundedRect(1, Color.white));
            image.type = Image.Type.Sliced;
            image.color = color;
            return image;
        }

        public static Text Label(
            string name, Transform parent, Font font, int size, Color color,
            TextAnchor alignment, string text)
        {
            var label = UiFactory.Label(name, parent, font, size, color, alignment, text);
            UiFactory.AddShadow(label, new Color(0f, 0f, 0f, 0.45f));
            return label;
        }

        /// <summary>A rounded button with pressed state and press feedback.</summary>
        public static Button Button(
            string name, Transform parent, Tone tone, Font font, int fontSize, string text)
        {
            Sprite normal;
            Sprite pressed;
            Sprites(tone, out normal, out pressed);
            var button = UiFactory.TextButton(name, parent, normal, font, fontSize, text);
            var image = button.GetComponent<Image>();
            image.type = Image.Type.Sliced;
            image.color = Color.white;
            if (pressed != null)
            {
                GodotButtonState.Of(button).SetPressedStyle(pressed, Color.white);
            }

            var label = button.transform.Find("label");
            if (label != null)
            {
                var text2 = label.GetComponent<Text>();
                text2.color = tone == Tone.Accent
                    ? new Color(0.1f, 0.08f, 0.03f, 1f)
                    : ModernTheme.TextPrimary;
            }

            button.gameObject.AddComponent<PressFeedback>();
            return button;
        }

        private static void Sprites(Tone tone, out Sprite normal, out Sprite pressed)
        {
            switch (tone)
            {
                case Tone.Green:
                    normal = ModernSprites.RoundedRect(ModernTheme.Radius, ModernTheme.GreenTop,
                        ModernTheme.GreenBottom, new Color(0.6f, 1f, 0.6f, 0.3f), 1.5f);
                    pressed = ModernSprites.RoundedRect(ModernTheme.Radius, ModernTheme.GreenBottom,
                        ModernTheme.GreenBottom, new Color(0.6f, 1f, 0.6f, 0.6f), 2f);
                    return;
                case Tone.Red:
                    normal = ModernSprites.RoundedRect(ModernTheme.Radius, ModernTheme.RedTop,
                        ModernTheme.RedBottom, new Color(1f, 0.6f, 0.55f, 0.3f), 1.5f);
                    pressed = ModernSprites.RoundedRect(ModernTheme.Radius, ModernTheme.RedBottom,
                        ModernTheme.RedBottom, new Color(1f, 0.6f, 0.55f, 0.6f), 2f);
                    return;
                case Tone.Accent:
                    normal = ModernSprites.RoundedRect(ModernTheme.Radius, new Color(1f, 0.76f, 0.3f, 1f),
                        ModernTheme.Accent, new Color(1f, 0.9f, 0.7f, 0.6f), 1.5f);
                    pressed = ModernSprites.RoundedRect(ModernTheme.Radius, new Color(0.85f, 0.56f, 0.08f, 1f),
                        new Color(0.85f, 0.56f, 0.08f, 1f), new Color(1f, 0.9f, 0.7f, 0.8f), 2f);
                    return;
                case Tone.Ghost:
                    normal = ModernSprites.RoundedRect(ModernTheme.Radius, new Color(1f, 1f, 1f, 0.001f));
                    pressed = ModernSprites.RoundedRect(ModernTheme.Radius, new Color(1f, 1f, 1f, 0.08f));
                    return;
                default:
                    normal = ModernSprites.RoundedRect(ModernTheme.Radius, ModernTheme.GlassTop,
                        ModernTheme.GlassBottom, ModernTheme.Stroke, 1.5f);
                    pressed = ModernSprites.RoundedRect(ModernTheme.Radius, ModernTheme.GlassPressedTop,
                        ModernTheme.GlassPressedBottom, ModernTheme.Accent, 2f);
                    return;
            }
        }

        /// <summary>Puts an original atlas icon centred in a button, clamped to a box.</summary>
        public static Image Icon(Transform parent, Sprite sprite, float maxWidth, float maxHeight)
        {
            var image = UiFactory.Picture("icon", parent, sprite);
            image.preserveAspect = true;
            var rt = image.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = Fit(sprite, maxWidth, maxHeight);
            image.enabled = sprite != null;
            return image;
        }

        /// <summary>Size of <paramref name="sprite"/> scaled down (never up) into a box.</summary>
        public static Vector2 Fit(Sprite sprite, float maxWidth, float maxHeight)
        {
            if (sprite == null)
            {
                return Vector2.zero;
            }

            var w = sprite.rect.width;
            var h = sprite.rect.height;
            var scale = Mathf.Min(1f, Mathf.Min(maxWidth / Mathf.Max(1f, w), maxHeight / Mathf.Max(1f, h)));
            return new Vector2(w * scale, h * scale);
        }

        /// <summary>Top-left anchored rect in parent space (Godot coordinates).</summary>
        public static void TopLeft(RectTransform rt, float x, float y, float w, float h)
        {
            GodotRect.Apply(rt, 0f, 0f, 0f, 0f, x, y, x + w, y + h, GodotRect.TopLeftPivot);
        }

        /// <summary>Top-right anchored: <paramref name="right"/> is the gap to the parent's right edge.</summary>
        public static void TopRight(RectTransform rt, float right, float y, float w, float h)
        {
            GodotRect.Apply(rt, 1f, 0f, 1f, 0f, -right - w, y, -right, y + h, GodotRect.TopLeftPivot);
        }

        /// <summary>Bottom-left anchored: <paramref name="bottom"/> is the gap to the parent's bottom edge.</summary>
        public static void BottomLeft(RectTransform rt, float x, float bottom, float w, float h)
        {
            GodotRect.Apply(rt, 0f, 1f, 0f, 1f, x, -bottom - h, x + w, -bottom, GodotRect.TopLeftPivot);
        }

        public static void BottomRight(RectTransform rt, float right, float bottom, float w, float h)
        {
            GodotRect.Apply(rt, 1f, 1f, 1f, 1f, -right - w, -bottom - h, -right, -bottom, GodotRect.TopLeftPivot);
        }

        /// <summary>Full width band at the bottom, <paramref name="height"/> tall.</summary>
        public static void BottomBand(RectTransform rt, float bottom, float height)
        {
            GodotRect.Apply(rt, 0f, 1f, 1f, 1f, 0f, -bottom - height, 0f, -bottom, GodotRect.TopLeftPivot);
        }

        public static void TopBand(RectTransform rt, float top, float height)
        {
            GodotRect.Apply(rt, 0f, 0f, 1f, 0f, 0f, top, 0f, top + height, GodotRect.TopLeftPivot);
        }

        public static void Centered(RectTransform rt, float x, float y, float w, float h)
        {
            GodotRect.Apply(rt, 0.5f, 0.5f, 0.5f, 0.5f, x - w * 0.5f, y - h * 0.5f, x + w * 0.5f, y + h * 0.5f,
                GodotRect.TopLeftPivot);
        }

        public static void Fill(RectTransform rt, float inset)
        {
            GodotRect.Apply(rt, 0f, 0f, 1f, 1f, inset, inset, -inset, -inset);
        }
    }

    /// <summary>Scales a control down a little while it is held, Modern style only.</summary>
    public sealed class PressFeedback : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        public float PressedScale = 0.96f;
        private float _target = 1f;

        public void OnPointerDown(PointerEventData eventData)
        {
            var selectable = GetComponent<Selectable>();
            if (selectable == null || selectable.interactable) { _target = PressedScale; }
        }

        public void OnPointerUp(PointerEventData eventData) { _target = 1f; }

        public void OnPointerExit(PointerEventData eventData) { _target = 1f; }

        private void OnDisable()
        {
            _target = 1f;
            transform.localScale = Vector3.one;
        }

        private void Update()
        {
            var s = transform.localScale.x;
            if (Mathf.Abs(s - _target) < 0.001f) { return; }
            s = Mathf.MoveTowards(s, _target, Time.unscaledDeltaTime * 1.2f);
            transform.localScale = new Vector3(s, s, 1f);
        }
    }

    /// <summary>Unscaled-time ease-out tweens for the Modern menus.</summary>
    public static class ModernTween
    {
        public static Coroutine Run(MonoBehaviour host, float duration, Action<float> step, Action done)
        {
            if (host == null || !host.isActiveAndEnabled)
            {
                if (step != null) { step(1f); }
                if (done != null) { done(); }
                return null;
            }

            return host.StartCoroutine(Routine(duration, step, done));
        }

        public static float EaseOut(float t)
        {
            t = Mathf.Clamp01(t);
            var inv = 1f - t;
            return 1f - inv * inv * inv;
        }

        private static IEnumerator Routine(float duration, Action<float> step, Action done)
        {
            var t = 0f;
            while (t < duration)
            {
                if (step != null) { step(EaseOut(t / duration)); }
                yield return null;
                // A long frame must not swallow the slide (MenuStyleRules.TweenStep).
                t += MenuStyleRules.TweenStep(Time.unscaledDeltaTime);
            }

            if (step != null) { step(1f); }
            if (done != null) { done(); }
        }
    }
}
