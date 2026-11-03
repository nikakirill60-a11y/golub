using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Port of the Blitz 4.8.0 <c>DestroyTimer</c> (<c>UIDestroyTimer</c>,
    /// <c>VehicleDestroyTimer.yaml</c>): a 130x130 plate 150 px under the
    /// screen centre showing the flooded-tank icon and the <c>mm:ss</c>
    /// drowning countdown. The ring of <c>WaterIndicator</c> frame 2 drains
    /// with the remaining time over the dark frame 1 ring.
    ///
    /// Before the roof goes under the plate already tells the player the
    /// tank is in water: once the water reaches
    /// <see cref="WaterRules.IndicatorLevel"/> of the flood line it shows
    /// the steady icon without a timer, its ring closing with the water
    /// level, so a crossing that is about to drown the tank is visible
    /// before the countdown starts.
    ///
    /// The same widget prints the two water messages of the 4.8.0 string
    /// table above the plate: <c>vehicle_drown</c> once the countdown
    /// destroys the player's tank and <c>cant_shoot_gun_under_water</c> when
    /// the trigger is pulled with the muzzle submerged.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleWaterIndicator : MonoBehaviour
    {
        /// <summary>HUDLayer.yaml: size 130x130, vCenterAnchor 150 on the 640 px layout.</summary>
        public const float Size = 130f;
        public const float CentreOffset = 150f;
        public const float LayoutHeight = 640f;
        public const float NoticeSeconds = 3f;

        /// <summary>Opacity of the plate while the tank only wades.</summary>
        public const float WadingAlpha = 0.8f;
        public const string DrownKey = "vehicle_drown";
        public const string GunUnderWaterKey = "cant_shoot_gun_under_water";

        /// <summary>VehicleDestroyTimer.yaml timer textColor and shadow.</summary>
        public static readonly Color TimerColour = new Color(1f, 0.5176f, 0f, 1f);
        public static readonly Color TimerShadow = new Color(0.8235f, 0f, 0f, 0.6667f);

        private BattleTank _tank;
        private LocalizationTable _lang;
        private RectTransform _plate;
        private CanvasGroup _plateGroup;
        private Image _ring;
        private Image _icon;
        private Text _timer;
        private Text _notice;
        private float _noticeUntil;
        private bool _combatVisible = true;

        public RectTransform Plate { get { return _plate; } }

        public Text TimerText { get { return _timer; } }

        public Text NoticeText { get { return _notice; } }

        public Image Ring { get { return _ring; } }

        /// <summary>Whether the water plate is on screen, wading or counting down.</summary>
        public bool Shown { get { return _plate != null && _plate.gameObject.activeSelf; } }

        /// <summary>Whether the plate counts down: the tank is flooded.</summary>
        public bool TimerShown { get { return Shown && _timer != null && _timer.gameObject.activeSelf; } }

        /// <summary>Whether the plate shows the tank in water without a countdown.</summary>
        public bool WadingShown { get { return Shown && !TimerShown; } }

        /// <summary>Whether a water message is on screen.</summary>
        public bool NoticeShown { get { return _notice != null && _notice.gameObject.activeSelf; } }

        public static BattleWaterIndicator Create(
            RectTransform parent, AtlasRegionTable atlas, LocalizationTable lang, Font font, BattleTank tank)
        {
            var root = UiFactory.Node("DestroyTimer", parent);
            GodotRect.FullRect(root);
            var widget = root.gameObject.AddComponent<BattleWaterIndicator>();
            widget._tank = tank;
            widget._lang = lang;
            widget.Build(atlas, font);
            TankWaterPhysics.AnyDrowned += widget.OnDrowned;
            TankWaterPhysics.AnyShotBlocked += widget.OnShotBlocked;
            widget.Refresh();
            return widget;
        }

        private void Build(AtlasRegionTable atlas, Font font)
        {
            var anchorY = 0.5f - CentreOffset / LayoutHeight;
            _plate = UiFactory.Node("VehicleDestroyTimer", transform);
            _plate.anchorMin = _plate.anchorMax = new Vector2(0.5f, anchorY);
            _plate.pivot = new Vector2(0.5f, 0.5f);
            _plate.sizeDelta = new Vector2(Size, Size);
            _plate.anchoredPosition = Vector2.zero;
            _plateGroup = _plate.gameObject.AddComponent<CanvasGroup>();
            _plateGroup.interactable = false;
            _plateGroup.blocksRaycasts = false;

            Layer("ring_shadow", Region(atlas, "water_indicator_1"));
            _ring = Layer("ring", Region(atlas, "water_indicator_2"));
            _ring.type = Image.Type.Filled;
            _ring.fillMethod = Image.FillMethod.Radial360;
            _ring.fillOrigin = (int)Image.Origin360.Top;
            _ring.fillClockwise = false;
            _icon = Layer("icon", Region(atlas, "water_indicator_0"));

            // timer: position [0, 70], size [130, 30] inside the plate.
            _timer = UiFactory.Label("timer", _plate, font, 21, TimerColour, TextAnchor.MiddleCenter, "00:00");
            var rt = _timer.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(Size, 30f);
            rt.anchoredPosition = new Vector2(0f, -70f);
            UiFactory.AddShadow(_timer, TimerShadow, new Vector2(0f, 1f));

            _notice = UiFactory.Label("water_message", transform, font, 21, Color.white, TextAnchor.MiddleCenter, string.Empty);
            var nrt = _notice.rectTransform;
            nrt.anchorMin = nrt.anchorMax = new Vector2(0.5f, anchorY);
            nrt.pivot = new Vector2(0.5f, 0f);
            nrt.sizeDelta = new Vector2(600f, 30f);
            nrt.anchoredPosition = new Vector2(0f, Size * 0.5f + 4f);
            UiFactory.AddShadow(_notice, new Color(0f, 0f, 0f, 0.8f), new Vector2(0f, 1f));
            _notice.gameObject.SetActive(false);
        }

        private Image Layer(string name, Sprite sprite)
        {
            var image = UiFactory.Picture(name, _plate, sprite);
            var rt = image.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            image.raycastTarget = false;
            image.enabled = sprite != null;
            return image;
        }

        private static Sprite Region(AtlasRegionTable atlas, string name)
        {
            return atlas == null ? null : atlas.Get(name);
        }

        /// <summary>Follows <c>set_combat_hud_visible</c>: hidden with the combat widgets.</summary>
        public void SetCombatVisible(bool on)
        {
            _combatVisible = on;
            Refresh();
        }

        /// <summary>Shows one localized water message for a few seconds.</summary>
        public void ShowNotice(string key)
        {
            if (_notice == null) { return; }
            _notice.text = _lang != null ? _lang.Get(key) : key;
            _noticeUntil = Time.unscaledTime + NoticeSeconds;
            _notice.gameObject.SetActive(true);
        }

        private void OnDrowned(TankWaterPhysics water)
        {
            if (water != null && water.Tank == _tank) { ShowNotice(DrownKey); }
        }

        private void OnShotBlocked(TankWaterPhysics water)
        {
            if (water != null && water.Tank == _tank) { ShowNotice(GunUnderWaterKey); }
        }

        private void Update()
        {
            Refresh();
        }

        /// <summary>Re-reads the tank's water state; public for tests.</summary>
        public void Refresh()
        {
            var water = _tank != null ? _tank.Water : null;
            var show = _combatVisible && water != null && water.IndicatorVisible;
            if (_plate != null && _plate.gameObject.activeSelf != show) { _plate.gameObject.SetActive(show); }
            if (show)
            {
                var flooded = water.TimerVisible;
                if (_timer.gameObject.activeSelf != flooded) { _timer.gameObject.SetActive(flooded); }
                _plateGroup.alpha = flooded ? 1f : WadingAlpha;
                var c = _icon.color;
                if (flooded)
                {
                    var timer = water.Timer;
                    _timer.text = WaterRules.FormatTimer(timer.Remaining);
                    _ring.fillAmount = timer.Duration > 0f ? Mathf.Clamp01(timer.Remaining / timer.Duration) : 0f;
                    // The icon blinks faster as the last seconds run out.
                    var rate = Mathf.Lerp(6f, 2f, _ring.fillAmount);
                    c.a = 0.65f + 0.35f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * rate));
                }
                else
                {
                    // Wading: the ring closes as the water climbs to the roof.
                    _ring.fillAmount = water.Level;
                    c.a = 1f;
                }

                _icon.color = c;
            }

            if (_notice != null && _notice.gameObject.activeSelf && Time.unscaledTime >= _noticeUntil)
            {
                _notice.gameObject.SetActive(false);
            }
        }

        private void OnDestroy()
        {
            TankWaterPhysics.AnyDrowned -= OnDrowned;
            TankWaterPhysics.AnyShotBlocked -= OnShotBlocked;
        }
    }
}
