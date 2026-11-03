using BlitzLegacy.Runtime.Assets;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// A strip across the top of the battle saying what the connection is
    /// doing ("reconnecting...", "host left, switching host..."). It never
    /// takes input, so the battle stays playable underneath it. Texts are
    /// localization keys.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NetStatusBanner : MonoBehaviour
    {
        private static NetStatusBanner _current;
        private Text _label;

        public static string CurrentText
        {
            get { return _current != null && _current._label != null ? _current._label.text : null; }
        }

        public static void Show(string key)
        {
            if (_current == null)
            {
                var go = new GameObject("NetStatusBanner");
                DontDestroyOnLoad(go);
                _current = go.AddComponent<NetStatusBanner>();
                _current.Build();
            }
            var lang = BattleFlowUi.LoadLanguage();
            _current._label.text = lang != null ? lang.Get(key) : key;
        }

        public static void Hide()
        {
            if (_current != null) { Destroy(_current.gameObject); }
            _current = null;
        }

        private void Build()
        {
            var canvas = BattleFlowUi.CreateCanvas(transform, false, false, 450);
            var raycaster = canvas.GetComponent<GraphicRaycaster>();
            if (raycaster != null) { raycaster.enabled = false; }
            var strip = BattleFlowUi.Solid("strip", canvas, BattleFlowUi.Panel);
            strip.raycastTarget = false;
            GodotRect.Apply(strip.rectTransform, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 44f);
            var bold = BlitzResources.LoadFont(BlitzResources.BoldFontName);
            _label = UiFactory.Label("text", strip.transform, bold, 22, BattleFlowUi.Yellow,
                TextAnchor.MiddleCenter, string.Empty);
            _label.raycastTarget = false;
            GodotRect.FullRect(_label.rectTransform);
            UiFactory.AddShadow(_label, new Color(0f, 0f, 0f, 0.75f));
        }

        private void OnDestroy()
        {
            if (_current == this) { _current = null; }
        }
    }
}
