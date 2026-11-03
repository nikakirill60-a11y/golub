using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Tanks;
using LiteNetLib.Utils;
using UnityEngine;

namespace BlitzLegacy.Direct
{
    /// <summary>
    /// Wire format of the dedicated-server online mode. Every packet starts
    /// with one <see cref="DirectMessage"/> byte. Nothing a client sends is
    /// trusted: the server parses it with bounds and drops the peer on garbage.
    ///
    /// The server is the only authority. A client sends its intent
    /// (<see cref="DirectInput"/>) and receives the world as per-slot
    /// snapshots stamped with the server's simulation time.
    /// </summary>
    public static class DirectProtocol
    {
        /// <summary>Bumped on any incompatible change of this file.</summary>
        public const int Version = 3;

        /// <summary>Connection request prefix (not a secret, only filters strangers).</summary>
        public const string ConnectKey = "blitz-legacy-direct";

        public const int DefaultPort = 7777;

        /// <summary>Secret reconnect token length; the server only ever keeps its SHA-256.</summary>
        public const int TokenLength = 32;

        /// <summary>Upper bound of a hello inside the connection request.</summary>
        public const int MaxHelloBytes = 1024;

        /// <summary>Upper bound of any packet from a client.</summary>
        public const int MaxClientPacket = 1024;

        /// <summary>Snapshots per second the server sends.</summary>
        public const int SnapshotRate = 30;

        /// <summary>Inputs per second a client sends.</summary>
        public const int InputRate = 30;

        public const int MaxChatLength = 160;
        public const int MaxKeyLength = 64;

        // ------------------------------------------------------------ connect

        /// <summary>Client: the connection request payload.</summary>
        public static void WriteConnect(NetDataWriter w, string build, BattleMode mode, byte[] token, BattleNetHello hello)
        {
            if (token == null || token.Length != TokenLength) { throw new ArgumentException("token"); }
            SandboxOnlineBan.Ensure(mode, "connect");
            var helloBytes = hello.ToBytes();
            if (helloBytes.Length > MaxHelloBytes) { throw new ArgumentException("hello too large"); }
            w.Put(ConnectKey);
            w.Put(Version);
            w.Put(build ?? string.Empty);
            w.Put((byte)mode);
            w.Put(token);
            w.Put(helloBytes.Length);
            w.Put(helloBytes);
        }

        public sealed class ConnectRequest
        {
            public int Version;
            public string Build;
            public BattleMode Mode;
            public byte[] Token;
            public BattleNetHello Hello;
        }

        /// <summary>
        /// Server: parses a connection request; throws on anything malformed.
        /// A request of another protocol version returns with only
        /// <see cref="ConnectRequest.Version"/> set.
        /// </summary>
        public static ConnectRequest ReadConnect(NetDataReader r)
        {
            var key = GetSafeString(r, 64);
            if (key != ConnectKey) { throw new FormatException("bad key"); }
            Need(r, 4);
            var request = new ConnectRequest { Version = r.GetInt() };
            if (request.Version != Version) { return request; }
            request.Build = GetSafeString(r, 64);
            Need(r, 1);
            var mode = r.GetByte();
            if (SandboxOnlineBan.IsSandbox(mode)) { throw new SandboxOnlineException("connect"); }
            if (!Enum.IsDefined(typeof(BattleMode), (int)mode)) { throw new FormatException("bad mode " + mode); }
            request.Mode = (BattleMode)mode;
            if (r.AvailableBytes < TokenLength) { throw new FormatException("short token"); }
            request.Token = new byte[TokenLength];
            r.GetBytes(request.Token, TokenLength);
            if (r.AvailableBytes < 4) { throw new FormatException("no hello"); }
            var length = r.GetInt();
            if (length <= 0 || length > MaxHelloBytes || length > r.AvailableBytes) { throw new FormatException("bad hello length"); }
            var hello = new byte[length];
            r.GetBytes(hello, length);
            request.Hello = BattleNetHello.FromBytes(hello);
            return request;
        }

        // ------------------------------------------------------------ helpers

        public static void Put(NetDataWriter w, Vector3 v) { w.Put(v.x); w.Put(v.y); w.Put(v.z); }

