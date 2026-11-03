using BlitzLegacy.Runtime.Assets;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// First-launch screen: black backdrop and the orange bar from the
    /// source's lobby atlas (ProgressBar / ProgressBar02, 380x16). The fill
    /// eases toward the reported value and a soft highlight sweeps along it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShaderWarmupScreen : MonoBehaviour
    {
        public const float BarWidth = 380f;
        public const float BarHeight = 16f;
        /// <summary>
        /// A third of the 1.6 the bar first shipped at. At full scale the
        /// 380 px source bar was drawn 608 px wide, more than half the logical
        /// canvas; the client's own loading bar is a short strip under the
        /// caption, not a band across the screen.
        /// </summary>
        public const float BarScale = 1.6f / 3f;
        public const float FillSpeed = 0.7f;
        private const float SweepWidth = 48f;
        private const float SweepPeriod = 1.3f;

        private RectTransform _clip;
        private RectTransform _sweep;
        private Text _label;
        private float _target;
        private float _shown;

        public float Shown { get { return _shown; } }
        public bool Filled { get { return _shown >= 0.999f; } }

        public static ShaderWarmupScreen Create(Transform parent, bool mobile)
        {
            var go = new GameObject("ShaderWarmupScreen");
            go.transform.SetParent(parent, false);
            var screen = go.AddComponent<ShaderWarmupScreen>();
            screen.Build(mobile);
            return screen;
        }

        public void SetProgress(float value)
        {
            _target = Mathf.Clamp01(value);
        }

        public void Close()
        {
            Destroy(gameObject);
        }

        private void Build(bool mobile)
        {
            var atlas = BattleFlowUi.LoadAtlas();
            var canvas = BattleFlowUi.CreateCanvas(transform, false, mobile, 900);
            var root = UiFactory.Node("warmup", canvas);
            GodotRect.FullRect(root);
            var black = BattleFlowUi.Solid("ColorRect", root, Color.black);
            GodotRect.FullRect(black.rectTransform);

            var bar = UiFactory.Node("bar", root);
            GodotRect.Centered(bar, -BarWidth / 2f, -BarHeight / 2f, BarWidth / 2f, BarHeight / 2f);
            bar.localScale = new Vector3(BarScale, BarScale, 1f);

            var under = UiFactory.Picture("under", bar, atlas.Get("loading_bar_under"));
            under.type = Image.Type.Sliced;
            GodotRect.FullRect(under.rectTransform);

            _clip = UiFactory.Node("clip", bar);
            GodotRect.FullRect(_clip);
            _clip.gameObject.AddComponent<RectMask2D>();

            var fill = UiFactory.Picture("fill", _clip, atlas.Get("loading_bar_fill"));
            fill.type = Image.Type.Sliced;
            var fillRect = fill.rectTransform;
            fillRect.anchorMin = new Vector2(0f, 0f);
            fillRect.anchorMax = new Vector2(0f, 1f);
            fillRect.pivot = new Vector2(0f, 0.5f);
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = new Vector2(BarWidth, 0f);

            var sweep = BattleFlowUi.Solid("sweep", _clip, new Color(1f, 1f, 1f, 0.28f));
            _sweep = sweep.rectTransform;
            _sweep.anchorMin = new Vector2(0f, 0f);
            _sweep.anchorMax = new Vector2(0f, 1f);
            _sweep.pivot = new Vector2(0f, 0.5f);
            _sweep.sizeDelta = new Vector2(SweepWidth, 0f);

            var font = BlitzResources.LoadFont(BlitzResources.BoldFontName);
            var caption = UiFactory.Label(
                "caption", root, font, 22, new Color(0.9f, 0.9f, 0.9f, 1f),
                TextAnchor.MiddleCenter, CaptionText());
            GodotRect.Centered(caption.rectTransform, -400f, 34f, 400f, 70f);
            _label = UiFactory.Label(
                "percent", root, font, 20, new Color(1f, 0.5f, 0f, 1f),
                TextAnchor.MiddleCenter, "0%");
            GodotRect.Centered(_label.rectTransform, -100f, -60f, 100f, -30f);
            Apply();
        }

        private static string CaptionText()
        {
            return Application.systemLanguage == SystemLanguage.Russian
                ? "Подготовка графики (только при первом запуске и после обновления)"
                : "Preparing graphics (first launch and after updates only)";
        }

        private void Update()
        {
            _shown = Mathf.MoveTowards(_shown, _target, FillSpeed * Time.unscaledDeltaTime);
            Apply();
        }

        private void Apply()
        {
            if (_clip == null) { return; }
            _clip.anchorMax = new Vector2(1f, 1f);
            _clip.offsetMax = new Vector2(-(1f - _shown) * BarWidth, 0f);
            var phase = (Time.unscaledTime % SweepPeriod) / SweepPeriod;
            var x = Mathf.Lerp(-SweepWidth, BarWidth, phase);
            _sweep.anchoredPosition = new Vector2(x, 0f);
            _label.text = Mathf.RoundToInt(_shown * 100f) + "%";
        }
    }
}
