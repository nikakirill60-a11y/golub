using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// The climate of a camouflage slot. The Blitz 4.8.0 shop
    /// (CamouflagesShop.yaml SlotsPanel) draws one slot per kind, in this
    /// order, and a battle uses the slot of its map's kind.
    /// </summary>
    public enum CamouflageKind
    {
        Summer = 0,
        Winter = 1,
        Desert = 2,
    }

    /// <summary>One camouflage of the ported 4.8.0 camouflages.xml.</summary>
    public sealed class CamouflageInfo
    {
        private readonly List<CamouflageKind> _kinds = new List<CamouflageKind>();

        public string Id { get; private set; }

        /// <summary>The camouflages.xml numeric id.</summary>
        public int SourceId { get; private set; }

        public string NameKey { get; private set; }

        public string DescriptionKey { get; private set; }

        public string Group { get; private set; }

        /// <summary>The 4.8.0 price category, e.g. <c>disposable-cheap</c>.</summary>
        public string Category { get; private set; }

        /// <summary>Atlas region of the shop tile.</summary>
        public string TileRegion { get; private set; }

        /// <summary>Atlas region an occupied slot shows.</summary>
        public string SlotIconRegion { get; private set; }

        /// <summary>The slots the camouflage can be installed into.</summary>
        public IReadOnlyList<CamouflageKind> Kinds
        {
            get { return _kinds; }
        }

        public bool Fits(CamouflageKind kind)
        {
            return _kinds.Contains(kind);
        }

        internal static CamouflageInfo FromJson(string id, JsonNode node)
        {
            var info = new CamouflageInfo
            {
                Id = id,
                SourceId = node["id"].AsInt(),
                NameKey = node["name"].AsString(),
                DescriptionKey = node["description"].AsString(),
                Group = node["group"].AsString(),
                Category = node["category"].AsString(),
                TileRegion = node["tile"].AsString(),
                SlotIconRegion = node["slot_icon"].AsString(),
            };
            foreach (var item in node["kinds"].Items)
            {
                CamouflageKind kind;
                if (!CamouflageCatalog.TryParseKind(item.AsString(), out kind))
                {
                    throw new InvalidOperationException(
                        "Camouflage " + id + " names an unknown kind " + item.AsString());
                }

                if (!info._kinds.Contains(kind))
                {
                    info._kinds.Add(kind);
                }
            }

            return info;
        }
    }

    /// <summary>A camouflages.xml group: one header of the shop list.</summary>
    public sealed class CamouflageGroupInfo
    {
        public string Id { get; private set; }

        public string NameKey { get; private set; }

        /// <summary>camouflages.xml group rarity.</summary>
        public int Rarity { get; private set; }

        internal static CamouflageGroupInfo FromJson(string id, JsonNode node)
        {
            return new CamouflageGroupInfo
            {
                Id = id,
                NameKey = node["name"].AsString(),
                Rarity = node["rarity"].AsInt(),
            };
        }
    }

    /// <summary>
    /// How one camouflage lies on one tank: the DAVA TILED_DECAL_MASK
    /// parameters the porter resolved from the vehicle, nation and
    /// camouflage defaults.
    /// </summary>
    public sealed class CamouflagePaint
    {
        /// <summary>decalTileColor alpha: how strongly the pattern covers the paint.</summary>
        public float Alpha { get; private set; }

        /// <summary>decalTileCoordScale.</summary>
        public float ScaleU { get; private set; }

        public float ScaleV { get; private set; }

        /// <summary>decalTileCoordOffset.</summary>
        public float OffsetU { get; private set; }

        public float OffsetV { get; private set; }

        /// <summary>Resources path of the pattern texture.</summary>
        public string Texture { get; private set; }

        internal static CamouflagePaint FromJson(JsonNode node)
        {
            var scale = node["scale"];
            var offset = node["offset"];
            return new CamouflagePaint
            {
                Alpha = Clamp01(node["alpha"].AsFloat(1f)),
                ScaleU = scale.Count > 0 ? scale[0].AsFloat(1f) : 1f,
                ScaleV = scale.Count > 1 ? scale[1].AsFloat(1f) : 1f,
                OffsetU = offset.Count > 0 ? offset[0].AsFloat() : 0f,
                OffsetV = offset.Count > 1 ? offset[1].AsFloat() : 0f,
                Texture = node["texture"].AsString(),
            };
        }

        private static float Clamp01(float value)
        {
            return value < 0f ? 0f : (value > 1f ? 1f : value);
        }
    }

    /// <summary>The camouflage data of one supported tank.</summary>
    public sealed class TankCamouflage
    {
        private readonly Dictionary<string, string> _masks =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private readonly Dictionary<string, CamouflagePaint> _paints =
            new Dictionary<string, CamouflagePaint>(StringComparer.Ordinal);

        public string Tank { get; private set; }

        /// <summary>The client the model came from: <c>480</c> or <c>1120</c>.</summary>
        public string Pack { get; private set; }

        /// <summary>The vehicle's camouflage <c>priceFactor</c>.</summary>
        public float PriceFactor { get; private set; }

        /// <summary>Credits for one camouflage on this tank.</summary>
        public int Price { get; private set; }

        /// <summary>Albedo texture id of the model to the Resources path of its mask.</summary>
        public IReadOnlyDictionary<string, string> Masks
        {
            get { return _masks; }
        }

        /// <summary>The Resources path of the mask of one albedo texture, or null.</summary>
        public string MaskOf(string albedoId)
        {
            string mask;
            return !string.IsNullOrEmpty(albedoId) && _masks.TryGetValue(albedoId, out mask)
                ? mask
                : null;
        }

        public bool Has(string camouflage)
        {
            return !string.IsNullOrEmpty(camouflage) && _paints.ContainsKey(camouflage);
        }

        /// <summary>How the camouflage lies on this tank, or null when it has no such paint.</summary>
        public CamouflagePaint PaintOf(string camouflage)
        {
            CamouflagePaint paint;
            return !string.IsNullOrEmpty(camouflage) && _paints.TryGetValue(camouflage, out paint)
                ? paint
                : null;
        }

        internal static TankCamouflage FromJson(string tank, JsonNode node)
        {
            var info = new TankCamouflage
            {
                Tank = tank,
                Pack = node["pack"].AsString(),
                PriceFactor = node["price_factor"].AsFloat(1f),
                Price = Math.Max(0, node["price"].AsInt()),
            };
            foreach (var mask in node["masks"].Fields)
            {
                if (!string.IsNullOrEmpty(mask.Value.AsString()))
                {
                    info._masks[mask.Key] = mask.Value.AsString();
                }
            }

            foreach (var paint in node["camouflages"].Fields)
            {
                info._paints[paint.Key] = CamouflagePaint.FromJson(paint.Value);
            }

            return info;
        }
    }

    /// <summary>
    /// The ported Blitz 4.8.0 camouflages (Tools/port_blitz_camouflage.py,
    /// <c>Data/camouflages.json</c>): the camouflage list, which tanks take
    /// a camouflage and how it lies on them, the price and the concealment
    /// bonus, and the climate of every map.
    /// </summary>
    public sealed class CamouflageCatalog
    {
        /// <summary>Resources/Data name of the ported camouflage document.</summary>
        public const string DataResource = "camouflages";

        /// <summary>The summer, winter and desert slots of every tank.</summary>
        public const int SlotCount = 3;

        private readonly List<CamouflageInfo> _all = new List<CamouflageInfo>();

        private readonly List<CamouflageGroupInfo> _groups = new List<CamouflageGroupInfo>();

        private readonly Dictionary<string, TankCamouflage> _tanks =
            new Dictionary<string, TankCamouflage>(StringComparer.Ordinal);

        private readonly Dictionary<string, string> _unsupported =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private readonly Dictionary<string, CamouflageKind> _mapKinds =
            new Dictionary<string, CamouflageKind>(StringComparer.Ordinal);

        /// <summary>The catalog of a build without camouflage data: nothing is supported.</summary>
        public static readonly CamouflageCatalog Empty = new CamouflageCatalog();

        /// <summary>The 4.8.0 shop price every vehicle price factor scales.</summary>
        public int BasePrice { get; private set; }

        /// <summary>Added to the camouflage factor while a camouflage is on.</summary>
        public float ConcealmentBonus { get; private set; }

        public IReadOnlyList<CamouflageInfo> All
        {
            get { return _all; }
        }

        public IReadOnlyList<CamouflageGroupInfo> Groups
        {
            get { return _groups; }
        }

        /// <summary>Tanks the port knows cannot carry a camouflage, with the reason.</summary>
        public IReadOnlyDictionary<string, string> Unsupported
        {
            get { return _unsupported; }
        }

        /// <summary>Number of tanks that take a camouflage.</summary>
        public int SupportedCount
        {
            get { return _tanks.Count; }
        }

        public static CamouflageCatalog Parse(string json)
        {
            JsonNode node;
            if (string.IsNullOrEmpty(json) || !JsonNode.TryParse(json, out node)
                || node.Kind != JsonKind.Object)
            {
                throw new InvalidOperationException("The camouflage data is missing or unreadable");
            }

            return FromJson(node);
        }

        public static CamouflageCatalog FromJson(JsonNode node)
        {
            if (node == null || node.Kind != JsonKind.Object)
            {
                throw new InvalidOperationException("The camouflage data is not an object");
            }

            var catalog = new CamouflageCatalog
            {
                BasePrice = Math.Max(0, node["base_price"].AsInt()),
                ConcealmentBonus = Math.Max(0f, node["concealment_bonus"].AsFloat()),
            };

            var slots = node["slots"];
            if (slots.Count != SlotCount)
            {
                throw new InvalidOperationException("The camouflage data has no summer/winter/desert slots");
            }

            for (var i = 0; i < SlotCount; i++)
            {
                CamouflageKind kind;
                if (!TryParseKind(slots[i].AsString(), out kind) || (int)kind != i)
                {
                    throw new InvalidOperationException(
                        "The camouflage slot " + i + " is " + slots[i].AsString());
                }
            }

            foreach (var group in node["groups"].Fields)
            {
                catalog._groups.Add(CamouflageGroupInfo.FromJson(group.Key, group.Value));
            }

            foreach (var camouflage in node["camouflages"].Fields)
            {
                var info = CamouflageInfo.FromJson(camouflage.Key, camouflage.Value);
                if (catalog.Group(info.Group) == null)
                {
                    throw new InvalidOperationException(
                        "Camouflage " + info.Id + " names an unknown group " + info.Group);
                }

                catalog._all.Add(info);
            }

            foreach (var map in node["map_kinds"].Fields)
            {
                CamouflageKind kind;
                if (TryParseKind(map.Value.AsString(), out kind))
                {
                    catalog._mapKinds[map.Key] = kind;
                }
            }

            foreach (var tank in node["tanks"].Fields)
            {
                catalog._tanks[tank.Key] = TankCamouflage.FromJson(tank.Key, tank.Value);
            }

            foreach (var tank in node["unsupported"].Fields)
            {
                catalog._unsupported[tank.Key] = tank.Value.AsString();
            }

            return catalog;
        }

        public static bool TryParseKind(string name, out CamouflageKind kind)
        {
            switch (name)
            {
                case "summer":
                    kind = CamouflageKind.Summer;
                    return true;
                case "winter":
                    kind = CamouflageKind.Winter;
                    return true;
                case "desert":
                    kind = CamouflageKind.Desert;
                    return true;
                default:
                    kind = CamouflageKind.Summer;
                    return false;
            }
        }

        /// <summary>The kind of a slot index of <c>TankSaveData.Camouflages</c>.</summary>
        public static CamouflageKind KindOfSlot(int slot)
        {
            return slot == 1 ? CamouflageKind.Winter
                : slot == 2 ? CamouflageKind.Desert
                : CamouflageKind.Summer;
        }

        /// <summary>The 4.8.0 <c>#camouflages:Slots/*</c> caption of a slot.</summary>
        public static string SlotNameKey(CamouflageKind kind)
        {
            switch (kind)
            {
                case CamouflageKind.Winter: return "#camouflages:Slots/Winter";
                case CamouflageKind.Desert: return "#camouflages:Slots/Desert";
                default: return "#camouflages:Slots/Summer";
            }
        }

        public CamouflageInfo Get(string id)
        {
            if (string.IsNullOrEmpty(id)) { return null; }
            for (var i = 0; i < _all.Count; i++)
            {
                if (string.Equals(_all[i].Id, id, StringComparison.Ordinal))
                {
                    return _all[i];
                }
            }

            return null;
        }

        public CamouflageGroupInfo Group(string id)
        {
            if (string.IsNullOrEmpty(id)) { return null; }
            for (var i = 0; i < _groups.Count; i++)
            {
                if (string.Equals(_groups[i].Id, id, StringComparison.Ordinal))
                {
                    return _groups[i];
                }
            }

            return null;
        }

        public bool Supports(string tank)
        {
            return Tank(tank) != null;
        }

        /// <summary>The camouflage data of a tank, or null when it takes none.</summary>
        public TankCamouflage Tank(string tank)
        {
            TankCamouflage info;
            return !string.IsNullOrEmpty(tank) && _tanks.TryGetValue(tank, out info) ? info : null;
        }

        /// <summary>Why the port left a tank without camouflage, or null.</summary>
        public string UnsupportedReason(string tank)
        {
            string reason;
            return !string.IsNullOrEmpty(tank) && _unsupported.TryGetValue(tank, out reason)
                ? reason
                : null;
        }

        /// <summary>Credits one camouflage costs on a tank; 0 when the tank takes none.</summary>
        public int Price(string tank)
        {
            var info = Tank(tank);
            return info == null ? 0 : info.Price;
        }

        /// <summary>Credits one camouflage costs on a tank; 0 when the tank cannot wear it.</summary>
        public int Price(string tank, string camouflage)
        {
            var info = Tank(tank);
            return info != null && info.Has(camouflage) ? info.Price : 0;
        }

        /// <summary>The camouflages a tank can take into the slot of one kind, in shop order.</summary>
        public List<CamouflageInfo> Available(string tank, CamouflageKind kind)
        {
            var available = new List<CamouflageInfo>();
            var info = Tank(tank);
            if (info == null) { return available; }
            for (var i = 0; i < _all.Count; i++)
            {
                if (_all[i].Fits(kind) && info.Has(_all[i].Id))
                {
                    available.Add(_all[i]);
                }
            }

            return available;
        }

        /// <summary>Whether the camouflage can go into the slot of a kind on a tank.</summary>
        public bool CanInstall(string tank, CamouflageKind kind, string camouflage)
        {
            var info = Get(camouflage);
            var vehicle = Tank(tank);
            return info != null && vehicle != null && info.Fits(kind) && vehicle.Has(camouflage);
        }

        /// <summary>The climate of a battle map; false for maps the port has no kind for.</summary>
        public bool TryKindOfMap(string mapId, out CamouflageKind kind)
        {
            kind = CamouflageKind.Summer;
            return !string.IsNullOrEmpty(mapId) && _mapKinds.TryGetValue(mapId, out kind);
        }
    }
}
