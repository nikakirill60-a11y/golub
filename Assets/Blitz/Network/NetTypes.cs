using Fusion;
using UnityEngine;

namespace BlitzLegacy.Network
{
    /// <summary>Button bits of <see cref="NetInput.Buttons"/>.</summary>
    public enum NetButton
    {
        Fire = 0,
        HasAim = 1,
        TurretBlock = 2,
        /// <summary>The overhead ATGM view is up: the host lobs the gun and the rocket.</summary>
        Arty = 3,
    }

    /// <summary>
    /// One tick of a client's controls. Only intent travels: the host runs the
    /// physics, the reload and the damage, so nothing here can be used to
    /// teleport, insta-reload or deal damage.
    /// </summary>
    public struct NetInput : INetworkInput
    {
        public float Forward;
        public float Right;
        public Vector3 Aim;
        public NetworkButtons Buttons;
        public byte ShellId;
        public byte UprisingAction;
        public byte UprisingSequence;
    }

    /// <summary>The replicated state of one tank, written by the host every tick.</summary>
    public struct NetTankState : INetworkStruct
    {
        public Vector3 Position;
        public Quaternion Rotation;
        /// <summary>Turret yaw, radians.</summary>
        public float Yaw;
        /// <summary>Gun pitch, radians.</summary>
        public float Pitch;
        public int Health;
        /// <summary>Applied drive axes, -1..1, so tracks and wheels turn the same way.</summary>
        public float Forward;
        public float Right;
        /// <summary>Signed ground speed, m/s, in the suspension's sense.</summary>
        public float SpeedMs;
        /// <summary>Wheel rays touching the ground this tick.</summary>
        public byte WheelHits;
        /// <summary>Bit 0: resting on another tank. Bit 1: blue team-killer status. Bit 2: arty (lob) aim.</summary>
        public byte Flags;
        /// <summary>The host's smoothed aim point (<c>aim_target_lerp</c>) a guided rocket homes on.</summary>
        public Vector3 Aim;
        /// <summary>The host's live dispersion of this gun.</summary>
        public float Dispersion;
    }

    /// <summary>One guided rocket in flight on the host.</summary>
    public struct NetRocketState : INetworkStruct
    {
        /// <summary>The host's shot id; 0 marks an empty entry.</summary>
        public int Id;
        public Vector3 Position;
        public Vector3 Velocity;
    }

    /// <summary>Replicated Uprising state of one slot; mirrors <c>UprisingSnapshot</c>.</summary>
    public struct NetUprisingState : INetworkStruct
    {
        public ulong Owned;
        public ulong Pending;
        public int Elixir;
        public int LifeSerial;
        public float RespawnIn, ActiveIn, ReadyIn, CloakIn;
        public float CurseIn, MarkIn, SplashIn, ChainIn;
        public byte Offer0, Offer1, Offer2, Flags, RespawnsLeft;
        /// <summary>1 once the host wrote this slot.</summary>
        public byte Valid;

        public static NetUprisingState From(BlitzLegacy.Core.Battle.UprisingSnapshot s)
        {
            return new NetUprisingState
            {
                Owned = s.Owned, Pending = s.Pending, Elixir = s.Elixir, LifeSerial = s.LifeSerial,
                RespawnIn = s.RespawnIn, ActiveIn = s.ActiveIn, ReadyIn = s.ReadyIn, CloakIn = s.CloakIn,
                CurseIn = s.CurseIn, MarkIn = s.MarkIn, SplashIn = s.SplashIn, ChainIn = s.ChainIn,
                Offer0 = s.Offer0, Offer1 = s.Offer1, Offer2 = s.Offer2, Flags = s.Flags,
                RespawnsLeft = s.RespawnsLeft, Valid = 1,
            };
        }

        public BlitzLegacy.Core.Battle.UprisingSnapshot ToSnapshot()
        {
            return new BlitzLegacy.Core.Battle.UprisingSnapshot
            {
                Owned = Owned, Pending = Pending, Elixir = Elixir, LifeSerial = LifeSerial,
                RespawnIn = RespawnIn, ActiveIn = ActiveIn, ReadyIn = ReadyIn, CloakIn = CloakIn,
                CurseIn = CurseIn, MarkIn = MarkIn, SplashIn = SplashIn, ChainIn = ChainIn,
                Offer0 = Offer0, Offer1 = Offer1, Offer2 = Offer2, Flags = Flags, RespawnsLeft = RespawnsLeft,
            };
        }
    }

    /// <summary>Big Boss role state of one slot (see <c>BigBossSnapshot</c>).</summary>
    public struct NetBigBossState : INetworkStruct
    {
        public int LifeSerial;
        public float Points, RespawnIn, Ready0, Ready1, Ready2;
        public float ShieldIn, ShieldDuration, PoisonCastIn, AuraIn, PoisonedIn, PoisonedTotal;
        public byte RoleId, Flags;
        public sbyte NextRoleId, Lives;
        /// <summary>1 once the host wrote this slot.</summary>
        public byte Valid;

        public static NetBigBossState From(BlitzLegacy.Core.Battle.BigBossSnapshot s)
        {
            return new NetBigBossState
            {
                LifeSerial = s.LifeSerial, Points = s.Points, RespawnIn = s.RespawnIn,
                Ready0 = s.Ready0, Ready1 = s.Ready1, Ready2 = s.Ready2,
                ShieldIn = s.ShieldIn, ShieldDuration = s.ShieldDuration, PoisonCastIn = s.PoisonCastIn, AuraIn = s.AuraIn,
                PoisonedIn = s.PoisonedIn, PoisonedTotal = s.PoisonedTotal,
                RoleId = s.RoleId, Flags = s.Flags, NextRoleId = s.NextRoleId, Lives = s.Lives, Valid = 1,
            };
        }

        public BlitzLegacy.Core.Battle.BigBossSnapshot ToSnapshot()
        {
            return new BlitzLegacy.Core.Battle.BigBossSnapshot
            {
                LifeSerial = LifeSerial, Points = Points, RespawnIn = RespawnIn,
                Ready0 = Ready0, Ready1 = Ready1, Ready2 = Ready2,
                ShieldIn = ShieldIn, ShieldDuration = ShieldDuration, PoisonCastIn = PoisonCastIn, AuraIn = AuraIn,
                PoisonedIn = PoisonedIn, PoisonedTotal = PoisonedTotal,
                RoleId = RoleId, Flags = Flags, NextRoleId = NextRoleId, Lives = Lives,
            };
        }
    }

    /// <summary>Reliable-data channels between host and clients.</summary>
    public static class NetChannel
    {
        /// <summary>client -> host: <c>BattleNetHello</c>.</summary>
        public const int Hello = 1;
        /// <summary>host -> client: <c>BattleNetPlan</c>.</summary>
        public const int Plan = 2;
        /// <summary>client -> host: the map is built and the intro is up.</summary>
        public const int Loaded = 3;
        /// <summary>host -> client: you are removed from the battle for team damage.</summary>
        public const int TeamKillKick = 4;
        /// <summary>host -> client: the host leaves the running battle, hand it over now.</summary>
        public const int HostLeaving = 5;

        /// <summary>
        /// Host -> client: the host hands the running battle to the player
        /// whose PlayerId is in the payload (int32) and becomes a client.
        /// </summary>
        public const int HostHandover = 6;
    }
}
