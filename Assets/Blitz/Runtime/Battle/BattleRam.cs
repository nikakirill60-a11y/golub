using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Telemetry;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Port of <c>Global.request_ram_damage</c> / <c>_resolve_ram_damage</c>.
    /// Both physics notifications of one collision are folded into a single
    /// pair, the pair resolves 0.03 s later, and both sides are computed
    /// before either is applied so a lethal ram keeps its counter hit.
    /// </summary>
    public sealed class BattleRam : MonoBehaviour
    {
        public const string SpallLinerId = "spallliner";

        /// <summary>tank_physics_collision_hits.fsb of the 4.8 client.</summary>
        public const string SoundHeavy = "blitz/hits_collision/ram_heavy_01";
        public const string SoundLight = "blitz/hits_collision/ram_light_01";

        /// <summary>Combined mass, in kg, that picks the heavy sample.</summary>
        public const float HeavySoundMass = 60000f;
        public const float SoundVolume = 0.8f;

        private sealed class Pending
        {
            public BattleTank A;
            public BattleTank B;
            public float Speed;
            public float ResolveAt;
        }

        private readonly List<Pending> _pending = new List<Pending>();

        public static BattleRam Current { get; private set; }

        /// <summary>How many pairs are waiting for their 0.03 s window.</summary>
        public int PendingCount { get { return _pending.Count; } }

        public static BattleRam Create(Transform parent)
        {
            var go = new GameObject("BattleRam");
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }

            var ram = go.AddComponent<BattleRam>();
            Current = ram;
            return ram;
        }

        private void OnDestroy()
        {
            if (Current == this)
            {
                Current = null;
            }
        }

        /// <summary><c>_on_ram_body_entered</c> hands both notifications here.</summary>
        public static void Request(BattleTank mine, BattleTank other, float closingSpeed)
        {
            if (Current != null)
            {
                Current.Enqueue(mine, other, closingSpeed, Time.time);
            }
        }

        public void Enqueue(
            BattleTank mine, BattleTank other, float closingSpeed, float now)
        {
            if (mine == null || other == null || mine == other)
            {
                return;
            }

            if (mine.Dead || other.Dead || (mine.Team == other.Team && !FriendlyRamLands()))
            {
                return;
            }

            var speed = Mathf.Max(0f, closingSpeed) * RamMath.ClosingSpeedFactor;
            if (speed <= 0f || float.IsNaN(speed) || float.IsInfinity(speed))
            {
                return;
            }

            for (var i = 0; i < _pending.Count; i++)
            {
                var p = _pending[i];
                if ((p.A == mine && p.B == other) || (p.A == other && p.B == mine))
                {
                    p.Speed = Mathf.Max(p.Speed, speed);
                    return;
                }
            }

            _pending.Add(new Pending
            {
                A = mine,
                B = other,
                Speed = speed,
                ResolveAt = now + RamMath.ResolveDelay,
            });
        }

        private void Update()
        {
            Tick(Time.time);
        }

        public void Tick(float now)
        {
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                var p = _pending[i];
                if (now < p.ResolveAt)
                {
                    continue;
                }

                _pending.RemoveAt(i);
                Resolve(p.A, p.B, p.Speed);
            }
        }

        /// <summary>The source resolver, minus the uprising life serials.</summary>
        public void Resolve(BattleTank a, BattleTank b, float speed)
        {
            using var slowScope = BlitzPerfMonitor.Slow("ram.resolve");
            if (a == null || b == null || a.Dead || b.Dead)
            {
                return;
            }

            // Offline team-mates never hurt each other by ramming. Online a
            // ram still lands, cut tenfold in BattleTank.ReceiveDamage.
            if (a.Team == b.Team && !FriendlyRamLands())
            {
                return;
            }

            var roster = BattleRoster.Current;
            if (roster != null && !roster.IsOngoing)
            {
                return;
            }

            // Uprising Play Dead: the tank playing dead rams like a live one
            // but takes nothing back, the collision only drops the ability.
            // Two tanks playing dead both lose it and nobody is hurt.
            var aDead = UprisingTank.InThanatos(a);
            var bDead = UprisingTank.InThanatos(b);
            if (aDead && bDead)
            {
                UprisingTank.BreakThanatos(a);
                UprisingTank.BreakThanatos(b);
                PlaySound(a, b);
                return;
            }

            var toA = aDead ? 0 : DamageFor(a, b, speed);
            var toB = bDead ? 0 : DamageFor(b, a, speed);
            // Between team-mates only the rammer is charged with team damage:
            // the one it drove into must not be blamed for being hit.
            var friendly = a.Team == b.Team;
            var aRams = friendly && RamsHarder(a, b);
            Hit(a, b, toA, friendly && aRams);
            Hit(b, a, toB, friendly && !aRams);
            if (aDead) { UprisingTank.BreakThanatos(a); }
            if (bDead) { UprisingTank.BreakThanatos(b); }
            PlaySound(a, b);
        }

        /// <summary>True when <paramref name="a"/> drove into <paramref name="b"/> faster than the other way round.</summary>
        public static bool RamsHarder(BattleTank a, BattleTank b)
        {
            var axis = b.transform.position - a.transform.position;
            axis.y = 0f;
            if (axis.sqrMagnitude < .0001f) { return true; }
            axis.Normalize();
            var aSpeed = Mathf.Max(Vector3.Dot(a.LinearVelocity, axis), Vector3.Dot(a.PreviousVelocity, axis));
            var bSpeed = Mathf.Max(Vector3.Dot(b.LinearVelocity, -axis), Vector3.Dot(b.PreviousVelocity, -axis));
            return aSpeed >= bSpeed;
        }

        /// <summary>True in an online battle, where team-mates take a tenth of a ram.</summary>
        public static bool FriendlyRamLands()
        {
            // The Sandbox switch is authoritative in its own battles.
            if (SandboxBattle.Active) { return SandboxBattle.FriendlyFireOn; }
            var bootstrap = BlitzBootstrap.Current;
            return bootstrap != null && bootstrap.Battle != null && bootstrap.Battle.Net != null;
        }

        /// <summary>calculate_ram_damage for one side of the pair.</summary>
        public static int DamageFor(BattleTank target, BattleTank other, float speed)
        {
            var combined = (Mass(target) + Mass(other)) * RamMath.WeightCoefficient;
            if (combined <= 0f)
            {
                return 0;
            }

            var dmg = RamMath.Damage(
                combined, Mass(target) * RamMath.WeightCoefficient, speed,
                target.HasEquipment(SpallLinerId));
            return dmg <= 0f ? 0 : (int)dmg;
        }

        public static float Mass(BattleTank tank)
        {
            if (tank == null)
            {
                return 0f;
            }

            var body = tank.GetComponent<Rigidbody>();
            return body != null ? body.mass : 0f;
        }

        private static void Hit(BattleTank target, BattleTank attacker, int dmg, bool notTeamDamage = false)
        {
            if (dmg <= 0 || target == null)
            {
                return;
            }

            var info = new DamageInfo
            {
                Dmg = dmg,
                DmgBase = dmg,
                DmgType = DamageType.Collision,
                Attacker = attacker,
                AttackerTank = attacker,
                NotTeamDamage = notTeamDamage,
            };
            target.ReceiveDamage(info);
        }

        private static void PlaySound(BattleTank a, BattleTank b)
        {
            var bootstrap = BlitzBootstrap.Current;
            if (bootstrap == null || bootstrap.Audio == null)
            {
                return;
            }

            var mass = Mass(a) + Mass(b);
            var clip = mass >= HeavySoundMass ? SoundHeavy : SoundLight;
            var at = (a.transform.position + b.transform.position) * 0.5f;
            bootstrap.Audio.PlaySound3D(clip, at, SoundVolume);
        }
    }
}
