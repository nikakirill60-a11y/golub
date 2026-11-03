using System.Collections.Generic;
using UnityEngine;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// Procedural chrome of the Modern style: anti-aliased rounded rects,
    /// circles, rings and gradients rendered once into small textures and
    /// handed out as 9-sliced sprites. Nothing here replaces an icon; the
    /// icons always come from the original atlas.
    /// </summary>
    public static class ModernSprites
    {
        private const float PixelsPerUnit = 100f;
        private static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>();

        /// <summary>Rounded rect with a vertical gradient fill and an inner stroke.</summary>
        public static Sprite RoundedRect(
            int radius, Color top, Color bottom, Color stroke, float strokeWidth)
        {
            var key = "rr" + radius + Key(top) + Key(bottom) + Key(stroke) + strokeWidth;
            Sprite cached;
            if (Cache.TryGetValue(key, out cached) && cached != null)
            {
                return cached;
            }

            var size = Mathf.Max(8, radius * 2 + 8);
            var texture = NewTexture(size, size, key);
            var pixels = new Color[size * size];
            for (var y = 0; y < size; y++)
            {
                // Texture rows grow upwards, the gradient runs top to bottom.
                var t = size <= 1 ? 0f : 1f - (y / (float)(size - 1));
                var fill = Color.Lerp(top, bottom, t);
                for (var x = 0; x < size; x++)
                {
                    var d = RoundedDistance(x + 0.5f, y + 0.5f, size, size, radius);
                    pixels[y * size + x] = Shade(d, fill, stroke, strokeWidth);
                }
            }

            texture.SetPixels(pixels);
            texture.Apply(false, true);
            var border = radius + 2f;
            return Store(key, Sprite.Create(
                texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
                PixelsPerUnit, 0, SpriteMeshType.FullRect,
                new Vector4(border, border, border, border)));
        }

        public static Sprite RoundedRect(int radius, Color fill)
        {
            return RoundedRect(radius, fill, fill, Color.clear, 0f);
        }

        /// <summary>A filled disc with an optional stroke, <paramref name="size"/> pixels wide.</summary>
        public static Sprite Circle(int size, Color fill, Color stroke, float strokeWidth)
        {
            var key = "c" + size + Key(fill) + Key(stroke) + strokeWidth;
            Sprite cached;
            if (Cache.TryGetValue(key, out cached) && cached != null)
            {
                return cached;
            }

            var texture = NewTexture(size, size, key);
            var pixels = new Color[size * size];
            var r = size * 0.5f;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = x + 0.5f - r;
                    var dy = y + 0.5f - r;
                    var d = Mathf.Sqrt(dx * dx + dy * dy) - (r - 1f);
                    pixels[y * size + x] = Shade(d, fill, stroke, strokeWidth);
                }
            }

            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return Store(key, Sprite.Create(
                texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), PixelsPerUnit));
        }

        /// <summary>
        /// A small disc centred in a transparent <paramref name="size"/> square,
        /// so it can replace a padded grabber without changing its layout.
        /// </summary>
        public static Sprite Dot(int size, float diameter, Color fill, Color stroke, float strokeWidth)
        {
            var key = "dot" + size + "_" + diameter + Key(fill) + Key(stroke) + strokeWidth;
            Sprite cached;
            if (Cache.TryGetValue(key, out cached) && cached != null)
            {
                return cached;
            }

            var texture = NewTexture(size, size, key);
            var pixels = new Color[size * size];
            var c = size * 0.5f;
            var r = diameter * 0.5f;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = x + 0.5f - c;
                    var dy = y + 0.5f - c;
                    var d = Mathf.Sqrt(dx * dx + dy * dy) - (r - 1f);
                    pixels[y * size + x] = Shade(d, fill, stroke, strokeWidth);
                }
            }

            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return Store(key, Sprite.Create(
                texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), PixelsPerUnit));
        }

        /// <summary>A ring for radial fills (queue timer, progress).</summary>
        public static Sprite Ring(int size, float thickness, Color color)
        {
            var key = "ring" + size + "_" + thickness + Key(color);
            Sprite cached;
            if (Cache.TryGetValue(key, out cached) && cached != null)
            {
                return cached;
            }

            var texture = NewTexture(size, size, key);
            var pixels = new Color[size * size];
            var r = size * 0.5f;
            var outer = r - 1f;
            var inner = outer - thickness;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = x + 0.5f - r;
                    var dy = y + 0.5f - r;
                    var dist = Mathf.Sqrt(dx * dx + dy * dy);
                    var a = Mathf.Clamp01(outer - dist + 0.5f) * Mathf.Clamp01(dist - inner + 0.5f);
                    var c = color;
                    c.a *= a;
                    pixels[y * size + x] = c;
                }
            }

            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return Store(key, Sprite.Create(
                texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), PixelsPerUnit));
        }

        /// <summary>A vertical gradient strip, stretched by a Simple image.</summary>
        public static Sprite VerticalGradient(Color top, Color bottom)
        {
            var key = "vg" + Key(top) + Key(bottom);
            Sprite cached;
            if (Cache.TryGetValue(key, out cached) && cached != null)
            {
                return cached;
            }

            const int height = 64;
            var texture = NewTexture(2, height, key);
            var pixels = new Color[2 * height];
            for (var y = 0; y < height; y++)
            {
                var c = Color.Lerp(bottom, top, y / (float)(height - 1));
                pixels[y * 2] = c;
                pixels[y * 2 + 1] = c;
            }

            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return Store(key, Sprite.Create(
                texture, new Rect(0, 0, 2, height), new Vector2(0.5f, 0.5f), PixelsPerUnit));
        }

        /// <summary>A horizontal gradient strip, e.g. a fading hairline.</summary>
        public static Sprite HorizontalGradient(Color left, Color right)
        {
            var key = "hg" + Key(left) + Key(right);
            Sprite cached;
            if (Cache.TryGetValue(key, out cached) && cached != null)
            {
                return cached;
            }

            const int width = 64;
            var texture = NewTexture(width, 2, key);
            var pixels = new Color[width * 2];
            for (var x = 0; x < width; x++)
            {
                var c = Color.Lerp(left, right, x / (float)(width - 1));
                pixels[x] = c;
                pixels[width + x] = c;
            }

            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return Store(key, Sprite.Create(
                texture, new Rect(0, 0, width, 2), new Vector2(0.5f, 0.5f), PixelsPerUnit));
        }

        /// <summary>A rounded check box, filled with the accent and a check mark when on.</summary>
        public static Sprite CheckBox(bool on)
        {
            var key = "check" + on;
            Sprite cached;
            if (Cache.TryGetValue(key, out cached) && cached != null)
            {
                return cached;
            }

            const int size = 48;
            var texture = NewTexture(size, size, key);
            var pixels = new Color[size * size];
            var fill = on ? ModernTheme.Accent : new Color(0.1f, 0.115f, 0.14f, 0.95f);
            var stroke = on ? new Color(1f, 0.85f, 0.5f, 1f) : ModernTheme.StrokeStrong;
            var mark = new Color(0.08f, 0.07f, 0.04f, 1f);
            var a = new Vector2(12f, 25f);
            var b = new Vector2(21f, 15f);
            var c = new Vector2(36f, 33f);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var d = RoundedDistance(x + 0.5f, y + 0.5f, size, size, 10f);
                    var color = Shade(d, fill, stroke, 2f);
                    if (on)
                    {
                        var p = new Vector2(x + 0.5f, y + 0.5f);
                        var line = Mathf.Min(SegmentDistance(p, a, b), SegmentDistance(p, b, c));
                        var m = Mathf.Clamp01(3.2f - line);
                        color = Color.Lerp(color, mark, m * color.a);
                    }

                    pixels[y * size + x] = color;
                }
            }

            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return Store(key, Sprite.Create(
                texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), PixelsPerUnit));
        }

        /// <summary>A small chevron pointing up (or down), for the carousel handle.</summary>
        public static Sprite Chevron(bool up, Color color)
        {
            var key = "chev" + up + Key(color);
            Sprite cached;
            if (Cache.TryGetValue(key, out cached) && cached != null)
            {
                return cached;
            }

            const int width = 32;
            const int height = 20;
            var texture = NewTexture(width, height, key);
            var pixels = new Color[width * height];
            var tip = new Vector2(16f, up ? 15f : 5f);
            var left = new Vector2(5f, up ? 5f : 15f);
            var right = new Vector2(27f, up ? 5f : 15f);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    var line = Mathf.Min(SegmentDistance(p, left, tip), SegmentDistance(p, tip, right));
                    var c = color;
                    c.a *= Mathf.Clamp01(2.4f - line);
                    pixels[y * width + x] = c;
                }
            }

            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return Store(key, Sprite.Create(
                texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), PixelsPerUnit));
        }

        /// <summary>Signed distance to a rounded rect of the given pixel size.</summary>
        public static float RoundedDistance(float x, float y, float width, float height, float radius)
        {
            radius = Mathf.Min(radius, Mathf.Min(width, height) * 0.5f - 0.5f);
            var cx = Mathf.Abs(x - width * 0.5f) - (width * 0.5f - radius - 0.5f);
            var cy = Mathf.Abs(y - height * 0.5f) - (height * 0.5f - radius - 0.5f);
            var ox = Mathf.Max(cx, 0f);
            var oy = Mathf.Max(cy, 0f);
            return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(cx, cy), 0f) - radius;
        }

        private static Color Shade(float distance, Color fill, Color stroke, float strokeWidth)
        {
            var coverage = Mathf.Clamp01(0.5f - distance);
            if (coverage <= 0f)
            {
                return new Color(fill.r, fill.g, fill.b, 0f);
            }

            var color = fill;
            if (strokeWidth > 0f && stroke.a > 0f)
            {
                // The stroke sits on the inside of the outline.
                var s = Mathf.Clamp01(distance + strokeWidth + 0.5f);
                var blended = Color.Lerp(fill, new Color(stroke.r, stroke.g, stroke.b, 1f), stroke.a * s);
                blended.a = Mathf.Lerp(fill.a, Mathf.Max(fill.a, stroke.a), s);
                color = blended;
            }

            color.a *= coverage;
            return color;
        }

        private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(0.0001f, ab.sqrMagnitude));
            return Vector2.Distance(p, a + ab * t);
        }

        private static Texture2D NewTexture(int width, int height, string name)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.name = "modern_" + name;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.hideFlags = HideFlags.DontSave;
            return texture;
        }

        private static Sprite Store(string key, Sprite sprite)
        {
            sprite.name = "modern_" + key;
            sprite.hideFlags = HideFlags.DontSave;
            Cache[key] = sprite;
            return sprite;
        }

        private static string Key(Color c)
        {
            var c32 = (Color32)c;
            return "_" + c32.r + "." + c32.g + "." + c32.b + "." + c32.a;
        }
    }
}
