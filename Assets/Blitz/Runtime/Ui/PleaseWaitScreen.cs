using BlitzLegacy.Runtime.Assets;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// maps/please_wait.tscn and scripts/ui_global.gd::please_wait_toggle: a
    /// full-rect black ColorRect with one centred LabelBold at font size 23.
    /// The scene contains no spinner.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PleaseWaitScreen : MonoBehaviour
    {
        public const int CaptionFontSize = 23;

        public RectTransform Root { get; private set; }
        public Text Caption { get; private set; }

        public static PleaseWaitScreen Create(
            Transform parent, bool battleScale, bool mobile, int sortingOrder = 500)
        {
            var go = new GameObject("PleaseWaitScreen");
            go.transform.SetParent(parent, false);
            var screen = go.AddComponent<PleaseWaitScreen>();
            screen.Build(battleScale, mobile, sortingOrder);
            return screen;
        }

        private void Build(bool battleScale, bool mobile, int sortingOrder)
        {
            var lang = BattleFlowUi.LoadLanguage();
            var bold = BlitzResources.LoadFont(BlitzResources.BoldFontName);
            var canvas = BattleFlowUi.CreateCanvas(
                transform, battleScale, mobile, sortingOrder);
            Root = UiFactory.Node("please_wait", canvas);
            GodotRect.FullRect(Root);

            var black = BattleFlowUi.Solid("ColorRect", Root, Color.black);
            GodotRect.FullRect(black.rectTransform);

            Caption = UiFactory.Label("Label", Root, bold, CaptionFontSize,
                Color.white, TextAnchor.MiddleCenter, lang.Get("PLEASE_WAIT"));
            GodotRect.Centered(Caption.rectTransform, -20f, -8f, 20f, 8f);
        }
    }
}
