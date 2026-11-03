using System;
using System.Collections.Generic;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// The only seam between the battle and the network layer. Runtime never
    /// references Fusion; the BlitzLegacy.Network assembly listens here.
    ///
    /// Security: on a client the host is the single authority over damage,
    /// deaths and the result, so <see cref="PuppetWorld"/> must be raised
    /// before the client builds its battle and dropped when it leaves.
    /// </summary>
    public static class BattleNetHooks
    {
        /// <summary>
        /// True on a network client: every tank is a puppet of the host's
        /// state, no damage is applied locally and no bot brain runs.
        /// </summary>
        public static bool PuppetWorld { get; set; }

        /// <summary>Round trip in ms while online, -1 offline. Set by the network layer.</summary>
        public static Func<int> PingMs;

        /// <summary>
        /// Online: true while the map intro must keep covering the screen
        /// because other players are still loading. Set by the network layer.
        /// </summary>
        public static Func<bool> HoldIntro;

        /// <summary>Online: the host released every intro at once; ends a running intro now.</summary>
        public static Func<bool> IntroReleased;

        /// <summary>
        /// Raised by the simulating peer after a real shell left a barrel, with
        /// the exact origin, muzzle and direction the projectile was built from,
        /// and the projectile's <see cref="BattleProjectile.ShotId"/>.
        /// </summary>
        public static event Action<BattleTank, int, Vector3, Vector3, Vector3, int> ShellLaunched;

        /// <summary>
        /// Raised by the simulating peer for every damage event a tank's health
        /// took (blocked hits included), so the network layer can replay the
        /// hit feedback (inside bounce / penetration sound) on the clients.
        /// </summary>
        public static event Action<BattleTank, BlitzLegacy.Core.Tanks.DamageInfo> DamageTaken;

        internal static void RaiseDamageTaken(BattleTank tank, BlitzLegacy.Core.Tanks.DamageInfo info)
        {
            var handler = DamageTaken;
            if (handler != null) { handler(tank, info); }
        }

        /// <summary>
        /// Client: the host's guided rockets in flight this frame, by the host's
        /// shot id, interpolated on the same clock as the tanks. A replayed
        /// rocket follows its entry, so it flies the host's path exactly.
        /// </summary>
        public static readonly Dictionary<int, RocketSample> HostRockets = new Dictionary<int, RocketSample>();

        public struct RocketSample
        {
            public Vector3 Position;
            public Vector3 Velocity;
        }

        /// <summary>
        /// Client request for a host-authoritative mode action: 1 Uprising active, 2..4 offer,
        /// 5 Gravity thruster, 6 Big Boss role ability, 7..9 Big Boss support equipment
        /// (see <see cref="BattleRemoteDriver"/>).
        /// </summary>
        public static event Action<byte> UprisingActionRequested;

        /// <summary>Host-only one-shot Uprising VFX that every proxy must replay.</summary>
        public static event Action<UprisingTank, string, Vector3> UprisingEffectRaised;
        public static event Action<UprisingTank, Vector3, Vector3> UprisingChainRaised;

        /// <summary>Host-only one-shot Big Boss effects (ability casts, heals, the shield
        /// hand-over) that every client replays on the same seat.</summary>
        public static event Action<BigBossTank, string, Vector3> BigBossEffectRaised;

        /// <summary>Host-only gravity trap placed by a seat: ability id and pull centre.</summary>
        public static event Action<BigBossTank, string, Vector3> BigBossTrapRaised;

        internal static void RaiseBigBossEffect(BigBossTank tank, string id, Vector3 point)
        {
            var handler = BigBossEffectRaised;
            if (handler != null) { handler(tank, id, point); }
        }

        internal static void RaiseBigBossTrap(BigBossTank tank, string id, Vector3 point)
        {
            var handler = BigBossTrapRaised;
            if (handler != null) { handler(tank, id, point); }
        }

        public static void RequestUprisingAction(byte action)
        {
            var handler = UprisingActionRequested;
            if (handler != null) { handler(action); }
        }

        internal static void RaiseUprisingEffect(UprisingTank tank, string id, Vector3 point)
        {
            var handler = UprisingEffectRaised;
            if (handler != null) { handler(tank, id, point); }
        }

        internal static void RaiseUprisingChain(UprisingTank tank, Vector3 from, Vector3 to)
        {
            var handler = UprisingChainRaised;
            if (handler != null) { handler(tank, from, to); }
        }

        internal static void RaiseShellLaunched(
            BattleTank tank, int shellId, Vector3 origin, Vector3 muzzle, Vector3 direction, int shotId)
        {
            var handler = ShellLaunched;
            if (handler != null) { handler(tank, shellId, origin, muzzle, direction, shotId); }
        }
    }

    /// <summary>
    /// Drives a tank on the host from a remote player's input, in place of the
    /// local <see cref="BattleControls"/> or an AI brain. The network layer
    /// writes the latest input; this applies it on every physics step.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleRemoteDriver : MonoBehaviour
    {
        [System.NonSerialized]
        public float Forward;
        [System.NonSerialized]
        public float Right;
        [System.NonSerialized]
        public Vector3 Aim;
        [System.NonSerialized]
        public bool HasAim;
        [System.NonSerialized]
        public bool Fire;
        [System.NonSerialized]
        public bool TurretBlock;
        /// <summary>The player is in the overhead ATGM view (lobbed rocket).</summary>
        [System.NonSerialized]
        public bool Arty;
        [System.NonSerialized]
        public int ShellId;
        /// <summary>One shot request that outlives a lost packet (see DirectInput.FireSequence).</summary>
        [System.NonSerialized]
        public bool FirePulse;

        /// <summary>Mode action code of the Gravity Mode thruster (1 = Uprising active, 2..4 = offers).</summary>
        public const byte JetJumpAction = 5;

        /// <summary>Mode action code of the Big Boss role ability (the cons_jump slot).</summary>
        public const byte BigBossAbilityAction = 6;

        /// <summary>First consumable code of every mode: 7..9 use consumable slots 0..2.</summary>
        public const byte ConsumableAction = 7;

        public const int ConsumableSlots = 3;

        /// <summary>Highest mode action code a client may send.</summary>
        public const byte MaxAction = ConsumableAction + ConsumableSlots - 1;

        private BattleTank _tank;
        private byte _uprisingSequence;
        private bool _uprisingPrimed;

        public static BattleRemoteDriver Attach(BattleTank tank)
        {
            if (tank == null) { return null; }
            var driver = tank.GetComponent<BattleRemoteDriver>();
            if (driver == null) { driver = tank.gameObject.AddComponent<BattleRemoteDriver>(); }
            driver._tank = tank;
            driver.ShellId = tank.ShellId;
            return driver;
        }

        /// <summary>Drops a stale input, e.g. when the player disconnects.</summary>
        public void Clear()
        {
            Forward = 0f;
            Right = 0f;
            Fire = false;
            FirePulse = false;
            TurretBlock = false;
            Arty = false;
        }

        public void ApplyUprisingAction(byte sequence, byte action)
        {
            // The first input only primes the counter: after a host migration the
            // rejoined client still carries its last request, which must not replay
            // (a repeated offer toggle would refund a purchase).
            if (!_uprisingPrimed) { _uprisingPrimed = true; _uprisingSequence = sequence; return; }
            if (sequence == 0 || sequence == _uprisingSequence || _tank == null) { return; }
            _uprisingSequence = sequence;
            if (action == JetJumpAction)
            {
                var gravity = _tank.GetComponent<GravityModeTank>();
                if (gravity != null && !_tank.Blocked) { gravity.UseJetJump(); }
                return;
            }
            if (action == BigBossAbilityAction)
            {
                if (_tank.BigBoss != null) { _tank.BigBoss.UseRoleAbility(); }
                return;
            }
            if (action >= ConsumableAction && action <= MaxAction)
            {
                // A consumable changes the simulated hull (repair, reload, engine), so the
                // host spends the remote crew's own loadout, behind the column's gate.
                if (BattleConsumableBar.CanUse(_tank)) { _tank.UseConsumable(action - ConsumableAction); }
                return;
            }
            var uprising = _tank.GetComponent<UprisingTank>();
            if (uprising == null || action == 0) { return; }
            if (action == 1) { uprising.UseActive(); return; }
            var offer = action - 2;
            if (offer < uprising.State.Offer.Count)
            {
                var id = uprising.State.Offer[offer];
                if (!string.IsNullOrEmpty(id)) { uprising.Toggle(id); }
            }
        }

        private void FixedUpdate()
        {
            if (_tank == null || _tank.Dead) { FirePulse = false; return; }
            var pulse = FirePulse;
            FirePulse = false;

            // Input from the wire is untrusted: clamp to the axes the local
            // controls can produce and ignore a non-finite aim point.
            var forward = Mathf.Clamp(Forward, -1f, 1f);
            var right = Mathf.Clamp(Right, -1f, 1f);
            _tank.Drive(forward, right);
            if (_tank.Blocked) { return; }

            _tank.TurretBlock = TurretBlock;
            if (ShellId != _tank.ShellId && ShellId >= 0 && ShellId < _tank.ShellAmounts.Count)
            {
                _tank.SelectShell(ShellId);
            }
            if (_tank.Turret != null)
            {
                // Same rule as the local camera: only a selected ATGM gets
                // the overhead view, which lifts the gun by the range and
                // lofts the rocket. Without it the host flew a client's arty
                // rocket flat and laid the barrel on the direct line.
                var shell = _tank.CurrentShell();
                _tank.Turret.LobAim = Arty && shell != null && shell.Atgm;
            }
            if (HasAim && IsFinite(Aim))
            {
                _tank.SetAimTarget(Aim);
            }
            if ((Fire || pulse) && _tank.Gun != null)
            {
                _tank.Gun.ShootAttempt();
            }
        }

        private static bool IsFinite(Vector3 v)
        {
            return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
                || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
        }
    }
}
