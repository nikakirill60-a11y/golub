using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;

namespace BlitzLegacy.Core.Battle
{
    /// <summary>
    /// Battle rule sets. <see cref="Sandbox"/> is Godot's BattleMode.TRAINING: a
    /// strictly offline launch context around one of the three shipped rule sets.
    /// It must never be serialised into an online plan, room or connect request.
    /// <see cref="WorldOfDucks"/> is Godot's BattleMode.WORLD_OF_DUCKS (War of
    /// Ducks): the 4.8.0 event arena, offline-only like the Sandbox.
    /// <see cref="BigBoss"/> is the 11.20 boss mode; the host (or the dedicated
    /// server) simulates its roles and the clients mirror them.
    /// </summary>
    public enum BattleMode { Normal, Gravity, Uprising, Sandbox, WorldOfDucks, BigBoss }

    public static class BattleModeCatalog
    {
        /// <summary>Modes a matchmaking queue, a room or a CLI may carry.</summary>
        public static readonly BattleMode[] SelectorModes =
        {
            BattleMode.Normal,
            BattleMode.Gravity,
            BattleMode.Uprising,
            BattleMode.BigBoss,
        };

        /// <summary>
        /// Cards of the hangar mode picker: the online modes with the BIG BOSS
        /// card right after Uprising, then the War of Ducks event and the
        /// Sandbox (battle_mode_menu.gd order otherwise).
        /// </summary>
        public static readonly BattleMode[] MenuModes =
        {
            BattleMode.Normal,
            BattleMode.Gravity,
            BattleMode.Uprising,
            BattleMode.BigBoss,
            BattleMode.WorldOfDucks,
            BattleMode.Sandbox,
        };

        /// <summary>Modes that never travel over the network.</summary>
        public static bool OfflineOnly(BattleMode mode)
        {
            return mode == BattleMode.Sandbox || mode == BattleMode.WorldOfDucks;
        }

        public static string Id(BattleMode mode)
        {
            switch (mode)
            {
                case BattleMode.Gravity: return "gravity";
                case BattleMode.Uprising: return "uprising";
                case BattleMode.Sandbox: return "training";
                case BattleMode.WorldOfDucks: return "world_of_ducks";
                case BattleMode.BigBoss: return "bossmode";
                default: return "normal";
            }
        }

        public static string NameKey(BattleMode mode)
        {
            return "battle_mode:" + Id(mode) + "/name";
        }

        public static string DescriptionKey(BattleMode mode)
        {
            return "battle_mode:" + Id(mode) + "/desc";
        }

        /// <summary>
        /// Whether a finished battle of this mode reaches the account (credits,
        /// XP, rewards and statistics). The War of Ducks event pays nothing and
        /// the Sandbox ends without a result; Big Boss pays like a random battle.
        /// </summary>
        public static bool PaysRewards(BattleMode mode)
        {
            return mode != BattleMode.WorldOfDucks && mode != BattleMode.Sandbox;
        }

        /// <summary>False for the Sandbox and for any value outside the enum.</summary>
        public static bool AllowsOnline(BattleMode mode)
        {
            return Array.IndexOf(SelectorModes, mode) >= 0;
        }
    }

    /// <summary>Immutable Godot catalogue; figures are generated, never hand-tuned.</summary>
    public sealed class UprisingCatalog
    {
        public readonly List<JsonNode> Abilities = new List<JsonNode>();
        public UprisingCatalog(string json)
        {
            foreach (var a in JsonNode.Parse(json)["abilities"].Items) Abilities.Add(a);
        }
        public JsonNode Get(string id)
        {
            foreach (var a in Abilities) if (a["id"].AsString() == id) return a;
            return JsonNode.Null();
        }
        public int Cost(string id) { return Get(id)["cost"].AsInt(); }
        public int Rarity(string id) { var p = Cost(id); return p >= 400 ? 3 : p >= 200 ? 2 : 1; }
        public static string Family(string id)
        {
            return id.EndsWith("1", StringComparison.Ordinal) || id.EndsWith("2", StringComparison.Ordinal) ? id.Substring(0, id.Length - 1) : id;
        }
        public string ForTank(string name, TankClass cls)
        {
            var key = cls == TankClass.Light ? "LT" : cls == TankClass.Medium ? "MT" : cls == TankClass.Heavy ? "HT" : "TD";
            var choices = new List<string>();
            foreach (var a in Abilities)
            {
                if (a["kind"].AsInt() != 0 || !a["wired"].AsBool()) continue;
                foreach (var c in a["classes"].Items) if (c.AsString() == key) choices.Add(a["id"].AsString());
            }
            // Godot String::hash: DJB2 over Unicode codepoints, not CLR randomized hash.
            uint hash = 5381;
            for (var i = 0; i < name.Length; i++)
            {
                var code = char.ConvertToUtf32(name, i); if (code > 65535) i++;
                unchecked { hash = hash * 33 + (uint)code; }
            }
            return choices.Count == 0 ? "" : choices[(int)(hash % (uint)choices.Count)];
        }
    }

