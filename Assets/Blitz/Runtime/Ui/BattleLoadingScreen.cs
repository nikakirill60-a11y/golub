using System;
using System.Collections.Generic;
using System.Text;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>
    /// assets/gfx/ui/battle/ui_battle_intro.tscn, rebuilt node for node: the map
    /// splash, the mirrored table background, both scoreboard team lists
    /// (assets/gfx/ui/battle/ui_scoreboard_loading_team.tscn) and the mode logo,
    /// map name and mode labels.  The source scene has no loading wheel, so this
    /// screen has none either; the wheel only belongs to
    /// <see cref="LoadingOverlay"/> (maps/loading_screen.tscn).
    /// Lifetime follows the source music formula and fades the track over the
    /// final 0.8 seconds.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed partial class BattleLoadingScreen : MonoBehaviour
    {
        public const float NoMusicDuration = 6f;
        public const float MusicFadeSeconds = 0.8f;
        public const float MusicRollMin = 0.4f;
        public const float MusicRollMax = 0.75f;

        /// <summary>ui_battle_intro.tscn: the "teams" control is authored at 0.5.</summary>
        public const float TeamsScale = 0.5f;

        /// <summary>tank_example.custom_minimum_size.y.</summary>
        public const float RowHeight = 42f;

        /// <summary>
        /// The Control ui_team_intro inserts after every entry. It is authored
        /// 4.5 tall, but Godot's containers round every child to whole pixels,
        /// so the live list stacks it 5 tall.
        /// </summary>
        public const float RowSpacerHeight = 5f;

        /// <summary>
        /// One entry, its spacer and the two separations between them: 55,
        /// measured from a headless layout of ui_scoreboard_loading_team.tscn.
        /// </summary>
        public const float RowPitch = 55f;

        /// <summary>
        /// spacer/mytank as Godot lays it out, in row space: the 872x52 strip
        /// starts 194 left of the row and runs to 678, edge to edge across
        /// the 885 wide table artwork behind the ally list.
        /// </summary>
        public const float MyTankLeft = -194f;
        public const float MyTankTop = -6f;
        public const float MyTankWidth = 872f;
        public const float MyTankHeight = 52f;

        /// <summary>Godot's default BoxContainer separation.</summary>
        public const float BoxSeparation = 4f;

        /// <summary>ui_scoreboard_loading_team.tscn: authored VBox width.</summary>
        public const float RowWidth = 622f;

        private static readonly Color RowTextColor =
            new Color(0.8537463f, 0.8804023f, 0.8911632f, 1f);

        private static readonly Color HeaderShadow = new Color(0f, 0f, 0f, 0.575f);

        private static readonly Color LabelShadow =
            new Color(0.365339f, 0.365339f, 0.365339f, 1f);

        /// <summary>ui_team_intro: Color("505484ff") for tank destroyers.</summary>
        private static readonly Color TankDestroyerIconColor =
            new Color(0x50 / 255f, 0x54 / 255f, 0x84 / 255f, 1f);

        /// <summary>ui_team_intro: Color("4f5356") for every other class.</summary>
        private static readonly Color TankIconColor =
            new Color(0x4f / 255f, 0x53 / 255f, 0x56 / 255f, 1f);

        private Action _completed;
        private string _introMusic = DefaultIntroMusic;
        private AudioSource _music;
        private float _musicVolume;
        private float _startedAt;
        private bool _running;

        public float Duration { get; private set; }
        public int AllyRows { get; private set; }
        public int EnemyRows { get; private set; }

        public static BattleLoadingScreen Create(
            Transform parent, BattleSession session, bool mobile, Action completed)
        {
            return Create(
                parent,
                session == null ? null : session.Participants,
                session == null || session.World == null ? null : session.World.Map,
                mobile, completed,
                session == null ? BattleMode.Normal
                    : (session.Sandbox != null ? BattleMode.Sandbox : session.Mode));
        }

        /// <summary>
        /// The same screen built straight from a roster, so the editor render
        /// can hold it against the Godot reference without a live battle.
        /// </summary>
        public static BattleLoadingScreen Create(
            Transform parent, IReadOnlyList<BattleParticipantStats> participants,
            MapDefinition map, bool mobile, Action completed,
            BattleMode mode = BattleMode.Normal)
        {
            var go = new GameObject("BattleLoadingScreen");
            go.transform.SetParent(parent, false);
            var screen = go.AddComponent<BattleLoadingScreen>();
            screen._introMusic = IntroMusic(map);
            screen.Build(participants, map, mobile, mode);
            screen.Begin(completed);
            return screen;
        }

        /// <summary>What Godot's loading screen plays for every map.</summary>
        public const string DefaultIntroMusic = "map_intro_rudniki";

        /// <summary>
        /// The map's own loading music when its client ships one (the 11.20
        /// maps), else the track every Godot map shares.
        /// </summary>
        public static string IntroMusic(MapDefinition map)
        {
            return map == null || string.IsNullOrEmpty(map.IntroMusic)
                ? DefaultIntroMusic
                : map.IntroMusic;
        }

        /// <summary>
        /// Key of the gamemode label. ui_battle_intro.tscn authors the map's
        /// own game mode (an encounter battle) and only a picked battle mode
        /// overrides it, so Gravity and Uprising announce themselves while a
        /// standard battle reads exactly as before.
        /// </summary>
        public static string ModeLabelKey(BattleMode mode)
        {
            return mode == BattleMode.Normal
                ? "map_intro:enc_battle"
                : BattleModeCatalog.NameKey(mode);
        }

        /// <summary>Text of the gamemode label as built.</summary>
        public string ModeText { get; private set; }

        private void Build(
            IReadOnlyList<BattleParticipantStats> participants, MapDefinition map, bool mobile,
            BattleMode battleMode)
        {
            // The loading screen belongs to the menu redesign even though a
            // battle already exists behind it, so it asks IsModern directly.
            if (MenuStyle.IsModern)
            {
                BuildModern(participants, map, battleMode);
                return;
            }

            var atlas = BattleFlowUi.LoadAtlas();
            var lang = BattleFlowUi.LoadLanguage();
            var bold = BlitzResources.LoadFont(BlitzResources.BoldFontName);
            var regular = BlitzResources.LoadFont(BlitzResources.RegularFontName);
            var canvas = BattleFlowUi.CreateCanvas(transform, true, mobile, 80);
            var root = BattleFlowUi.AuthoredRoot(canvas);

            // The splash itself only covers the authored band, so on a screen
            // that is taller than the band the live map used to show through
            // above and below it. loading_screen.tscn sits on an opaque root,
            // and this curtain is that root: it fills the whole canvas, hides
            // the battle completely and swallows the touches that would
            // otherwise reach the world behind it.
            var curtainObject = new GameObject("curtain", typeof(RectTransform));
            curtainObject.transform.SetParent(canvas, false);
            curtainObject.transform.SetAsFirstSibling();
            GodotRect.FullRect((RectTransform)curtainObject.transform);
            var curtain = curtainObject.AddComponent<Image>();
            curtain.color = Color.black;
            curtain.raycastTarget = true;

            // bg: anchors_preset 14 with expand_mode 1 in the source, which on
            // a taller window left the authored band letterboxed. The splash is
            // the whole screen here, so it hangs off the canvas rather than the
            // authored root and stretches edge to edge. Canvas children draw in
            // sibling order, so it has to sit right behind the authored root -
            // appended last it painted over the team lists, map name and mode.
            var backgroundObject = new GameObject("bg", typeof(RectTransform));
            backgroundObject.transform.SetParent(canvas, false);
            backgroundObject.transform.SetSiblingIndex(
                curtainObject.transform.GetSiblingIndex() + 1);
            var background = backgroundObject.AddComponent<RawImage>();
            background.raycastTarget = false;
            background.color = Color.white;
            background.texture = map == null
                ? null : Resources.Load<Texture2D>(map.LoadingScreen);
            GodotRect.FullRect((RectTransform)backgroundObject.transform);

            var teams = UiFactory.Node("teams", root);
            GodotRect.Apply(teams, 0.5f, 0.5f, 0.5f, 0.5f,
                -18.99997f, -117f, 21f, -77f, new Vector2(18.48f / 40f, 1f));
            teams.localScale = new Vector3(TeamsScale, TeamsScale, 1f);

            var table = atlas.Get("flow_intro_table");
            var bg1 = UiFactory.Picture("bg1", teams, table);
            CenterOfTeams(bg1.rectTransform, -885.22f, -82.5f, 0.78f, 544.5f);
            var bg2 = UiFactory.Picture("bg2", teams, table);
            CenterOfTeams(bg2.rectTransform, 884.78f, -82.5f, 1770.78f, 544.5f);
            bg2.rectTransform.localScale = new Vector3(-1f, 1f, 1f);

            // Online a client keeps the host's team numbers, so the left table
            // is whichever team the local player sits in, not team 0.
            var ownTeam = OwnTeam(participants);
            AllyRows = BuildTeam(teams, atlas, lang, bold, regular, participants, ownTeam,
                "team_ally", -8.480026f, -8.2202f, false);
            EnemyRows = BuildTeam(teams, atlas, lang, bold, regular, participants, 1 - ownTeam,
                "team_enemies", 753.77954f, 753.77954f, true);

            var allies = UiFactory.Label("T_ALLIES", teams, bold, 40, Color.white,
                TextAnchor.UpperLeft, lang.Get("ALLIES"));
            CenterOfTeams(allies.rectTransform,
                -738.96436f, -61.666687f, -630.96436f, -20.666687f);
            UiFactory.AddShadow(allies, HeaderShadow, new Vector2(0f, 3f));
            var enemies = UiFactory.Label("T_ENEMIES", teams, bold, 40, Color.white,
                TextAnchor.UpperLeft, lang.Get("ENEMIES"));
            CenterOfTeams(enemies.rectTransform,
                598.31287f, -61.666687f, 737.31287f, -20.666687f);
            UiFactory.AddShadow(enemies, HeaderShadow, new Vector2(0f, 3f));

            // gamemode_icon: anchors_preset 5, scale 0.495, stretch_mode
            // KEEP_CENTERED, so the 158x156 logo is drawn at its own size.
            var logo = UiFactory.Node("gamemode_icon", root);
            GodotRect.CenterTop(logo, -78.99997f, -8f, 79f, 148f);
            logo.localScale = new Vector3(0.495f, 0.495f, 1f);
            UiFactory.KeepCentered(logo, atlas.Get("flow_intro_logo"));

            var mapName = UiFactory.Label("mapname", root, bold, 29, Color.white,
                TextAnchor.UpperCenter,
                map == null ? string.Empty : lang.Get(map.NameKey));
            GodotRect.CenterTop(mapName.rectTransform, -111f, 126f, 109f, 156f);
            UiFactory.AddShadow(mapName, LabelShadow, new Vector2(1f, 2f));

            var mode = UiFactory.Label("gamemode", root, bold, 18, Color.white,
                TextAnchor.UpperCenter, lang.Get(ModeLabelKey(battleMode)));
            ModeText = mode.text;
            GodotRect.CenterTop(mode.rectTransform, -110f, 161f, 110f, 184f);
            UiFactory.AddShadow(mode, LabelShadow, new Vector2(1f, 1f));
        }

        /// <summary>
        /// bg1, bg2, T_ALLIES and T_ENEMIES use anchors_preset 8 in
        /// ui_battle_intro.tscn: they hang off the centre of the 40x40 teams
        /// node, not its top-left corner. Placing them top-left moved the
        /// table and headers 20 units up and left of the rows, so the list
        /// looked shifted down and the player strip ran past the table edge.
        /// Scale (bg2 is mirrored) still pivots on the top-left, as in Godot.
        /// </summary>
        private static void CenterOfTeams(
            RectTransform rt, float left, float top, float right, float bottom)
        {
            GodotRect.Apply(rt, 0.5f, 0.5f, 0.5f, 0.5f,
                left, top, right, bottom, GodotRect.TopLeftPivot);
        }

        /// <summary>Team of the local player's row; 0 when there is none.</summary>
        public static int OwnTeam(IReadOnlyList<BattleParticipantStats> participants)
        {
            if (participants == null) { return 0; }
            for (var i = 0; i < participants.Count; i++)
            {
                var participant = participants[i];
                if (participant != null && participant.IsPlayer)
                {
                    return participant.Team == 1 ? 1 : 0;
                }
            }
            return 0;
        }

        /// <summary>ui_scoreboard_loading_team.tscn instanced into "teams".</summary>
        private static int BuildTeam(
            Transform parent, AtlasRegionTable atlas, LocalizationTable lang,
            Font bold, Font regular,
            IReadOnlyList<BattleParticipantStats> participants, int team,
            string name, float offsetLeft, float offsetRight, bool reversed)
        {
            var node = UiFactory.Node(name, parent);
            GodotRect.Apply(node, 0.5f, 0f, 0.5f, 0f,
                offsetLeft, 70f, offsetRight, 70f, GodotRect.TopLeftPivot);
            var box = UiFactory.Node("tanks", node);
            GodotRect.TopLeft(box, -672.00006f, 0f, -50.00006f, 374f);

            var rows = 0;
            if (participants != null)
            {
                for (var i = 0; i < participants.Count; i++)
                {
                    var participant = participants[i];
                    if (participant == null || participant.Team != team)
                    {
                        continue;
                    }
                    // VBoxContainer: entry, rounded spacer, separation 4 between.
                    var y = rows * RowPitch;
                    BuildRow(box, atlas, lang, bold, regular, participant, y, reversed);
                    rows++;
                }
            }
            return rows;
        }

        /// <summary>tank_example / tank_example_reversed from the team scene.</summary>
        private static void BuildRow(
            Transform parent, AtlasRegionTable atlas, LocalizationTable lang,
            Font bold, Font regular, BattleParticipantStats participant,
            float y, bool reversed)
        {
            var row = UiFactory.Node(reversed ? "tank_reversed" : "tank", parent);
            BattleFlowUi.Place(row, 0f, y, RowWidth, RowHeight);

            // HBoxContainer child order after the hidden platoon icon has been
            // removed from layout. Godot containers do not reserve either its
            // 32 px width or the adjacent 4 px separation. The old Unity port
            // still did, shifting only the ally rows 36 px towards the centre
            // and making the two lists read as one continuous line.
            // Godot rounds the 30.77 wide spacer to 31, so every column after
            // it sits on a whole pixel (dumped from the live container).
            var platoonX = reversed ? 597f : 0f;
            var spacerX = reversed ? 562f : 0f;
            var playerX = reversed ? 558f : 35f;
            var tankNameX = reversed ? 36f : 493f;
            var levelX = reversed ? 0f : 561f;

            // vzvod is only shown for platooned players; this port has none.
            var platoon = UiFactory.Picture("vzvod", row,
                atlas.Get("flow_intro_platoon"));
            BattleFlowUi.Place(platoon.rectTransform, platoonX, 0f, 32f, 42f);
            platoon.gameObject.SetActive(false);

            var spacer = UiFactory.Node("spacer", row);
            BattleFlowUi.Place(spacer, spacerX, 0f, 31f, RowHeight);
            // mytank hangs off the spacer at offset (-194, -6) and is drawn
            // 872x52, so the yellow strip runs past the row on both sides and
            // spans the whole ally table. Clamping it to the 622 wide row cut
            // it off short of the table edge and squashed it to 46 tall.
            var mine = UiFactory.Picture("mytank", row,
                atlas.Get("flow_intro_player"));
            BattleFlowUi.Place(mine.rectTransform,
                spacerX + MyTankLeft, MyTankTop, MyTankWidth, MyTankHeight);
            // Drawn first, as in the source: the spacer is the first visible
            // child, so the strip sits behind the names and the icon.
            mine.transform.SetSiblingIndex(0);
            mine.gameObject.SetActive(!reversed && participant.IsPlayer);

            var playerHandle = UiFactory.Node("name_player_handle", row);
            BattleFlowUi.Place(playerHandle, playerX, 0f, 0f, RowHeight);
            var playerName = UiFactory.Label("name_player", playerHandle, regular, 28,
                RowTextColor,
                reversed ? TextAnchor.UpperRight : TextAnchor.UpperLeft,
                participant.Nickname);
            if (reversed)
            {
                GodotRect.TopLeft(playerName.rectTransform,
                    -323.99994f, 6f, -5f, 35f);
            }
            else
            {
                GodotRect.TopLeft(playerName.rectTransform, 0f, 6f, 46f, 35f);
            }

            var filler = UiFactory.Node("spacer2", row);
            BattleFlowUi.Place(filler, reversed ? 104f : 39f, 0f, 450f, RowHeight);

            var card = participant.Tank == null ? null : participant.Tank.Card;
            var tankHandle = UiFactory.Node("name_handle", row);
            BattleFlowUi.Place(tankHandle, tankNameX, 0f, 64f, RowHeight);
            var tankName = UiFactory.Label("name_tank", tankHandle, bold, 28,
                RowTextColor,
                reversed ? TextAnchor.UpperLeft : TextAnchor.UpperRight,
                card == null ? string.Empty : lang.GetTankName(card, true));
            if (reversed)
            {
                GodotRect.TopLeft(tankName.rectTransform, 47.00006f, 7f, 395f, 36f);
            }
            else
            {
                GodotRect.TopLeft(tankName.rectTransform, -326.99994f, 7f, 21f, 36f);
            }

            var level = UiFactory.Node("lvl", row);
            BattleFlowUi.Place(level, levelX, 0f, 32f, RowHeight);
            BuildTankIcon(level, atlas, card, reversed);

            var tier = UiFactory.Node("lvl_icon", level);
            if (reversed)
            {
                GodotRect.CenterTop(tier, 16f, -2f, 33f, 19f);
            }
            else
            {
                GodotRect.CenterTop(tier, -32f, -2f, -15f, 19f);
            }
            UiFactory.KeepCentered(tier, atlas.Get(TierRegion(card)));
        }

        /// <summary>
        /// lvl/vbox/hbox/tank: a bottom-aligned VBoxContainer holding an
        /// HBoxContainer whose middle child is the small tank icon, recoloured by
        /// ui_mat_color_replace.tres.
        /// </summary>
        private static void BuildTankIcon(
            RectTransform parent, AtlasRegionTable atlas, TankInfo card, bool reversed)
        {
            var sprite = atlas.Get(SmallIconRegion(card));
            if (sprite == null)
            {
                return;
            }
            var width = sprite.rect.width;
            var height = sprite.rect.height;
            var corner = IconRect(reversed, width, height);

            var image = UiFactory.Picture("tank", parent, sprite);
            var rt = image.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(width, height);
            rt.anchoredPosition = new Vector2(
                corner.x + width * 0.5f, -(corner.y + height * 0.5f));
            if (reversed)
            {
                rt.localScale = new Vector3(-1f, 1f, 1f);
            }

            var shader = Shader.Find("Blitz/UiColorReplace");
            if (shader == null)
            {
                return;
            }
            var material = new Material(shader);
            material.SetColor("_PrevColor", new Color(0f, 0f, 0f, 0.846f));
            material.SetColor("_NewColor",
                card != null && card.Class == TankClass.TankDestroyer
                    ? TankDestroyerIconColor : TankIconColor);
            material.SetFloat("_Tolerance", 0.999999977648f);
            image.material = material;
        }

        /// <summary>
        /// Top-left of lvl/vbox/hbox/tank inside the 32 wide lvl control, as
        /// Godot's containers place it. vbox (alignment END) grows to fit
        /// hbox + Control2 + one separation and packs them to its bottom, so
        /// every icon shares one baseline 4 above the vbox floor; hbox grows
        /// to fit icon + Control + one separation and, reversed, packs to its
        /// right edge. The old fixed floor dropped every icon 4 px and left
        /// the tall ones 1 px off the rest.
        /// </summary>
        public static Vector2 IconRect(bool reversed, float width, float height)
        {
            var vboxLeft = reversed ? -80f : -21f;
            var vboxTop = reversed ? -1f : 0f;
            var vboxWidth = Mathf.Max(137f, width + BoxSeparation);
            var vboxHeight = Mathf.Max(49f, height + BoxSeparation);
            var top = vboxTop + vboxHeight - BoxSeparation - height;
            var x = reversed
                ? vboxLeft + vboxWidth - BoxSeparation - width
                : vboxLeft + BoxSeparation;
            return new Vector2(x, top);
        }

        /// <summary>TanksDataBase.get_lvl_icon(lvl, false, true).</summary>
        private static string TierRegion(TankInfo card)
        {
            var level = card == null ? 1 : Mathf.Clamp(card.Level, 1, 10);
            return "flow_intro_tier_" + level;
        }

        /// <summary>tank_info.get_small_icon(), shared with the scoreboard.</summary>
        private static string SmallIconRegion(TankInfo card)
        {
            return BattleFlowUi.SmallTankIconRegion(card);
        }

        public void Begin(Action completed)
        {
            _completed = completed;
            var audio = BlitzBootstrap.Current == null ? null : BlitzBootstrap.Current.Audio;
            if (audio != null)
            {
                _music = audio.PlaySound2D(_introMusic, AudioBus.Music, false, 1f);
            }
            var settings = BlitzBootstrap.Current == null
                ? null : BlitzBootstrap.Current.Settings;
            Duration = CalculateDuration(
                _music == null || _music.clip == null ? 0f : _music.clip.length,
                UnityEngine.Random.value,
                settings == null
                    ? SettingsData.DefaultMapIntroTimeMul
                    : settings.MapIntroTimeMul);
            _musicVolume = _music == null ? 0f : _music.volume;
            _startedAt = Time.unscaledTime;
            _running = true;
        }

        public static float CalculateDuration(float trackLength, float roll)
        {
            return CalculateDuration(
                trackLength, roll, SettingsData.DefaultMapIntroTimeMul);
        }

        /// <summary>
        /// <c>_battle.intro_screen_len</c>. The roll stretches the wanted slice
        /// of the track, the player's "MAP INTRO TIME MUL" scales it, and the
        /// fade tail still has to fit inside the file, so the cap wins.
        /// </summary>
        public static float CalculateDuration(
            float trackLength, float roll, float mul)
        {
            if (trackLength <= 0f)
            {
                return NoMusicDuration;
            }
            var wanted = trackLength * Mathf.Lerp(
                MusicRollMin, MusicRollMax, Mathf.Clamp01(roll)) *
                SettingsData.ClampMapIntroTimeMul(mul);
            var covered = Mathf.Max(trackLength - MusicFadeSeconds, 0.6f);
            return Mathf.Min(wanted, covered);
        }

        private void Update()
        {
            if (!_running)
            {
                return;
            }
            var elapsed = Time.unscaledTime - _startedAt;
            if (_modern)
            {
                UpdateModern(elapsed);
            }
            // Online: once the host released every intro, the countdown has to
            // begin on all peers together, however far this intro has got.
            if (BlitzLegacy.Runtime.Battle.BattleNetHooks.IntroReleased != null && BlitzLegacy.Runtime.Battle.BattleNetHooks.IntroReleased())
            {
                Complete();
                return;
            }
            if (_music != null && elapsed >= Duration - MusicFadeSeconds)
            {
                var remaining = Mathf.Clamp01((Duration - elapsed) / MusicFadeSeconds);
                _music.volume = _musicVolume * remaining;
            }
            if (elapsed < Duration)
            {
                return;
            }
            // Online: keep the curtain up while other players still load.
            if (BlitzLegacy.Runtime.Battle.BattleNetHooks.HoldIntro != null && BlitzLegacy.Runtime.Battle.BattleNetHooks.HoldIntro())
            {
                return;
            }
            Complete();
        }

        /// <summary>The intro's own minimum time is over.</summary>
        public bool TimeUp
        {
            get { return _running && Time.unscaledTime - _startedAt >= Duration; }
        }

        public void Complete()
        {
            if (!_running)
            {
                return;
            }
            _running = false;
            if (_music != null)
            {
                _music.Stop();
            }
            var callback = _completed;
            if (callback != null)
            {
                callback();
            }
        }

        /// <summary>
        /// Drop the intro without advancing the flow. Global.to_hangar can be
        /// confirmed while the map intro is still on screen, and the source
        /// tears the whole battle branch down instead of letting its timer run
        /// on into a countdown that no longer has a battle behind it.
        /// </summary>
        public void Cancel()
        {
            _running = false;
            _completed = null;
            if (_music != null)
            {
                _music.Stop();
                _music = null;
            }
        }

        private void OnDestroy()
        {
            Cancel();
        }
    }
}
