using System;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;

namespace BlitzLegacy.Core.Battle
{
    /// <summary>
    /// War of Ducks: the Blitz 4.8.0 "World of Ducks" event as an offline
    /// battle mode. The arena, the WarDuck card, its sounds and particles are
    /// ported from the 4.8.0 pack (Tools/port_world_of_ducks.py) into
    /// <c>Data/world_of_ducks.json</c>; the battle rules follow the Godot
    /// reference (<c>_battle.gd</c> WORLD_OF_DUCKS_*): a forced map, every
    /// seat a WarDuck with a seeded gun, no friendly fire, no base capture,
    /// and a battle-local result that never touches the account.
    /// </summary>
    public sealed class WorldOfDucksRules
    {
        /// <summary>Resources/Data name of the ported event document.</summary>
        public const string DataResource = "world_of_ducks";

        /// <summary>_battle.gd WORLD_OF_DUCKS_TEAM_SIZE.</summary>
        public const int DefaultTeamSize = 7;

        /// <summary>_battle.gd WORLD_OF_DUCKS_GUN_COUNT: the three gun "hats".</summary>
        public const int GunCount = 3;

        /// <summary>
        /// Height of the 4.8 <c>sea_surf.sc2</c> water plane of the arena.
        /// The ducks sail on the invisible <c>CollisionPlain</c> half a metre
        /// below it (<see cref="CollisionPlaneLevel"/>).
        /// </summary>
        public const float WaterLevel = 14.7f;

        /// <summary>Height of the arena's invisible CollisionPlain floor.</summary>
        public const float CollisionPlaneLevel = 14.2f;

        /// <summary>
        /// A spawner height raised onto the CollisionPlain floor. The ported
        /// spawners stand on the sea bed some seven metres down; a duck seated
        /// there would sail under the floor instead of on it.
        /// </summary>
        public static float FloatingHeight(float height)
        {
            return height < CollisionPlaneLevel ? CollisionPlaneLevel : height;
        }

        /// <summary>
        /// A shell bursting this close above the water plane (or below it)
        /// landed in the water rather than on a prop.
        /// </summary>
        public const float WaterHitTolerance = 0.45f;

        public string Id { get; private set; }
        public string MapId { get; private set; }
        public int TeamSize { get; private set; }
        public int TimeLimitSeconds { get; private set; }
        public bool FriendlyFire { get; private set; }
        public bool BaseCapture { get; private set; }

        /// <summary>The WarDuck card exactly as the porter wrote it.</summary>
        public JsonNode CardJson { get; private set; }

        /// <summary>The whole event document (sounds, fx, map effects).</summary>
        public JsonNode Document { get; private set; }

        public string CardName { get { return CardJson == null ? string.Empty : CardJson["name"].AsString(); } }

        public static WorldOfDucksRules Parse(string json)
        {
            JsonNode node;
            if (string.IsNullOrEmpty(json) || !JsonNode.TryParse(json, out node) || node.Kind != JsonKind.Object)
            {
                throw new InvalidOperationException("The War of Ducks data is missing or unreadable");
            }

            return FromJson(node);
        }

        public static WorldOfDucksRules FromJson(JsonNode node)
        {
            var card = node["card"];
            if (card.Kind != JsonKind.Object || string.IsNullOrEmpty(card["name"].AsString()))
            {
                throw new InvalidOperationException("The War of Ducks data carries no WarDuck card");
            }

            var rules = new WorldOfDucksRules
            {
                Id = node["id"].AsString("world_of_ducks"),
                MapId = node["map"].AsString("sea_of_ducks"),
                TeamSize = Math.Max(1, node["teamSize"].AsInt(DefaultTeamSize)),
                TimeLimitSeconds = Math.Max(1, node["timeLimitSeconds"].AsInt(420)),
                FriendlyFire = node["friendlyFire"].AsBool(false),
                BaseCapture = node["baseCapture"].AsBool(false),
                CardJson = card,
                Document = node,
            };
            return rules;
        }

        /// <summary>A fresh WarDuck card; the battle owns its copy.</summary>
        public TankInfo CreateCard()
        {
            return TankInfo.FromJson(CardJson);
        }

        /// <summary>
        /// _battle.gd: <c>mini(WORLD_OF_DUCKS_TEAM_SIZE, mini(team A spawners,
        /// team B spawners))</c>.
        /// </summary>
        public int TeamSizeFor(int mapCapacity)
        {
            return Math.Max(0, Math.Min(TeamSize, mapCapacity));
        }

        /// <summary>
        /// _battle.gd world_of_ducks_gun_for_slot: a Godot
        /// <c>RandomNumberGenerator</c> seeded with
        /// <c>seed + team * 104729 + 8191</c> rolls <c>randi_range(0, 2)</c>
        /// <c>slot + 1</c> times and keeps the last roll, so one battle seed
        /// gives every seat an independent, reproducible gun.
        /// </summary>
        public static int GunForSlot(long seed, int team, int slot, int gunCount = GunCount)
        {
            if (gunCount <= 1) { return 0; }
            var random = new GodotPcg(unchecked((ulong)(seed + team * 104729L + 8191L)));
            var selected = 0;
            for (var i = 0; i < Math.Max(slot, 0) + 1; i++)
            {
                selected = random.RandiRange(0, gunCount - 1);
            }

            return selected;
        }

        /// <summary>Whether a burst at this height splashed into the arena's water.</summary>
        /// <summary>
        /// battle_shows_result without progression: the event battle keeps its
        /// score table but pays no credits, XP, gold or rewards.
        /// </summary>
        public static void StripProgression(BattleResultData result)
        {
            if (result == null) { return; }
            result.PremiumAccount = false;
            result.CreditsEarned = 0;
            result.CreditCosts = 0;
            result.Credits = 0;
            result.Gold = 0;
            result.Xp = 0;
            result.FreeXp = 0;
            result.Rewards.Clear();
            for (var i = 0; i < result.Participants.Count; i++)
            {
                result.Participants[i].Xp = 0;
                result.Participants[i].XpRaw = 0;
            }
        }

        public static bool IsWaterHit(float height)
        {
            return height <= WaterLevel + WaterHitTolerance;
        }

        /// <summary>
        /// Godot's RandomPCG (pcg32 with the default 64-bit increment), so the
        /// seeded rolls match the reference roster bit for bit.
        /// </summary>
        public sealed class GodotPcg
        {
            private const ulong DefaultIncrement = 1442695040888963407UL;
            private ulong _state;
            private readonly ulong _inc;

            public GodotPcg(ulong seed)
            {
                // pcg32_srandom_r(&pcg, seed, PCG_DEFAULT_INC_64)
                _state = 0UL;
                _inc = (DefaultIncrement << 1) | 1UL;
                Next();
                _state = unchecked(_state + seed);
                Next();
            }

            public uint Next()
            {
                var old = _state;
                _state = unchecked(old * 6364136223846793005UL + _inc);
                var xorshifted = (uint)(((old >> 18) ^ old) >> 27);
                var rot = (int)(old >> 59);
                return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
            }

            /// <summary>pcg32_boundedrand_r.</summary>
            public uint Bounded(uint bound)
            {
                if (bound == 0) { return 0; }
                var threshold = unchecked((0u - bound) % bound);
                while (true)
                {
                    var r = Next();
                    if (r >= threshold) { return r % bound; }
                }
            }

            /// <summary>RandomPCG::random(from, to), inclusive.</summary>
            public int RandiRange(int from, int to)
            {
                if (from == to) { return from; }
                return (int)Bounded((uint)Math.Abs(from - to) + 1u) + Math.Min(from, to);
            }
        }
    }
}
