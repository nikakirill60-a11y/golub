using System.Collections.Generic;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// assets/gfx/ui/ui_player_scoreboard_team.tscn, instanced twice by
    /// ui_player.tscn: once as team_ally at the left edge and once inside
    /// team_enemy_handle at the right edge with reversed = true.
    ///
    /// Each instance is a 0.6-scaled VBoxContainer of rows cut from
    /// scoreboard_tank_template (TankIconBG02) or, for the local player's row,
    /// TankIconBG. A row carries the small tank icon recoloured by
    /// ui_mat_color_replace.tres, the level icon, the player name clipped to
    /// 15 characters, the tank's short name and a kill counter that stays
    /// hidden until the player scores. A dead row drops to half alpha, has its
    /// class icon lightened by 0.3 and is moved to the end of the box.
    ///
    /// Both panels slide with anims/scoreboard_slide on the anims_scoreboard
    /// player: toggle_scoreboard() plays it forward when the board is off and
    /// backwards when it is on, and ui_player_battle.gd calls it once during
    /// setup. The "scoreboard" action (Tab) and either team button call the
    /// same toggle. The animation also parks minimap_handle, which
    /// BattleMinimap follows through <see cref="Playhead"/>.
    ///
    /// Geometry, artwork and the animation keys come from
    /// Resources/Data/battle_menu_source_manifest.json, dumped headlessly by
    /// Tools/godot_battle_menu_dump.gd; BattleScoreboardTests re-read that
    /// manifest and fail if these constants drift from it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleScoreboard : MonoBehaviour
    {
        /// <summary>team_box.scale.</summary>
        public const float BoxScale = 0.6f;

        /// <summary>scoreboard_tank_template.size.</summary>
        public const float RowWidth = 508f;
        public const float RowHeight = 46f;

        /// <summary>Godot's default BoxContainer separation.</summary>
        public const float BoxSeparation = 4f;

        /// <summary>anims/scoreboard_slide length and key easing.</summary>
        public const float SlideSeconds = 0.66f;
        public const float SlideTransition = 1.1095701f;

        /// <summary>team_ally:position keys.</summary>
        public const float AllyClosedX = -109f;
        public const float AllyOpenX = 152f;

        /// <summary>
        /// team_enemy_handle/team_enemy:position keys. The handle is pinned to
        /// the right edge and is <see cref="EnemyHandleWidth"/> wide, so a
        /// Godot position of x maps to an offset_left of x - 40.
        /// </summary>
        public const float EnemyClosedX = 145f;
        public const float EnemyOpenX = -113f;

        /// <summary>team_enemy_handle.size.x.</summary>
        public const float EnemyHandleWidth = 40f;

        /// <summary>The RESET pose, i.e. the authored positions.</summary>
        public const float AllyRestX = -101f;
        public const float EnemyRestX = 137f;

        public const int NameFontSize = 25;
        public const int KillsFontSize = 30;

        /// <summary>ui_player_scoreboard_team.gd clips longer nicknames.</summary>
        public const int NameLimit = 15;

        /// <summary>ButtonCustom, the bare click area over one list.</summary>
        public const float ButtonLeft = -208f;
        public const float ButtonRight = 228f;
        public const float ButtonHeight = 138f;

        /// <summary>_set_entry_dead: modulate.a of a destroyed row.</summary>
        public const float DeadAlpha = 0.5f;

        private sealed class Row
        {
            public BattleParticipantStats Stats;
            public RectTransform Rect;
            public CanvasGroup Group;
            public Image Background;
            public Image ClassIcon;
            public Text Kills;
            public Text Name;
            public bool TeamKiller;
            public Material IconMaterial;
            public Color AliveColor;
            public int AliveIndex;
            public bool Dead;
            public int ShownKills = -1;
        }

        private sealed class Team
        {
            public RectTransform Root;
            public RectTransform Box;
            public Button Handle;
            public bool Reversed;
            public readonly List<Row> Rows = new List<Row>();
        }

        private AtlasRegionTable _atlas;
        private LocalizationTable _lang;
        private Font _bold;
        private Font _regular;
        private Team _ally;
        private Team _enemy;
        private bool _open;
        private float _playhead;
        private bool _sliding;

        /// <summary>scoreboard_on in ui_player_battle.gd.</summary>
        public bool IsOpen { get { return _open; } }

        /// <summary>Where the slide currently sits, 0 closed .. 1 open.</summary>
        public float Playhead { get { return _playhead; } }

        public RectTransform AllyRoot { get { return _ally.Root; } }

        public RectTransform EnemyRoot { get { return _enemy.Root; } }

        /// <summary>team_enemy_handle, the right-edge anchor of the enemy list.</summary>
        public RectTransform EnemyHandle { get; private set; }

        public Button AllyButton { get { return _ally.Handle; } }

        public Button EnemyButton { get { return _enemy.Handle; } }

        public int AllyRowCount { get { return _ally.Rows.Count; } }

        public int EnemyRowCount { get { return _enemy.Rows.Count; } }

        /// <summary>
        /// Above BattleHud.SortingOrder and the BattleControls canvas (one
        /// above it), so the team buttons win the raycast over the touch surface.
        /// </summary>
        public const int SortingOrder = BattleHud.SortingOrder + 2;

        public static BattleScoreboard Create(RectTransform parent)
        {
            var root = UiFactory.Node("scoreboard", parent);
            GodotRect.FullRect(root);

            // The HUD canvas carries no raycaster and the controls canvas above
            // it is covered by the full screen touch surface, so a tap on a team
            // button never reached it; the desktop only toggled through Tab.
            // A nested canvas sorted above the controls gives the two buttons a
            // raycaster of their own. Rows and labels keep raycastTarget off, so
            // only the ButtonCustom areas stop camera drags, as in the source.
            var canvas = root.gameObject.AddComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = SortingOrder;
            var raycaster = root.gameObject.AddComponent<BattleScoreboardRaycaster>();
            var board = root.gameObject.AddComponent<BattleScoreboard>();
            board.Build();
            raycaster.Bind(board.AllyButton, board.EnemyButton, SortingOrder);
            return board;
        }

        private void Build()
        {
            _atlas = BattleFlowUi.LoadAtlas();
            _lang = BattleFlowUi.LoadLanguage();
            _bold = BlitzResources.LoadFont(BlitzResources.BoldFontName);
            _regular = BlitzResources.LoadFont(BlitzResources.RegularFontName);

            var root = (RectTransform)transform;

            _ally = new Team { Reversed = false };
            _ally.Root = UiFactory.Node("team_ally", root);
            GodotRect.TopLeft(_ally.Root, AllyRestX, 0f, AllyRestX + 21f, 24f);
            BuildTeam(_ally);

            // team_enemy_handle: an empty Control pinned to the right edge,
            // with the second scoreboard instance inside it.
            EnemyHandle = UiFactory.Node("team_enemy_handle", root);
            GodotRect.Apply(
                EnemyHandle, 1f, 0f, 1f, 0f, -40f, 0f, 0f, 40f,
                GodotRect.TopLeftPivot);

            _enemy = new Team { Reversed = true };
            _enemy.Root = UiFactory.Node("team_enemy", EnemyHandle);
            GodotRect.Apply(
                _enemy.Root, 1f, 0f, 1f, 0f,
                EnemyRestX - EnemyHandleWidth, 0f,
                EnemyRestX - EnemyHandleWidth + 20.99936f, 24f,
                GodotRect.TopLeftPivot);
            BuildTeam(_enemy);
        }

        private void BuildTeam(Team team)
        {
            team.Box = UiFactory.Node("team_box", team.Root);
            GodotRect.TopLeft(team.Box, -152f, 0f, 356f, 46f);
            team.Box.localScale = new Vector3(BoxScale, BoxScale, 1f);

            // ButtonCustom: self_modulate alpha 0 and no_sound, a bare click
            // area over the whole list.
            var rect = UiFactory.Node("ButtonCustom", team.Root);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0f);
            image.canvasRenderer.cullTransparentMesh = false;
            var button = rect.gameObject.AddComponent<BattleScoreboardButton>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.None;
            GodotRect.TopLeft(
                (RectTransform)button.transform,
                ButtonLeft, 0f, ButtonRight, ButtonHeight);
            button.onClick.AddListener(Toggle);
            team.Handle = button;
        }

        /// <summary>
        /// init(team): one row per player, in roster order. The local player's
        /// row uses the highlighted background.
        /// </summary>
        public void SetRoster(IReadOnlyList<BattleParticipantStats> participants, int playerTeam)
        {
            Clear(_ally);
            Clear(_enemy);
            if (participants != null)
            {
                for (var i = 0; i < participants.Count; i++)
                {
                    var participant = participants[i];
                    if (participant == null)
                    {
                        continue;
                    }

                    AddRow(
                        participant.Team == playerTeam ? _ally : _enemy, participant);
                }
            }

            FitHandle(_ally);
            FitHandle(_enemy);
            Refresh();
        }

        private static void Clear(Team team)
        {
            for (var i = 0; i < team.Rows.Count; i++)
            {
                var rect = team.Rows[i].Rect;
                if (rect != null)
                {
                    if (Application.isPlaying) { Destroy(rect.gameObject); }
                    else { DestroyImmediate(rect.gameObject); }
                }
            }

            team.Rows.Clear();
        }

        private void AddRow(Team team, BattleParticipantStats participant)
        {
            var card = participant.Tank == null ? null : participant.Tank.Card;
            var row = new Row
            {
                Stats = participant,
                AliveIndex = team.Rows.Count,
            };

            var background = UiFactory.Picture(
                "scoreboard_tank", team.Box,
                _atlas.Get(participant.IsPlayer
                    ? "battle_scoreboard_row_me" : "battle_scoreboard_row"));
            row.Background = background;
            row.Rect = background.rectTransform;
            row.Group = background.gameObject.AddComponent<CanvasGroup>();
            BattleFlowUi.Place(
                row.Rect, 0f, team.Rows.Count * (RowHeight + BoxSeparation),
                RowWidth, RowHeight);

            BuildTankIcon(row, team.Reversed, card);
            BuildLevel(row.Rect, team.Reversed, card);
            BuildNames(row, team.Reversed, participant, card);

            team.Rows.Add(row);
        }

        /// <summary>vbox/hbox/class, the recoloured small tank icon.</summary>
        private void BuildTankIcon(Row row, bool reversed, TankInfo card)
        {
            var sprite = _atlas.Get(BattleFlowUi.SmallTankIconRegion(card));
            if (sprite == null)
            {
                return;
            }

            // vbox is bottom aligned (alignment 2) and holds the hbox plus an
            // empty Control, so the icon row sits one separation above the
            // box's bottom edge; the hbox packs to the right when reversed.
            var width = sprite.rect.width;
            var height = sprite.rect.height;
            var rect = IconRect(reversed, width, height);

            var image = UiFactory.Picture("class", row.Rect, sprite);
            BattleFlowUi.Place(image.rectTransform, rect.x, rect.y, width, height);
            if (reversed)
            {
                // flip_h on the reversed template. Godot mirrors the texture
                // inside its own rect, so the flip has to pivot on the icon's
                // centre: mirrored about Place's top-left pivot the enemy
                // icons landed a whole icon width outside the list.
                var rt = image.rectTransform;
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition += new Vector2(width * 0.5f, 0f);
                rt.localScale = new Vector3(-1f, 1f, 1f);
            }

            row.ClassIcon = image;
            row.AliveColor = card != null && card.Class == TankClass.TankDestroyer
                ? BattleFlowUi.TankDestroyerIconColor
                : BattleFlowUi.TankIconColor;
            row.IconMaterial = BattleFlowUi.ColorReplaceMaterial(row.AliveColor);
            if (row.IconMaterial != null)
            {
                image.material = row.IconMaterial;
            }
        }

        /// <summary>
        /// Top-left of vbox/hbox/class inside a row, laid out the way Godot's
        /// containers do it. vbox (alignment END) grows to fit hbox + the
        /// empty Control2 + one separation and packs them to its bottom; hbox
        /// grows to fit Control + separation + icon and, in the reversed
        /// template only, packs them to its right edge. Verified against a
        /// headless dump of ui_player_scoreboard_team.tscn.
        /// </summary>
        public static Vector2 IconRect(bool reversed, float width, float height)
        {
            var vboxLeft = reversed ? -88.3333f : 471.6665f;
            var vboxTop = reversed ? 0f : -3.3333f;
            var vboxWidth = Mathf.Max(137f, width + BoxSeparation);
            var vboxHeight = Mathf.Max(reversed ? 49f : 51f, height + BoxSeparation);
            var top = vboxTop + vboxHeight - BoxSeparation - height;
            var x = reversed
                ? vboxLeft + vboxWidth - width
                : vboxLeft + BoxSeparation;
            return new Vector2(x, top);
        }

        /// <summary>lvl: TanksDataBase.get_lvl_icon(lvl, false, true).</summary>
        private void BuildLevel(RectTransform parent, bool reversed, TankInfo card)
        {
            var level = card == null ? 1 : Mathf.Clamp(card.Level, 1, TanksDatabase.MaxLevel);
            var sprite = _atlas.Get("battle_otm_level_" + level);
            if (sprite == null)
            {
                return;
            }

            var slot = UiFactory.Node("lvl", parent);
            if (reversed)
            {
                GodotRect.Apply(
                    slot, 0f, 0.5f, 0f, 0.5f,
                    23.3333f, -24.6667f, 46.6665f, 3.1111f, GodotRect.TopLeftPivot);
            }
            else
            {
                GodotRect.Apply(
                    slot, 1f, 0.5f, 1f, 0.5f,
                    -29.6667f, -24.6667f, -6.3336f, 3.1111f, GodotRect.TopLeftPivot);
            }

            // stretch_mode 3: the icon keeps its aspect and is centred.
            UiFactory.KeepCentered(slot, sprite);
        }

        private void BuildNames(
            Row row, bool reversed, BattleParticipantStats participant, TankInfo card)
        {
            var nickname = participant.Nickname ?? string.Empty;
            if (nickname.Length > NameLimit)
            {
                nickname = nickname.Substring(0, NameLimit) + "...";
            }

            var player = UiFactory.Label(
                "name_ply", row.Rect, _regular, NameFontSize, UiTheme.Label,
                TextAnchor.UpperLeft, nickname);
            row.Name = player;
            if (reversed)
            {
                GodotRect.Apply(
                    player.rectTransform, 1f, 0f, 1f, 0f,
                    -134.6668f, 10f, -59.6668f, 36f, GodotRect.TopLeftPivot);
            }
            else
            {
                GodotRect.TopLeft(player.rectTransform, 61.6667f, 10f, 136.6667f, 36f);
            }

            var tank = UiFactory.Label(
                "name_tank", row.Rect, _bold, NameFontSize, UiTheme.BoldLabel,
                TextAnchor.UpperCenter,
                card == null ? string.Empty : _lang.GetTankName(card, true));
            UiFactory.AddShadow(tank, UiTheme.BoldLabelShadow);
            if (reversed)
            {
                GodotRect.TopLeft(tank.rectTransform, 34f, 11.6667f, 344.6666f, 37.6667f);
            }
            else
            {
                GodotRect.Apply(
                    tank.rectTransform, 1f, 0f, 1f, 0f,
                    -350.6666f, 11.6667f, -40f, 37.6667f, GodotRect.TopLeftPivot);
            }

            var kills = UiFactory.Label(
                "kills", row.Rect, _bold, KillsFontSize, UiTheme.BoldLabel,
                TextAnchor.UpperCenter, "0");
            UiFactory.AddShadow(kills, Color.black);
            if (reversed)
            {
                GodotRect.TopLeft(kills.rectTransform, 26.6667f, 10f, 98.3333f, 41f);
            }
            else
            {
                GodotRect.Apply(
                    kills.rectTransform, 1f, 0f, 1f, 0f,
                    -89.6669f, 10f, -18.0003f, 41f, GodotRect.TopLeftPivot);
            }

            // The template hides its kill counter until the player scores.
            kills.gameObject.SetActive(false);
            row.Kills = kills;
        }

        /// <summary>
        /// ButtonCustom is authored 138 tall, the height of the two rows
        /// team_box ships with in the scene. A battle lists seven or ten, so
        /// every row past the authored height fell through the board canvas
        /// onto the controls touch surface and a tap there never toggled the
        /// lists - only the first rows and the Tab key did. The click area
        /// grows with the rows it covers and never shrinks below the authored
        /// height.
        /// </summary>
        private static void FitHandle(Team team)
        {
            if (team == null || team.Handle == null)
            {
                return;
            }

            var rows = team.Rows.Count;
            var listHeight = rows <= 0
                ? 0f
                : ((rows * (RowHeight + BoxSeparation)) - BoxSeparation) * BoxScale;
            GodotRect.TopLeft(
                (RectTransform)team.Handle.transform,
                ButtonLeft, 0f, ButtonRight, Mathf.Max(ButtonHeight, listHeight));
        }

        /// <summary>
        /// toggle_scoreboard(): forward when the board is off, backwards when
        /// it is on. The playhead resumes from wherever it was caught, the way
        /// play_backwards does.
        /// </summary>
        public void Toggle()
        {
            _open = !_open;
            _sliding = true;
        }

        private void Update()
        {
            Advance(Time.deltaTime);
            Refresh();
        }

        /// <summary>The AnimationPlayer tick, exposed for the tests.</summary>
        public void Advance(float delta)
        {
            if (!_sliding)
            {
                return;
            }

            var step = SlideSeconds <= 0f ? 1f : delta / SlideSeconds;
            _playhead = Mathf.Clamp01(_playhead + (_open ? step : -step));
            if ((_open && _playhead >= 1f) || (!_open && _playhead <= 0f))
            {
                _sliding = false;
            }

            Apply();
        }

        private void Apply()
        {
            // Math::ease with a transition above one is pow(t, transition).
            var eased = Mathf.Pow(_playhead, SlideTransition);
            Place(_ally.Root, Mathf.LerpUnclamped(AllyClosedX, AllyOpenX, eased), 21f);
            Place(
                _enemy.Root,
                Mathf.LerpUnclamped(EnemyClosedX, EnemyOpenX, eased) -
                    EnemyHandleWidth,
                20.99936f);
        }

        private static void Place(RectTransform rect, float x, float width)
        {
            var min = rect.offsetMin;
            var max = rect.offsetMax;
            rect.offsetMin = new Vector2(x, min.y);
            rect.offsetMax = new Vector2(x + width, max.y);
        }

        /// <summary>
        /// _update_kills and _set_entry_dead, which the source drives from the
        /// tank signals; the rows read the same live stats here.
        /// </summary>
        public void Refresh()
        {
            Refresh(_ally);
            Refresh(_enemy);
        }

        private void Refresh(Team team)
        {
            var reordered = false;
            for (var i = 0; i < team.Rows.Count; i++)
            {
                var row = team.Rows[i];
                if (row.Stats == null)
                {
                    continue;
                }

                // Team-mates see a team killer's nick in light blue.
                var blue = team == _ally && row.Stats.Tank != null && row.Stats.Tank.TeamKiller;
                if (row.Name != null && blue != row.TeamKiller)
                {
                    row.TeamKiller = blue;
                    row.Name.color = blue ? BattleTankPlate.TeamKillerColour : UiTheme.Label;
                }

                if (row.Kills != null && row.Stats.Kills != row.ShownKills)
                {
                    row.ShownKills = row.Stats.Kills;
                    row.Kills.text = row.ShownKills.ToString();
                    row.Kills.gameObject.SetActive(row.ShownKills > 0);
                }

                var dead = row.Stats.Tank == null || row.Stats.Tank.ShownDeadToLocalPlayer;
                if (dead == row.Dead)
                {
                    continue;
                }

                row.Dead = dead;
                reordered = true;
                if (row.Group != null)
                {
                    row.Group.alpha = dead ? DeadAlpha : 1f;
                }

                if (row.IconMaterial != null)
                {
                    row.IconMaterial.SetColor(
                        "_NewColor",
                        dead ? Lightened(row.AliveColor, 0.3f) : row.AliveColor);
                }
            }

            if (reordered)
            {
                Reorder(team);
            }
        }

        /// <summary>
        /// team_box.move_child(row, -1) for a dead row and back to its alive
        /// index when it comes back; the box then re-stacks its children.
        /// </summary>
        private static void Reorder(Team team)
        {
            var order = new List<Row>(team.Rows.Count);
            for (var i = 0; i < team.Rows.Count; i++)
            {
                if (!team.Rows[i].Dead) { order.Add(team.Rows[i]); }
            }
            for (var i = 0; i < team.Rows.Count; i++)
            {
                if (team.Rows[i].Dead) { order.Add(team.Rows[i]); }
            }

            for (var i = 0; i < order.Count; i++)
            {
                var row = order[i];
                row.Rect.SetSiblingIndex(i);
                BattleFlowUi.Place(
                    row.Rect, 0f, i * (RowHeight + BoxSeparation), RowWidth, RowHeight);
            }
        }

        /// <summary>Color.lightened(amount) from Godot.</summary>
        private static Color Lightened(Color color, float amount)
        {
            return new Color(
                color.r + (1f - color.r) * amount,
                color.g + (1f - color.g) * amount,
                color.b + (1f - color.b) * amount,
                color.a);
        }
    }
}
