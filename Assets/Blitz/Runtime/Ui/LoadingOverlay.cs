using BlitzLegacy.Runtime.Assets;
using UnityEngine;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// maps/loading_screen.tscn and scripts/ui_global.gd::loading_screen_toggle.
    /// The scene is exactly a full-rect black ColorRect plus the "bobber1"
    /// TextureRect: a 40x40 centred control holding the 50x51 wheel at its
    /// native size (stretch_mode KEEP_CENTERED) and scaled by 0.515.  The
    /// source scene has no caption, no animation player and no rotating
    /// script, so nothing here spins or writes text.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LoadingOverlay : MonoBehaviour
    {
        /// <summary>bobber1.scale in maps/loading_screen.tscn.</summary>
        public const float SpinnerScale = 0.515f;

        public UnityEngine.UI.Image Spinner { get; private set; }
        public RectTransform SpinnerHolder { get; private set; }
        public RectTransform Root { get; private set; }

        public static LoadingOverlay Create(
            Transform parent, bool battleScale, bool mobile, int sortingOrder = 500)
        {
            var go = new GameObject("LoadingOverlay");
            go.transform.SetParent(parent, false);
            var overlay = go.AddComponent<LoadingOverlay>();
            overlay.Build(battleScale, mobile, sortingOrder);
            return overlay;
        }

        private void Build(bool battleScale, bool mobile, int sortingOrder)
        {
            var atlas = BattleFlowUi.LoadAtlas();
            var canvas = BattleFlowUi.CreateCanvas(
                transform, battleScale, mobile, sortingOrder);
            Root = UiFactory.Node("loading", canvas);
            GodotRect.FullRect(Root);

            var black = BattleFlowUi.Solid("ColorRect", Root, Color.black);
            GodotRect.FullRect(black.rectTransform);

            SpinnerHolder = UiFactory.Node("bobber1", Root);
            GodotRect.Centered(SpinnerHolder, -20f, -20f, 20f, 20f);
            SpinnerHolder.localScale = new Vector3(SpinnerScale, SpinnerScale, 1f);
            Spinner = UiFactory.KeepCentered(
                SpinnerHolder, atlas.Get("flow_loading_spinner"));
        }
    }
}
