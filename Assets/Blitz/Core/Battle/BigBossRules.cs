using System;
using System.Collections.Generic;
using System.Globalization;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;

namespace BlitzLegacy.Core.Battle
{
    /// <summary>11.20 client tables plus an explicitly labelled offline compatibility profile.
    /// No values in profile are asserted to be recovered server code.</summary>
    public sealed class BigBossRules
    {
        public const string Resource = "big_boss_1120";
        public readonly JsonNode Data;
        public readonly JsonNode Profile;
        private readonly Dictionary<int, string[][]> _slots = new Dictionary<int, string[][]>();
        private readonly Dictionary<int, JsonNode> _roles = new Dictionary<int, JsonNode>();
        public BigBossRules(string json)
        {
            Data = JsonNode.Parse(json);
            if (Data["sourceVersion"].AsString() != "11.20.0") throw new ArgumentException("Big Boss requires the supplied 11.20 tables.");
            Profile = Data["profile"];
            foreach (var role in Data["roles"].Items)
            {
                var id = role["id"].AsInt(); _roles.Add(id, role); var levels = new string[5][];
                for (var l = 0; l < 5; l++) { var slots = role["levels"][l]; levels[l] = new string[slots.Count]; for (var i = 0; i < slots.Count; i++) levels[l][i] = slots[i].AsString(); }
                _slots.Add(id, levels);
            }
            if (_roles.Count != 5 || Data["levels"].Count != 5) throw new ArgumentException("Invalid Big Boss role tables.");
        }
        public JsonNode Role(int id) { return _roles.TryGetValue(id, out var role) ? role : throw new ArgumentOutOfRangeException(nameof(id)); }
        public bool HasRole(int id) { return _roles.ContainsKey(id); }
        public JsonNode Ability(string id) { return Data["abilities"][id]; }
        public float Number(string ability, string key, float fallback = 0) { return (float)Ability(ability)["params"][key].AsDouble(fallback); }
        public float P(string key) { return (float)Profile[key].AsDouble(); }
        public bool IsBoss(int role) { return Role(role)["boss"].AsBool(); }
        public int Level(double points)
        {
            var level = 1;
            for (var i = 1; i < 5; i++) if (points >= Data["levels"][i].AsDouble()) level = i + 1;
            return level;
        }
        public float Progress(double points)
        {
            var level = Level(points);
            if (level == 5) return 1;
            var lo = Data["levels"][level - 1].AsDouble(); var hi = Data["levels"][level].AsDouble();
            return (float)Math.Max(0, Math.Min(1, (points - lo) / (hi - lo)));
        }
        public string[] Abilities(int role, int level)
        {
            return _slots[role][Math.Max(0, Math.Min(4, level - 1))];
        }
        public int OrdinaryRole(Random random, int previous)
        {
            var choices = new List<int>(4);
            foreach (var role in _roles) if (!IsBoss(role.Key) && role.Key != previous) choices.Add(role.Key);
            return choices[random.Next(choices.Count)];
        }
        public static bool EligibleTier(int tier) { return tier >= 5 && tier <= TanksDatabase.MaxLevel; }