    /// <summary>
    /// Compact host-authoritative Uprising state of one tank for the network.
    /// Timers are seconds remaining (negative = none) so peer clocks never mix;
    /// abilities are catalogue indices (bit masks / <see cref="NoOffer"/>).
    /// </summary>
    public struct UprisingSnapshot
    {
        public const byte NoOffer = 255;
        public const byte EmptyOffer = 254;
        public const byte FlagRespawning = 1, FlagShield = 2, FlagLastStand = 4;
        public ulong Owned, Pending;
        public byte Offer0, Offer1, Offer2, Flags, RespawnsLeft;
        public int Elixir, LifeSerial;
        public float RespawnIn, ActiveIn, ReadyIn, CloakIn;
        public float CurseIn, MarkIn, SplashIn, ChainIn;
        public bool Respawning { get { return (Flags & FlagRespawning) != 0; } }
        /// <summary>LifeSerial is even while a life is running (death and respawn each bump it).</summary>
        public bool AliveLife { get { return (LifeSerial & 1) == 0; } }
    }

    /// <summary>Battle-wide ownership and per-life transactions. Clock is supplied by callers.</summary>
    public sealed class UprisingState
    {
        public const double RespawnDelay = 10;
        public readonly UprisingCatalog Catalog;
        public readonly UprisingRules Rules;
        public readonly HashSet<string> Owned = new HashSet<string>(StringComparer.Ordinal);
        public readonly List<string> Offer = new List<string>();
        public readonly Dictionary<string, int> Pending = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<int> _shots = new HashSet<int>();
        private double _fraction;
        private int _damageRemainder;
        private bool _deathPaid;
        private Security.ObscuredInt _elixir;
        public int Elixir { get { return _elixir.Get("uprising.elixir"); } private set { _elixir = value; } }
        public int RespawnsLeft { get; private set; }
        public int LifeSerial { get; private set; }
        public bool Respawning { get; private set; }
        public double RespawnAt { get; private set; }
        public string ActiveId { get; private set; }
        public double ActiveUntil = -1, ReadyAt, CloakUntil = -1;
        public bool ShieldArmed, LastStandUsed;
        public bool Has(string id) { return Owned.Contains(id); }
        public UprisingState(UprisingCatalog catalog, string active, UprisingRules rules = null)
        {
            Catalog = catalog; ActiveId = active; Rules = rules ?? UprisingRules.Standard; RespawnsLeft = Rules.Respawns;
        }
        /// <summary>Elixir price under the current rules.</summary>
        public int Cost(string id) { return Rules.Cost(Catalog, id); }
        public void AddElixir(int amount) { if (amount > 0) Elixir = checked(Elixir + amount); }
        public void Accrue(double delta)
        {
            if (delta <= 0 || double.IsNaN(delta) || double.IsInfinity(delta)) return;
            _fraction += delta * Rules.ElixirPerSecond; var whole = (int)Math.Floor(_fraction + 1e-9);
            _fraction = Math.Max(0, _fraction - whole); AddElixir(whole);
        }
        public void RewardDamage(int amount, int shotId, DamageType type)
        {
            if (amount <= 0 || shotId == 0 || (type != DamageType.Shell && type != DamageType.Explosion)) return;
            _damageRemainder += amount; var steps = _damageRemainder / Rules.ElixirDamageStep; _damageRemainder %= Rules.ElixirDamageStep;
            AddElixir(steps * Rules.ElixirPerDamageStep + (_shots.Add(shotId) ? Rules.ElixirPerDamagingShot : 0));
        }
        public bool CanBuy(string id)
        {
            var a = Catalog.Get(id);
            return a.Kind != JsonKind.Null && a["kind"].AsInt() == 1 && a["wired"].AsBool()
                && !Has(id) && !(id.EndsWith("1", StringComparison.Ordinal) && Has(id.Substring(0, id.Length-1)+"2"))
                && Cost(id) > 0 && Elixir >= Cost(id);
        }
        public bool Grant(string id)
        {
            var a = Catalog.Get(id);
            return a.Kind != JsonKind.Null && a["kind"].AsInt() == 1 && a["wired"].AsBool() && Owned.Add(id);
        }
        public bool Toggle(string id, double now, bool ongoing)
        {
            if (!ongoing || !Respawning || now >= RespawnAt || !Offer.Contains(id)) return false;
            int paid;
            if (Pending.TryGetValue(id, out paid)) { Pending.Remove(id); Owned.Remove(id); AddElixir(paid); return true; }
            if (!CanBuy(id)) return false;
            paid = Cost(id); Elixir -= paid; Owned.Add(id); Pending.Add(id, paid); return true;
        }
        public bool OnDeath(double now, Func<int, int> random)
        {
            if (_deathPaid || Respawning) return false;
            _deathPaid = true; LifeSerial++; AddElixir(Rules.ElixirPerDeath);
            ActiveUntil = CloakUntil = -1; ShieldArmed = false; Offer.Clear(); Pending.Clear();
            if (RespawnsLeft <= 0) return false;
            Respawning = true; RespawnAt = now + RespawnDelay;
            var families = new HashSet<string>();
            for (var grade = 1; grade <= 3; grade++)
            {
                var pool = new List<string>();
                foreach (var a in Catalog.Abilities)
                {
                    var id = a["id"].AsString();
                    if (a["kind"].AsInt() != 1 || !a["wired"].AsBool() || Has(id) || Catalog.Rarity(id) != grade || families.Contains(UprisingCatalog.Family(id))) continue;
                    if (id.EndsWith("1", StringComparison.Ordinal) && Has(id.Substring(0,id.Length-1)+"2")) continue;
                    pool.Add(id);
                }
                var pick = pool.Count == 0 ? "" : pool[Math.Max(0, Math.Min(pool.Count - 1, random(pool.Count)))];
                Offer.Add(pick); if (pick != "") families.Add(UprisingCatalog.Family(pick));
            }
            return true;
        }
        public bool Respawn(double now, bool ongoing)
        {
            if (!Respawning || !ongoing || now < RespawnAt) return false;
            RespawnsLeft--; LifeSerial++; Respawning = false; _deathPaid = false;
            Offer.Clear(); Pending.Clear(); LastStandUsed = ShieldArmed = false; ActiveUntil = -1; ReadyAt = 0; CloakUntil = now + 10;
            return true;
        }
        public bool Running(double now) { return ActiveUntil > now; }
        private int IndexOf(string id)
        {
            for (var i = 0; i < Catalog.Abilities.Count && i < 64; i++) if (Catalog.Abilities[i]["id"].AsString() == id) return i;
            return -1;
        }
        private string IdAt(int index)
        {
            return index >= 0 && index < Catalog.Abilities.Count ? Catalog.Abilities[index]["id"].AsString() : null;
        }
        private static float Remaining(double until, double now) { return until > now ? (float)(until - now) : -1f; }
        private static double Until(float remaining, double now) { return remaining > 0f ? now + remaining : -1; }
        private byte OfferCode(int slot)
        {
            if (slot >= Offer.Count) return UprisingSnapshot.NoOffer;
            if (string.IsNullOrEmpty(Offer[slot])) return UprisingSnapshot.EmptyOffer;
            var index = IndexOf(Offer[slot]);
            return index < 0 ? UprisingSnapshot.EmptyOffer : (byte)index;
        }
        /// <summary>Host: the replicated part of this state at <paramref name="now"/>.</summary>
        public UprisingSnapshot Capture(double now)
        {
            var s = new UprisingSnapshot
            {
                Elixir = Elixir, LifeSerial = LifeSerial, RespawnsLeft = (byte)Math.Max(0, Math.Min(255, RespawnsLeft)),
                RespawnIn = Respawning ? (float)Math.Max(0, RespawnAt - now) : -1f,
                ActiveIn = Remaining(ActiveUntil, now), ReadyIn = Remaining(ReadyAt, now), CloakIn = Remaining(CloakUntil, now),
                Offer0 = OfferCode(0), Offer1 = OfferCode(1), Offer2 = OfferCode(2),
                Flags = (byte)((Respawning ? UprisingSnapshot.FlagRespawning : 0) | (ShieldArmed ? UprisingSnapshot.FlagShield : 0) | (LastStandUsed ? UprisingSnapshot.FlagLastStand : 0)),
            };
            foreach (var id in Owned) { var i = IndexOf(id); if (i >= 0) s.Owned |= 1UL << i; }
            foreach (var id in Pending.Keys) { var i = IndexOf(id); if (i >= 0) s.Pending |= 1UL << i; }
            return s;
        }
        /// <summary>Client puppet: mirror the host state. Never used by the authority.</summary>
        public void Apply(UprisingSnapshot s, double now)
        {
            Elixir = Math.Max(0, s.Elixir); LifeSerial = s.LifeSerial; RespawnsLeft = s.RespawnsLeft;
            Respawning = s.Respawning; RespawnAt = Respawning ? now + Math.Max(0f, s.RespawnIn) : 0;
            _deathPaid = !s.AliveLife;
            ActiveUntil = Until(s.ActiveIn, now); ReadyAt = s.ReadyIn > 0f ? now + s.ReadyIn : 0; CloakUntil = Until(s.CloakIn, now);
            ShieldArmed = (s.Flags & UprisingSnapshot.FlagShield) != 0; LastStandUsed = (s.Flags & UprisingSnapshot.FlagLastStand) != 0;
            Owned.Clear(); Pending.Clear(); Offer.Clear();
            for (var i = 0; i < Catalog.Abilities.Count && i < 64; i++)
            {
                var id = IdAt(i);
                if ((s.Owned & (1UL << i)) != 0) Owned.Add(id);
                if ((s.Pending & (1UL << i)) != 0) { Owned.Add(id); Pending[id] = Cost(id); }
            }
            for (var slot = 0; slot < 3; slot++)
            {
                var code = slot == 0 ? s.Offer0 : slot == 1 ? s.Offer1 : s.Offer2;
                if (code == UprisingSnapshot.NoOffer) break;
                Offer.Add(code == UprisingSnapshot.EmptyOffer ? "" : IdAt(code) ?? "");
            }
        }
        /// <summary>Catalogue figure; Elixir-shop upgrades scale with <see cref="UprisingRules.UpgradePower"/>.</summary>
        public float Number(string id, string key, float fallback = 1)
        {
            var ability = Catalog.Get(id);
            return Rules.Scale(ability, key, (float)ability[key].AsDouble(fallback));
        }
        public float[] Numbers(string id, string key, float[] fallback)
        {
            var ability = Catalog.Get(id); var node = ability[key];
            var values = fallback;
            if (node.Kind == JsonKind.Array)
            {
                var items = new List<float>(); foreach (var item in node.Items) items.Add((float)item.AsDouble());
                values = items.ToArray();
            }
            return Rules.ScaleAll(ability, values);
        }
        public float Outgoing(DamageType type, double now)
        {
            if (type == DamageType.Ability) return 1;
            var factor = 1f;
            if (type == DamageType.Shell || type == DamageType.Explosion) factor *= Has("damageBoost2") ? Number("damageBoost2", "dmg_out_mul", 1.4f) : Has("damageBoost1") ? Number("damageBoost1", "dmg_out_mul", 1.2f) : 1;
            if (type == DamageType.Collision) factor *= Has("ramming2") ? Number("ramming2", "ram_out_mul", 2.1f) : Has("ramming1") ? Number("ramming1", "ram_out_mul", 1.6f) : 1;
            if (Running(now) && ActiveId == "berserk") factor *= 1.5f;
            return factor;
        }
        public float ReloadMultiplier(double now)
        {
            return (Has("reloadBoost2") ? Number("reloadBoost2", "reload_mul", .65f) : Has("reloadBoost1") ? Number("reloadBoost1", "reload_mul", .8f) : 1) * (Running(now) && ActiveId == "berserk" ? .7f : 1);
        }
        public int Absorb(int amount, int health, double now)
        {
            if (amount <= 0) return amount;
            if (ShieldArmed && Running(now)) { ShieldArmed = false; return 0; }
            if (Has("comeBackKit") && !LastStandUsed && amount >= health) { LastStandUsed = true; return Math.Max(0,health-1); }
            return amount;
        }
    }
}
