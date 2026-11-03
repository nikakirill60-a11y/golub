using System;
using System.Collections.Generic;

namespace BlitzLegacy.Core.Mods.Rempack
{
    /// <summary>
    /// Constants of the Remaster Package (<c>.rempack</c>) format. The game and the
    /// SDK compile this very file, so both agree on what a package may contain.
    /// </summary>
    public static class RempackFormat
    {
        public const string Extension = ".rempack";
        public const string Name = "rempack";
        public const string ManifestFile = "rempack.json";
        public const string MimeType = "application/x-rempack";

        /// <summary>The newest container/manifest version this build understands.</summary>
        public const int FormatVersion = 1;

        public const string TanksFeature = "tanks";
        public const string HangarsFeature = "hangars";
        public const string MapsFeature = "maps";
        public const string OverridesFeature = "overrides";

        /// <summary>The feature a kind of content needs in <c>game.features</c>.</summary>
        public static string Feature(RempackContentKind kind)
        {
            return Folder(kind);
        }

        /// <summary>The folder a kind of content lives in, also its manifest key.</summary>
        public static string Folder(RempackContentKind kind)
        {
            switch (kind)
            {
                case RempackContentKind.Tank: return "tanks";
                case RempackContentKind.Hangar: return "hangars";
                case RempackContentKind.Map: return "maps";
                default: throw new ArgumentOutOfRangeException("kind");
            }
        }

        /// <summary>The definition file of one piece of content, e.g. <c>tanks/t34/tank.json</c>.</summary>
        public static string DefinitionPath(RempackContentKind kind, string localId)
        {
            return Folder(kind) + "/" + localId + "/" + DefinitionFile(kind);
        }

        public static string DefinitionFile(RempackContentKind kind)
        {
            switch (kind)
            {
                case RempackContentKind.Tank: return "tank.json";
                case RempackContentKind.Hangar: return "hangar.json";
                case RempackContentKind.Map: return "map.json";
                default: throw new ArgumentOutOfRangeException("kind");
            }
        }

        /// <summary>The singular word used in stock references: <c>stock:tank/&lt;id&gt;</c>.</summary>
        public static string KindWord(RempackContentKind kind)
        {
            switch (kind)
            {
                case RempackContentKind.Tank: return "tank";
                case RempackContentKind.Hangar: return "hangar";
                case RempackContentKind.Map: return "map";
                default: throw new ArgumentOutOfRangeException("kind");
            }
        }

        public static readonly RempackContentKind[] Kinds =
        {
            RempackContentKind.Tank, RempackContentKind.Hangar, RempackContentKind.Map,
        };
    }

    public enum RempackContentKind
    {
        Tank,
        Hangar,
        Map,
    }

    /// <summary>
    /// Features a game build can load, each with its highest version. A package names
    /// the features it needs so an older game says "update the game" instead of failing
    /// half way through a load.
    /// </summary>
    public sealed class RempackFeatureSet
    {
        private readonly Dictionary<string, int> _features = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// What this build of the game loads; the SDK checks packages against the same
        /// list. A feature joins it in the change that adds its loader.
        /// </summary>
        public static readonly RempackFeatureSet Game = new RempackFeatureSet();

        /// <summary>A set of <c>name@version</c> (or bare <c>name</c>, version 1) entries.</summary>
        public RempackFeatureSet(params string[] features)
        {
            foreach (var feature in features ?? new string[0])
            {
                string name;
                int version;
                if (!Split(feature, out name, out version))
                {
                    throw new ArgumentException("Not a feature: " + feature);
                }

                int known;
                if (!_features.TryGetValue(name, out known) || known < version)
                {
                    _features[name] = version;
                }
            }
        }

