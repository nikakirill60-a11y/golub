namespace BlitzLegacy.Core.Ui
{
    /// <summary>RectTransform values converted from a Godot Control.</summary>
    public struct RectAnchors
    {
        public float AnchorMinX;
        public float AnchorMinY;
        public float AnchorMaxX;
        public float AnchorMaxY;
        public float OffsetMinX;
        public float OffsetMinY;
        public float OffsetMaxX;
        public float OffsetMaxY;
    }

    /// <summary>
    /// Godot anchors/offsets -> Unity RectTransform anchors/offsets.
    ///
    /// Godot measures every edge from the parent's top-left with +Y pointing
    /// down; Unity measures from the bottom-left with +Y pointing up. Only the
    /// vertical axis changes, and anchors outside 0..1 survive the conversion,
    /// which the login background relies on (it uses anchor_right = 1.709).
    /// </summary>
    public static class GodotAnchors
    {
        public static RectAnchors Convert(
            float anchorLeft,
            float anchorTop,
            float anchorRight,
            float anchorBottom,
            float offsetLeft,
            float offsetTop,
            float offsetRight,
            float offsetBottom)
        {
            var result = new RectAnchors();
            result.AnchorMinX = anchorLeft;
            result.AnchorMaxX = anchorRight;
            result.AnchorMinY = 1f - anchorBottom;
            result.AnchorMaxY = 1f - anchorTop;
            result.OffsetMinX = offsetLeft;
            result.OffsetMaxX = offsetRight;
            result.OffsetMinY = -offsetBottom;
            result.OffsetMaxY = -offsetTop;
            return result;
        }

        /// <summary>Godot anchors_preset 15: full rect.</summary>
        public static RectAnchors FullRect()
        {
            return Convert(0f, 0f, 1f, 1f, 0f, 0f, 0f, 0f);
        }

        /// <summary>Godot anchors_preset 8: centered.</summary>
        public static RectAnchors Centered(
            float offsetLeft, float offsetTop, float offsetRight, float offsetBottom)
        {
            return Convert(0.5f, 0.5f, 0.5f, 0.5f,
                offsetLeft, offsetTop, offsetRight, offsetBottom);
        }
    }
}
