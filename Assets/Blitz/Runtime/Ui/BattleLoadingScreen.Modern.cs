using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// The Modern battle loading screen: the map artwork full screen with a
    /// slow push-in, the two rosters in glass columns with the original tier
    /// and tank icons, a VS mark, a progress bar and a tip. Timing, music and
    /// online hold/release are the classic screen's.
    /// </summary>
    public sealed partial class BattleLoadingScreen
    {
        private static readonly string[] TipKeys =
        {
            "ui:loading/tip1", "ui:loading/tip2", "ui:loading/tip3",
            "ui:loading/tip4", "ui:loading/tip5", "ui:loading/tip6",
        };

        private RectTransform _kenBurns;
        private Image _progress;
        private bool _modern;

        public bool IsModern { get { return _modern; } }

        private void BuildModern(
            IReadOnlyList<BattleParticipantStats> participants, MapDefinition map, BattleMode battleMode)
        {
            _modern = true;
            var atlas = BattleFlowUi.LoadAtlas();
            var lang = BattleFlowUi.LoadLanguage();
            var settings = BlitzBootstrap.Current == null ? null : BlitzBootstrap.Current.Settings;
            if (settings != null) { lang.Language = settings.Language; }
            var bold = BlitzResources.LoadFont(BlitzResources.BoldFontName);
            var regular = BlitzResources.LoadFont(BlitzResources.RegularFontName);
            var canvas = BattleFlowUi.CreateCanvas(transform, false, false, 80);

            var curtain = UiFactory.Picture("curtain", canvas, null);
            curtain.color = Color.black;
            curtain.raycastTarget = true;
            GodotRect.FullRect(curtain.rectTransform);

            _kenBurns = UiFactory.Node("bg_holder", canvas);
            GodotRect.FullRect(_kenBurns);
            var background = _kenBurns.gameObject.AddComponent<RawImage>();
            background.raycastTarget = false;
            background.texture = map == null ? null : Resources.Load<Texture2D>(map.LoadingScreen);
            background.enabled = background.texture != null;

            var shade = UiFactory.Picture("shade", canvas, ModernSprites.VerticalGradient(
                new Color(0f, 0f, 0f, 0.7f), new Color(0f, 0f, 0f, 0.82f)));
            GodotRect.FullRect(shade.rectTransform);

            var mapName = ModernUi.Label("mapname", canvas, bold, 44, ModernTheme.TextPrimary,
                TextAnchor.MiddleCenter, map == null ? string.Empty : lang.Get(map.NameKey));
            GodotRect.Apply(mapName.rectTransform, 0f, 0f, 1f, 0f, 40f, 26f, -40f, 80f);
            var mode = ModernUi.Label("gamemode", canvas, bold, 20, ModernTheme.Accent,
                TextAnchor.MiddleCenter, lang.Get(ModeLabelKey(battleMode)));
            ModeText = mode.text;
            GodotRect.Apply(mode.rectTransform, 0f, 0f, 1f, 0f, 40f, 80f, -40f, 108f);
            var logo = UiFactory.Picture("gamemode_icon", canvas, atlas.Get("flow_intro_logo"));
            logo.preserveAspect = true;
            ModernUi.Centered(logo.rectTransform, 0f, -40f, 110f, 110f);
            var vs = ModernUi.Label("vs", canvas, bold, 54, ModernTheme.TextPrimary,
                TextAnchor.MiddleCenter, lang.Get("ui:loading/vs"));
            ModernUi.Centered(vs.rectTransform, 0f, 50f, 140f, 70f);

            var ownTeam = OwnTeam(participants);
            AllyRows = ModernTeam(canvas, atlas, lang, bold, regular, participants, ownTeam, false,
                lang.Get("ALLIES"), ModernTheme.Positive);
            EnemyRows = ModernTeam(canvas, atlas, lang, bold, regular, participants, 1 - ownTeam, true,
                lang.Get("ENEMIES"), ModernTheme.Negative);

            // Bottom: tip and progress.
            var tipKey = TipKeys[Random.Range(0, TipKeys.Length)];
            var tipTitle = ModernUi.Label("tip_title", canvas, bold, 16, ModernTheme.Accent,
                TextAnchor.MiddleCenter, lang.Get("ui:loading/tip_title"));
            GodotRect.Apply(tipTitle.rectTransform, 0f, 1f, 1f, 1f, 80f, -96f, -80f, -74f);
            var tip = ModernUi.Label("tip", canvas, regular, 19, ModernTheme.TextPrimary,
                TextAnchor.MiddleCenter, lang.Get(tipKey));
            tip.horizontalOverflow = HorizontalWrapMode.Wrap;
            GodotRect.Apply(tip.rectTransform, 0f, 1f, 1f, 1f, 120f, -74f, -120f, -40f);

            var track = ModernUi.Strip("progress_track", canvas, new Color(1f, 1f, 1f, 0.12f));
            GodotRect.Apply(track.rectTransform, 0f, 1f, 1f, 1f, 160f, -26f, -160f, -20f);
            _progress = ModernUi.Strip("progress", track.rectTransform, ModernTheme.Accent);
            var rt = _progress.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = new Vector2(0f, 1f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static int ModernTeam(
            RectTransform canvas, AtlasRegionTable atlas, LocalizationTable lang, Font bold, Font regular,
            IReadOnlyList<BattleParticipantStats> participants, int team, bool right, string title, Color tint)
        {
            var panel = ModernUi.Panel(right ? "team_enemies" : "team_ally", canvas, ModernTheme.PanelRadius,
                new Color(0.04f, 0.05f, 0.065f, 0.78f), true);
            var rt = panel.rectTransform;
            ModernUi.Centered(rt, right ? 400f : -400f, 20f, 420f, 470f);

            var header = ModernUi.Label("title", rt, bold, 24, tint,
                right ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft, title);
            GodotRect.Apply(header.rectTransform, 0f, 0f, 1f, 0f, 20f, 10f, -20f, 46f);
            var line = ModernUi.Strip("line", rt, new Color(tint.r, tint.g, tint.b, 0.6f));
            GodotRect.Apply(line.rectTransform, 0f, 0f, 1f, 0f, 20f, 48f, -20f, 50f);

            var rows = 0;
            if (participants == null)
            {
                return 0;
            }

            for (var i = 0; i < participants.Count; i++)
            {
                var participant = participants[i];
                if (participant == null || participant.Team != team)
                {
                    continue;
                }

                var y = 58f + rows * 34f;
                ModernRow(rt, atlas, lang, bold, regular, participant, y, right);
                rows++;
            }

            return rows;
        }

        private static void ModernRow(
            RectTransform panel, AtlasRegionTable atlas, LocalizationTable lang, Font bold, Font regular,
            BattleParticipantStats participant, float y, bool right)
        {
            var row = UiFactory.Node(right ? "tank_reversed" : "tank", panel);
            GodotRect.Apply(row, 0f, 0f, 1f, 0f, 12f, y, -12f, y + 32f);
            if (participant.IsPlayer)
            {
                var mine = ModernUi.Panel("mine", row, 8, ModernTheme.AccentSoft, false);
                GodotRect.FullRect(mine.rectTransform);
            }

            var card = participant.Tank == null ? null : participant.Tank.Card;
            var tier = UiFactory.Picture("lvl", row, atlas.Get(TierRegion(card)));
            tier.preserveAspect = true;
            var icon = UiFactory.Picture("tank", row, atlas.Get(SmallIconRegion(card)));
            icon.preserveAspect = true;
            icon.enabled = icon.sprite != null;
            var name = ModernUi.Label("tank_name", row, bold, 16, ModernTheme.TextPrimary,
                right ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft,
                card == null ? string.Empty : lang.GetTankName(card, true));
            var player = ModernUi.Label("player", row, regular, 16,
                participant.IsPlayer ? ModernTheme.Accent : ModernTheme.TextSecondary,
                right ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight, participant.Nickname);

            if (right)
            {
                GodotRect.Apply(tier.rectTransform, 1f, 0f, 1f, 1f, -34f, 4f, -8f, -4f);
                GodotRect.Apply(icon.rectTransform, 1f, 0f, 1f, 1f, -96f, 3f, -38f, -3f);
                icon.rectTransform.localScale = new Vector3(-1f, 1f, 1f);
                GodotRect.Apply(name.rectTransform, 0.5f, 0f, 1f, 1f, 0f, 0f, -102f, 0f);
                GodotRect.Apply(player.rectTransform, 0f, 0f, 0.5f, 1f, 8f, 0f, -4f, 0f);
            }
            else
            {
                GodotRect.Apply(tier.rectTransform, 0f, 0f, 0f, 1f, 8f, 4f, 34f, -4f);
                GodotRect.Apply(icon.rectTransform, 0f, 0f, 0f, 1f, 38f, 3f, 96f, -3f);
                GodotRect.Apply(name.rectTransform, 0f, 0f, 0.5f, 1f, 102f, 0f, 0f, 0f);
                GodotRect.Apply(player.rectTransform, 0.5f, 0f, 1f, 1f, 4f, 0f, -8f, 0f);
            }
        }

        /// <summary>Ken Burns push-in and the progress bar, driven by the classic timer.</summary>
        private void UpdateModern(float elapsed)
        {
            var t = Duration > 0f ? Mathf.Clamp01(elapsed / Duration) : 1f;
            if (_kenBurns != null)
            {
                var s = 1f + 0.08f * t;
                _kenBurns.localScale = new Vector3(s, s, 1f);
            }

            if (_progress != null)
            {
                _progress.rectTransform.anchorMax = new Vector2(t, 1f);
            }
        }
    }
}
