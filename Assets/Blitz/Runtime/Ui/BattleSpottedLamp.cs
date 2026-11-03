using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// Port of the <c>lamp</c> node of ui_player.tscn and the
    /// <c>_on_player_spotted</c> / <c>_show_spotted_lamp</c> /
    /// <c>_hide_spotted_lamp</c> chain of ui_player_battle.gd.
    ///
    /// Every spot event starts its own three-second timer. When one runs out
    /// the bulb is shown, lamp_01.wav plays, "anims/lamp" starts and a 5.2 s
    /// timer hides the node again; a timer that expires while the animation
    /// is still playing does nothing.
    ///
    /// The component sits on the <c>lamp</c> node itself and never deactivates
    /// it: Godot's <c>hide()</c> becomes a zero CanvasGroup alpha, so the
    /// timers keep ticking while the bulb is off screen.
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class BattleSpottedLamp : MonoBehaviour
    {
        /// <summary>Util.timer(3, _show_spotted_lamp).</summary>
        public const float Delay = SpottedLampTuning.ShowDelay;

        /// <summary>Util.timer(5.2, _hide_spotted_lamp).</summary>
        public const float Duration = SpottedLampTuning.HideDelay;

        public const string LampRegion = "battle_six_sense";
        public const string ShineRegion = "battle_six_sense_shine";

        private readonly List<float> _pendingShows = new List<float>();
        private readonly List<float> _pendingHides = new List<float>();
        private CanvasGroup _group;
        private Image _shine;
        private Image _bulb;
        private float _animStart = float.NegativeInfinity;
        private float _lastTick;
        private bool _combatVisible = true;

        /// <summary>Whether the <c>lamp</c> node is shown (Godot <c>visible</c>).</summary>
        public bool Visible { get; private set; }

        /// <summary>
        /// <c>_show_spotted_lamp</c> bails out for a dead player tank; the
        /// session wires the check in. Null means always alive.
        /// </summary>
        public Func<bool> CanShow { get; set; }

        /// <summary>The lamp root, whose alpha is <c>lamp:modulate</c>.</summary>
        public CanvasGroup Group { get { return _group; } }

        /// <summary><c>lamp/lamp_shine</c>.</summary>
        public Image Shine { get { return _shine; } }

        /// <summary>Spot timers still running.</summary>
        public int PendingCount { get { return _pendingShows.Count; } }

        /// <summary>Times the bulb was lit (sound and animation started).</summary>
        public int ShowCount { get; private set; }

        public static BattleSpottedLamp Create(Transform parent)
        {
            var root = UiFactory.Node("lamp", parent);
            root.gameObject.AddComponent<CanvasGroup>();
            var lamp = root.gameObject.AddComponent<BattleSpottedLamp>();
            lamp.Build(root);
            return lamp;
        }

        private void Build(RectTransform root)
        {
            var atlas = BattleFlowUi.LoadAtlas();
            GodotRect.Apply(
                root, 0.5f, 0.5f, 0.5f, 0.5f,
                -9.9999695f, -155.99998f, 30f, -115.999985f,
                GodotRect.TopLeftPivot);
            root.localScale = new Vector3(0.5f, 0.5f, 1f);

            _group = root.GetComponent<CanvasGroup>();
            _group.interactable = false;
            _group.blocksRaycasts = false;

            _shine = UiFactory.Picture("lamp_shine", root, atlas.Get(ShineRegion));
            GodotRect.Apply(
                _shine.rectTransform, 0.5f, 0.5f, 0.5f, 0.5f,
                -100f, -100f, 100f, 100f, GodotRect.TopLeftPivot);

            _bulb = UiFactory.Picture("lamp", root, atlas.Get(LampRegion));
            GodotRect.Apply(
                _bulb.rectTransform, 0.5f, 0.5f, 0.5f, 0.5f,
                -29f, -41f, 29f, 41f, GodotRect.TopLeftPivot);

            // setup(): $lamp.modulate = Color.TRANSPARENT until the first run.
            Visible = false;
            Apply(SpottedLampTuning.AnimationLength);
            _group.alpha = 0f;
        }

        /// <summary><c>_on_player_spotted(true)</c>.</summary>
        public void Schedule()
        {
            Schedule(Time.time);
        }

        public void Schedule(float now)
        {
            if (CanShow != null && !CanShow())
            {
                return;
            }

            _pendingShows.Add(now + Delay);
        }

        /// <summary>True while "anims/lamp" is playing.</summary>
        public bool Playing
        {
            get { return _lastTick - _animStart < SpottedLampTuning.AnimationLength; }
        }

        private void Update()
        {
            Tick(Time.time);
        }

        public void Tick(float now)
        {
            _lastTick = now;

            // Timers fire in the order they were started; a hide timer and a
            // show timer due on the same frame keep that order too.
            while (true)
            {
                var show = Earliest(_pendingShows);
                var hide = Earliest(_pendingHides);
                var showDue = show >= 0 && _pendingShows[show] <= now;
                var hideDue = hide >= 0 && _pendingHides[hide] <= now;
                if (!showDue && !hideDue)
                {
                    break;
                }

                if (showDue && (!hideDue || _pendingShows[show] < _pendingHides[hide]))
                {
                    var at = _pendingShows[show];
                    _pendingShows.RemoveAt(show);
                    Show(at);
                }
                else
                {
                    _pendingHides.RemoveAt(hide);
                    Visible = false;
                }
            }

            Apply(now - _animStart);
        }

        private static int Earliest(List<float> times)
        {
            var best = -1;
            for (var i = 0; i < times.Count; i++)
            {
                if (best < 0 || times[i] < times[best])
                {
                    best = i;
                }
            }

            return best;
        }

        /// <summary><c>_show_spotted_lamp</c>.</summary>
        private void Show(float at)
        {
            if (!_combatVisible || (CanShow != null && !CanShow()))
            {
                return;
            }

            if (at - _animStart < SpottedLampTuning.AnimationLength)
            {
                return;
            }

            Visible = true;
            ShowCount++;
            PlaySound();
            _animStart = at;
            _pendingHides.Add(at + Duration);
        }

        private static void PlaySound()
        {
            var bootstrap = BlitzBootstrap.Current;
            if (bootstrap == null || bootstrap.Audio == null)
            {
                return;
            }

            var audio = bootstrap.Audio;
            audio.PlayPool2D(
                audio.Sounds.Sound(SpottedLampTuning.SoundName),
                AudioBus.Ui,
                SpottedLampTuning.SoundVolume);
        }

        private void Apply(float animTime)
        {
            var t = Mathf.Clamp(animTime, 0f, SpottedLampTuning.AnimationLength);
            if (_group != null)
            {
                _group.alpha = Visible ? SpottedLampTuning.LampAlpha(t) : 0f;
            }

            if (_shine != null)
            {
                _shine.color = new Color(1f, 1f, 1f, SpottedLampTuning.ShineAlpha(t));
            }
        }

        /// <summary>
        /// <c>set_combat_hud_visible</c>: the lamp is hidden when the combat
        /// HUD goes, and <c>_show_spotted_lamp</c> refuses to run until it is
        /// back. Timers already started keep running, as in the source.
        /// </summary>
        public void SetCombatVisible(bool on)
        {
            _combatVisible = on;
            if (!on)
            {
                Hide();
            }
        }

        /// <summary><c>_hide_spotted_lamp</c>.</summary>
        public void Hide()
        {
            Visible = false;
            if (_group != null)
            {
                _group.alpha = 0f;
            }
        }
    }
}
