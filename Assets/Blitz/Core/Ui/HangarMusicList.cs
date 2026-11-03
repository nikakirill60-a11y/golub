using System;

namespace BlitzLegacy.Core.Ui
{
    /// <summary>
    /// The scrolling of the hangar music playlist: rows of one height under
    /// a viewport, which is scrolled by the distance from the list's top to
    /// its own, in canvas units. Only the rows the viewport shows are built,
    /// as a ring of <see cref="RingSize"/> rows that take turns.
    /// </summary>
    public static class HangarMusicList
    {
        /// <summary>
        /// How many rows a viewport shows any part of at most, and so how many
        /// a ring needs: one more than fit in it whole, for the row cut at the
        /// top and the one cut at the bottom.
        /// </summary>
        public static int RingSize(float rowHeight, float viewport)
        {
            return (int)Math.Ceiling(viewport / rowHeight) + 1;
        }

        /// <summary>The farthest the list scrolls: its last row at the viewport's bottom.</summary>
        public static float MaxOffset(int count, float rowHeight, float viewport)
        {
            return Math.Max(0f, (count * rowHeight) - viewport);
        }

        /// <summary>An offset held between the list's top and its end.</summary>
        public static float Clamp(float offset, int count, float rowHeight, float viewport)
        {
            if (float.IsNaN(offset))
            {
                return 0f;
            }

            return Math.Max(0f, Math.Min(MaxOffset(count, rowHeight, viewport), offset));
        }

        /// <summary>
        /// The first and the last row the viewport shows any part of; last is
        /// under first for an empty list.
        /// </summary>
        public static void Window(int count, float rowHeight, float viewport, float offset, out int first, out int last)
        {
            if (count <= 0 || rowHeight <= 0f)
            {
                first = 0;
                last = -1;
                return;
            }

            offset = Clamp(offset, count, rowHeight, viewport);
            first = Math.Min(count - 1, (int)Math.Floor(offset / rowHeight));
            last = Math.Max(first, Math.Min(count - 1, (int)Math.Ceiling((offset + viewport) / rowHeight) - 1));
        }

        /// <summary>The offset that puts row <paramref name="index"/> in the middle of the viewport, as far as the ends allow.</summary>
        public static float Centered(int index, int count, float rowHeight, float viewport)
        {
            return Clamp(((index + 0.5f) * rowHeight) - (viewport / 2f), count, rowHeight, viewport);
        }

        /// <summary>Whether the whole of row <paramref name="index"/> is in the viewport.</summary>
        public static bool Shows(int index, float rowHeight, float viewport, float offset)
        {
            var top = index * rowHeight;
            return top >= offset - 0.5f && top + rowHeight <= offset + viewport + 0.5f;
        }
    }
}
