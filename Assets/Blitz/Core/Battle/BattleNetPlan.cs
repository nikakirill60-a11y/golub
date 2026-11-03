using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BlitzLegacy.Core.Tanks;

namespace BlitzLegacy.Core.Battle
{
    /// <summary>
    /// The roster of a networked battle as the host decided it. Every peer
    /// builds its <c>BattleSession</c> from the same plan, so the slots, the
    /// tanks, their modules and the map are identical everywhere and a slot
    /// index is enough to address a tank over the wire.
    ///
    /// Slots are ordered team 0 first, then team 1, which is the order
    /// <c>BattleSession</c> seats tanks in.
    /// </summary>
    public sealed class BattleNetPlan
    {
        /// <summary>Bumped whenever the byte layout changes.</summary>
        public const int FormatVersion = 5;

        /// <summary>Hex SHA-256 of a peer's reconnect token.</summary>
        public const int MaxSeatLength = 64;

        /// <summary>Owner value of a slot driven by the host's AI.</summary>
        public const int BotOwner = -1;

        /// <summary>Hard upper bound of tanks in one battle (10 x 10).</summary>
        public const int MaxSlots = 20;

        public string MapId = string.Empty;
        public BattleMode Mode = BattleMode.Normal;

        /// <summary>The build id every peer must share; a mismatch is refused.</summary>
        public string Build = string.Empty;

        /// <summary>PlayerId of the peer that hosts this plan; survivors skip it when choosing the next host.</summary>
        public int HostOwner = BotOwner;

        public readonly List<Slot> Slots = new List<Slot>();

        public sealed class Slot
        {
            public int Team;
            /// <summary>Fusion PlayerId of the human driving it, or <see cref="BotOwner"/>.</summary>
            public int Owner = BotOwner;
            public string TankId = string.Empty;
            public string Name = string.Empty;
            public int Gun;
            public int Engine;
            public int Tracks;
            public int Turret;
            /// <summary><c>AiSkillLevel</c> of a bot slot; ignored for humans.</summary>
            public int Skill;
            /// <summary>Hidden host reputation of a human seat (see <see cref="OnlineReputation"/>).</summary>
            public int Reputation = OnlineReputation.Neutral;
            /// <summary>
            /// Public proof of a human seat: hex SHA-256 of the secret token the
            /// player connects with. Lets the host (or a migrated host) give the
            /// seat back to whoever presents the matching token, and only them.
            /// </summary>
            public string Seat = string.Empty;
            public readonly List<int> Ammo = new List<int>();
            public readonly List<string> Equipment = new List<string>();

            /// <summary>
            /// A human seat's hangar consumables in slot order, so whoever hosts
            /// the battle (a migrated host too) spends them for that crew.
            /// Bots carry none here: their mode supply follows from the rules.
            /// </summary>
            public readonly List<string> Consumables = new List<string>();

            /// <summary>
            /// The camouflage the seat fights in: the hangar slot of the map's
            /// kind, resolved by the host. Empty for bots and bare slots.
            /// </summary>
            public string Camouflage = string.Empty;

            public bool IsHuman { get { return Owner != BotOwner; } }
        }

