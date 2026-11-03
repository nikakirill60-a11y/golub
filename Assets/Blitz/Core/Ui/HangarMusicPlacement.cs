using System;

namespace BlitzLegacy.Core.Ui
{
    /// <summary>
    /// Where the hangar music panel may stand, after the Panzer War Miku
    /// player: its right and top edges are measured in from the canvas's
    /// right and top, in canvas units, and the whole panel stays inside the
    /// screen's safe area (cutouts, rounded corners) by <see cref="Margin"/>.
    /// Measured from the top, a panel that folds, unfolds or changes its view
    /// keeps its top edge, and only rises when its bottom would leave the area.
    /// </summary>
    public static class HangarMusicPlacement
    {
        /// <summary>The gap the panel keeps to the safe area's edges.</summary>
        public const float Margin = 8f;

        /// <summary>
        /// Holds a panel of <paramref name="width"/> by <paramref name="height"/>
        /// standing <paramref name="right"/> and <paramref name="top"/> in
        /// from the corner of a <paramref name="canvasWidth"/> by
        /// <paramref name="canvasHeight"/> canvas inside its safe area, which
        /// lies the given insets in from each canvas edge. A panel larger than
        /// the area keeps its top right corner in it.
        /// </summary>
        public static void Clamp(
            float canvasWidth, float canvasHeight,
            float insetLeft, float insetTop, float insetRight, float insetBottom,
            float width, float height, ref float right, ref float top)
        {
            right = Hold(right, insetRight + Margin, canvasWidth - insetLeft - Margin - width);
            top = Hold(top, insetTop + Margin, canvasHeight - insetBottom - Margin - height);
        }

        /// <summary>
        /// The safe area's insets from the canvas edges, in canvas units, for a
        /// safe area of the screen given in pixels with y up, as Unity's
        /// Screen.safeArea reports it. An empty screen has no insets.
        /// </summary>
        public static void Insets(
            float screenWidth, float screenHeight,
            float safeX, float safeY, float safeWidth, float safeHeight,
            float canvasWidth, float canvasHeight,
            out float left, out float top, out float right, out float bottom)
        {
            left = top = right = bottom = 0f;
            if (screenWidth <= 0f || screenHeight <= 0f)
            {
                return;
            }

            var x = canvasWidth / screenWidth;
            var y = canvasHeight / screenHeight;
            left = Math.Max(0f, safeX) * x;
            right = Math.Max(0f, screenWidth - (safeX + safeWidth)) * x;
            bottom = Math.Max(0f, safeY) * y;
            top = Math.Max(0f, screenHeight - (safeY + safeHeight)) * y;
        }

        private static float Hold(float value, float min, float max)
        {
            if (float.IsNaN(value))
            {
                value = min;
            }

            // The near edge wins when the panel is larger than the room.
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
