using System;
using System.Collections;
using System.Collections.Generic;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Runtime.Assets;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// 1:1 port of ui_hangar.tscn screens/battle_result.
    ///
    /// Every rectangle below is the authored Godot geometry resolved against the
    /// stretched menu viewport (1040x550 / stretch scale 0.75 = 1386.67x733.33),
    /// which is the space the screen's anchors are evaluated in. Node scales are
    /// folded into the resolved rects exactly as Godot folds them:
    /// bg layers 1.4 around pivot (0.5, 0), the ribbons 0.5 around their inner
    /// edge, rewards_handle 1.25, rewards_box 1.135 and earned 0.4.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleResultScreen : MonoBehaviour
    {
        /// <summary>project.godot window/size divided by stretch/scale.</summary>
        private const float ScreenWidth = UiLayout.ReferenceWidth;

        private const float ScreenHeight = UiLayout.ReferenceHeight;

        /// <summary>The anchor every anchor_left = 0.5 offset is measured from.</summary>
        private const float CenterX = ScreenWidth * 0.5f;

        /// <summary>ui_battle_result._ready(): Audio.fade_out_player(music, 2).</summary>
        private const float MusicFadeSeconds = 2f;

        /// <summary>buttons/btn_main.custom_minimum_size.</summary>
        private const float TabButtonWidth = 117.49f;

        private const float TabButtonHeight = 87f;

        /// <summary>ui_battle_result_team_player.tscn root size.</summary>
        private const float RowHeight = 45.47f;

        /// <summary>ui_bresult_player.col_dead = Color("700c007c").</summary>
        private static readonly Color DeadRowColor =
            new Color(0x70 / 255f, 0x0c / 255f, 0f, 0x7c / 255f);

        /// <summary>ui_battle_result_team.tscn header plate and its underline.</summary>
        private static readonly Color TeamHeaderColor =
            new Color(0.1254902f, 0.13333334f, 0.13333334f, 1f);

        private static readonly Color TeamHeaderLineColor =
            new Color(0.62352943f, 0.7254902f, 0.80784315f, 1f);

        /// <summary>ui_bresult_player.init(): Color.ORANGE for the local player.</summary>
        private static readonly Color PlayerRowColor = new Color(1f, 0.647f, 0f, 1f);

        private readonly List<GameObject> _tabs = new List<GameObject>();
        private readonly List<Button> _tabButtons = new List<Button>();
        private Action _closed;
        private AudioSource _music;
        private BattleResultData _result;
        private bool _closing;
        private RawImage _backdrop;
        private RectTransform _viewport, _outcomeFrame;
        private FrozenMenuBackdrop _frozenBackdrop;
        private bool _playMusic = true;
        private Action _openProfile;

        public int ActiveTab { get; private set; }
        public int TeamRowCount { get; private set; }
        public BattleOutcome Outcome { get { return _result.Outcome; } }

        /// <summary>While the result fanfare plays, until it ends or the screen closes.</summary>
        public bool MusicPlaying { get { return _music != null && _music.isPlaying; } }

        public static BattleResultScreen Create(
            Transform parent, BattleResultData result, Action closed, Action openProfile = null, bool playMusic = true)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }
            var go = new GameObject("BattleResultScreen");
            go.transform.SetParent(parent, false);
            var screen = go.AddComponent<BattleResultScreen>();
            screen._result = result;
            screen._closed = closed;
            screen._openProfile = openProfile;
            screen._playMusic = playMusic;
            screen.Build();
            return screen;
        }

        private void Build()
        {
            var atlas = BattleFlowUi.LoadAtlas();
            var lang = BattleFlowUi.LoadLanguage();
            var bold = BlitzResources.LoadFont(BlitzResources.BoldFontName);
            var regular = BlitzResources.LoadFont(BlitzResources.RegularFontName);
            var canvas = BattleFlowUi.CreateCanvas(transform, false, false, 130);
            var root = UiFactory.Node("battle_result", canvas);
            GodotRect.FullRect(root);
            _viewport = root;
            Canvas.willRenderCanvases += RefreshOutcomeWidth;

            // screens/battle_result/blur: the hangar behind the screen stays
            // visible through a darkened blur plate.
            var blur = UiFactory.Node("blur", root);
            GodotRect.FullRect(blur);
            _backdrop = blur.gameObject.AddComponent<RawImage>();
            _backdrop.raycastTarget = false;
            _backdrop.color = new Color(.08f, .12f, .15f, .75f);
            _frozenBackdrop = FrozenMenuBackdrop.Attach(_backdrop, canvas.GetComponent<Canvas>());

            var main = BuildMain(root, atlas, lang, bold, regular);
            var team = BuildTeam(root, atlas, lang, bold, regular);
            var details = UiFactory.Node("details", root);
            BattleFlowUi.Place(details, 0f, 0f, ScreenWidth, ScreenHeight);
            _tabs.Add(main.gameObject);
            _tabs.Add(team.gameObject);
            // screens/battle_result/TabContainerCustom/details has no children in
            // the source scene; the tab is intentionally empty.
            _tabs.Add(details.gameObject);
            if(_result.ModeId=="bossmode")
            {
                var header=UiFactory.Label("Mode",details,bold,24,Color.white,TextAnchor.MiddleCenter,lang.Get("battle_mode:bossmode/name"));GodotRect.Centered(header.rectTransform,-240,-180,240,-140);
                var tickets=UiFactory.Label("RemainingPoints",details,bold,24,Color.white,TextAnchor.MiddleCenter,_result.RemainingAllyPoints+" : "+_result.RemainingEnemyPoints);GodotRect.Centered(tickets.rectTransform,-240,-130,240,-90);
                var healed=UiFactory.Label("HealedHp",details,regular,20,Color.white,TextAnchor.MiddleLeft,lang.Get("#battle_results:DetailsPage/StatisticPage/HealedHP"));GodotRect.Centered(healed.rectTransform,-256,-52,160,-12);
                var value=UiFactory.Label("HealedValue",details,bold,20,Color.white,TextAnchor.MiddleRight,FormatNumber(_result.Player==null?0:_result.Player.Healing));GodotRect.Centered(value.rectTransform,160,-52,256,-12);
            }

            BuildMenuButtons(root, atlas, lang, bold);
            BuildTabButtons(root, atlas, lang, bold);
            ShowTab(0);
            if (_playMusic) PlayMusic();
        }

        // ------------------------------------------------------------------
        // main tab
        // ------------------------------------------------------------------

        private static void PlaceCenteredContent(RectTransform rt)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(.5f, 1);
            rt.pivot = new Vector2(.5f, 1);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(ScreenWidth, ScreenHeight);
        }

        private RectTransform BuildMain(
            Transform root, AtlasRegionTable atlas, LocalizationTable lang,
            Font bold, Font regular)
        {
            var main = UiFactory.Node("main", root);
            PlaceCenteredContent(main);

            BuildOutcomeBackground(main, atlas);
            BuildRibbons(main, atlas, lang, bold);

            // main/line
            BattleFlowUi.Art("line", main, atlas, "flow_result_line",
                CenterX - 475f, 512f, 952f, 2f);

            BuildStatus(main, lang, bold, regular);
            BuildEarned(main, atlas, lang, bold, regular);
            BuildTankCard(main, atlas, lang, bold, regular);
            return main;
        }

        private void RefreshOutcomeWidth()
        {
            // Godot main/bg anchors span the viewport, unlike the centered card.
            if (_viewport != null && _outcomeFrame != null && _viewport.rect.width > 0)
                _outcomeFrame.sizeDelta = new Vector2(_viewport.rect.width, 176);
        }

        private void BuildOutcomeBackground(Transform main, AtlasRegionTable atlas)
        {
            // main/bg: a TabContainer clipped to offset_top 87 .. offset_bottom 263.
            var frame = UiFactory.Node("bg", main);
            _outcomeFrame = frame;
            frame.anchorMin = frame.anchorMax = new Vector2(.5f, 1);
            frame.pivot = new Vector2(.5f, 1);
            frame.anchoredPosition = new Vector2(0, -87);
            frame.sizeDelta = new Vector2(ScreenWidth, 176);
            frame.gameObject.AddComponent<RectMask2D>();

            string[] layers;
            switch (_result.Outcome)
            {
                case BattleOutcome.Victory:
                    layers = new[] { "flow_result_win_top" };
                    break;
                case BattleOutcome.Defeat:
                    layers = new[] {
                        "flow_result_lose_1", "flow_result_lose_2", "flow_result_lose_3",
                        "flow_result_lose_4", "flow_result_lose_5", "flow_result_lose_6",
                        "flow_result_lose_7",
                    };
                    break;
                default:
                    layers = new[] { "flow_result_draw_top", "flow_result_draw_detail" };
                    break;
            }

            for (var index = 0; index < layers.Length; index++)
            {
                // stretch_mode = 3 (KEEP_CENTERED): the layer keeps its atlas size
                // and is centred in the authored 1024x260 rect, which is then
                // scaled 1.4 around pivot (0.5, 0).
                var sprite = atlas.Get(layers[index]);
                if (sprite == null)
                {
                    continue;
                }
                var width = sprite.rect.width * 1.4f;
                var height = sprite.rect.height * 1.4f;
                var image = UiFactory.Picture("layer_" + index, frame, sprite);
                var left = -512f;
                var top = -96f;
                if (_result.Outcome == BattleOutcome.Draw)
                {
                    left = index == 0 ? -520f : -942f;
                    top = index == 0 ? -112f : -88f;
                }
                if (_result.Outcome == BattleOutcome.Defeat && index == 5) left = -800f;
                // scale 1.4 about (512, 0), texture centred in 1024x260.
                GodotRect.Apply(image.rectTransform, .5f, 0, .5f, 0,
                    left + 512f - width * .5f, top + 182f - height * .5f,
                    left + 512f + width * .5f, top + 182f + height * .5f);
                // Original emitters are interleaved with the layered artwork.
                if (_result.Outcome == BattleOutcome.Draw && index == 1)
                    ResultFireGraphic.Create(frame, "fire1_draw", -430f -154.28564f*1.4f, -88f+192f*1.4f, false, true, 1.4f);
                if (_result.Outcome == BattleOutcome.Defeat)
                {
                    if (index == 1) { ResultFireGraphic.Create(frame,"fire2_small_01",359,120,true,false); ResultFireGraphic.Create(frame,"fire1_03",200,112); }
                    if (index == 4) ResultFireGraphic.Create(frame,"fire1_05",-296,152);
                    if (index == 5) { ResultFireGraphic.Create(frame,"fire1_01",504,160); ResultFireGraphic.Create(frame,"fire1_02",424,144); ResultFireGraphic.Create(frame,"fire1_06",-168,184); }
                }
            }
        }

        private void BuildRibbons(
            Transform main, AtlasRegionTable atlas, LocalizationTable lang, Font bold)
        {
            string left;
            string right;
            switch (_result.Outcome)
            {
                case BattleOutcome.Victory:
                    left = "flow_result_ribbon_left_win";
                    right = "flow_result_ribbon_right_win";
                    break;
                case BattleOutcome.Defeat:
                    left = "flow_result_ribbon_left_lose";
                    right = "flow_result_ribbon_right_lose";
                    break;
                default:
                    left = "flow_result_ribbon_left_draw";
                    right = "flow_result_ribbon_right_draw";
                    break;
            }

            // main/ribbon_l and main/ribbon_r, both 0.5 scale around the inner edge.
            BattleFlowUi.Art("ribbon_l", main, atlas, left, -209f, 255f, 905.4f, 70f);
            BattleFlowUi.Art("ribbon_r", main, atlas, right, 695.75f, 255f, 904f, 70f);
            BuildRewards(main, atlas, lang, bold);
        }

        private void BuildRewards(
            Transform main, AtlasRegionTable atlas, LocalizationTable lang, Font bold)
        {
            // ribbon_l/rewards_handle/rewards_box, resolved scale 0.5*1.25*1.135.
            const float scale = 0.709375f;
            const float medalHeight = 150f * scale;
            const float separation = 4f * scale;

            var rewards = new List<BattleRewardData>();
            var counts = new Dictionary<string, int>();
            foreach (var reward in _result.Rewards)
            {
                if (!counts.ContainsKey(reward.Id)) { counts[reward.Id] = 0; rewards.Add(reward); }
                counts[reward.Id]++;
            }
            // Source sorts only rare vs. ordinary; preserve insertion order for ties.
            var ordered = new List<BattleRewardData>();
            foreach (var reward in rewards) if (RewardIsRare(reward.Id)) ordered.Add(reward);
            foreach (var reward in rewards) if (!RewardIsRare(reward.Id)) ordered.Add(reward);
            rewards = ordered;
            var box = UiFactory.Node("rewards_box", main);
            BattleFlowUi.Place(box, 465.7f, 233.8f, 660f * scale, medalHeight);
            if (rewards.Count == 0)
            {
                return;
            }

            var widths = new List<float>();
            var total = (rewards.Count - 1) * separation;
            var height = medalHeight;
            foreach (var reward in rewards)
            {
                var sprite = atlas.Get(RewardRegion(reward.Id));
                var width = ((sprite == null ? 152f : sprite.rect.width) + 10f) * scale;
                widths.Add(width);
                total += width;
                if (sprite != null) height = Mathf.Max(height, sprite.rect.height * scale);
            }
            box.sizeDelta = new Vector2(box.sizeDelta.x, height);
            var cursor = (660f * scale - total) * 0.5f;
            for (var index = 0; index < rewards.Count; index++)
            {
                BuildMedal(box, atlas, lang, bold, rewards[index], cursor,
                    widths[index], height, scale, counts[rewards[index].Id]);
                cursor += widths[index] + separation;
            }
        }

        private void BuildMedal(
            Transform box, AtlasRegionTable atlas, LocalizationTable lang, Font bold,
            BattleRewardData reward, float x, float width, float height, float scale, int count)
        {
            // ui_medal.tscn: the reward icon plus its SeriesCounter badge.
            var region = RewardRegion(reward.Id);
            var medal = UiFactory.Node("medal_" + reward.Id, box);
            BattleFlowUi.Place(medal, x, 0f, width, height);
            NativeArt("icon", medal, atlas.Get(region), new Rect(0, 0, width, height), scale, Color.white);

            var hit = medal.gameObject.AddComponent<Image>(); hit.color = Color.clear; hit.raycastTarget = true;
            var button = medal.gameObject.AddComponent<Button>(); button.targetGraphic = hit;
            button.onClick.AddListener(() => MedalInfoPopup.Open(transform, reward.Id));
            if (count <= 1)
            {
                return;
            }
            // series / series_counter: offsets (96, 110) size 100x47, scale 0.605.
            var badge = BattleFlowUi.Art("series", medal, atlas,
                "flow_result_medal_series",
                96f * scale, 110f * scale, 100f * 0.605f * scale, 47f * 0.605f * scale);
            badge.preserveAspect = true;
            BattleFlowUi.Label("series_counter", badge.transform, bold,
                Mathf.Max(8, Mathf.RoundToInt(38f * 0.605f * scale)),
                BattleFlowUi.White, TextAnchor.MiddleCenter,
                count.ToString(),
                11.900803f * 0.605f * scale, 0f,
                75.702487f * 0.605f * scale, 39f * 0.605f * scale);
        }

        private static JsonNode _rewardDefinitions;

        private static bool RewardIsRare(string id)
        {
            if (_rewardDefinitions == null)
            {
                var text = Resources.Load<TextAsset>("Data/battle_reward_definitions");
                _rewardDefinitions = text == null ? JsonNode.NewObject() : JsonNode.Parse(text.text)["definitions"];
            }
            return _rewardDefinitions[id]["rare"].AsBool();
        }

        private static string RewardRegion(string id)
        {
            switch (id)
            {
                case "first_victory": return "flow_reward_first_victory";
                case "warrior": return "flow_reward_warrior";
                case "first_blood": return "flow_reward_first_blood";
                default: return "reward_" + id;
            }
        }

        private void BuildStatus(
            Transform main, LocalizationTable lang, Font bold, Font regular)
        {
            string status;
            switch (_result.Outcome)
            {
                case BattleOutcome.Victory:
                    status = lang.Get("battle_notif:victory");
                    break;
                case BattleOutcome.Defeat:
                    status = lang.Get("battle_notif:defeat");
                    break;
                default:
                    status = lang.Get("battle_notif:draw");
                    break;
            }

            // main/status: font_size 45, shadow (0, 2) of Color(0.169, 0.169, 0.169).
            BattleFlowUi.Label("status", main, bold, 45, BattleFlowUi.White,
                TextAnchor.MiddleCenter, status, CenterX - 157f, 142f, 316f, 46f);
            var reasonKey = _result.FinishReason == BattleFinishReason.Timeout
                ? "battleResult:reason/timeout"
                : "battleResult:reason/extermination";
            var reason = BattleFlowUi.Label("status_reason", main, regular, 19, BattleFlowUi.White,
                TextAnchor.MiddleCenter, lang.Get(reasonKey),
                CenterX - 157f, 182f, 316f, 46f);
            reason.gameObject.SetActive(false);
        }

        private void BuildEarned(
            Transform main, AtlasRegionTable atlas, LocalizationTable lang,
            Font bold, Font regular)
        {
            // main/earned is a 0.4-scaled Control anchored at (CenterX - 19, 379).
            var earned = UiFactory.Node("earned", main);
            BattleFlowUi.Place(earned, 0f, 0f, ScreenWidth, ScreenHeight);

            BattleFlowUi.Label("with_without_prem", earned, regular, 18,
                BattleFlowUi.Muted, TextAnchor.MiddleCenter,
                lang.Get(_result.PremiumAccount
                    ? "battleResult:prem/has" : "battleResult:prem/hasnt"),
                545.33f, 363.24f, 288.82f, 20.4f);

            // table2 / table1 NinePatchRects of bgTable_02.
            var top = BattleFlowUi.Art("table2", earned, atlas, "flow_result_table",
                568.15f, 343.12f, 223.03f, 46.06f);
            top.type = Image.Type.Sliced;
            top.pixelsPerUnitMultiplier = 2.5f;
            var bottom = BattleFlowUi.Art("table1", earned, atlas, "flow_result_table",
                568.15f, 386.76f, 223.03f, 95.76f);
            bottom.type = Image.Type.Sliced;
            bottom.pixelsPerUnitMultiplier = 2.5f;

            EarnedRow(earned, atlas, bold, "xp_earned", "flow_result_currency_xp",
                _result.Xp, 403f, BattleFlowUi.White);
            EarnedRow(earned, atlas, bold, "credits_earned",
                "flow_result_currency_credits", _result.Credits, 428f,
                _result.Credits < 0 ? BattleFlowUi.Enemy : BattleFlowUi.White);
            EarnedRow(earned, atlas, bold, "gold_earned", "flow_result_currency_gold",
                _result.Gold, 455f, BattleFlowUi.White);
        }

        private static void EarnedRow(
            Transform earned, AtlasRegionTable atlas, Font bold, string name,
            string iconRegion, int amount, float y, Color colour)
        {
            // *_earned labels are right aligned, font_size 55 * 0.4.
            BattleFlowUi.Label(name, earned, bold, 22, colour, TextAnchor.MiddleRight,
                FormatNumber(amount), 581.33f, y, 187f, 24.4f);
            // currency_icon: offsets (472.5, -2.5) size 52x49 in earned space.
            BattleFlowUi.Art(name + "_icon", earned, atlas, iconRegion,
                770.33f, y - 1f, 20.8f, 19.6f);
        }

        private void BuildTankCard(
            Transform main, AtlasRegionTable atlas, LocalizationTable lang,
            Font bold, Font regular)
        {
            // main/carousel_tank: the hangar carousel card, disabled, 196x130.
            var card = UiFactory.Node("carousel_tank", main);
            BattleFlowUi.Place(card, CenterX - 105f, 536f, 196f, 130f);

            var player = _result.Player;
            var tankId = _result.TankId;
            if (string.IsNullOrEmpty(tankId) && player != null)
            {
                tankId = player.TankId;
            }

            var database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            var info = database.Get(tankId);
            if (info == null) { card.gameObject.SetActive(false); }
            else
            {
                BattleFlowUi.Art("slot", card, atlas, HangarUiKit.CarouselNationRegion(info.Nation),
                    0f, 0f, 196f, 130f);
                var icon = atlas.Get("tank_icon_" + TankModelAssets.For(info).DataId);
                if (icon != null) NativeArt("icon", card, icon,
                    new Rect(.43f, 4.5f, 195.2f, 122f), .61f, Color.white);
                NativeArt("class", card, atlas.Get(HangarUiKit.TankClassRegion(info)),
                    new Rect(6.48f, 1.92f, 27.04f, 30.16f), .52f, HangarUiKit.TankGlyphTint(info));
                NativeArt("lvl", card, atlas.Get(HangarUiKit.TankLevelRegion(info)),
                    new Rect(32.42f, 2.88f, 29.7f, 30.78f), .54f, HangarUiKit.TankGlyphTint(info));
                BattleFlowUi.Label("short_name", card, regular, 24, HangarUiKit.TankNameColor(info),
                    TextAnchor.MiddleLeft, lang.GetTankName(info, true), 8f, 100f, 110f, 25f);
                var account = BlitzBootstrap.Current == null ? null : BlitzBootstrap.Current.Accounts.GetCurrentAccount();
                if (account != null && new TankResearchService(account, database).IsTankElite(tankId))
                {
                    var elite = BattleFlowUi.Art("elite", card, atlas,
                        info.Premium && !info.Collector ? "result_elite_prem" : "result_elite", 0f, -1f, 40f, 37f);
                    elite.color = HangarUiKit.TankGlyphTint(info);
                }
            }

            // carousel_tank/player_name and player_survived.
            BattleFlowUi.Label("player_name", main, bold, 25, BattleFlowUi.White,
                TextAnchor.MiddleLeft, player == null ? string.Empty : player.Name,
                798.33f, 542f, 292f, 26f);
            var survived = player != null && player.Survived;
            BattleFlowUi.Label("player_survived", main, regular, 20,
                survived ? BattleFlowUi.Ally : BattleFlowUi.Enemy,
                TextAnchor.MiddleLeft,
                lang.Get(survived ? "Survived" : "battleResult:destroyed"),
                797.33f, 575f, 292f, 26f);
        }

        private static void NativeArt(string name, Transform parent, Sprite sprite, Rect rect, float scale, Color tint)
        {
            if (sprite == null) return;
            var image = UiFactory.Picture(name, parent, sprite);
            image.color = tint;
            var placed = HangarScreen.KeepCenteredRect(rect, sprite.rect.size, scale);
            BattleFlowUi.Place(image.rectTransform, placed.x, placed.y, placed.width, placed.height);
        }

        // ------------------------------------------------------------------
        // team tab
        // ------------------------------------------------------------------

        private RectTransform BuildTeam(
            Transform root, AtlasRegionTable atlas, LocalizationTable lang,
            Font bold, Font regular)
        {
            var team = UiFactory.Node("team", root);
            PlaceCenteredContent(team);

            BattleFlowUi.Label("allies_title", team, bold, 25, BattleFlowUi.White,
                TextAnchor.MiddleLeft, lang.Get("ALLIES"),
                CenterX - 514f, 176f, 311f, 26f);
            BattleFlowUi.Label("enemies_title", team, bold, 25, BattleFlowUi.White,
                TextAnchor.MiddleLeft, lang.Get("ENEMIES"),
                CenterX + 46f, 176f, 311f, 26f);

            var playerTeam = _result.Player == null ? 0 : _result.Player.Team;
            BuildTeamPanel(team, atlas, lang, bold, regular, "team_a",
                CenterX - 544.5f, playerTeam);
            BuildTeamPanel(team, atlas, lang, bold, regular, "team_e",
                CenterX + 15.5f, playerTeam == 0 ? 1 : 0);
            return team;
        }

        private void BuildTeamPanel(
            Transform team, AtlasRegionTable atlas, LocalizationTable lang,
            Font bold, Font regular, string name, float x, int side)
        {
            // ui_battle_result_team.tscn: 529x346 with a 41 px header plate.
            var panel = UiFactory.Node(name, team);
            BattleFlowUi.Place(panel, x, 214f, 529f, 346f);

            var header = BattleFlowUi.Solid(name + "_header", panel, TeamHeaderColor);
            BattleFlowUi.Place(header.rectTransform, 0f, 0f, 529f, 41f);
            var underline = BattleFlowUi.Solid(name + "_line", panel, TeamHeaderLineColor);
            BattleFlowUi.Place(underline.rectTransform, 0f, 39f, 529f, 2f);

            // Header stat glyphs, scale 0.55 around their centre.
            HeaderIcon(panel, atlas, "flow_result_stat_player", 0f, 0f, 72f, 40f);
            HeaderIcon(panel, atlas, "flow_result_stat_tank", 224f, 3f, 72f, 34f);
            HeaderIcon(panel, atlas, "flow_result_stat_damage", 330f, -3f, 60f, 46f);
            HeaderIcon(panel, atlas, "flow_result_stat_kills", 389f, -8f, 72f, 56f);
            HeaderIcon(panel, atlas, "flow_result_stat_xp", 469f, -5f, 48f, 48f);

            // ui_result_team.init(): sort by damage, descending.
            var players = new List<BattleParticipantResult>();
            for (var index = 0; index < _result.Participants.Count; index++)
            {
                if (_result.Participants[index].Team == side)
                {
                    players.Add(_result.Participants[index]);
                }
            }
            players.Sort((a, b) => b.Damage.CompareTo(a.Damage));

            for (var index = 0; index < players.Count; index++)
            {
                BuildTeamRow(panel, atlas, lang, bold, regular, players[index],
                    40f + index * RowHeight);
                TeamRowCount++;
            }
        }

        private static void HeaderIcon(
            Transform panel, AtlasRegionTable atlas, string region,
            float x, float y, float width, float height)
        {
            var icon = BattleFlowUi.Art(region, panel, atlas, region,
                x + width * (1f - 0.55f) * 0.5f, y + height * (1f - 0.55f) * 0.5f,
                width * 0.55f, height * 0.55f);
            icon.preserveAspect = true;
        }

        private static void BuildTeamRow(
            Transform panel, AtlasRegionTable atlas, LocalizationTable lang,
            Font bold, Font regular, BattleParticipantResult player, float y)
        {
            // ui_battle_result_team_player.tscn
            var row = UiFactory.Node("row", panel);
            BattleFlowUi.Place(row, 0f, y, 529f, RowHeight);

            var background = BattleFlowUi.Solid("bg", row,
                player.Survived ? Color.clear : DeadRowColor);
            BattleFlowUi.Place(background.rectTransform, 0f, 0f, 529f, RowHeight + 1.53f);

            var colour = player.IsPlayer ? PlayerRowColor : Color.white;
            BattleFlowUi.Label("name", row, bold, 20, colour, TextAnchor.MiddleLeft,
                player.Name, 16f, 12f, 238f, 21f);
            BattleFlowUi.Label("tank_name", row, bold, 16, colour,
                TextAnchor.MiddleCenter, lang.GetTankName(player.TankId, player.TankName),
                186f, 14f, 145f, 17f);
            BattleFlowUi.Label("dmg", row, regular, 18, colour, TextAnchor.MiddleCenter,
                FormatNumber(player.Damage), 335f, 13f, 57f, 19f);
            if (player.Kills > 0)
            {
                BattleFlowUi.Label("kills", row, regular, 18, colour,
                    TextAnchor.MiddleCenter, player.Kills.ToString(),
                    396f, 13f, 51f, 19f);
            }
            BattleFlowUi.Label("xp", row, regular, 18, colour, TextAnchor.MiddleCenter,
                FormatNumber(player.XpRaw), 462f, 13f, 61f, 19f);

            // divider: bgTable, scale (1, 0.29) pinned to the bottom edge.
            BattleFlowUi.Art("divider", row, atlas, "flow_result_divider",
                0f, RowHeight - 3.47f, 529f, 2.03f);
        }

        // ------------------------------------------------------------------
        // chrome
        // ------------------------------------------------------------------

        private void BuildMenuButtons(
            Transform root, AtlasRegionTable atlas, LocalizationTable lang, Font bold)
        {
            // btn_menu and btn_menu_battleres2, both ButtonSmallGeneric.
            var menu = BattleFlowUi.Button("btn_menu", root, atlas,
                "button_small_generic", bold, 15, string.Empty, 5f, 4f, 94f, 77f);
            IconOnly(menu, atlas, "result_menu_home", 50f);
            menu.onClick.AddListener(Close);

            var second = BattleFlowUi.Button("btn_menu_battleres2", root, atlas,
                "button_small_generic", bold, 15, string.Empty, 117f, 4f, 94f, 77f);
            IconOnly(second, atlas, "flow_result_menu_profile", 42f);
            second.onClick.AddListener(() => { var callback = _openProfile; Close(); callback?.Invoke(); });
        }

        private static void IconOnly(
            Button button, AtlasRegionTable atlas, string region, float maxWidth)
        {
            var label = button.transform.Find("label");
            if (label != null)
            {
                label.gameObject.SetActive(false);
            }
            var sprite = atlas.Get(region);
            if (sprite == null)
            {
                return;
            }
            var scale = Mathf.Min(1f, maxWidth / Mathf.Max(1f, sprite.rect.width));
            var width = sprite.rect.width * scale;
            var height = sprite.rect.height * scale;
            var icon = UiFactory.Picture("icon", button.transform, sprite);
            icon.preserveAspect = true;
            BattleFlowUi.Place(icon.rectTransform,
                (94f - width) * 0.5f, (77f - height) * 0.5f, width, height);
        }

        private void BuildTabButtons(
            Transform root, AtlasRegionTable atlas, LocalizationTable lang, Font bold)
        {
            var keys = new[] { "bresult/PERSONAL", "bresult/TEAM", "bresult/DETAILS" };
            var icons = new[] {
                "flow_result_tab_personal", "flow_result_tab_team",
                "flow_result_tab_details",
            };
            for (var index = 0; index < keys.Length; index++)
            {
                var captured = index;
                // buttons HBoxContainer: anchored to the right edge, alignment = END.
                var x = ScreenWidth - TabButtonWidth * (keys.Length - index);
                var button = BattleFlowUi.Button("tab_" + index, root, atlas,
                    null, bold, 20, string.Empty,
                    x, 0f, TabButtonWidth, TabButtonHeight);
                var rt = (RectTransform)button.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(1, 1);
                rt.anchoredPosition = new Vector2(-TabButtonWidth * (keys.Length - index), 0);
                var label = button.transform.Find("label");
                if (label != null)
                {
                    label.gameObject.SetActive(false);
                }
                // btn_*/icon: 94x90 centred, scale 0.49, offset 8 px upwards.
                var icon = UiFactory.Picture("icon", button.transform, atlas.Get(icons[index]));
                icon.preserveAspect = true;
                BattleFlowUi.Place(icon.rectTransform,
                    TabButtonWidth * 0.5f - 94f * 0.49f * 0.5f,
                    TabButtonHeight * 0.5f - 8f * 0.49f - 90f * 0.49f * 0.5f,
                    94f * 0.49f, 90f * 0.49f);
                // btn_*/name: bottom centred, font_size 20.
                BattleFlowUi.Label("name", button.transform, bold, 20,
                    BattleFlowUi.White, TextAnchor.MiddleCenter, lang.Get(keys[index]),
                    0, TabButtonHeight - 24f, TabButtonWidth, 24f);
                button.onClick.AddListener(() => ShowTab(captured));
                button.transition = Selectable.Transition.None;
                _tabButtons.Add(button);
            }
        }

        public void ShowTab(int index)
        {
            ActiveTab = Mathf.Clamp(index, 0, _tabs.Count - 1);
            for (var i = 0; i < _tabs.Count; i++)
            {
                _tabs[i].SetActive(i == ActiveTab);
                if (i >= _tabButtons.Count)
                {
                    continue;
                }
                // ButtonCustom toggle state: the pressed tab keeps full modulate.
                var image = _tabButtons[i].GetComponent<Image>();
                image.color = i == ActiveTab
                    ? new Color(.65206665f, .9211909f, .96818036f, .10980392f) : Color.clear;
            }
        }

        /// <summary>Util.format_number(): thin-space grouped thousands.</summary>
        private static string FormatNumber(int value)
        {
            return HangarUiKit.FormatNumber(value);
        }

        private void PlayMusic()
        {
            var audio = BlitzBootstrap.Current == null ? null : BlitzBootstrap.Current.Audio;
            if (audio == null)
            {
                return;
            }
            var name = _result.Outcome == BattleOutcome.Victory
                ? "result_victory"
                : _result.Outcome == BattleOutcome.Defeat
                    ? "result_defeat" : "result_draw";
            _music = audio.PlaySound2D(name, AudioBus.Music, false, 1f);
        }

        private void Update()
        {
            if (MedalInfoPopup.EscapeConsumedFrame != Time.frameCount && GetComponentInChildren<MedalInfoPopup>() == null && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                Close();
            }
        }

        public void Close()
        {
            if (_closing)
            {
                return;
            }
            _closing = true;
            // Both menu buttons run Audio.fade_out_player(music, 2).
            if (_music != null && isActiveAndEnabled)
            {
                var fade = _music.gameObject.AddComponent<ResultMusicFade>();
                fade.Begin(_music, MusicFadeSeconds);
                _music = null;
            }
            else if (_music != null)
            {
                _music.Stop();
            }
            var callback = _closed;
            _closed = null;
            if (callback != null)
            {
                callback();
            }
        }

        public bool BackdropReady { get { return _frozenBackdrop != null && _frozenBackdrop.Ready; } }
        public RenderTexture BackdropTexture { get { return _frozenBackdrop == null ? null : _frozenBackdrop.Texture; } }
        public void SetBackdrop(Texture capture) { _frozenBackdrop.SetBackdrop(capture); }

        private void OnDestroy()
        {
            Canvas.willRenderCanvases -= RefreshOutcomeWidth;
            if (_music != null)
            {
                _music.Stop();
            }
        }
    }
}
