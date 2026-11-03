using BlitzLegacy.Core.Ui;
using UnityEngine;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>Applies Godot Control geometry to a RectTransform.</summary>
    public static class GodotRect
    {
        private static readonly Vector2 CenterPivot = new Vector2(0.5f, 0.5f);

        /// <summary>Godot's default pivot_offset is the node's top-left corner.</summary>
        public static readonly Vector2 TopLeftPivot = new Vector2(0f, 1f);

        public static void Apply(
            RectTransform rt,
            float anchorLeft,
            float anchorTop,
            float anchorRight,
            float anchorBottom,
            float offsetLeft,
            float offsetTop,
            float offsetRight,
            float offsetBottom)
        {
            Apply(rt, anchorLeft, anchorTop, anchorRight, anchorBottom,
                offsetLeft, offsetTop, offsetRight, offsetBottom, CenterPivot);
        }

        public static void Apply(
            RectTransform rt,
            float anchorLeft,
            float anchorTop,
            float anchorRight,
            float anchorBottom,
            float offsetLeft,
            float offsetTop,
            float offsetRight,
            float offsetBottom,
            Vector2 pivot)
        {
            // Pivot first: changing it afterwards would move the rect, because
            // Unity stores the position relative to the pivot.
            rt.pivot = pivot;

            var a = GodotAnchors.Convert(
                anchorLeft, anchorTop, anchorRight, anchorBottom,
                offsetLeft, offsetTop, offsetRight, offsetBottom);

            rt.anchorMin = new Vector2(a.AnchorMinX, a.AnchorMinY);
            rt.anchorMax = new Vector2(a.AnchorMaxX, a.AnchorMaxY);
            rt.offsetMin = new Vector2(a.OffsetMinX, a.OffsetMinY);
            rt.offsetMax = new Vector2(a.OffsetMaxX, a.OffsetMaxY);
        }

        /// <summary>Godot anchors_preset 8.</summary>
        public static void Centered(
            RectTransform rt, float left, float top, float right, float bottom)
        {
            Apply(rt, 0.5f, 0.5f, 0.5f, 0.5f, left, top, right, bottom);
        }

        /// <summary>Godot anchors_preset 5.</summary>
        public static void CenterTop(
            RectTransform rt, float left, float top, float right, float bottom)
        {
            Apply(rt, 0.5f, 0f, 0.5f, 0f, left, top, right, bottom);
        }

        /// <summary>Godot anchors_preset 15.</summary>
        public static void FullRect(RectTransform rt)
        {
            Apply(rt, 0f, 0f, 1f, 1f, 0f, 0f, 0f, 0f);
        }

        /// <summary>Top-left anchored, which is Godot's default for a bare Control.</summary>
        public static void TopLeft(
            RectTransform rt, float left, float top, float right, float bottom)
        {
            Apply(rt, 0f, 0f, 0f, 0f, left, top, right, bottom, TopLeftPivot);
        }
    }
}
