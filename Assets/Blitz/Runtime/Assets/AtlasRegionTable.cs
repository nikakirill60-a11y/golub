using System.Collections.Generic;
using System.Globalization;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Ui;
using UnityEngine;

namespace BlitzLegacy.Runtime.Assets
{
    /// <summary>
    /// Godot AtlasTexture / NinePatchRect regions, rebuilt as Unity sprites at
    /// runtime. The rects come from a JSON dump of the original .tres files, so
    /// re-exporting the atlases never means editing C#.
    /// </summary>
    public sealed class AtlasRegionTable
    {
        private sealed class Region
        {
            public string Texture;
            public float X;
            public float Y;
            public float W;
            public float H;
            public Vector4 Border;
        }

        private readonly Dictionary<string, Region> _regions =
            new Dictionary<string, Region>();

        private readonly Dictionary<string, Sprite> _sprites =
            new Dictionary<string, Sprite>();

        private readonly Dictionary<string, Sprite> _ninePatches =
            new Dictionary<string, Sprite>();

        public int Count
        {
            get { return _regions.Count; }
        }

        public static AtlasRegionTable Parse(string json)
        {
            var table = new AtlasRegionTable();
            JsonNode root;
            if (!JsonNode.TryParse(json, out root))
            {
                Debug.LogError("[Blitz] atlas_regions.json could not be parsed");
                return table;
            }

            foreach (var entry in root["regions"].Fields)
            {
                var node = entry.Value;
                var region = new Region();
                region.Texture = node["texture"].AsString();
                region.X = node["x"].AsFloat();
                region.Y = node["y"].AsFloat();
                region.W = node["w"].AsFloat();
                region.H = node["h"].AsFloat();

                var border = node["border"];
                if (border.Count == 4)
                {
                    // Godot patch margins are left/top/right/bottom; Unity sprite
                    // borders are left/bottom/right/top.
                    region.Border = new Vector4(
                        border[0].AsFloat(),
                        border[3].AsFloat(),
                        border[2].AsFloat(),
                        border[1].AsFloat());
                }

                table._regions[entry.Key] = region;
            }

            return table;
        }

        /// <summary>
        /// Optional menu skin consulted before the atlas. The modern menu
        /// style uses it to swap panel and button chrome for its own sprites
        /// while every icon keeps its original artwork. Null (the default and
        /// the classic style) leaves <see cref="Get"/> exactly as it was.
        /// </summary>
        public IAtlasSkin Skin { get; set; }

        public bool Contains(string name)
        {
            return !string.IsNullOrEmpty(name) && _regions.ContainsKey(name);
        }

        /// <summary>Authored pixel size and Unity-order border of a region.</summary>
        public bool TryGetMetrics(string name, out Vector2 size, out Vector4 border)
        {
            Region region;
            if (string.IsNullOrEmpty(name) || !_regions.TryGetValue(name, out region))
            {
                size = Vector2.zero;
                border = Vector4.zero;
                return false;
            }

            size = new Vector2(region.W, region.H);
            border = region.Border;
            return true;
        }

        public Sprite Get(string name)
        {
            if (Skin != null && !string.IsNullOrEmpty(name))
            {
                var skinned = Skin.Resolve(name, this);
                if (skinned != null)
                {
                    return skinned;
                }
            }

            return GetOriginal(name);
        }

        /// <summary>The atlas artwork itself, bypassing any <see cref="Skin"/>.</summary>
        public Sprite GetOriginal(string name)
        {
            Sprite cached;
            if (_sprites.TryGetValue(name, out cached))
            {
                return cached;
            }

            Region region;
            if (!_regions.TryGetValue(name, out region))
            {
                Debug.LogWarning("[Blitz] unknown atlas region: " + name);
                return null;
            }

            var texture = BlitzResources.LoadTexture(region.Texture);
            if (texture == null)
            {
                Debug.LogWarning("[Blitz] missing texture: " + region.Texture);
                return null;
            }

            var y = AtlasRect.FlipY(texture.height, region.Y, region.H);
            var rect = new Rect(region.X, y, region.W, region.H);
            var sprite = Sprite.Create(
                texture,
                rect,
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                region.Border);
            sprite.name = name;

            _sprites[name] = sprite;
            return sprite;
        }

        /// <summary>
        /// The atlas artwork the way a Godot NinePatchRect draws it: a scene
        /// sets its own patch_margin_* on the node, which need not match the
        /// border the region was exported with (border*/hangar_select_tile
        /// slices its 46 px tile at 20, not at the atlas' 14). Uniform
        /// margin, bypassing any <see cref="Skin"/>; null when the region is
        /// unknown.
        /// </summary>
        public Sprite GetNinePatch(string name, float margin)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            var key = name + "#" + margin.ToString("R", CultureInfo.InvariantCulture);
            Sprite cached;
            if (_ninePatches.TryGetValue(key, out cached))
            {
                return cached;
            }

            var original = GetOriginal(name);
            if (original == null)
            {
                return null;
            }

            var sprite = Sprite.Create(
                original.texture,
                original.rect,
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(margin, margin, margin, margin));
            sprite.name = name;

            _ninePatches[key] = sprite;
            return sprite;
        }
    }

    /// <summary>A replacement source for atlas regions; see <see cref="AtlasRegionTable.Skin"/>.</summary>
    public interface IAtlasSkin
    {
        /// <summary>The replacement sprite, or null to use the atlas artwork.</summary>
        Sprite Resolve(string region, AtlasRegionTable source);
    }
}
