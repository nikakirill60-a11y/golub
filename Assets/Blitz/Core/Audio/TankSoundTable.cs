using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Audio
{
    /// <summary>Port of Godot <c>ModuleGun.ShootSound</c>.</summary>
    public enum ShootSound
    {
        ShotAuto = 0,
        ShotSmall = 1,
        ShotMedium = 2,
        ShotMain = 3,
        ShotLarge = 4,
        ShotHuge = 5,

        /// <summary>
        /// <c>shot_atgm</c> of the Blitz client's gun_effects.xml: the launch
        /// report of a guided-rocket launcher. Godot's ModuleGun stops at
        /// shot_huge, so this value only ever comes from a 4.8.0 card.
        /// </summary>
        ShotAtgm = 6,
    }

    /// <summary>Port of Godot <c>TankInfoMain.EngineSound</c>.</summary>
    public enum EngineSound
    {
        DeLt = 0,
        DeMt = 1,
        DeHt = 2,
        DeTraktor = 3,
        UkLt = 4,
        UkMt = 5,
        UkHt = 6,
        UkTraktor = 7,
        UssrLt = 8,
        UssrMt = 9,
        UssrHt = 10,
        UssrTraktor = 11,
        UsaLt = 12,
        UsaMt = 13,
        UsaHt = 14,
        UsaTraktor = 15,
    }

    /// <summary>Port of Godot <c>TankInfoMain.TrackSound</c>.</summary>
    public enum TrackSound
    {
        Lt = 0,
        Mt = 1,
        Ht = 2,
        Traktor = 3,
    }

    /// <summary>Port of Godot <c>Audio.CrewMessage</c>, in source order.</summary>
    public enum CrewMessage
    {
        BattleStart = 0,
        AllyKilled = 1,
        ArmorNotPierce = 2,
        ArmorPierced = 3,
        ArmorNotHit = 4,
        Ricoshet = 5,
        EnemyIgnited = 6,
        EnemyKilled = 7,
        FireStarted = 8,
        FireStopped = 9,
        ModuleTrackCrit = 10,
        ModuleTrackDestroyed = 11,
        ModuleTrackFixed = 12,
        ModuleAmmoBayCrit = 13,
        TankDestroyed = 14,
    }

    /// <summary>
    /// The five shell impact reports <c>ShellSounds</c> keeps one pool of per
    /// gun calibre, selected by <c>projectile.gd:play_sound(hit_type)</c>.
    /// </summary>
    public enum ShellSoundKind
    {
        Pen = 0,
        Bounce = 1,
        Ricoshet = 2,
        Crit = 3,
        Impact = 4,
    }

    /// <summary>
    /// Port of Godot <c>StreamRandom</c>: interchangeable samples plus the
    /// gain the set was authored with. <c>pick_random()</c> is Godot's array
    /// helper, so the caller supplies the randomness.
    /// </summary>
    public sealed class SoundPool
    {
        public static readonly SoundPool Empty = new SoundPool();

        private readonly List<string> _streams = new List<string>();

        public float Volume = 1f;

        public IReadOnlyList<string> Streams { get { return _streams; } }

        public bool HasStreams { get { return _streams.Count > 0; } }

        /// <summary>
        /// <c>Array.pick_random()</c>. Returns null for an empty pool the way
        /// Godot returns a null AudioStream the callers already test for.
        /// </summary>
        public string PickRandom(Func<int, int> randomBelow)
        {
            if (_streams.Count == 0)
            {
                return null;
            }

            if (_streams.Count == 1 || randomBelow == null)
            {
                return _streams[0];
            }

            var index = randomBelow(_streams.Count);
            if (index < 0 || index >= _streams.Count)
            {
                index = 0;
            }

            return _streams[index];
        }

        public static SoundPool FromJson(JsonNode node)
        {
            var pool = new SoundPool();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return pool;
            }

            pool.Volume = node["volume"].AsFloat(1f);
            var streams = node["streams"];
            if (streams.Kind == JsonKind.Array)
            {
                for (var i = 0; i < streams.Count; i++)
                {
                    var name = streams[i].AsString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        pool._streams.Add(name);
                    }
                }
            }

            return pool;
        }
    }

    /// <summary>Port of Godot <c>EngineSounds</c>.</summary>
    public sealed class EngineSoundSet
    {
        /// <summary><c>pick_snd_by_param</c> clamps the load to [0, 0.8].</summary>
        public const float ParamMin = 0f;
        public const float ParamMax = 0.8f;

        public SoundPool Accel = SoundPool.Empty;
        public SoundPool Deccel = SoundPool.Empty;
        public SoundPool Start = SoundPool.Empty;
        public SoundPool Stop = SoundPool.Empty;
        public SoundPool Idle = SoundPool.Empty;
        public SoundPool LowRpm = SoundPool.Empty;
        public SoundPool MidRpm = SoundPool.Empty;
        public SoundPool HighRpm = SoundPool.Empty;

        /// <summary>Null for sets without a dedicated turning bed.</summary>
        public SoundPool Turning;

        /// <summary>
        /// <c>pick_snd_by_param(param)</c>. The source bands are
        /// [0,0], [0.01,0.3], [0.3,0.95] and [0.95,0.8]; the last one can
        /// never contain a value that was just clamped to 0.8, so a fully
        /// loaded engine stays on mid_rpm. That is reproduced deliberately:
        /// high_rpm is only ever reached through the turning slot, which
        /// several sets alias to it.
        /// </summary>
        public SoundPool PickByParam(float param)
        {
            if (param < ParamMin) { param = ParamMin; }
            if (param > ParamMax) { param = ParamMax; }

            var picked = Idle;
            if (ParamMin <= param && ParamMin >= param) { picked = Idle; }
            if (0.01f <= param && 0.3f >= param) { picked = LowRpm; }
            if (0.3f <= param && 0.95f >= param) { picked = MidRpm; }
            if (0.95f <= param && ParamMax >= param) { picked = HighRpm; }
            return picked;
        }

        public static EngineSoundSet FromJson(JsonNode node)
        {
            var set = new EngineSoundSet();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return set;
            }

            set.Accel = SoundPool.FromJson(node["accel"]);
            set.Deccel = SoundPool.FromJson(node["deccel"]);
            set.Start = SoundPool.FromJson(node["start"]);
            set.Stop = SoundPool.FromJson(node["stop"]);
            set.Idle = SoundPool.FromJson(node["idle"]);
            set.LowRpm = SoundPool.FromJson(node["low_rpm"]);
            set.MidRpm = SoundPool.FromJson(node["mid_rpm"]);
            set.HighRpm = SoundPool.FromJson(node["high_rpm"]);
            set.Turning = node.Has("turning")
                ? SoundPool.FromJson(node["turning"])
                : null;
            return set;
        }
    }

    /// <summary>Port of Godot <c>TrackSounds</c>.</summary>
    public sealed class TrackSoundSet
    {
        /// <summary><c>pick_snd_by_speed</c> clamps to [2, 100] km/h.</summary>
        public const float SpeedMin = 2f;
        public const float SpeedMax = 100f;

        public SoundPool Start = SoundPool.Empty;
        public SoundPool Stop = SoundPool.Empty;

        private readonly SoundPool[] _bands = new SoundPool[7];

        public IReadOnlyList<SoundPool> Bands { get { return _bands; } }

        public TrackSoundSet()
        {
            for (var i = 0; i < _bands.Length; i++)
            {
                _bands[i] = SoundPool.Empty;
            }
        }

        /// <summary>
        /// <c>pick_snd_by_speed(speed)</c>. Below 2 km/h the tracks fall back
        /// to the stop transient; the seven bands are the source's overlapping
        /// ranges, and a later band wins where two overlap.
        /// </summary>
        public SoundPool PickBySpeed(float speed)
        {
            if (speed < SpeedMin)
            {
                return Stop;
            }

            if (speed > SpeedMax) { speed = SpeedMax; }

            var lower = new[]
            {
                SpeedMin, 10.2342f, 22.2342f, 32.2342f, 43.2342f, 54.2342f,
                61.2342f,
            };
            var upper = new[]
            {
                10f, 22.2342f, 32.2342f, 43.2342f, 54.2342f, 62.2342f,
                SpeedMax,
            };

            var picked = _bands[0];
            for (var i = 0; i < _bands.Length; i++)
            {
                if (lower[i] <= speed && upper[i] >= speed)
                {
                    picked = _bands[i];
                }
            }

            return picked;
        }

        public static TrackSoundSet FromJson(JsonNode node)
        {
            var set = new TrackSoundSet();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return set;
            }

            set.Start = SoundPool.FromJson(node["start"]);
            set.Stop = SoundPool.FromJson(node["stop"]);
            for (var i = 0; i < 7; i++)
            {
                var key = "speed_" + ((i + 1) * 10);
                set._bands[i] = SoundPool.FromJson(node[key]);
            }

            return set;
        }
    }

    /// <summary>Port of Godot <c>ExplosionSound</c>: gun and death reports.</summary>
    public sealed class ExplosionSoundSet
    {
        public SoundPool Near = SoundPool.Empty;
        public SoundPool Dist = SoundPool.Empty;
        public SoundPool Inside = SoundPool.Empty;

        public static ExplosionSoundSet FromJson(JsonNode node)
        {
            var set = new ExplosionSoundSet();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return set;
            }

            set.Near = SoundPool.FromJson(node["sound_near"]);
            set.Dist = SoundPool.FromJson(node["sound_dist"]);
            set.Inside = SoundPool.FromJson(node["sound_inside"]);
            return set;
        }
    }

    /// <summary>
    /// Port of Godot <c>ShellSounds</c>. The source keeps five pools per
    /// report kind and its <c>get_*</c> helpers fold <c>shot_auto</c> into the
    /// small pool, so the same mapping is applied here.
    /// </summary>
    public sealed class ShellSoundSet
    {
        private readonly Dictionary<string, SoundPool> _pools =
            new Dictionary<string, SoundPool>();

        public SoundPool Pool(ShellSoundKind kind, ShootSound gun)
        {
            SoundPool pool;
            var key = KindKey(kind) + "_" + SizeKey(gun);
            return _pools.TryGetValue(key, out pool) ? pool : SoundPool.Empty;
        }

        /// <summary><c>get_pen</c> and friends map shot_auto onto small.</summary>
        public static string SizeKey(ShootSound gun)
        {
            switch (gun)
            {
                case ShootSound.ShotAuto:
                case ShootSound.ShotSmall:
                    return "small";
                case ShootSound.ShotMedium:
                    return "medium";
                case ShootSound.ShotMain:
                    return "main";
                case ShootSound.ShotLarge:
                    return "large";
                case ShootSound.ShotHuge:
                // The pack keeps no rocket-sized shell reports, so a launcher
                // lands with the heaviest set the shell sounds carry.
                case ShootSound.ShotAtgm:
                    return "huge";
                default:
                    return "small";
            }
        }

        public static string KindKey(ShellSoundKind kind)
        {
            switch (kind)
            {
                case ShellSoundKind.Bounce:
                    return "bounce";
                case ShellSoundKind.Ricoshet:
                    return "ricoshet";
                case ShellSoundKind.Crit:
                    return "crit";
                case ShellSoundKind.Impact:
                    return "impact";
                default:
                    return "pen";
            }
        }

        public static ShellSoundSet FromJson(JsonNode node)
        {
            var set = new ShellSoundSet();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return set;
            }

            foreach (var field in node.Fields)
            {
                set._pools[field.Key] = SoundPool.FromJson(field.Value);
            }

            return set;
        }
    }

    /// <summary>
    /// Flattened port of the Godot <c>Audio</c> autoload sound graph, loaded
    /// from <c>Resources/Data/tank_sounds.json</c> (written by
    /// <c>Tools/port_godot_sounds.py</c> out of the .tres resources).
    /// </summary>
    public sealed class TankSoundTable
    {
        public const string Resource = "tank_sounds";

        /// <summary><c>get_gun_sound</c> falls back to the autocannon set.</summary>
        public const string DefaultGunSet = "shot_auto";

        /// <summary><c>get_engine_sound</c> falls back to the USSR LT set.</summary>
        public const string DefaultEngineSet = "ussr_lt";

        /// <summary><c>get_track_sound</c> falls back to the LT set.</summary>
        public const string DefaultTrackSet = "lt";

        public const string TankExplosionSet = "tank_explosion";

        /// <summary>
        /// The bed a guided rocket loops while it flies, which the Blitz
        /// client plays as <c>/weapons/tracer/tracer</c>.
        /// </summary>
        public const string ShellFlightSet = "shell_flight";

        /// <summary><c>hits_tow</c>: a shaped-charge rocket landing.</summary>
        public const string AtgmPenHeatSet = "atgm_pen_heat";

        /// <summary><c>hits_tow</c>: a high-explosive rocket landing.</summary>
        public const string AtgmPenExplosiveSet = "atgm_pen_he";

        /// <summary><c>hits_tow</c>: the same hit heard from inside.</summary>
        public const string AtgmPenInsideSet = "atgm_pen_inside";

        private readonly Dictionary<string, ExplosionSoundSet> _guns =
            new Dictionary<string, ExplosionSoundSet>();
        private readonly Dictionary<string, EngineSoundSet> _engines =
            new Dictionary<string, EngineSoundSet>();
        private readonly Dictionary<string, TrackSoundSet> _tracks =
            new Dictionary<string, TrackSoundSet>();
        private readonly Dictionary<string, ShellSoundSet> _shells =
            new Dictionary<string, ShellSoundSet>();
        private readonly Dictionary<string, ExplosionSoundSet> _explosions =
            new Dictionary<string, ExplosionSoundSet>();
        private readonly Dictionary<string, SoundPool> _sounds =
            new Dictionary<string, SoundPool>();
        private readonly Dictionary<string, SoundPool> _crew =
            new Dictionary<string, SoundPool>();

        public int GunSetCount { get { return _guns.Count; } }

        public int EngineSetCount { get { return _engines.Count; } }

        public int TrackSetCount { get { return _tracks.Count; } }

        public int ShellSetCount { get { return _shells.Count; } }

        /// <summary>
        /// The enum names the card table stores as integers, lowercased the
        /// way the exported sound sets are keyed.
        /// </summary>
        public static string GunKey(ShootSound sound)
        {
            switch (sound)
            {
                case ShootSound.ShotSmall: return "shot_small";
                case ShootSound.ShotMedium: return "shot_medium";
                case ShootSound.ShotMain: return "shot_main";
                case ShootSound.ShotLarge: return "shot_large";
                case ShootSound.ShotHuge: return "shot_huge";
                case ShootSound.ShotAtgm: return "shot_atgm";
                default: return DefaultGunSet;
            }
        }

        public static string EngineKey(EngineSound sound)
        {
            switch (sound)
            {
                case EngineSound.DeLt: return "de_lt";
                case EngineSound.DeMt: return "de_mt";
                case EngineSound.DeHt: return "de_ht";
                case EngineSound.DeTraktor: return "de_traktor";
                case EngineSound.UkLt: return "uk_lt";
                case EngineSound.UkMt: return "uk_mt";
                case EngineSound.UkHt: return "uk_ht";
                case EngineSound.UkTraktor: return "uk_traktor";
                case EngineSound.UssrMt: return "ussr_mt";
                case EngineSound.UssrHt: return "ussr_ht";
                case EngineSound.UssrTraktor: return "ussr_traktor";
                case EngineSound.UsaLt: return "usa_lt";
                case EngineSound.UsaMt: return "usa_mt";
                case EngineSound.UsaHt: return "usa_ht";
                case EngineSound.UsaTraktor: return "usa_traktor";
                default: return DefaultEngineSet;
            }
        }

        public static string TrackKey(TrackSound sound)
        {
            switch (sound)
            {
                case TrackSound.Mt: return "mt";
                case TrackSound.Ht: return "ht";
                case TrackSound.Traktor: return "traktor";
                default: return DefaultTrackSet;
            }
        }

        /// <summary><c>Audio.shell_type_to_sound</c>: HESH reuses the HE set.</summary>
        public static string ShellKey(Tanks.ShellType type)
        {
            switch (type)
            {
                case Tanks.ShellType.Apcr: return "apcr";
                case Tanks.ShellType.Heat: return "heat";
                case Tanks.ShellType.He:
                case Tanks.ShellType.Hesh: return "he";
                default: return "ap";
            }
        }

        public static string CrewKey(CrewMessage message)
        {
            switch (message)
            {
                case CrewMessage.AllyKilled: return "ally_killed";
                case CrewMessage.ArmorNotPierce: return "armor_not_pierce";
                case CrewMessage.ArmorPierced: return "armor_pierced";
                case CrewMessage.ArmorNotHit: return "armor_not_hit";
                case CrewMessage.Ricoshet: return "ricoshet";
                case CrewMessage.EnemyIgnited: return "enemy_ignited";
                case CrewMessage.EnemyKilled: return "enemy_killed";
                case CrewMessage.FireStarted: return "fire_started";
                case CrewMessage.FireStopped: return "fire_stopped";
                case CrewMessage.ModuleTrackCrit: return "module_track_crit";
                case CrewMessage.ModuleTrackDestroyed:
                    return "module_track_destroyed";
                case CrewMessage.ModuleTrackFixed: return "module_track_fixed";
                case CrewMessage.ModuleAmmoBayCrit:
                    return "module_ammo_bay_crit";
                case CrewMessage.TankDestroyed: return "tank_destroyed";
                default: return "battle_start";
            }
        }

        public ExplosionSoundSet Gun(ShootSound sound)
        {
            ExplosionSoundSet set;
            if (_guns.TryGetValue(GunKey(sound), out set))
            {
                return set;
            }

            return _guns.TryGetValue(DefaultGunSet, out set)
                ? set : new ExplosionSoundSet();
        }

        public EngineSoundSet Engine(EngineSound sound)
        {
            EngineSoundSet set;
            if (_engines.TryGetValue(EngineKey(sound), out set))
            {
                return set;
            }

            return _engines.TryGetValue(DefaultEngineSet, out set)
                ? set : new EngineSoundSet();
        }

        public TrackSoundSet Tracks(TrackSound sound)
        {
            TrackSoundSet set;
            if (_tracks.TryGetValue(TrackKey(sound), out set))
            {
                return set;
            }

            return _tracks.TryGetValue(DefaultTrackSet, out set)
                ? set : new TrackSoundSet();
        }

        public ShellSoundSet Shell(Tanks.ShellType type)
        {
            ShellSoundSet set;
            return _shells.TryGetValue(ShellKey(type), out set)
                ? set : new ShellSoundSet();
        }

        public ExplosionSoundSet Explosion(string name)
        {
            ExplosionSoundSet set;
            return _explosions.TryGetValue(name ?? string.Empty, out set)
                ? set : new ExplosionSoundSet();
        }

        public SoundPool Sound(string name)
        {
            SoundPool pool;
            return _sounds.TryGetValue(name ?? string.Empty, out pool)
                ? pool : SoundPool.Empty;
        }

        public SoundPool Crew(CrewMessage message)
        {
            SoundPool pool;
            return _crew.TryGetValue(CrewKey(message), out pool)
                ? pool : SoundPool.Empty;
        }

        public static TankSoundTable Parse(string json)
        {
            var table = new TankSoundTable();
            JsonNode root;
            if (string.IsNullOrEmpty(json) || !JsonNode.TryParse(json, out root))
            {
                return table;
            }

            Read(root["guns"], table._guns, ExplosionSoundSet.FromJson);
            Read(root["engines"], table._engines, EngineSoundSet.FromJson);
            Read(root["tracks"], table._tracks, TrackSoundSet.FromJson);
            Read(root["shells"], table._shells, ShellSoundSet.FromJson);
            Read(root["explosions"], table._explosions, ExplosionSoundSet.FromJson);
            Read(root["sounds"], table._sounds, SoundPool.FromJson);
            Read(root["crew"], table._crew, SoundPool.FromJson);
            return table;
        }

        private static void Read<T>(
            JsonNode node, Dictionary<string, T> into, Func<JsonNode, T> parse)
        {
            if (node == null || node.Kind != JsonKind.Object)
            {
                return;
            }

            foreach (var field in node.Fields)
            {
                into[field.Key] = parse(field.Value);
            }
        }
    }
}