        public static Vector3 GetVector3(NetDataReader r) { return new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()); }

        public static void Put(NetDataWriter w, Quaternion q) { w.Put(q.x); w.Put(q.y); w.Put(q.z); w.Put(q.w); }

        public static Quaternion GetQuaternion(NetDataReader r) { return new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat()); }

        /// <summary>
        /// A length-prefixed string that may not run past the packet: the
        /// reader itself does not bound reads by the received size.
        /// </summary>
        public static string GetSafeString(NetDataReader r, int maxChars)
        {
            if (r.AvailableBytes < 2) { throw new FormatException("short string"); }
            var size = r.PeekUShort();
            if (size == 0) { r.GetUShort(); return string.Empty; }
            if (size - 1 > r.AvailableBytes - 2 || size - 1 > maxChars * 4) { throw new FormatException("bad string length"); }
            var value = r.GetString();
            if (value.Length > maxChars) { throw new FormatException("string too long"); }
            return value;
        }

        /// <summary>Throws unless at least <paramref name="bytes"/> remain.</summary>
        public static void Need(NetDataReader r, int bytes)
        {
            if (r.AvailableBytes < bytes) { throw new FormatException("short packet"); }
        }

        public static bool IsFinite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }

        public static bool IsFinite(Vector3 v) { return IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z); }

        /// <summary>Lower-case hex SHA-256 of a token: the public proof of a seat.</summary>
        public static string SeatOf(byte[] token)
        {
            if (token == null || token.Length != TokenLength) { return null; }
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha.ComputeHash(token);
                var chars = new char[hash.Length * 2];
                const string hex = "0123456789abcdef";
                for (var i = 0; i < hash.Length; i++)
                {
                    chars[i * 2] = hex[hash[i] >> 4];
                    chars[i * 2 + 1] = hex[hash[i] & 15];
                }
                return new string(chars);
            }
        }
    }

    /// <summary>First byte of every packet.</summary>
    public enum DirectMessage : byte
    {
        // client -> server
        Loaded = 1,
        Input = 2,
        Chat = 3,

        // server -> client
        Lobby = 20,
        Plan = 21,
        Snapshot = 22,
        Rockets = 23,
        Shot = 24,
        UprisingEffect = 25,
        UprisingChain = 26,
        BattleStart = 27,
        IntroReleased = 28,
        BattleEnd = 29,
        ChatDeliver = 30,
        ChatNotice = 31,
        /// <summary>The server removes this peer; payload is a localization key.</summary>
        Kick = 32,
        /// <summary>A shell, explosion or ability landed on (or bounced off) a tank: <see cref="DirectHit"/>.</summary>
        Hit = 33,
        /// <summary>slot:byte id:string point:vector3, a one-shot Big Boss effect.</summary>
        BigBossEffect = 34,
        /// <summary>slot:byte id:string point:vector3, a gravity trap placed by the slot.</summary>
        BigBossTrap = 35,
    }

    /// <summary>Button bits of <see cref="DirectInput.Buttons"/>.</summary>
    [Flags]
    public enum DirectButtons : byte
    {
        None = 0,
        Fire = 1,
        HasAim = 2,
        TurretBlock = 4,
        Arty = 8,
    }

    /// <summary>One sample of a client's controls: intent only, never state.</summary>
    public struct DirectInput
    {
        public uint Sequence;
        public float Forward;
        public float Right;
        public Vector3 Aim;
        public DirectButtons Buttons;
        public byte ShellId;
        public byte UprisingAction;
        public byte UprisingSequence;
        /// <summary>
        /// Bumped once per input interval in which the trigger was pressed. A
        /// lost packet then cannot swallow a tap: the next one still carries
        /// the changed counter and the server fires once for it.
        /// </summary>
        public byte FireSequence;

        public void Write(NetDataWriter w)
        {
            w.Put((byte)DirectMessage.Input);
            w.Put(Sequence);
            w.Put(Forward);
            w.Put(Right);
            DirectProtocol.Put(w, Aim);
            w.Put((byte)Buttons);
            w.Put(ShellId);
            w.Put(UprisingAction);
            w.Put(UprisingSequence);
            w.Put(FireSequence);
        }

        /// <summary>Reads the body after the message byte.</summary>
        public static DirectInput Read(NetDataReader r)
        {
            return new DirectInput
            {
                Sequence = r.GetUInt(),
                Forward = r.GetFloat(),
                Right = r.GetFloat(),
                Aim = DirectProtocol.GetVector3(r),
                Buttons = (DirectButtons)r.GetByte(),
                ShellId = r.GetByte(),
                UprisingAction = r.GetByte(),
                UprisingSequence = r.GetByte(),
                FireSequence = r.GetByte(),
            };
        }

        public bool Has(DirectButtons button) { return (Buttons & button) != 0; }

        /// <summary>Body bytes after the message byte; anything else is malformed.</summary>
        public const int Size = 4 + 4 + 4 + 12 + 1 + 1 + 1 + 1 + 1;
    }

    /// <summary>
    /// Server-side prediction of a player whose input stopped arriving (packet
    /// loss, a short link drop). The tank keeps the last drive and aim for
    /// <see cref="HoldSeconds"/> — it goes on the way it was going, so the
    /// others can still lead, hit and penetrate it — then the axes fade to a
    /// stop over <see cref="FadeSeconds"/>. When input returns the server simply
    /// resumes from where its own simulation is: the server is the only
    /// authority, so there is nothing to rewind and nothing teleports.
    /// </summary>
    public static class DirectInputCoast
    {
        public const float HoldSeconds = 0.6f;
        public const float FadeSeconds = 0.6f;

        /// <summary>A held trigger is released this long after the last input (never fires blind).</summary>
        public const float FireSeconds = 0.15f;

        /// <summary>Scale of the last drive axes for an input this old (seconds).</summary>
        public static float DriveScale(float age)
        {
            if (!(age > HoldSeconds)) { return 1f; }
            if (age >= HoldSeconds + FadeSeconds) { return 0f; }
            return 1f - (age - HoldSeconds) / FadeSeconds;
        }

        public static bool KeepsFire(float age) { return !(age > FireSeconds); }

        /// <summary>The whole coast is over: the input is dropped.</summary>
        public static bool Expired(float age) { return age >= HoldSeconds + FadeSeconds; }
    }

    /// <summary>
    /// One hit the server's damage model decided, sent to the battle so the
    /// victim's client plays what the offline game plays: the bounce or the
    /// penetration heard inside the hull. Health itself still only rides the
    /// snapshots; this message never moves it.
    /// </summary>
    public struct DirectHit
    {
        public const byte NoSlot = 255;
        public const byte FlagSuppress = 1;
        public const byte FlagNotTeamDamage = 2;

        public byte Victim;
        public byte Attacker;
        public int Dmg;
        public int DmgBase;
        public DamageType Type;
        public ShellType Shell;
        public byte Flags;
        public Vector3 Position;
        public int ShotId;

        /// <summary>Body bytes after the message byte.</summary>
        public const int Size = 1 + 1 + 4 + 4 + 1 + 1 + 1 + 12 + 4;

        public static DirectHit From(int victim, int attacker, DamageInfo info)
        {
            return new DirectHit
            {
                Victim = (byte)victim,
                Attacker = attacker >= 0 && attacker < NoSlot ? (byte)attacker : NoSlot,
                Dmg = Math.Max(0, info.Dmg),
                DmgBase = Math.Max(0, info.DmgBase),
                Type = info.DmgType,
                Shell = info.Shell,
                Flags = (byte)((info.SuppressFeedback ? FlagSuppress : 0) | (info.NotTeamDamage ? FlagNotTeamDamage : 0)),
                Position = new Vector3(info.Pos.X, info.Pos.Y, info.Pos.Z),
                ShotId = info.ShotId,
            };
        }

        public void Write(NetDataWriter w)
        {
            w.Put((byte)DirectMessage.Hit);
            w.Put(Victim);
            w.Put(Attacker);
            w.Put(Dmg);
            w.Put(DmgBase);
            w.Put((byte)Type);
            w.Put((byte)Shell);
            w.Put(Flags);
            DirectProtocol.Put(w, Position);
            w.Put(ShotId);
        }

        /// <summary>Reads the body after the message byte; anything out of range throws.</summary>
        public static DirectHit Read(NetDataReader r)
        {
            DirectProtocol.Need(r, Size);
            var hit = new DirectHit
            {
                Victim = r.GetByte(),
                Attacker = r.GetByte(),
                Dmg = r.GetInt(),
                DmgBase = r.GetInt(),
            };
            var type = r.GetByte();
            var shell = r.GetByte();
            hit.Flags = r.GetByte();
            hit.Position = DirectProtocol.GetVector3(r);
            hit.ShotId = r.GetInt();
            if (type > (byte)DamageType.Environment || shell > (byte)ShellType.Hesh) { throw new FormatException("hit kind"); }
            if (hit.Dmg < 0 || hit.DmgBase < 0 || !DirectProtocol.IsFinite(hit.Position)) { throw new FormatException("hit values"); }
            hit.Type = (DamageType)type;
            hit.Shell = (ShellType)shell;
            return hit;
        }

        /// <summary>The client-side copy, attributed to the puppet of the attacker slot.</summary>
        public DamageInfo ToInfo(object attackerTank)
        {
            return new DamageInfo
            {
                AttackerTank = attackerTank,
                Dmg = Dmg,
                DmgBase = DmgBase,
                DmgType = Type,
                Shell = Shell,
                ShotId = ShotId,
                Pos = new Vec3(Position.x, Position.y, Position.z),
                SuppressFeedback = (Flags & FlagSuppress) != 0,
                NotTeamDamage = (Flags & FlagNotTeamDamage) != 0,
            };
        }
    }

    /// <summary>
    /// Server-side sanity limits on remote input, the same rules as the Photon
    /// host's guard: honest clients only send axes in [-1, 1] and a finite aim.
    /// </summary>
    public static class DirectInputGuard
    {
        public const float MaxAimDistance = 5000f;

        /// <summary>Hard strikes after which the server removes the player from the battle.</summary>
        public const int KickStrikes = 8;

        /// <summary>
        /// Returns false when the input had to be corrected. <paramref name="hard"/>:
        /// an axis outside [-1, 1] or non-finite, which no honest client produces.
        /// </summary>
        public static bool Sanitize(ref DirectInput input, out bool hard)
        {
            var axes = true;
            input.Forward = Axis(input.Forward, ref axes);
            input.Right = Axis(input.Right, ref axes);
            hard = !axes;
            var ok = axes;
            if (!DirectProtocol.IsFinite(input.Aim) || input.Aim.sqrMagnitude > MaxAimDistance * MaxAimDistance)
            {
                input.Aim = Vector3.zero;
                input.Buttons &= ~DirectButtons.HasAim;
                ok = false;
            }
            return ok;
        }

        private static float Axis(float v, ref bool ok)
        {
            if (!DirectProtocol.IsFinite(v)) { ok = false; return 0f; }
            if (v < -1f || v > 1f) { ok = false; return Mathf.Clamp(v, -1f, 1f); }
            return v;
        }
    }

    /// <summary>The replicated state of one tank (mirrors the Photon <c>NetTankState</c>).</summary>
    public struct DirectTankState
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public float Yaw;
        public float Pitch;
        public int Health;
        public float Forward;
        public float Right;
        public float SpeedMs;
        public byte WheelHits;
        /// <summary>Bit 0: on another tank. Bit 1: blue team-killer. Bit 2: arty (lob) aim.</summary>
        public byte Flags;
        public Vector3 Aim;
        public float Dispersion;
        /// <summary>Team damage this slot dealt this battle.</summary>
        public int TeamDamage;

        public void Write(NetDataWriter w)
        {
            DirectProtocol.Put(w, Position);
            DirectProtocol.Put(w, Rotation);
            w.Put(Yaw);
            w.Put(Pitch);
            w.Put(Health);
            w.Put((sbyte)Mathf.RoundToInt(Mathf.Clamp(Forward, -1f, 1f) * 127f));
            w.Put((sbyte)Mathf.RoundToInt(Mathf.Clamp(Right, -1f, 1f) * 127f));
            w.Put(SpeedMs);
            w.Put(WheelHits);
            w.Put(Flags);
            DirectProtocol.Put(w, Aim);
            w.Put(Dispersion);
            w.Put(TeamDamage);
        }

        public static DirectTankState Read(NetDataReader r)
        {
            return new DirectTankState
            {
                Position = DirectProtocol.GetVector3(r),
                Rotation = DirectProtocol.GetQuaternion(r),
                Yaw = r.GetFloat(),
                Pitch = r.GetFloat(),
                Health = r.GetInt(),
                Forward = r.GetSByte() / 127f,
                Right = r.GetSByte() / 127f,
                SpeedMs = r.GetFloat(),
                WheelHits = r.GetByte(),
                Flags = r.GetByte(),
                Aim = DirectProtocol.GetVector3(r),
                Dispersion = r.GetFloat(),
                TeamDamage = r.GetInt(),
            };
        }

        /// <summary>Bytes of one <see cref="Write"/>.</summary>
        public const int Size = 12 + 16 + 4 + 4 + 4 + 1 + 1 + 4 + 1 + 1 + 12 + 4 + 4;
    }

    /// <summary>Uprising state of one slot on the wire.</summary>
    public static class DirectUprising
    {
        public const int Size = 8 + 8 + 4 + 4 + 8 * 4 + 5;

        public static void Write(NetDataWriter w, UprisingSnapshot s)
        {
            w.Put(s.Owned); w.Put(s.Pending); w.Put(s.Elixir); w.Put(s.LifeSerial);
            w.Put(s.RespawnIn); w.Put(s.ActiveIn); w.Put(s.ReadyIn); w.Put(s.CloakIn);
            w.Put(s.CurseIn); w.Put(s.MarkIn); w.Put(s.SplashIn); w.Put(s.ChainIn);
            w.Put(s.Offer0); w.Put(s.Offer1); w.Put(s.Offer2); w.Put(s.Flags); w.Put(s.RespawnsLeft);
        }

        public static UprisingSnapshot Read(NetDataReader r)
        {
            return new UprisingSnapshot
            {
                Owned = r.GetULong(), Pending = r.GetULong(), Elixir = r.GetInt(), LifeSerial = r.GetInt(),
                RespawnIn = r.GetFloat(), ActiveIn = r.GetFloat(), ReadyIn = r.GetFloat(), CloakIn = r.GetFloat(),
                CurseIn = r.GetFloat(), MarkIn = r.GetFloat(), SplashIn = r.GetFloat(), ChainIn = r.GetFloat(),
                Offer0 = r.GetByte(), Offer1 = r.GetByte(), Offer2 = r.GetByte(), Flags = r.GetByte(),
                RespawnsLeft = r.GetByte(),
            };
        }
    }

    /// <summary>Big Boss state of one slot on the wire.</summary>
    public static class DirectBigBoss
    {
        /// <summary>Four bytes, the life serial, the points and ten timers.</summary>
        public const int Size = 4 + 4 + 4 + 10 * 4;

        public static void Write(NetDataWriter w, BigBossSnapshot s)
        {
            w.Put(s.RoleId); w.Put(s.Flags); w.Put(s.NextRoleId); w.Put(s.Lives);
            w.Put(s.LifeSerial); w.Put(s.Points); w.Put(s.RespawnIn);
            w.Put(s.Ready0); w.Put(s.Ready1); w.Put(s.Ready2);
            w.Put(s.ShieldIn); w.Put(s.ShieldDuration); w.Put(s.PoisonCastIn); w.Put(s.AuraIn);
            w.Put(s.PoisonedIn); w.Put(s.PoisonedTotal);
        }

        public static BigBossSnapshot Read(NetDataReader r)
        {
            return new BigBossSnapshot
            {
                RoleId = r.GetByte(), Flags = r.GetByte(), NextRoleId = r.GetSByte(), Lives = r.GetSByte(),
                LifeSerial = r.GetInt(), Points = r.GetFloat(), RespawnIn = r.GetFloat(),
                Ready0 = r.GetFloat(), Ready1 = r.GetFloat(), Ready2 = r.GetFloat(),
                ShieldIn = r.GetFloat(), ShieldDuration = r.GetFloat(), PoisonCastIn = r.GetFloat(), AuraIn = r.GetFloat(),
                PoisonedIn = r.GetFloat(), PoisonedTotal = r.GetFloat(),
            };
        }
    }

    /// <summary>One guided rocket in flight on the server.</summary>
    public struct DirectRocket
    {
        public int Id;
        public Vector3 Position;
        public Vector3 Velocity;

        public const int Size = 4 + 12 + 12;
    }

    /// <summary>
    /// Snapshot chunk layout. The world of one tick is split over as many
    /// unreliable packets as the path MTU needs; each carries whole slots, so
    /// a lost chunk only freezes its slots for one tick.
    ///
    ///   [Snapshot] tick:uint time:double slotCount:byte mode:byte first:byte count:byte
    ///              [teamA:short teamB:short when mode is Big Boss]
    ///              count x (DirectTankState [+ UprisingSnapshot | BigBossSnapshot])
    /// </summary>
    public static class DirectSnapshot
    {
        /// <summary>Mode byte: the rule set whose per-slot extension follows each tank state.</summary>
        public const byte ModeNone = 0, ModeUprising = 1, ModeBigBoss = 2;

        /// <summary>The fixed part of every snapshot header.</summary>
        public const int HeaderSize = 1 + 4 + 8 + 1 + 1 + 1 + 1;

        /// <summary>The Big Boss ticket ledger after the fixed header.</summary>
        public const int TicketsSize = 2 + 2;

        public static byte ModeOf(BattleMode mode)
        {
            return mode == BattleMode.Uprising ? ModeUprising : mode == BattleMode.BigBoss ? ModeBigBoss : ModeNone;
        }

        public static int HeaderBytes(byte mode) { return HeaderSize + (mode == ModeBigBoss ? TicketsSize : 0); }

        public static int SlotSize(byte mode)
        {
            return DirectTankState.Size + (mode == ModeUprising ? DirectUprising.Size : mode == ModeBigBoss ? DirectBigBoss.Size : 0);
        }

        /// <summary>Slots that fit in one packet of <paramref name="maxPacket"/> bytes (at least one).</summary>
        public static int SlotsPerPacket(int maxPacket, byte mode)
        {
            return Math.Max(1, (maxPacket - HeaderBytes(mode)) / SlotSize(mode));
        }

        public static void WriteHeader(NetDataWriter w, uint tick, double time, int slotCount, byte mode, int first, int count,
            int teamA = 0, int teamB = 0)
        {
            w.Put((byte)DirectMessage.Snapshot);
            w.Put(tick);
            w.Put(time);
            w.Put((byte)slotCount);
            w.Put(mode);
            w.Put((byte)first);
            w.Put((byte)count);
            if (mode == ModeBigBoss)
            {
                w.Put((short)Math.Max(0, Math.Min(short.MaxValue, teamA)));
                w.Put((short)Math.Max(0, Math.Min(short.MaxValue, teamB)));
            }
        }

        public struct Header
        {
            public uint Tick;
            public double Time;
            public int SlotCount;
            public byte Mode;
            public int First;
            public int Count;
            /// <summary>Big Boss tickets of this tick; 0 in the other modes.</summary>
            public int TeamA, TeamB;
            public bool Uprising { get { return Mode == ModeUprising; } }
            public bool BigBoss { get { return Mode == ModeBigBoss; } }
        }

        /// <summary>Reads the header after the message byte; throws when it is out of range.</summary>
        public static Header ReadHeader(NetDataReader r)
        {
            var h = new Header
            {
                Tick = r.GetUInt(),
                Time = r.GetDouble(),
                SlotCount = r.GetByte(),
                Mode = r.GetByte(),
                First = r.GetByte(),
                Count = r.GetByte(),
            };
            if (h.Mode > ModeBigBoss) { throw new FormatException("snapshot mode " + h.Mode); }
            if (h.SlotCount > BattleNetPlan.MaxSlots || h.First + h.Count > h.SlotCount)
            {
                throw new FormatException("snapshot slots out of range");
            }
            if (h.BigBoss)
            {
                DirectProtocol.Need(r, TicketsSize);
                h.TeamA = r.GetShort();
                h.TeamB = r.GetShort();
                if (h.TeamA < 0 || h.TeamB < 0) { throw new FormatException("snapshot tickets out of range"); }
            }
            return h;
        }

        public const int RocketHeaderSize = 1 + 4 + 8 + 1;

        public static void WriteRockets(NetDataWriter w, uint tick, double time, IList<DirectRocket> rockets, int first, int count)
        {
            w.Put((byte)DirectMessage.Rockets);
            w.Put(tick);
            w.Put(time);
            w.Put((byte)count);
            for (var i = first; i < first + count; i++)
            {
                w.Put(rockets[i].Id);
                DirectProtocol.Put(w, rockets[i].Position);
                DirectProtocol.Put(w, rockets[i].Velocity);
            }
        }

        /// <summary>Reads a rocket packet after the message byte.</summary>
        public static void ReadRockets(NetDataReader r, out uint tick, out double time, List<DirectRocket> into)
        {
            tick = r.GetUInt();
            time = r.GetDouble();
            var count = r.GetByte();
            if (count > 64) { throw new FormatException("too many rockets"); }
            for (var i = 0; i < count; i++)
            {
                into.Add(new DirectRocket
                {
                    Id = r.GetInt(),
                    Position = DirectProtocol.GetVector3(r),
                    Velocity = DirectProtocol.GetVector3(r),
                });
            }
        }
    }

    /// <summary>Lobby status the server pushes to a waiting client.</summary>
    public struct DirectLobby
    {
        /// <summary>Humans in the lobby this client would start with.</summary>
        public int Players;
        public int Capacity;
        /// <summary>Seconds until the start, negative while waiting.</summary>
        public float StartsIn;
        /// <summary>Humans waiting on the whole server in this mode.</summary>
        public int Waiting;
        /// <summary>True while another battle runs and this client waits for it.</summary>
        public bool Busy;
        public int[] Tiers;
        public int[] Classes;
        public int[] QueueTiers;
        public int[] QueueClasses;

        public void Write(NetDataWriter w)
        {
            w.Put((byte)DirectMessage.Lobby);
            w.Put((byte)Mathf.Clamp(Players, 0, 255));
            w.Put((byte)Mathf.Clamp(Capacity, 0, 255));
            w.Put(StartsIn);
            w.Put((byte)Mathf.Clamp(Waiting, 0, 255));
            w.Put(Busy);
            PutMix(w, Tiers, OnlineQueue.Tiers);
            PutMix(w, Classes, OnlineQueue.Classes);
            PutMix(w, QueueTiers, OnlineQueue.Tiers);
            PutMix(w, QueueClasses, OnlineQueue.Classes);
        }

        /// <summary>Reads the body after the message byte.</summary>
        public static DirectLobby Read(NetDataReader r)
        {
            var lobby = new DirectLobby
            {
                Players = r.GetByte(),
                Capacity = r.GetByte(),
                StartsIn = r.GetFloat(),
                Waiting = r.GetByte(),
                Busy = r.GetBool(),
            };
            lobby.Tiers = GetMix(r, OnlineQueue.Tiers);
            lobby.Classes = GetMix(r, OnlineQueue.Classes);
            lobby.QueueTiers = GetMix(r, OnlineQueue.Tiers);
            lobby.QueueClasses = GetMix(r, OnlineQueue.Classes);
            return lobby;
        }

        private static void PutMix(NetDataWriter w, int[] values, int length)
        {
            for (var i = 0; i < length; i++)
            {
                w.Put((byte)(values != null && i < values.Length ? Mathf.Clamp(values[i], 0, 255) : 0));
            }
        }

        private static int[] GetMix(NetDataReader r, int length)
        {
            var values = new int[length];
            for (var i = 0; i < length; i++) { values[i] = r.GetByte(); }
            return values;
        }
    }
}