        public int TeamCount(int team)
        {
            var count = 0;
            for (var i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].Team == team) { count++; }
            }
            return count;
        }

        /// <summary>Index of the human slot holding this seat, or -1.</summary>
        public int SlotOfSeat(string seat)
        {
            if (string.IsNullOrEmpty(seat)) { return -1; }
            for (var i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].IsHuman && Slots[i].Seat == seat) { return i; }
            }
            return -1;
        }

        /// <summary>Index of the slot driven by <paramref name="owner"/>, or -1.</summary>
        public int SlotOf(int owner)
        {
            if (owner == BotOwner) { return -1; }
            for (var i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].Owner == owner) { return i; }
            }
            return -1;
        }

        /// <summary>
        /// Throws when the plan cannot be seated: no slots, more than the hard
        /// cap, a team missing, an empty or mod tank id or one owner on two slots.
        /// </summary>
        public void Validate()
        {
            // The Sandbox never travels: a plan carrying it is refused outright.
            SandboxOnlineBan.Ensure(Mode, "plan");
            if (Slots.Count == 0 || Slots.Count > MaxSlots)
            {
                throw new InvalidDataException("plan slot count out of range: " + Slots.Count);
            }
            if (string.IsNullOrEmpty(MapId))
            {
                throw new InvalidDataException("plan has no map");
            }
            var owners = new HashSet<int>();
            var lastTeam = 0;
            for (var i = 0; i < Slots.Count; i++)
            {
                var slot = Slots[i];
                if (slot.Team != 0 && slot.Team != 1)
                {
                    throw new InvalidDataException("plan slot " + i + " has team " + slot.Team);
                }
                if (slot.Team < lastTeam)
                {
                    throw new InvalidDataException("plan slots are not ordered by team");
                }
                lastTeam = slot.Team;
                if (string.IsNullOrEmpty(slot.TankId))
                {
                    throw new InvalidDataException("plan slot " + i + " has no tank");
                }
                // Mod tanks never travel either: every seat must be a stock tank.
                ModTankOnlineBan.EnsureBattle(slot.TankId, "plan slot " + i);
                if (slot.IsHuman && !owners.Add(slot.Owner))
                {
                    throw new InvalidDataException("plan owner " + slot.Owner + " seated twice");
                }
                BattleNetConsumables.Validate(slot.Consumables, "plan slot " + i);
                BattleNetCamouflage.Validate(slot.Camouflage, "plan slot " + i);
            }
            if (TeamCount(0) == 0 || TeamCount(1) == 0)
            {
                throw new InvalidDataException("plan needs both teams");
            }
        }

        public byte[] ToBytes()
        {
            Validate();
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(FormatVersion);
                writer.Write(Build ?? string.Empty);
                writer.Write(HostOwner);
                writer.Write(MapId);
                writer.Write((int)Mode);
                writer.Write(Slots.Count);
                for (var i = 0; i < Slots.Count; i++)
                {
                    var slot = Slots[i];
                    writer.Write(slot.Team);
                    writer.Write(slot.Owner);
                    writer.Write(slot.TankId);
                    writer.Write(slot.Name ?? string.Empty);
                    writer.Write(slot.Gun);
                    writer.Write(slot.Engine);
                    writer.Write(slot.Tracks);
                    writer.Write(slot.Turret);
                    writer.Write(slot.Skill);
                    writer.Write(slot.Seat ?? string.Empty);
                    writer.Write(OnlineReputation.Clamp(slot.Reputation));
                    writer.Write(slot.Ammo.Count);
                    for (var a = 0; a < slot.Ammo.Count; a++) { writer.Write(slot.Ammo[a]); }
                    writer.Write(slot.Equipment.Count);
                    for (var e = 0; e < slot.Equipment.Count; e++) { writer.Write(slot.Equipment[e] ?? string.Empty); }
                    BattleNetConsumables.Write(writer, slot.Consumables);
                    writer.Write(slot.Camouflage ?? string.Empty);
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>
        /// Parses bytes that came off the network. Every count is bounded so a
        /// malformed or hostile packet fails fast instead of allocating.
        /// </summary>
        public static BattleNetPlan FromBytes(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                throw new InvalidDataException("empty plan");
            }
            try
            {
                using (var stream = new MemoryStream(data, false))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    var version = reader.ReadInt32();
                    if (version != FormatVersion)
                    {
                        throw new InvalidDataException("plan format " + version + " != " + FormatVersion);
                    }
                    var plan = new BattleNetPlan();
                    plan.Build = reader.ReadString();
                    plan.HostOwner = reader.ReadInt32();
                    plan.MapId = reader.ReadString();
                    var mode = reader.ReadInt32();
                    if (SandboxOnlineBan.IsSandbox(mode))
                    {
                        throw new SandboxOnlineException("plan");
                    }
                    if (!Enum.IsDefined(typeof(BattleMode), mode))
                    {
                        throw new InvalidDataException("plan mode " + mode);
                    }
                    plan.Mode = (BattleMode)mode;
                    var count = Bounded(reader.ReadInt32(), MaxSlots, "slots");
                    for (var i = 0; i < count; i++)
                    {
                        var slot = new Slot
                        {
                            Team = reader.ReadInt32(),
                            Owner = reader.ReadInt32(),
                            TankId = reader.ReadString(),
                            Name = reader.ReadString(),
                            Gun = reader.ReadInt32(),
                            Engine = reader.ReadInt32(),
                            Tracks = reader.ReadInt32(),
                            Turret = reader.ReadInt32(),
                            Skill = reader.ReadInt32(),
                            Seat = reader.ReadString(),
                        };
                        slot.Reputation = OnlineReputation.Clamp(reader.ReadInt32());
                        if (slot.Seat.Length > MaxSeatLength)
                        {
                            throw new InvalidDataException("plan seat too long");
                        }
                        var ammo = Bounded(reader.ReadInt32(), 8, "ammo");
                        for (var a = 0; a < ammo; a++) { slot.Ammo.Add(reader.ReadInt32()); }
                        var equipment = Bounded(reader.ReadInt32(), 16, "equipment");
                        for (var e = 0; e < equipment; e++) { slot.Equipment.Add(reader.ReadString()); }
                        BattleNetConsumables.Read(reader, slot.Consumables, "plan");
                        slot.Camouflage = BattleNetCamouflage.Read(reader, "plan");
                        plan.Slots.Add(slot);
                    }
                    if (stream.Position != stream.Length)
                    {
                        throw new InvalidDataException("trailing bytes after plan");
                    }
                    plan.Validate();
                    return plan;
                }
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException("truncated plan");
            }
        }

        private static int Bounded(int value, int max, string what)
        {
            if (value < 0 || value > max)
            {
                throw new InvalidDataException("plan " + what + " count out of range: " + value);
            }
            return value;
        }
    }

    /// <summary>
    /// What a joining client tells the host about itself: which tank it brings
    /// and under which name. The host seats it with exactly these modules.
    /// </summary>
    public sealed class BattleNetHello
    {
        public const int FormatVersion = 5;
        public const int MaxNameLength = 24;

        public string Build = string.Empty;
        public string Name = string.Empty;
        public string TankId = string.Empty;
        public int Gun;
        public int Engine;
        public int Tracks;
        public int Turret;
        public readonly List<int> Ammo = new List<int>();
        /// <summary>The hangar consumables in slot order, empty slots left out.</summary>
        public readonly List<string> Consumables = new List<string>();

        /// <summary>
        /// The summer, winter and desert camouflage slots, empty ones as the
        /// empty string; the host picks the one of the battle map's kind.
        /// </summary>
        public readonly List<string> Camouflages = new List<string>();
        /// <summary>The account is blue (team killer) when the battle starts.</summary>
        public bool TeamKiller;
        /// <summary>The account is in its strict week (see <see cref="TeamKillPolicy"/>).</summary>
        public bool TeamKillWatched;
        /// <summary>Team damage already counted towards the next blue status.</summary>
        public int TeamKillPending;
        /// <summary>
        /// Hidden host reputation, 0..1000. SECURITY: self-reported by the
        /// client; it only orders the host choice, it grants no other right.
        /// </summary>
        public int Reputation = OnlineReputation.Neutral;

        public byte[] ToBytes()
        {
            // A mod tank never goes online: the hello is not even written.
            ModTankOnlineBan.Ensure(TankId, "hello");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(FormatVersion);
                writer.Write(Build ?? string.Empty);
                writer.Write(Clip(Name));
                writer.Write(TankId ?? string.Empty);
                writer.Write(Gun);
                writer.Write(Engine);
                writer.Write(Tracks);
                writer.Write(Turret);
                writer.Write(Ammo.Count);
                for (var i = 0; i < Ammo.Count; i++) { writer.Write(Ammo[i]); }
                writer.Write((byte)((TeamKiller ? 1 : 0) | (TeamKillWatched ? 2 : 0)));
                writer.Write(Math.Max(0, TeamKillPending));
                writer.Write(OnlineReputation.Clamp(Reputation));
                BattleNetConsumables.Write(writer, Consumables);
                BattleNetCamouflage.WriteSlots(writer, Camouflages);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static BattleNetHello FromBytes(byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length > 1024)
            {
                throw new InvalidDataException("hello size out of range");
            }
            try
            {
                using (var stream = new MemoryStream(data, false))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadInt32() != FormatVersion)
                    {
                        throw new InvalidDataException("hello format mismatch");
                    }
                    var hello = new BattleNetHello
                    {
                        Build = reader.ReadString(),
                        Name = Clip(reader.ReadString()),
                        TankId = reader.ReadString(),
                        Gun = reader.ReadInt32(),
                        Engine = reader.ReadInt32(),
                        Tracks = reader.ReadInt32(),
                        Turret = reader.ReadInt32(),
                    };
                    var ammo = reader.ReadInt32();
                    if (ammo < 0 || ammo > 8) { throw new InvalidDataException("hello ammo count"); }
                    for (var i = 0; i < ammo; i++) { hello.Ammo.Add(reader.ReadInt32()); }
                    var teamKill = reader.ReadByte();
                    hello.TeamKiller = (teamKill & 1) != 0;
                    hello.TeamKillWatched = (teamKill & 2) != 0;
                    hello.TeamKillPending = Math.Max(0, reader.ReadInt32());
                    hello.Reputation = OnlineReputation.Clamp(reader.ReadInt32());
                    BattleNetConsumables.Read(reader, hello.Consumables, "hello");
                    BattleNetCamouflage.ReadSlots(reader, hello.Camouflages, "hello");
                    if (stream.Position != stream.Length)
                    {
                        throw new InvalidDataException("trailing bytes after hello");
                    }
                    if (string.IsNullOrEmpty(hello.TankId)) { throw new InvalidDataException("hello without tank"); }
                    ModTankOnlineBan.Ensure(hello.TankId, "hello");
                    return hello;
                }
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException("truncated hello");
            }
        }

        private static string Clip(string name)
        {
            if (string.IsNullOrEmpty(name)) { return string.Empty; }
            name = name.Trim();
            return name.Length > MaxNameLength ? name.Substring(0, MaxNameLength) : name;
        }
    }

    /// <summary>
    /// A consumable loadout as it travels in a hello or a plan slot: at most
    /// <see cref="ConsumableCatalog.MaxActive"/> known, distinct ids in slot
    /// order. Anything else is refused like every other malformed field.
    /// </summary>
    public static class BattleNetConsumables
    {
        /// <summary>
        /// The part of a hangar loadout that may travel: known ids, each once,
        /// at most <see cref="ConsumableCatalog.MaxActive"/>, in slot order.
        /// </summary>
        public static List<string> Sanitized(IEnumerable<string> ids)
        {
            var result = new List<string>();
            if (ids == null) { return result; }
            foreach (var id in ids)
            {
                if (result.Count >= ConsumableCatalog.MaxActive) { break; }
                if (string.IsNullOrEmpty(id) || ConsumableCatalog.Get(id) == null || result.Contains(id)) { continue; }
                result.Add(id);
            }
            return result;
        }

        public static void Validate(IReadOnlyList<string> ids, string what)
        {
            if (ids == null) { return; }
            if (ids.Count > ConsumableCatalog.MaxActive)
            {
                throw new InvalidDataException(what + " carries " + ids.Count + " consumables");
            }
            for (var i = 0; i < ids.Count; i++)
            {
                if (string.IsNullOrEmpty(ids[i]) || ConsumableCatalog.Get(ids[i]) == null)
                {
                    throw new InvalidDataException(what + " carries an unknown consumable");
                }
                for (var j = 0; j < i; j++)
                {
                    if (ids[j] == ids[i])
                    {
                        throw new InvalidDataException(what + " carries a consumable twice");
                    }
                }
            }
        }

        public static void Write(BinaryWriter writer, IReadOnlyList<string> ids)
        {
            var count = ids != null ? ids.Count : 0;
            writer.Write(count);
            for (var i = 0; i < count; i++) { writer.Write(ids[i] ?? string.Empty); }
        }

        public static void Read(BinaryReader reader, List<string> into, string what)
        {
            var count = reader.ReadInt32();
            if (count < 0 || count > ConsumableCatalog.MaxActive)
            {
                throw new InvalidDataException(what + " consumable count out of range: " + count);
            }
            for (var i = 0; i < count; i++) { into.Add(reader.ReadString()); }
            Validate(into, what);
        }
    }

    /// <summary>
    /// A camouflage id as it travels: empty, or at most
    /// <see cref="MaxIdLength"/> characters of <c>[a-z0-9_]</c>. The peers
    /// look it up in their own camouflage catalog, so an id that is well
    /// formed but unknown simply paints nothing and adds no concealment.
    /// </summary>
    public static class BattleNetCamouflage
    {
        public const int MaxIdLength = 32;

        public static bool IsWellFormed(string id)
        {
            if (id == null) { return false; }
            if (id.Length > MaxIdLength) { return false; }
            for (var i = 0; i < id.Length; i++)
            {
                var c = id[i];
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
                {
                    return false;
                }
            }

            return true;
        }

        public static void Validate(string id, string what)
        {
            if (!IsWellFormed(id ?? string.Empty))
            {
                throw new InvalidDataException(what + " carries a malformed camouflage");
            }
        }

        public static string Read(BinaryReader reader, string what)
        {
            var id = reader.ReadString();
            Validate(id, what);
            return id;
        }

        /// <summary>The slots of a hello: exactly the three kinds, or none from an older save.</summary>
        public static void WriteSlots(BinaryWriter writer, IReadOnlyList<string> slots)
        {
            var count = slots != null ? Math.Min(slots.Count, CamouflageCatalog.SlotCount) : 0;
            writer.Write(count);
            for (var i = 0; i < count; i++)
            {
                var id = slots[i] ?? string.Empty;
                writer.Write(IsWellFormed(id) ? id : string.Empty);
            }
        }

        public static void ReadSlots(BinaryReader reader, List<string> into, string what)
        {
            var count = reader.ReadInt32();
            if (count < 0 || count > CamouflageCatalog.SlotCount)
            {
                throw new InvalidDataException(what + " camouflage count out of range: " + count);
            }
            for (var i = 0; i < count; i++) { into.Add(Read(reader, what)); }
        }
    }
}
