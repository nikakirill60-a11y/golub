using System;
using System.Collections.Generic;
using System.Globalization;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>One tile of the ability status row.</summary>
    public struct BattleStatusTile
    {
        /// <summary>Stable identity: the row rebuilds its tiles when the ids change.</summary>
        public string Id;

        /// <summary>Resources path of the artwork.</summary>
        public string Icon;

        /// <summary>Seconds left of the countdown, 0 for none.</summary>
        public float Seconds;

        /// <summary>Full length of the countdown, for the shade and the progress bar.</summary>
        public float Total;

        /// <summary>A timed effect (or an armed charge) rather than a cooldown.</summary>
        public bool Running;

        /// <summary>An effect another crew put on this tank.</summary>
        public bool Debuff;

        /// <summary>Charged and waiting for its trigger: framed without a countdown.</summary>
        public bool Armed;

        /// <summary>Used up for this life: dimmed.</summary>
        public bool Spent;
    }

    /// <summary>
    /// The ability status row above the bottom health bar (the Uprising ability_status
    /// strip), one for every mode: passive and self-activating abilities with their
    /// cooldowns, timed effects while they run and effects received from other crews. The
    /// crew's battle mode fills it (<see cref="BattleHudMode.CollectStatuses"/>); the
    /// standard battle leaves it empty and hidden. The HUD refreshes it every frame.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleStatusRow : MonoBehaviour
    {
        /// <summary>Uprising ability_status tile size and pitch.</summary>
        public const float TileWidth = 44f, TileHeight = 48f, TileStep = 45f;

        public const string BackgroundArt = "Uprising/Icons/skills_bg";

        private static readonly Color DebuffColor = new Color(1f, 0.3f, 0.3f);
        private static readonly Color RunningColor = new Color(1f, 0.7f, 0.25f);
        private static readonly Color CooldownColor = new Color(0.8f, 0.85f, 1f);

        private sealed class Tile
        {
            public Image Icon, Shade, Bar;
            public RectTransform Border;
            public Image[] Edges;
            public Text Timer;
            public int Shown = -1;
        }

        private BattleTank _tank;
        private BattleSession _session;
        private readonly List<BattleStatusTile> _tiles = new List<BattleStatusTile>();
        private readonly List<string> _ids = new List<string>();
        private readonly List<Tile> _built = new List<Tile>();
        private readonly Dictionary<string, Sprite> _art = new Dictionary<string, Sprite>(StringComparer.Ordinal);

        /// <summary>Ids of the tiles currently built, left to right.</summary>
        public IReadOnlyList<string> Ids { get { return _ids; } }

        public RectTransform Root { get { return (RectTransform)transform; } }

        public static BattleStatusRow Create(RectTransform hudRoot, BattleTank tank)
        {
            var node = UiFactory.Node("ability_status", hudRoot);
            var row = node.gameObject.AddComponent<BattleStatusRow>();
            row._tank = tank;
            row._session = tank != null ? tank.GetComponentInParent<BattleSession>() : null;
            node.gameObject.SetActive(false);
            return row;
        }

        /// <summary>
        /// Reads the mode's tiles: shown while the battle runs and the crew is alive, and
        /// only when the mode has something to show.
        /// </summary>
        public void Refresh()
        {
            _tiles.Clear();
            var started = _session == null || _session.Started;
            if (_tank != null && !_tank.Dead && started)
            {
                BattleHudMode.Of(_tank).CollectStatuses(_tiles);
            }
            if (!SameIds()) { Build(); }

            var visible = _ids.Count > 0;
            if (gameObject.activeSelf != visible) { gameObject.SetActive(visible); }
            if (!visible) { return; }

            var width = Mathf.Max(TileWidth, _ids.Count * TileStep - 1f);
            GodotRect.Apply(Root, 0.5f, 1f, 0.5f, 1f, -width / 2f, -105f, width / 2f, -57f);
            var parent = Root.parent as RectTransform;
            var room = parent != null ? parent.rect.width - 48f : width;
            var fit = Mathf.Min(1f, Mathf.Max(TileWidth, room) / width);
            Root.localScale = Vector3.one * fit;
            Root.anchoredPosition = new Vector2(0f, 57f + 24f * fit);
            for (var i = 0; i < _built.Count; i++) { Draw(_built[i], _tiles[i]); }
        }

        private bool SameIds()
        {
            if (_tiles.Count != _ids.Count) { return false; }

            for (var i = 0; i < _ids.Count; i++)
            {
                if (!string.Equals(_ids[i], _tiles[i].Id, StringComparison.Ordinal)) { return false; }
            }
            return true;
        }

        private static void Draw(Tile tile, BattleStatusTile data)
        {
            var seconds = Mathf.Max(0f, data.Seconds);
            var fraction = Mathf.Clamp01(seconds / Mathf.Max(0.01f, data.Total));
            var cooling = seconds > 0f && !data.Running;
            tile.Icon.color = new Color(1f, 1f, 1f, data.Spent || cooling ? 0.35f : 1f);
            tile.Shade.gameObject.SetActive(cooling);
            if (cooling) { GodotRect.TopLeft(tile.Shade.rectTransform, 2f, 42f - 40f * fraction, 42f, 42f); }

            var color = data.Debuff ? DebuffColor : data.Running ? RunningColor : CooldownColor;
            tile.Border.gameObject.SetActive(seconds > 0f || data.Armed);
            for (var e = 0; e < tile.Edges.Length; e++) { tile.Edges[e].color = color; }
            tile.Bar.gameObject.SetActive(seconds > 0f);
            tile.Bar.color = color;
            if (seconds > 0f) { GodotRect.TopLeft(tile.Bar.rectTransform, 2f, 44f, 2f + 40f * fraction, 46f); }

            var shown = seconds > 0f ? Mathf.CeilToInt(seconds) : 0;
            tile.Timer.gameObject.SetActive(shown > 0);
            if (shown != tile.Shown)
            {
                tile.Shown = shown;
                tile.Timer.text = shown > 0 ? shown.ToString(CultureInfo.InvariantCulture) : string.Empty;
            }
        }

        private void Build()
        {
            for (var i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i).gameObject;
                child.SetActive(false);
                Release(child);
            }
            _ids.Clear();
            _built.Clear();
            if (_tiles.Count == 0) { return; }

            var font = BattleHudMode.Of(_tank).StatusFont;
            if (font == null) { font = BlitzResources.LoadFont(BlitzResources.BoldFontName); }
            for (var i = 0; i < _tiles.Count; i++)
            {
                var data = _tiles[i];
                _ids.Add(data.Id);
                var node = UiFactory.Node("status" + i, transform);
                GodotRect.TopLeft(node, i * TileStep, 0f, i * TileStep + TileWidth, TileHeight);
                var background = UiFactory.Picture("background", node, Art(BackgroundArt));
                GodotRect.TopLeft(background.rectTransform, 2f, 2f, 42f, 42f);

                var tile = new Tile();
                tile.Icon = UiFactory.Picture("icon", node, Art(data.Icon));
                tile.Icon.preserveAspect = true;
                GodotRect.TopLeft(tile.Icon.rectTransform, 5f, 5f, 39f, 39f);
                tile.Shade = UiFactory.Picture("cooldown_shade", node, null);
                tile.Shade.color = new Color(0f, 0f, 0f, 0.5f);
                GodotRect.TopLeft(tile.Shade.rectTransform, 2f, 2f, 42f, 42f);
                tile.Border = UiFactory.Node("timer_border", node);
                GodotRect.TopLeft(tile.Border, 2f, 2f, 42f, 42f);
                BattleModeScreen.Border(tile.Border, Color.white, 1f);
                tile.Edges = tile.Border.GetComponentsInChildren<Image>(true);
                tile.Bar = UiFactory.Picture("progress", node, null);
                GodotRect.TopLeft(tile.Bar.rectTransform, 2f, 44f, 42f, 46f);
                tile.Timer = UiFactory.Label(
                    "time", node, font, 17, Color.white, TextAnchor.MiddleCenter, string.Empty);
                GodotRect.TopLeft(tile.Timer.rectTransform, 0f, 8f, 44f, 36f);
                var outline = tile.Timer.gameObject.AddComponent<Outline>();
                outline.effectColor = Color.black;
                outline.effectDistance = new Vector2(4f, -4f);
                _built.Add(tile);
            }
        }

        private Sprite Art(string resource)
        {
            if (string.IsNullOrEmpty(resource)) { return null; }

            Sprite sprite;
            if (_art.TryGetValue(resource, out sprite)) { return sprite; }

            var texture = Resources.Load<Texture2D>(resource);
            sprite = texture == null ? null : Sprite.Create(
                texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
            _art[resource] = sprite;
            return sprite;
        }

        private static void Release(UnityEngine.Object value)
        {
            if (value == null) { return; }

            if (Application.isPlaying) { Destroy(value); } else { DestroyImmediate(value); }
        }

        private void OnDestroy()
        {
            foreach (var sprite in _art.Values) { Release(sprite); }
            _art.Clear();
        }
    }
}