        /// <summary>Every feature as <c>name@version</c>, sorted.</summary>
        public IEnumerable<string> Names
        {
            get
            {
                var names = new List<string>();
                foreach (var pair in _features)
                {
                    names.Add(pair.Key + "@" + pair.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                names.Sort(StringComparer.Ordinal);
                return names;
            }
        }

        /// <summary>Whether <c>name@version</c> (or a bare <c>name</c>, meaning version 1) is provided.</summary>
        public bool Provides(string feature)
        {
            string name;
            int version;
            int provided;
            return Split(feature, out name, out version) && _features.TryGetValue(name, out provided) && version <= provided;
        }

        /// <summary><c>name@version</c>: a lowercase name and a positive version.</summary>
        public static bool Split(string feature, out string name, out int version)
        {
            name = null;
            version = 0;
            if (string.IsNullOrEmpty(feature) || feature.Length > 64)
            {
                return false;
            }

            var at = feature.IndexOf('@');
            var head = at < 0 ? feature : feature.Substring(0, at);
            if (head.Length == 0)
            {
                return false;
            }

            foreach (var c in head)
            {
                if (!((c >= 'a' && c <= 'z') || c == '_'))
                {
                    return false;
                }
            }

            var parsed = 1;
            if (at >= 0)
            {
                var tail = feature.Substring(at + 1);
                if (tail.Length == 0 || tail.Length > 4)
                {
                    return false;
                }

                parsed = 0;
                foreach (var c in tail)
                {
                    if (c < '0' || c > '9')
                    {
                        return false;
                    }

                    parsed = parsed * 10 + (c - '0');
                }
            }

            if (parsed < 1)
            {
                return false;
            }

            name = head;
            version = parsed;
            return true;
        }
    }

    /// <summary>
    /// Hard limits refuse a package; soft limits only warn and mark it as heavy.
    /// Sizes are bytes. Chosen for a mid-range Android phone (4 GB RAM) and checked by
    /// the SDK before a package is ever shipped.
    /// </summary>
    public static class RempackLimits
    {
        public const long ArchiveBytes = 1L << 30;
        public const long ArchiveSoftBytes = 300L * 1000 * 1000;
        public const long UnpackedBytes = 2L << 30;
        public const int Entries = 20000;
        public const long FileBytes = 512L << 20;
        public const int PathLength = 240;
        public const int SegmentLength = 96;

        /// <summary>Entries below this size are never ratio-checked.</summary>
        public const long RatioCheckFloor = 1L << 20;

        /// <summary>Deflate tops out near 1032:1; real content stays far below this.</summary>
        public const long CompressionRatio = 200;

        public const long ManifestBytes = 256 * 1024;
        public const long JsonBytes = 4L << 20;
        public const long JsonSoftBytes = 256 * 1024;
        public const int JsonDepth = 64;

        public const long ModelBytes = 256L << 20;
        public const long GltfJsonBytes = 16L << 20;
        public const int TextureDimension = 8192;
        public const int TextureSoftDimension = 4096;
        public const long ImageBytes = 64L << 20;
        public const int IconDimension = 1024;

        public const long AudioBytes = 64L << 20;
        public const int AudioChannels = 2;
        public const int AudioSampleRate = 48000;

        public const int NameLength = 64;
        public const int DescriptionLength = 2000;
        public const int Authors = 16;
        public const int Dependencies = 32;
        public const int ContentItems = 256;
        public const int Previews = 8;
        public const int ScriptBytes = 512 * 1024;
    }

    /// <summary>Per-content budgets: the first number warns, the second refuses.</summary>
    public sealed class RempackBudget
    {
        public RempackBudget(string name, long soft, long hard)
        {
            Name = name;
            Soft = soft;
            Hard = hard;
        }

        public string Name { get; private set; }
        public long Soft { get; private set; }
        public long Hard { get; private set; }

        public static readonly RempackBudget TankTriangles = new RempackBudget("tank triangles", 150000, 500000);
        public static readonly RempackBudget TankMaterials = new RempackBudget("tank materials", 16, 64);
        public static readonly RempackBudget TankTextureMemory = new RempackBudget("tank texture memory", 64L << 20, 256L << 20);
        public static readonly RempackBudget TankNodes = new RempackBudget("tank nodes", 512, 4096);
        public static readonly RempackBudget TankArmorPlates = new RempackBudget("tank armour plates", 512, 2048);
        public static readonly RempackBudget HangarTriangles = new RempackBudget("hangar triangles", 1500000, 5000000);
        public static readonly RempackBudget HangarTextureMemory = new RempackBudget("hangar texture memory", 256L << 20, 1L << 30);
        public static readonly RempackBudget HangarNodes = new RempackBudget("hangar nodes", 5000, 50000);
        public static readonly RempackBudget MapTriangles = new RempackBudget("map triangles", 3000000, 10000000);
        public static readonly RempackBudget MapTextureMemory = new RempackBudget("map texture memory", 512L << 20, 1536L << 20);
        public static readonly RempackBudget MapNodes = new RempackBudget("map objects", 50000, 200000);
        public static readonly RempackBudget MapSize = new RempackBudget("map size (m)", 2000, 4000);
    }
}