        /// <summary>Lowest factor a scaled boost reaches - a reload or aim time never drops to
        /// zero - and the most damage a scaled shield absorbs.</summary>
        public const double MinBoostFactor = .05, MaxDamageReductionPercent = 90;
        // Passive boosts the Sandbox multiplier scales: factors around 1, bonuses around 0.
        private static readonly string[] BoostFactors = {"gunReloadFactor", "pumpGunReloadFactor", "gunAimingFactor", "commonDamageFactor", "enginePowerFactor", "speedLimitsFactor"};
        private static readonly string[] BoostBonuses = {"maxHealthPercentBonus", "damageReductionPercent", "healFactor"};
        // Ability card rows printing those boosts: the row, its parameter and the form shown -
        // the gain (factor - 1) or the cut (1 - factor) in percent, a percentage or a fraction.
        private static readonly string[][] BoostRows =
        {
            new[] {"Boss_reloadtime_description", "gunReloadFactor", "cut"}, new[] {"Boss_damage_description", "commonDamageFactor", "gain"},
            new[] {"Boss_enginepower_description", "enginePowerFactor", "gain"}, new[] {"Boss_maxspeed_description", "speedLimitsFactor", "gain"},
            new[] {"Graviton_shield_description", "damageReductionPercent", "percent"}, new[] {"Healshot_power_description", "healFactor", "fraction"},
            new[] {"Killer_damage_description", "commonDamageFactor", "gain"}, new[] {"Killer_cd_description", "gunReloadFactor", "cut"},
            new[] {"Killer_aim_description", "gunAimingFactor", "cut"}, new[] {"Kamikazi_enginepower_description", "enginePowerFactor", "gain"},
            new[] {"Kamikazi_maxspeed_description", "speedLimitsFactor", "gain"},
        };
        /// <summary>Sandbox tables: <paramref name="bossLives"/> lives per boss, <paramref name="startPoints"/>
        /// points per team and every passive boost of the boss and the roles scaled by
        /// <paramref name="buffMultiplier"/>: a factor keeps its distance from 1 times the multiplier, a
        /// bonus is multiplied. 1 plays the shipped tables, 0 removes the boosts, 2 doubles them;
        /// the ability cards print the tuned numbers.</summary>
        public static BigBossRules Tuned(string json, int bossLives, int startPoints, float buffMultiplier)
        {
            if (bossLives < 1 || bossLives > sbyte.MaxValue || startPoints < 1 || !(buffMultiplier >= 0) || float.IsInfinity(buffMultiplier)) throw new ArgumentOutOfRangeException();
            var rules = new BigBossRules(json);
            rules.Profile.Set("bossLives", bossLives).Set("startPoints", startPoints);
            if (buffMultiplier == 1) return rules;
            rules.Profile.Set("bossMaxHealthFactor", Boost(rules.P("bossMaxHealthFactor"), buffMultiplier));
            foreach (var entry in rules.Data["abilities"].Fields)
            {
                var ability = entry.Value; if (ability["kind"].AsString() != "passive") continue;
                var p = ability["params"];
                foreach (var key in BoostFactors) if (p.Has(key)) p.Set(key, Boost(p[key].AsDouble(), buffMultiplier));
                foreach (var key in BoostBonuses) if (p.Has(key)) p.Set(key, Bonus(key, p[key].AsDouble(), buffMultiplier));
                foreach (var row in ability["descrParams"].Items)
                    foreach (var boost in BoostRows)
                        if (row["nameKey"].AsString() == boost[0] && p.Has(boost[1])) row.Set("value", Shown(p[boost[1]].AsDouble(), boost[2]));
            }
            return rules;
        }
        private static double Boost(double factor, double multiplier) { return Math.Max(MinBoostFactor, 1 + (factor - 1) * multiplier); }
        private static double Bonus(string key, double bonus, double multiplier)
        {
            var value = Math.Max(0, bonus * multiplier);
            return key == "damageReductionPercent" ? Math.Min(MaxDamageReductionPercent, value) : value;
        }
        private static string Shown(double value, string form)
        {
            var shown = form == "gain" ? (value - 1) * 100 : form == "cut" ? (1 - value) * 100 : form == "fraction" ? value * 100 : value;
            var rounded = Math.Round(shown, MidpointRounding.AwayFromZero);
            // A cut of exactly nothing must not print as "-0".
            return (rounded == 0 ? 0 : rounded).ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Host-authoritative Big Boss view of one seat for the network. Timers are
    /// seconds remaining so peer clocks never mix; Ready0..2 follow the ability order of
    /// <see cref="BigBossRules.Abilities"/> for the role and level.</summary>
    public struct BigBossSnapshot
    {
        public const byte FlagRespawning = 1, FlagRailgun = 2, FlagShield = 4;
        public byte RoleId, Flags;
        /// <summary>-1 when none; ordinary seats report -1 lives.</summary>
        public sbyte NextRoleId, Lives;
        public int LifeSerial;
        public float Points, RespawnIn, Ready0, Ready1, Ready2;
        /// <summary>Received energy shield: seconds left and the length it was granted for.</summary>
        public float ShieldIn, ShieldDuration;
        /// <summary>Own timed effects: the poisoned shells and the healing aura.</summary>
        public float PoisonCastIn, AuraIn;
        /// <summary>Longest poison on this hull and its full length.</summary>
        public float PoisonedIn, PoisonedTotal;
        public bool Respawning { get { return (Flags & FlagRespawning) != 0; } }
        public float Ready(int index) { return index == 0 ? Ready0 : index == 1 ? Ready1 : index == 2 ? Ready2 : 0; }
        public void SetReady(int index, float seconds)
        {
            if (index == 0) Ready0 = seconds; else if (index == 1) Ready1 = seconds; else if (index == 2) Ready2 = seconds;
        }
    }

    /// <summary>Shared progression survives ordinary role reassignment. Readiness is keyed by
    /// source family: a level-up readies only the role's button ability
    /// (<see cref="ManualAbility"/>) and never resets a self-activating cooldown.</summary>
    public sealed class BigBossState
    {
        public readonly BigBossRules Rules;
        public int RoleId { get; private set; }
        public int LifeSerial { get; private set; }
        public int Lives { get; private set; }
        public double Points { get; private set; }
        public int Level { get { return Rules.IsBoss(RoleId) ? 1 : Rules.Level(Points); } }
        public bool Boss { get { return Rules.IsBoss(RoleId); } }
        public bool Respawning { get; private set; }
        public bool Exhausted { get { return Boss && Lives == 0; } }
        public double RespawnAt { get; private set; }
        /// <summary>Ordinary role chosen at death so the respawn screen can present it; -1 when none.</summary>
        public int NextRoleId { get; private set; } = -1;
        private readonly Dictionary<string, double> _ready = new Dictionary<string, double>();
        public BigBossState(BigBossRules rules, int role)
        {
            Rules = rules; Rules.Role(role); RoleId = role; Lives = Boss ? (int)rules.P("bossLives") : -1;
        }
        public void Reward(int damage, int healing = 0, bool kill = false)
        {
            if (Boss) return;
            var level = Level;
            Points += Math.Max(0, damage) * Rules.P("rolePointsPerDamage") + Math.Max(0, healing) * Rules.P("rolePointsPerHeal") + (kill ? Rules.P("rolePointsPerKill") : 0);
            if (Level <= level) return;
            // A level-up hands the crew its stronger button ability at once.
            var id = ManualAbility(Level);
            if (id != null) _ready.Remove(Family(id));
        }
        /// <summary>The single button-driven ability of the current role at <paramref name="level"/>.
        /// Families the profile marks as self-activating never occupy the mode slot.</summary>
        public string ManualAbility(int level)
        {
            foreach (var id in Rules.Abilities(RoleId, level))
                if (Rules.Ability(id)["kind"].AsString() == "active" && !AutoActivated(id)) return id;
            return null;
        }
        public void StartLife(double now)
        {
            _ready.Clear();
            foreach (var id in Rules.Abilities(RoleId, Level))
                _ready[Family(id)] = now + Rules.Number(id, "startCooldown");
        }
        private string Family(string id) { return Rules.Ability(id)["family"].AsString(); }
        public double Remaining(string id, double now) { return Math.Max(0, (_ready.TryGetValue(Family(id), out var at) ? at : now) - now); }
        public bool Activate(string id, double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now) || Respawning || Exhausted || Remaining(id, now) > 0) return false;
            if (Array.IndexOf(Rules.Abilities(RoleId, Level), id) < 0 || Rules.Ability(id)["kind"].AsString() != "active") return false;
            _ready[Family(id)] = now + Rules.Number(id, "cooldown"); return true;
        }
        /// <summary>Restart a family's cooldown from <paramref name="now"/>, e.g. when a charged
        /// shot is actually fired rather than when the charge was armed.</summary>
        public void Restart(string id, double now)
        {
            if (!double.IsNaN(now) && !double.IsInfinity(now) && !Respawning && !Exhausted) _ready[Family(id)] = now + Rules.Number(id, "cooldown");
        }
        /// <summary>Profile families that arm themselves instead of waiting for a button.</summary>
        public bool AutoActivated(string id)
        {
            var family = Family(id); var families = Rules.Profile["autoActivatedFamilies"];
            for (var i = 0; i < families.Count; i++) if (families[i].AsString() == family) return true;
            return false;
        }
        public bool Die(double now, Random random = null)
        {
            if (Respawning || Exhausted) return false;
            if (Boss) Lives--;
            Respawning = !Exhausted; RespawnAt = now + Rules.P("respawnSeconds");
            NextRoleId = Respawning && !Boss && random != null ? Rules.OrdinaryRole(random, RoleId) : -1;
            return true;
        }
        public bool Return(double now, Random random)
        {
            if (!Respawning || now < RespawnAt) return false;
            if (!Boss) RoleId = NextRoleId >= 0 ? NextRoleId : Rules.OrdinaryRole(random, RoleId);
            NextRoleId = -1; LifeSerial++; Respawning = false; StartLife(now); return true;
        }
        /// <summary>Host: the replicated part of this state at <paramref name="now"/>.</summary>
        public BigBossSnapshot Capture(double now)
        {
            var s = new BigBossSnapshot
            {
                RoleId = (byte)RoleId, LifeSerial = LifeSerial, Points = (float)Points,
                NextRoleId = (sbyte)Math.Max(-1, Math.Min(sbyte.MaxValue, NextRoleId)),
                Lives = (sbyte)Math.Max(-1, Math.Min(sbyte.MaxValue, Lives)),
                RespawnIn = Respawning ? (float)Math.Max(0, RespawnAt - now) : 0,
                Flags = Respawning ? BigBossSnapshot.FlagRespawning : (byte)0,
            };
            var ids = Rules.Abilities(RoleId, Level);
            for (var i = 0; i < ids.Length && i < 3; i++) s.SetReady(i, (float)Remaining(ids[i], now));
            return s;
        }
        /// <summary>Client puppet: mirror the host state. Never used by the authority. A snapshot
        /// these tables cannot describe leaves the state untouched and returns false.</summary>
        public bool Apply(BigBossSnapshot s, double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now) || float.IsNaN(s.Points) || float.IsInfinity(s.Points)) return false;
            if (!Rules.HasRole(s.RoleId) || (s.NextRoleId >= 0 && !Rules.HasRole(s.NextRoleId))) return false;
            RoleId = s.RoleId; NextRoleId = s.NextRoleId < 0 ? -1 : s.NextRoleId; LifeSerial = s.LifeSerial;
            Points = Math.Max(0, s.Points); Lives = Boss ? Math.Max(0, (int)s.Lives) : -1;
            Respawning = s.Respawning && !Exhausted; RespawnAt = Respawning ? now + Seconds(s.RespawnIn) : 0;
            _ready.Clear();
            var ids = Rules.Abilities(RoleId, Level);
            for (var i = 0; i < ids.Length && i < 3; i++) _ready[Family(ids[i])] = now + Seconds(s.Ready(i));
            return true;
        }
        /// <summary>A wire timer as a safe non-negative span (an hour at most).</summary>
        private static double Seconds(float value) { return float.IsNaN(value) || value <= 0 ? 0 : Math.Min(value, 3600f); }
    }

    /// <summary>Ticket ledger deduplicated by seat/life. The caller resolves after the whole
    /// frame's damage transaction, never inside a nested explosion/death callback.</summary>
    public sealed class BigBossMatch
    {
        public readonly BigBossRules Rules;
        public int TeamA { get; private set; }
        public int TeamB { get; private set; }
        private readonly HashSet<long> _deaths = new HashSet<long>();
        public BigBossMatch(BigBossRules rules) { Rules = rules; Reset(); }
        public void Reset() { TeamA = TeamB = (int)Rules.P("startPoints"); _deaths.Clear(); }
        public bool Death(int slot, int life, int team, bool boss)
        {
            if (slot < 0 || life < 0 || (team != 0 && team != 1)) throw new ArgumentOutOfRangeException();
            if (!_deaths.Add(((long)slot << 32) | (uint)life)) return false;
            var cost = (int)Rules.P(boss ? "bossDeathCost" : "ordinaryDeathCost");
            if (team == 0) TeamA = Math.Max(0, TeamA - cost); else TeamB = Math.Max(0, TeamB - cost);
            return true;
        }
        /// <summary>Client puppet: the host's ledger. Deaths are never counted locally.</summary>
        public void ApplyNet(int teamA, int teamB)
        {
            TeamA = Math.Max(0, teamA); TeamB = Math.Max(0, teamB);
        }
        public bool Verdict(bool timeout, out int winner)
        {
            winner = -1;
            if (TeamA > 0 && TeamB > 0 && !timeout) return false;
            winner = TeamA == TeamB ? -1 : TeamA > TeamB ? 0 : 1; return true;
        }
    }
}
