using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// minimap_handle of assets/gfx/ui/ui_player.tscn and the minimap code of
    /// ui_player_battle.gd.
    ///
    /// The handle is a bare top-left Control holding the dmg_log damage icons
    /// and the minimap TextureRect, scaled by 0.66. fit_minimap_to_image()
    /// crops the 512px texture to its painted frame and moves the control by
    /// the same amount, so the artwork stays where it was authored and the
    /// icons clip against the real picture. init_minimap_projection() spreads
    /// the ground rectangle under that frame over it: u from x, v from z in
    /// source space. A frame with a drawn size (minimapArea.size) is resampled
    /// whole into it and the ground is projected over that size: the 11.20
    /// retina MiniMapSmall@2x paints 505 px where the 4.8.0 MiniMapSmall
    /// paints 326, and is drawn at 326 like every other minimap.
    ///
    /// Icons:
    /// - every spawn area of both teams, numbered like the 4.8.0 client's
    ///   MinimapSpawnAreaAlly/EnemyProto (Godot draws the averaged "1" only,
    ///   which is what a map with a single area per team still gets);
    /// - every other tank with its class icon, green for the player's team and
    ///   red for the other one. An enemy shows only while spotted and leaves a
    ///   dimmed copy where it was last seen; a destroyed tank halves its tint
    ///   and fades out over a second;
    /// - the camera's view cone, pivoting at its bottom centre, and the
    ///   player's own arrow on top of it;
    /// - the player's own guided missiles.
    ///
    /// update_minimap() moves the icons every half second. anims/scoreboard_slide
    /// parks the handle above the screen while the team lists are out and
    /// brings it down when Tab folds them away, so the handle follows
    /// <see cref="BattleScoreboard.Playhead"/>.
    ///
    /// The constants come from Resources/Data/battle_minimap_source_manifest.json,
    /// written by Tools/port_battle_minimap.py; BattleMinimapTests fail when
    /// they drift from it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleMinimap : MonoBehaviour
    {
        /// <summary>minimap_handle offsets.</summary>
        public const float HandleLeft = 126f;
        public const float HandleTop = -216f;
        public const float HandleRight = 166f;
        public const float HandleBottom = -176f;

        /// <summary>anims/scoreboard_slide minimap_handle:position keys.</summary>
        public const float HandleShownY = 0f;
        public const float HandleParkedY = -216f;
        public const float HandleSlideSeconds = 0.26666668f;
        public const float HandleTransition = 1.8025012f;

        /// <summary>minimap: offset_right/bottom (the texture size) and scale.</summary>
        public const float MapControlSize = 512f;
        public const float MapScale = 0.66f;

        /// <summary>dmg_log: anchor_top/bottom 0.5 and its offsets.</summary>
        public const float DmgLogLeft = 215f;
        public const float DmgLogTop = -19f;
        public const float DmgLogRight = 335f;
        public const float DmgLogBottom = 88f;

        /// <summary>dmg_log/dico, the dealt damage icon.</summary>
        public const float DmgIconLeft = 11f;
        public const float DmgIconTop = 16f;
        public const float DmgIconRight = 63f;
        public const float DmgIconBottom = 68f;
        public const float DmgIconScale = 0.425f;

        /// <summary>dmg_log/bico, the blocked damage icon.</summary>
        public const float BlockIconLeft = 7f;
        public const float BlockIconTop = 50f;
        public const float BlockIconRight = 73f;
        public const float BlockIconBottom = 100f;
        public const float BlockIconScale = 0.41f;

        /// <summary>track_missile: mini.scale.</summary>
        public const float MissileScale = 0.45f;

        /// <summary>add_minimap_tank: a tank destroyer's icon is shrunk.</summary>
        public const float DestroyerScale = 0.75f;

        /// <summary>The unspotted copy: COLOR_ENEMY.lightened(0.5) * 0.5, alpha 1.</summary>
        public const float GhostLighten = 0.5f;
        public const float GhostDim = 0.5f;

        /// <summary>on_death: modulate *= 0.5, then alpha 1 -> 0 over one second.</summary>
        public const float DeathDim = 0.5f;
        public const float DeathFadeSeconds = 1f;

        /// <summary>The player arrow's texture points right: rot_offset = -PI / 2.</summary>
        public const float PlayerRotationOffset = -Mathf.PI * 0.5f;

        /// <summary>
        /// The look cone points up (90 degrees counter-clockwise from the
        /// right) with no offset; the arrow's offset turns that to the right.
        /// </summary>
        public const float LookTextureDegrees = 90f;
        public const float PlayerTextureDegrees = LookTextureDegrees + PlayerRotationOffset * Mathf.Rad2Deg;

        /// <summary>The 4.8.0 client numbers at most four spawn areas per team.</summary>
        public const int RespawnSprites = 4;

        /// <summary>Global.COLOR_ALLY / COLOR_ENEMY.</summary>
        public static readonly Color AllyColor = new Color(0.396f, 0.867f, 0f, 1f);
        public static readonly Color EnemyColor = new Color(0.96f, 0f, 0f, 1f);

        private sealed class Icon
        {
            public RectTransform Rect;
            public Image Image;
        }

        private sealed class TankMarker
        {
            public BattleTank Tank;
            public Icon Icon;
            public Icon Ghost;
            public bool Enemy;
            public Color Tint;
            public bool Spotted;
            public bool Dead;
            public float Fade = -1f;
        }

        private sealed class MissileMarker
        {
            public BattleProjectile Missile;
            public Icon Icon;
        }

        private AtlasRegionTable _atlas;
        private MapMinimapArea _area;
        private RectTransform _handle;
        private RectTransform _map;
        private BattleTank _player;
        private int _playerTeam;
        private Icon _look;
        private Icon _arrow;
        private readonly List<TankMarker> _tanks = new List<TankMarker>();
        private readonly List<MissileMarker> _missiles = new List<MissileMarker>();
        private readonly List<Icon> _spawns = new List<Icon>();
        private readonly List<int> _spawnNumbers = new List<int>();
        private readonly List<bool> _spawnAllied = new List<bool>();
        private float _nextRefresh;

        /// <summary>Where the slide sits, 0 lists folded .. 1 lists out.</summary>
        public Func<float> Playhead { get; set; }

        /// <summary>The camera's look direction in world space.</summary>
        public Func<Vector3> ViewForward { get; set; }

        /// <summary>Every tank of the battle; polled for late arrivals.</summary>
        public Func<IEnumerable<BattleTank>> Tanks { get; set; }

        public RectTransform Handle { get { return _handle; } }

        /// <summary>The cropped minimap TextureRect the icons live in.</summary>
        public RectTransform Map { get { return _map; } }

        public MapMinimapArea Area { get { return _area; } }

        public int SpawnIconCount { get { return _spawns.Count; } }

        public int TankMarkerCount { get { return _tanks.Count; } }

        public int MissileMarkerCount { get { return _missiles.Count; } }

        public RectTransform PlayerArrow { get { return _arrow != null ? _arrow.Rect : null; } }

        public RectTransform LookDirection { get { return _look != null ? _look.Rect : null; } }

        /// <summary>
        /// Builds the widget under <paramref name="parent"/>, or returns null
        /// when the map ships no minimap or no ground rectangle for it.
        /// </summary>
        /// <param name="sidesSwapped">The local team spawned on teamB (the Sandbox
        /// side choice), so the spawn marks swap colours with it.</param>
        public static BattleMinimap Create(RectTransform parent, MapDefinition map, bool sidesSwapped = false)
        {
            if (map == null || map.MinimapArea == null || string.IsNullOrEmpty(map.Minimap))
            {
                return null;
            }

            var texture = Resources.Load<Texture2D>(map.Minimap);
            if (texture == null)
            {
                Debug.LogWarning("[Blitz] minimap texture is missing: " + map.Minimap);
                return null;
            }

            var root = UiFactory.Node("minimap_handle", parent);
            var minimap = root.gameObject.AddComponent<BattleMinimap>();
            minimap._sidesSwapped = sidesSwapped;
            minimap.Build(root, map, texture);
            return minimap;
        }

        /// <summary>
        /// The handle's Godot y for a scoreboard playhead: the key pair runs
        /// over the first 0.267 s of the 0.66 s slide with the first key's
        /// ease, then holds.
        /// </summary>
        public static float HandleY(float playhead)
        {
            var time = Mathf.Clamp01(playhead) * BattleScoreboard.SlideSeconds;
            var progress = Mathf.Clamp01(time / HandleSlideSeconds);
            return Mathf.LerpUnclamped(
                HandleShownY, HandleParkedY, Mathf.Pow(progress, HandleTransition));
        }

        /// <summary>
        /// minimap_scale_pos: source x, z to pixels of the drawn frame, y down.
        /// The ground rectangle always spans the whole drawn frame, so a frame
        /// drawn smaller than it is painted keeps every icon over the spot of
        /// the picture it belongs to.
        /// </summary>
        public static Vector2 Project(MapMinimapArea area, float x, float z)
        {
            var width = area.Max[0] - area.Min[0];
            var depth = area.Max[1] - area.Min[1];
            if (width <= 0f || depth <= 0f)
            {
                return new Vector2(area.Width, area.Height) * 0.5f;
            }

            return new Vector2(
                (x - area.Min[0]) / width * area.Width,
                (z - area.Min[1]) / depth * area.Height);
        }

        /// <summary>A Unity world position in the source's x, z (Z is mirrored).</summary>
        public static Vector2 SourceXZ(Vector3 world)
        {
            return new Vector2(world.x, -world.z);
        }

        /// <summary>
        /// The UI rotation, in degrees counter-clockwise, of an icon whose
        /// texture points <paramref name="textureDegrees"/> counter-clockwise
        /// from the right when its node looks along <paramref name="forward"/>.
        /// The minimap has +x to the right and the source's -z (Unity +z) up,
        /// so this equals Godot's -global_rotation.y + rot_offset.
        /// </summary>
        public static float IconDegrees(Vector3 forward, float textureDegrees)
        {
            return Mathf.Atan2(forward.z, forward.x) * Mathf.Rad2Deg - textureDegrees;
        }

        /// <summary>The sprite of a numbered spawn area, clamped to the four the client draws.</summary>
        public static string RespawnRegion(bool ally, int number)
        {
            var clamped = Mathf.Clamp(number, 1, RespawnSprites);
            return (ally ? "battle_minimap_respawn_ally_" : "battle_minimap_respawn_enemy_") + clamped;
        }

        public static string ClassRegion(TankClass type)
        {
            switch (type)
            {
                case TankClass.Light: return "battle_minimap_tank_light";
                case TankClass.Medium: return "battle_minimap_tank_medium";
                case TankClass.Heavy: return "battle_minimap_tank_heavy";
                default: return "battle_minimap_tank_destroyer";
            }
        }

        /// <summary>COLOR_ENEMY.lightened(0.5) * 0.5 with alpha 1.</summary>
        public static Color GhostColor(Color tint)
        {
            var light = Color.LerpUnclamped(tint, Color.white, GhostLighten);
            light *= GhostDim;
            light.a = 1f;
            return light;
        }

        private void Build(RectTransform root, MapDefinition map, Texture2D texture)
        {
            _atlas = BattleFlowUi.LoadAtlas();
            _area = map.MinimapArea;
            _handle = root;
            GodotRect.TopLeft(root, HandleLeft, HandleTop, HandleRight, HandleBottom);

            // dmg_log sits right of the minimap, centred on the handle's height.
            var log = UiFactory.Node("dmg_log", root);
            GodotRect.Apply(log, 0f, 0.5f, 0f, 0.5f,
                DmgLogLeft, DmgLogTop, DmgLogRight, DmgLogBottom, GodotRect.TopLeftPivot);
            AddStaticIcon(log, "dico", "battle_dmg",
                DmgIconLeft, DmgIconTop, DmgIconRight, DmgIconBottom, DmgIconScale);
            AddStaticIcon(log, "bico", "battle_dmg_block",
                BlockIconLeft, BlockIconTop, BlockIconRight, BlockIconBottom, BlockIconScale);

            // fit_minimap_to_image: pixel_scale is the control over the
            // texture size, which is one for every 512px minimap. The sprite
            // is always the whole painted frame; a frame with a drawn size
            // (the 11.20 retina minimaps) is resampled into that size, so it
            // shows as large as the 4.8.0 ones without losing its edges.
            var frame = _area.Frame;
            var pixel = MapControlSize / texture.width;
            var fitX = _area.Width / frame[2];
            var fitY = _area.Height / frame[3];
            var image = UiFactory.Picture("minimap", root, Sprite.Create(
                texture,
                new Rect(frame[0], texture.height - frame[1] - frame[3], frame[2], frame[3]),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect));
            _map = image.rectTransform;
            var left = frame[0] * fitX * pixel;
            var top = frame[1] * fitY * pixel;
            GodotRect.TopLeft(_map, left, top,
                left + _area.Width * pixel, top + _area.Height * pixel);
            _map.localScale = new Vector3(MapScale, MapScale, 1f);
            _map.gameObject.AddComponent<RectMask2D>();

            AddSpawns(map);
            FollowScoreboard();
        }

        /// <summary>
        /// init_minimap(): binds the tanks and the player and places every
        /// icon at once instead of at the next half-second update.
        /// </summary>
        public void Bind(BattleTank player, int playerTeam, IEnumerable<BattleTank> tanks)
        {
            _player = player;
            _playerTeam = playerTeam;
            RecolourSpawns();
            if (tanks != null)
            {
                foreach (var tank in tanks) { Track(tank); }
            }

            if (player != null && _look == null)
            {
                _look = AddIcon("look_direction", "battle_minimap_look_direction", 1f);
                // lookdir.pivot_offset = (w / 2, h): the cone turns about its tip.
                _look.Rect.pivot = new Vector2(0.5f, 0f);
                _arrow = AddIcon("player", "battle_minimap_player", 1f);
            }

            Refresh();
        }

        private void AddStaticIcon(RectTransform parent, string name, string region,
            float left, float top, float right, float bottom, float scale)
        {
            var sprite = _atlas.Get(region);
            var image = UiFactory.Picture(name, parent, sprite);
            // A TextureRect never shrinks below its texture (EXPAND_KEEP_SIZE).
            var width = right - left;
            var height = bottom - top;
            if (sprite != null)
            {
                width = Mathf.Max(width, sprite.rect.width);
                height = Mathf.Max(height, sprite.rect.height);
            }
            GodotRect.TopLeft(image.rectTransform, left, top, left + width, top + height);
            image.rectTransform.localScale = new Vector3(scale, scale, 1f);
        }

        private Icon AddIcon(string name, string region, float size)
        {
            var sprite = _atlas.Get(region);
            var image = UiFactory.Picture(name, _map, sprite);
            var rect = image.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            var native = sprite != null ? sprite.rect.size : Vector2.one;
            rect.sizeDelta = native * size;
            return new Icon { Rect = rect, Image = image };
        }

        private static void Place(Icon icon, Vector2 point)
        {
            icon.Rect.anchoredPosition = new Vector2(point.x, -point.y);
        }

        /// <summary>The icon's anchor in frame pixels, y down, like Godot.</summary>
        public static Vector2 AnchorOf(RectTransform icon)
        {
            return new Vector2(icon.anchoredPosition.x, -icon.anchoredPosition.y);
        }

        private bool _sidesSwapped;

        private void AddSpawns(MapDefinition map)
        {
            AddSpawnTeam(map.TeamA, _sidesSwapped ? 1 : 0);
            AddSpawnTeam(map.TeamB, _sidesSwapped ? 0 : 1);
        }

        private void AddSpawnTeam(List<MapSpawnPoint> spawns, int team)
        {
            var sums = new SortedDictionary<int, Vector3>();
            foreach (var spawn in spawns)
            {
                var number = Mathf.Max(1, spawn.Respawn);
                Vector3 sum;
                sums.TryGetValue(number, out sum);
                sums[number] = sum + new Vector3(spawn.Origin[0], spawn.Origin[2], 1f);
            }

            foreach (var pair in sums)
            {
                var icon = AddIcon("spawn_team" + team + "_" + pair.Key,
                    RespawnRegion(team == 0, pair.Key), 1f);
                Place(icon, Project(_area, pair.Value.x / pair.Value.z, pair.Value.y / pair.Value.z));
                _spawns.Add(icon);
                _spawnNumbers.Add(pair.Key);
                _spawnAllied.Add(team == 0);
            }
        }

        /// <summary>Green numbers for the player's team, red for the other.</summary>
        private void RecolourSpawns()
        {
            for (var i = 0; i < _spawns.Count; i++)
            {
                var team = _spawnAllied[i] ? 0 : 1;
                _spawns[i].Image.sprite = _atlas.Get(RespawnRegion(team == _playerTeam, _spawnNumbers[i]));
            }
        }

        /// <summary>The spawn sprite region currently drawn for one spawn icon.</summary>
        public string SpawnRegionAt(int index)
        {
            var sprite = _spawns[index].Image.sprite;
            return sprite != null ? sprite.name : null;
        }

        public RectTransform SpawnIconAt(int index)
        {
            return _spawns[index].Rect;
        }

        private void Track(BattleTank tank)
        {
            if (tank == null || tank == _player) { return; }
            for (var i = 0; i < _tanks.Count; i++)
            {
                if (_tanks[i].Tank == tank) { return; }
            }

            var type = tank.Card != null ? tank.Card.Class : TankClass.Medium;
            var size = type == TankClass.TankDestroyer ? DestroyerScale : 1f;
            var marker = new TankMarker
            {
                Tank = tank,
                Enemy = tank.Team != _playerTeam,
                Icon = AddIcon(tank.name, ClassRegion(type), size),
            };
            marker.Tint = marker.Enemy ? EnemyColor : AllyColor;
            marker.Icon.Image.color = marker.Tint;
            if (marker.Enemy)
            {
                marker.Ghost = AddIcon(tank.name + "_unspotted", ClassRegion(type), size);
                marker.Ghost.Image.color = GhostColor(marker.Tint);
                marker.Ghost.Rect.gameObject.SetActive(false);
                marker.Spotted = tank.Spotted;
                marker.Icon.Rect.gameObject.SetActive(marker.Spotted);
            }

            // Keep the player's cone and arrow above every tank.
            if (_look != null)
            {
                _look.Rect.SetAsLastSibling();
                _arrow.Rect.SetAsLastSibling();
            }
            _tanks.Add(marker);
            PlaceTank(marker);
        }

        private TankMarker Find(BattleTank tank)
        {
            for (var i = 0; i < _tanks.Count; i++)
            {
                if (_tanks[i].Tank == tank) { return _tanks[i]; }
            }
            return null;
        }

        public RectTransform MarkerOf(BattleTank tank)
        {
            var marker = Find(tank);
            return marker != null ? marker.Icon.Rect : null;
        }

        public RectTransform GhostOf(BattleTank tank)
        {
            var marker = Find(tank);
            return marker != null && marker.Ghost != null ? marker.Ghost.Rect : null;
        }

        public Color MarkerColour(BattleTank tank)
        {
            var marker = Find(tank);
            return marker != null ? marker.Icon.Image.color : Color.clear;
        }

        private void PlaceTank(TankMarker marker)
        {
            Place(marker.Icon, ProjectWorld(marker.Tank.transform.position));
        }

        private Vector2 ProjectWorld(Vector3 world)
        {
            var source = SourceXZ(world);
            return Project(_area, source.x, source.y);
        }

        /// <summary>update_minimap(): moves every tracked icon.</summary>
        public void Refresh()
        {
            if (Tanks != null)
            {
                foreach (var tank in Tanks()) { Track(tank); }
            }

            for (var i = 0; i < _tanks.Count; i++)
            {
                if (_tanks[i].Tank != null) { PlaceTank(_tanks[i]); }
            }

            if (_player != null && _arrow != null)
            {
                var pose = _player.transform;
                var anchor = ProjectWorld(pose.position);
                Place(_arrow, anchor);
                _arrow.Rect.localEulerAngles = new Vector3(0f, 0f,
                    IconDegrees(pose.forward, PlayerTextureDegrees));
                Place(_look, anchor);
                var forward = ViewForward != null ? ViewForward() : pose.forward;
                _look.Rect.localEulerAngles = new Vector3(0f, 0f, IconDegrees(forward, LookTextureDegrees));
            }

            SyncMissiles();
            for (var i = 0; i < _missiles.Count; i++)
            {
                Place(_missiles[i].Icon, ProjectWorld(_missiles[i].Missile.transform.position));
            }
        }

        /// <summary>
        /// update_missile_markers / drop_missile_marker: one rocket marker per
        /// guided missile the player has in the air.
        /// </summary>
        private void SyncMissiles()
        {
            var live = BattleProjectile.LiveGuided;
            for (var i = _missiles.Count - 1; i >= 0; i--)
            {
                var missile = _missiles[i].Missile;
                var alive = false;
                for (var j = 0; j < live.Count; j++)
                {
                    if (live[j] == missile) { alive = true; break; }
                }
                if (missile == null || !alive)
                {
                    Destroy(_missiles[i].Icon.Rect.gameObject);
                    _missiles.RemoveAt(i);
                }
            }

            if (_player == null) { return; }
            for (var j = 0; j < live.Count; j++)
            {
                var missile = live[j];
                if (missile == null || missile.Owner != _player) { continue; }
                var known = false;
                for (var i = 0; i < _missiles.Count; i++)
                {
                    if (_missiles[i].Missile == missile) { known = true; break; }
                }
                if (known) { continue; }
                var icon = AddIcon("missile", "battle_rocket_marker", 1f);
                icon.Rect.localScale = new Vector3(MissileScale, MissileScale, 1f);
                Place(icon, ProjectWorld(missile.transform.position));
                _missiles.Add(new MissileMarker { Missile = missile, Icon = icon });
            }
        }

        /// <summary>
        /// on_spotted and on_death of every tracked tank, plus the death fade.
        /// </summary>
        public void Tick(float deltaTime)
        {
            for (var i = 0; i < _tanks.Count; i++)
            {
                var marker = _tanks[i];
                if (marker.Tank == null) { continue; }
                if (marker.Enemy)
                {
                    var spotted = marker.Tank.Spotted;
                    if (spotted != marker.Spotted)
                    {
                        marker.Spotted = spotted;
                        marker.Icon.Rect.gameObject.SetActive(spotted && marker.Fade != 0f);
                        marker.Ghost.Rect.gameObject.SetActive(!spotted);
                        if (!spotted)
                        {
                            marker.Ghost.Rect.anchoredPosition = marker.Icon.Rect.anchoredPosition;
                        }
                    }
                }

                var dead = marker.Tank.ShownDeadToLocalPlayer;
                if (dead != marker.Dead)
                {
                    marker.Dead = dead;
                    if (dead)
                    {
                        var dim = marker.Tint * DeathDim;
                        dim.a = 1f;
                        marker.Icon.Image.color = dim;
                        marker.Fade = 1f;
                    }
                    else
                    {
                        // A respawned tank is a new life: undo the wreck tint.
                        marker.Icon.Image.color = marker.Tint;
                        marker.Fade = -1f;
                        marker.Icon.Rect.gameObject.SetActive(!marker.Enemy || marker.Spotted);
                    }
                }

                if (marker.Fade > 0f)
                {
                    marker.Fade = Mathf.Max(0f, marker.Fade - deltaTime / DeathFadeSeconds);
                    var colour = marker.Icon.Image.color;
                    colour.a = marker.Fade;
                    marker.Icon.Image.color = colour;
                    if (marker.Fade <= 0f)
                    {
                        marker.Icon.Rect.gameObject.SetActive(false);
                    }
                }
            }
        }

        /// <summary>Moves the handle to where the scoreboard slide puts it.</summary>
        public void FollowScoreboard()
        {
            var playhead = Playhead != null ? Playhead() : 1f;
            _handle.anchoredPosition = new Vector2(HandleLeft, -HandleY(playhead));
        }

        private void Update()
        {
            FollowScoreboard();
            Tick(Time.deltaTime);
            if (Time.time >= _nextRefresh)
            {
                _nextRefresh = Time.time + HudTuning.MinimapUpdateInterval;
                Refresh();
            }
        }
    }
}
