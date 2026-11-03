using System;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Audio;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>The five-second COUNTDOWN state between map intro and battle_start.</summary>
    [DisallowMultipleComponent]
    public sealed class BattleCountdownScreen : MonoBehaviour
    {
        public const float SourceDurationSeconds = 5f;

        /// <summary>modulate shared by both glows of ui_player.tscn/countdown.</summary>
        private static readonly Color GlowModulate =
            new Color(0.6659348f, 1f, 0.28918967f, 0.485f);

        private static readonly Color TipColour =
            new Color(0.8719275f, 1f, 0.76924175f, 0.80100006f);

        private static readonly Color StartsInColour =
            new Color(0.7094448f, 0.9999996f, 0.41735148f, 1f);

        private static readonly Color NumberColour =
            new Color(0.8219557f, 1f, 0.6715396f, 1f);

        private const int TipFontSize = 20;
        private const int TextFontSize = 60;

        private Text _number;
        private Action _completed;
        private float _startedAt;
        private bool _running;

        public int SecondsLeft { get; private set; }

        public static BattleCountdownScreen Create(
            Transform parent, bool mobile, Action completed)
        {
            var go = new GameObject("BattleCountdownScreen");
            go.transform.SetParent(parent, false);
            var screen = go.AddComponent<BattleCountdownScreen>();
            screen.Build(mobile);
            screen._completed = completed;
            screen._startedAt = Time.unscaledTime;
            screen._running = true;
            screen.Refresh(0f);
            PlayTick();
            return screen;
        }

        /// <summary>
        /// ui_player.tscn/countdown: a 40x40 control centred 85 px above the
        /// screen centre, with every child offset from that centre. The values
        /// below are those offsets resolved into the authored 1040x550 window,
        /// so the plate keeps the source's size and place on every aspect.
        /// </summary>
        private void Build(bool mobile)
        {
            var atlas = BattleFlowUi.LoadAtlas();
            var lang = BattleFlowUi.LoadLanguage();
            var bold = BlitzResources.LoadFont(BlitzResources.BoldFontName);
            var canvas = BattleFlowUi.CreateCanvas(transform, true, mobile, 70);
            var root = UiFactory.Node("root", canvas);
            GodotRect.FullRect(root);
            var content = BattleFlowUi.AuthoredRoot(root);

            Glow(content, atlas.Get("battle_touch"), "glow",
                0f, -37f, 140f, 140f);
            Glow(content, atlas.Get("battle_countdown_bg"), "glow2",
                -1f, -114f, 404f, 140f);

            Caption("text_tip", content, bold, TipFontSize, TipColour,
                TextAnchor.UpperCenter, lang.Get("battle/brief:base"),
                0f, -181f, 536f, 26f);
            // The source box is 335 px wide, which is exactly what the English
            // line needs, so its left aligned label never shows the difference.
            // Longer translations run off the right of the plate instead of
            // sitting over it, so the line is centred on the box it overflows.
            Caption("text_startsin", content, bold, TextFontSize, StartsInColour,
                TextAnchor.UpperCenter, lang.Get("battle/startsIn"),
                0f, -133f, 335f, 61f);
            _number = Caption("countdown", content, bold, TextFontSize,
                NumberColour, TextAnchor.UpperCenter, "5",
                0f, -30.5f, 30f, 61f);
        }

        /// <summary>A countdown TextureRect at the source modulate.</summary>
        private static Image Glow(
            Transform parent, Sprite sprite, string name,
            float x, float y, float width, float height)
        {
            var image = UiFactory.Picture(name, parent, sprite);
            image.color = GlowModulate;
            BattleFlowUi.Center(image.rectTransform, x, y, width, height);
            return image;
        }

        /// <summary>A countdown Label, placed against the authored centre.</summary>
        private static Text Caption(
            string name, Transform parent, Font font, int size, Color colour,
            TextAnchor anchor, string text,
            float x, float y, float width, float height)
        {
            var label = BattleFlowUi.Label(
                name, parent, font, size, colour, anchor, text,
                0f, 0f, width, height);
            BattleFlowUi.Center(label.rectTransform, x, y, width, height);
            return label;
        }

        /// <summary>
        /// <c>_on_battle_start_countdown</c> plays ui_countdown once, as the
        /// plate appears. Retriggering it on every tick stacked five copies of
        /// a six-second sample, which is why it was still ticking well after
        /// the battle had started and the plate was gone.
        /// </summary>
        private static void PlayTick()
        {
            if (BlitzBootstrap.Current == null || BlitzBootstrap.Current.Audio == null)
            {
                return;
            }

            var audio = BlitzBootstrap.Current.Audio;
            audio.PlayPool2D(audio.Sounds.Sound("ui_countdown"));
        }

        private void Update()
        {
            if (!_running)
            {
                return;
            }
            var elapsed = Time.unscaledTime - _startedAt;
            Refresh(elapsed);
            if (elapsed >= SourceDurationSeconds)
            {
                Complete();
            }
        }

        private void Refresh(float elapsed)
        {
            SecondsLeft = Mathf.Clamp(
                Mathf.CeilToInt(SourceDurationSeconds - elapsed), 0, 5);
            if (_number != null)
            {
                _number.text = SecondsLeft.ToString();
            }
        }

        public void Complete()
        {
            if (!_running)
            {
                return;
            }
            _running = false;
            if (BlitzBootstrap.Current != null && BlitzBootstrap.Current.Audio != null)
            {
                BlitzBootstrap.Current.Audio.PlaySound2D(
                    AudioService.SoundBattle, Core.Audio.AudioBus.Ui, false, 1f);
            }
            var callback = _completed;
            if (callback != null)
            {
                callback();
            }
        }

        /// <summary>
        /// Drop the countdown without starting a battle. to_hangar can be
        /// confirmed while the plate is still counting, and the source never
        /// reaches battle_start on that branch.
        /// </summary>
        public void Cancel()
        {
            _running = false;
            _completed = null;
        }

        private void OnDestroy()
        {
            Cancel();
        }
    }
}
