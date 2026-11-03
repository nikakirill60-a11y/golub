using System;
using UnityEngine;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// The icons and frames of the hangar music panel, drawn at runtime the
    /// way the Panzer War Miku player draws its own: white shapes whose alpha
    /// is their supersampled coverage, tinted by the Image that shows them.
    /// The 4.8 atlas has no transport icons, and a dozen small textures drawn
    /// once cost less than files in the build. Coordinates are texture pixels
    /// with y up, as in the source.
    /// </summary>
    public sealed class HangarMusicSprites
    {
        /// <summary>The frame line of the panel, in canvas units.</summary>
        public const float FrameWidth = 2f;

        /// <summary>The corner brackets of the panel.</summary>
        public const float BracketWidth = 3f;
        public const float BracketLength = 14f;

        private const int IconSize = 64;
        private const int Supersampling = 4;

        private static HangarMusicSprites _shared;

        private HangarMusicSprites()
        {
            // 9-sliced: a square frame line, and brackets in the four corners.
            Frame = Draw("Frame", 16, 4, false, (x, y) =>
                Math.Min(Math.Min(x, 16f - x), Math.Min(y, 16f - y)) < FrameWidth);
            Corners = Draw("Corners", 64, 20, false, (x, y) =>
            {
                var u = Math.Min(x, 64f - x);
                var v = Math.Min(y, 64f - y);
                return (u < BracketWidth && v < BracketLength) || (v < BracketWidth && u < BracketLength);
            });

            Play = Icon("Play", (x, y) => Triangle(x, y, 22f, 14f, 22f, 50f, 51f, 32f));
            Pause = Icon("Pause", (x, y) => Rect(x, y, 17f, 14f, 27f, 50f) || Rect(x, y, 37f, 14f, 47f, 50f));
            Previous = Icon("Previous", (x, y) =>
                Rect(x, y, 13f, 16f, 19f, 48f) || Triangle(x, y, 50f, 16f, 50f, 48f, 21f, 32f));
            Next = Icon("Next", (x, y) =>
                Rect(x, y, 45f, 16f, 51f, 48f) || Triangle(x, y, 14f, 16f, 14f, 48f, 43f, 32f));

            // Spotify's mode icons in the hangar's straight lines: two crossing
            // arrows, and a loop of two arrows with a 1 inside for repeat one.
            Shuffle = Icon("Shuffle", ShuffleShape);
            Repeat = Icon("Repeat", RepeatLoop);
            RepeatOne = Icon("RepeatOne", (x, y) => RepeatLoop(x, y) || DigitOne(x, y));

            ChevronUp = Icon("ChevronUp", (x, y) =>
                Capsule(x, y, 16f, 25f, 32f, 41f, 3.5f) || Capsule(x, y, 32f, 41f, 48f, 25f, 3.5f));
            ChevronDown = Icon("ChevronDown", (x, y) =>
                Capsule(x, y, 16f, 39f, 32f, 23f, 3.5f) || Capsule(x, y, 32f, 23f, 48f, 39f, 3.5f));
            Volume = Icon("Volume", (x, y) =>
                RoundRect(x, y, 8f, 24f, 19f, 40f, 1.5f) || Quad(x, y, 18f, 24f, 32f, 12f, 32f, 52f, 18f, 40f) ||
                Arc(x, y, 32f, 32f, 11f, 4f, 50f) || Arc(x, y, 32f, 32f, 20f, 4f, 50f));

            // The header's handle: a cross with an arrowhead on each arm.
            Move = Icon("Move", (x, y) =>
                Capsule(x, y, 14f, 32f, 50f, 32f, 2.5f) || Capsule(x, y, 32f, 14f, 32f, 50f, 2.5f) ||
                Triangle(x, y, 32f, 61f, 22f, 49f, 42f, 49f) || Triangle(x, y, 32f, 3f, 22f, 15f, 42f, 15f) ||
                Triangle(x, y, 3f, 32f, 15f, 22f, 15f, 42f) || Triangle(x, y, 61f, 32f, 49f, 22f, 49f, 42f));

            // The header's view buttons and the equalizer's switch: a list of
            // three rows, three faders, and the power mark.
            List = Icon("List", (x, y) =>
                Circle(x, y, 10f, 47f, 4f) || Capsule(x, y, 22f, 47f, 54f, 47f, 3f) ||
                Circle(x, y, 10f, 32f, 4f) || Capsule(x, y, 22f, 32f, 54f, 32f, 3f) ||
                Circle(x, y, 10f, 17f, 4f) || Capsule(x, y, 22f, 17f, 54f, 17f, 3f));
            Equalizer = Icon("Equalizer", (x, y) =>
                Capsule(x, y, 16f, 10f, 16f, 54f, 2.25f) || Circle(x, y, 16f, 22f, 6.5f) ||
                Capsule(x, y, 32f, 10f, 32f, 54f, 2.25f) || Circle(x, y, 32f, 42f, 6.5f) ||
                Capsule(x, y, 48f, 10f, 48f, 54f, 2.25f) || Circle(x, y, 48f, 30f, 6.5f));
            Power = Icon("Power", (x, y) =>
                OpenRing(x, y, 32f, 29f, 19f, 6f, 38f) || Capsule(x, y, 32f, 30f, 32f, 56f, 3.25f));
        }

        /// <summary>The set, drawn on first use and kept for the session.</summary>
        public static HangarMusicSprites Shared
        {
            get
            {
                // A sprite of the editor's last play session is gone by now.
                if (_shared == null || _shared.Play == null)
                {
                    _shared = new HangarMusicSprites();
                }

                return _shared;
            }
        }

        public Sprite Frame { get; private set; }
        public Sprite Corners { get; private set; }
        public Sprite Play { get; private set; }
        public Sprite Pause { get; private set; }
        public Sprite Previous { get; private set; }
        public Sprite Next { get; private set; }
        public Sprite Shuffle { get; private set; }
        public Sprite Repeat { get; private set; }
        public Sprite RepeatOne { get; private set; }
        public Sprite ChevronUp { get; private set; }
        public Sprite ChevronDown { get; private set; }
        public Sprite Volume { get; private set; }
        public Sprite Move { get; private set; }
        public Sprite List { get; private set; }
        public Sprite Equalizer { get; private set; }
        public Sprite Power { get; private set; }

        private static Sprite Icon(string name, Func<float, float, bool> shape)
        {
            // Icons are drawn a size or two down, so they get mipmaps.
            return Draw(name, IconSize, 0, true, shape);
        }

        private static Sprite Draw(string name, int size, int border, bool mipmaps, Func<float, float, bool> shape)
        {
            const int samples = Supersampling * Supersampling;
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var hits = 0;
                    for (var j = 0; j < Supersampling; j++)
                    {
                        for (var i = 0; i < Supersampling; i++)
                        {
                            if (shape(x + ((i + 0.5f) / Supersampling), y + ((j + 0.5f) / Supersampling)))
                            {
                                hits++;
                            }
                        }
                    }

                    pixels[(y * size) + x] = new Color32(255, 255, 255, (byte)(((hits * 255) + (samples / 2)) / samples));
                }
            }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, mipmaps, false);
            texture.name = "HangarMusic" + name;
            texture.hideFlags = HideFlags.DontSave;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = mipmaps ? FilterMode.Trilinear : FilterMode.Bilinear;
            texture.SetPixels32(pixels);
            texture.Apply(mipmaps, true);
            var sprite = Sprite.Create(
                texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(border, border, border, border));
            sprite.name = texture.name;
            sprite.hideFlags = HideFlags.DontSave;
            return sprite;
        }

        private static bool ShuffleShape(float x, float y)
        {
            const float r = 3f;
            var rising = Capsule(x, y, 7f, 16f, 19f, 16f, r) || Capsule(x, y, 19f, 16f, 39f, 48f, r) ||
                         Capsule(x, y, 39f, 48f, 47f, 48f, r) || Triangle(x, y, 47f, 56f, 47f, 40f, 58f, 48f);
            // The falling arrow breaks where it passes under the rising one.
            var falling = Capsule(x, y, 7f, 48f, 19f, 48f, r) || Capsule(x, y, 39f, 16f, 47f, 16f, r) ||
                          Triangle(x, y, 47f, 24f, 47f, 8f, 58f, 16f) ||
                          (Capsule(x, y, 19f, 48f, 39f, 16f, r) && !Capsule(x, y, 19f, 16f, 39f, 48f, r + 3.5f));
            return rising || falling;
        }

        // Top arrow to the right, bottom arrow to the left, square corners.
        private static bool RepeatLoop(float x, float y)
        {
            return Rect(x, y, 9f, 45f, 48f, 51f) || Rect(x, y, 9f, 28f, 15f, 51f) ||
                   Triangle(x, y, 47f, 56f, 47f, 40f, 58f, 48f) ||
                   Rect(x, y, 16f, 13f, 55f, 19f) || Rect(x, y, 49f, 13f, 55f, 36f) ||
                   Triangle(x, y, 17f, 24f, 17f, 8f, 6f, 16f);
        }

        private static bool DigitOne(float x, float y)
        {
            return Rect(x, y, 31.5f, 24.5f, 36.5f, 39.5f) ||
                   Quad(x, y, 31.5f, 39.5f, 27.5f, 35.5f, 27.5f, 31f, 31.5f, 35f);
        }

        private static bool Rect(float x, float y, float x0, float y0, float x1, float y1)
        {
            return x >= x0 && x <= x1 && y >= y0 && y <= y1;
        }

        private static bool RoundRect(float x, float y, float x0, float y0, float x1, float y1, float r)
        {
            if (x < x0 || x > x1 || y < y0 || y > y1)
            {
                return false;
            }

            var cx = Mathf.Clamp(x, x0 + r, x1 - r);
            var cy = Mathf.Clamp(y, y0 + r, y1 - r);
            var dx = x - cx;
            var dy = y - cy;
            return (dx * dx) + (dy * dy) <= r * r;
        }

        private static float Cross(float ax, float ay, float bx, float by, float px, float py)
        {
            return ((bx - ax) * (py - ay)) - ((by - ay) * (px - ax));
        }

        private static bool Triangle(float x, float y, float ax, float ay, float bx, float by, float cx, float cy)
        {
            var d1 = Cross(ax, ay, bx, by, x, y);
            var d2 = Cross(bx, by, cx, cy, x, y);
            var d3 = Cross(cx, cy, ax, ay, x, y);
            var negative = d1 < 0f || d2 < 0f || d3 < 0f;
            var positive = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(negative && positive);
        }

        // Convex quadrilateral a-b-c-d.
        private static bool Quad(
            float x, float y, float ax, float ay, float bx, float by, float cx, float cy, float dx, float dy)
        {
            return Triangle(x, y, ax, ay, bx, by, cx, cy) || Triangle(x, y, ax, ay, cx, cy, dx, dy);
        }

        private static bool Capsule(float x, float y, float ax, float ay, float bx, float by, float r)
        {
            var vx = bx - ax;
            var vy = by - ay;
            var t = Mathf.Clamp01((((x - ax) * vx) + ((y - ay) * vy)) / ((vx * vx) + (vy * vy)));
            var dx = x - (ax + (t * vx));
            var dy = y - (ay + (t * vy));
            return (dx * dx) + (dy * dy) <= r * r;
        }

        private static bool Circle(float x, float y, float cx, float cy, float r)
        {
            var dx = x - cx;
            var dy = y - cy;
            return (dx * dx) + (dy * dy) <= r * r;
        }

        // A ring with a gap of gapDegrees either side of straight up.
        private static bool OpenRing(float x, float y, float cx, float cy, float radius, float width, float gapDegrees)
        {
            var dx = x - cx;
            var dy = y - cy;
            var d = Mathf.Sqrt((dx * dx) + (dy * dy));
            return Mathf.Abs(d - radius) <= width / 2f && Mathf.Abs(Mathf.Atan2(dx, dy) * Mathf.Rad2Deg) >= gapDegrees;
        }

        // Arc of a ring opening to the right, within +-maxAngle degrees.
        private static bool Arc(float x, float y, float cx, float cy, float radius, float width, float maxAngle)
        {
            var dx = x - cx;
            var dy = y - cy;
            var d = Mathf.Sqrt((dx * dx) + (dy * dy));
            return dx > 0f && Mathf.Abs(d - radius) <= width / 2f &&
                   Mathf.Abs(Mathf.Atan2(dy, dx) * Mathf.Rad2Deg) <= maxAngle;
        }
    }
}
