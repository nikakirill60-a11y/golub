using BlitzLegacy.Core.Tanks;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>Per-tank Gravity Mode controller: low gravity, air control and jet jump.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BattleTank))]
    [RequireComponent(typeof(Rigidbody))]
    public sealed class GravityModeTank : MonoBehaviour
    {
        public const float GravityMultiplier = 0.3f;
        public const float EnginePowerMultiplier = 1.4f;
        public const float SpeedMultiplier = 1.45f;
        public const float AirYawSpeed = 48f;
        public const float AirYawAccel = 6f;
        public const float AirPitchAccel = 1.1f;
        public const float AirPitchMax = 30f;
        public const float AirLevelAccel = 2.4f;
        public const float AirAngularDamp = 2.2f;
        public const float AirAngularMax = 0.9f;
        public const float RecoilMultiplier = 3f;
        public const float HitDeltaVReference = 4f;
        public const float HitCaliberExponent = 1.6f;
        public const float HitDeltaVMin = 2f;
        public const float HitRicochetMultiplier = 1.8f;
        public const float HitBounceMultiplier = 1.35f;
        public const float HitDeltaVMax = 12f;
        public const float HitStaggerPerDeltaV = 0.12f;
        public const float HitStaggerMin = 0.18f;
        public const int JetJumpCooldown = 20;
        public const float JetJumpSpeedReference = 50f;
        public const float JetJumpDeltaVUp = 9f;
        public const float JetJumpDeltaVForward = 6f;
        public const string JetJumpIconResource = "Gravity/jetJump";

        public enum HitType { Penetration, Bounce, Ricochet, Impact }

        private Rigidbody _body;
        private bool _configured;

        /// <summary>
        /// Rule figures of this battle: the shipped constants, or the Sandbox
        /// preset's (Global.gravity_mode_*_multiplier() in Training).
        /// </summary>
        public float GravityScale { get; private set; } = GravityMultiplier;
        public float SpeedScale { get; private set; } = SpeedMultiplier;
        public float ShotPowerMultiplier { get; private set; } = 1f;
        public float JumpPowerMultiplier { get; private set; } = 1f;

        public BattleTank Tank { get; private set; }
        public BattleSession Session { get; private set; }
        public ConsumableRuntime JetJump { get; private set; }

        public static GravityModeTank Attach(BattleTank tank, BattleSession session)
        {
            if (tank == null) return null;
            var existing = tank.GetComponent<GravityModeTank>();
            if (existing != null)
            {
                existing.Session = session;
                existing.Configure();
                existing.RegisterHud(tank);
                return existing;
            }

            var controller = tank.gameObject.AddComponent<GravityModeTank>();
            controller.Tank = tank;
            controller.Session = session;
            controller.Configure();
            controller.RegisterHud(tank);
            return controller;
        }

        /// <summary>The jet jump takes the HUD's mode slot.</summary>
        private void RegisterHud(BattleTank tank)
        {
            if (!(tank.HudMode is GravityHudMode mode && mode.Owner == this)) tank.HudMode = new GravityHudMode(this);
        }

        private void OnDestroy()
        {
            if (Tank != null && Tank.HudMode is GravityHudMode mode && mode.Owner == this) Tank.HudMode = null;
        }

        private void Awake()
        {
            if (Tank == null) Tank = GetComponent<BattleTank>();
            if (_body == null) _body = GetComponent<Rigidbody>();
        }

        private void Configure()
        {
            if (_configured || Tank == null || Tank.Suspension == null) return;
            if (_body == null) _body = GetComponent<Rigidbody>();

            var sandbox = SandboxBattle.Active ? SandboxBattle.Current : null;
            if (sandbox != null)
            {
                GravityScale = sandbox.GravityGravity;
                SpeedScale = sandbox.GravitySpeed;
                ShotPowerMultiplier = sandbox.GravityShotPower;
                JumpPowerMultiplier = sandbox.GravityJumpPower;
            }
            Tank.Suspension.EnginePower = ApplyMobilityMultiplier(Tank.Suspension.EnginePower, EnginePowerMultiplier);
            Tank.Suspension.MaxSpeedForward = ApplyMobilityMultiplier(Tank.Suspension.MaxSpeedForward, SpeedScale);
            Tank.Suspension.MaxSpeedBack = ApplyMobilityMultiplier(Tank.Suspension.MaxSpeedBack, SpeedScale);
            Tank.Suspension.AirControlEnabled = true;
            SetupJetJump();
            _configured = true;
        }

        public static float ApplyMobilityMultiplier(float value, float multiplier)
        {
            return (float)(int)(value * multiplier);
        }

        public bool CanUseJetJump()
        {
            return Tank != null && Tank.Card != null && Tank.Card.Class != TankClass.Heavy;
        }

        public void SetupJetJump()
        {
            if (JetJump != null || !CanUseJetJump()) return;
            JetJump = new ConsumableRuntime(new ConsumableInfo
            {
                Id = "jet_jump",
                NameKey = "consumables:jet_jump/name",
                DescriptionKey = "consumables:jet_jump/desc",
                IconRegion = "consumable_afterburning",
                Cooldown = JetJumpCooldown,
                UseTime = 0,
                Type = ConsumableType.Reusable,
                CantUseKey = string.Empty,
            });
            JetJump.Apply = DoJetJump;
            JetJump.Stop = () => { };
            JetJump.Message = Tank != null ? new System.Action<string>(Tank.SendPlayerMessage) : null;
        }

        public bool UseJetJump()
        {
            return JetJump != null && JetJump.UseTry(Time.time, false) == ConsumableUse.Used;
        }

        public bool JetJumpReady()
        {
            return JetJump != null && Tank != null && !Tank.Dead && !Tank.Blocked && JetJump.Ready(Time.time);
        }

        private bool DoJetJump()
        {
            if (Tank == null || Tank.Dead) return false;
            // Online client: the puppet has no physics of its own. The host
            // fires the thruster (it keeps the authoritative cooldown); the
            // local runtime only runs the button's cooldown for the HUD.
            if (BattleNetHooks.PuppetWorld)
            {
                if (Session == null || Tank != Session.Player) return false;
                BattleNetHooks.RequestUprisingAction(BattleRemoteDriver.JetJumpAction);
                return true;
            }
            if (_body == null || Tank.Suspension == null) return false;
            var impulse = JetJumpImpulse(
                Tank.Suspension.MaxSpeedForward,
                Tank.Suspension.Speed,
                Tank.LinearVelocity,
                _body.mass,
                JumpPowerMultiplier);
            if (impulse.sqrMagnitude <= 0f) return false;
            _body.AddForce(impulse, ForceMode.Impulse);
            return true;
        }

        public static Vector3 JetJumpImpulse(
            float topSpeedKmh,
            float speedKmh,
            Vector3 linearVelocity,
            float mass,
            float jumpPowerMultiplier = 1f)
        {
            if (mass <= 0f) return Vector3.zero;
            var top = Mathf.Max(topSpeedKmh, 1f);
            var lightness = Mathf.Clamp(top / JetJumpSpeedReference, 0.6f, 1.5f);
            var speedFraction = Mathf.Clamp(Mathf.Abs(speedKmh) / top, 0f, 1f);
            var dvUp = JetJumpDeltaVUp * lightness * jumpPowerMultiplier;
            var dvForward = JetJumpDeltaVForward * lightness * speedFraction * jumpPowerMultiplier;
            var flat = new Vector3(linearVelocity.x, 0f, linearVelocity.z);
            var direction = flat.magnitude > 1f ? flat.normalized : Vector3.zero;
            return (Vector3.up * dvUp + direction * dvForward) * mass;
        }

        public static float KnockbackDeltaV(float caliber, HitType type, float shotPowerMultiplier = 1f)
        {
            if (caliber <= 0f) return 0f;
            var multiplier = 1f;
            if (type == HitType.Ricochet) multiplier *= HitRicochetMultiplier;
            else if (type == HitType.Bounce) multiplier *= HitBounceMultiplier;
            var deltaV = HitDeltaVReference * shotPowerMultiplier * Mathf.Pow(caliber / 100f, HitCaliberExponent);
            return Mathf.Clamp(deltaV * multiplier, HitDeltaVMin, HitDeltaVMax);
        }

        public static Vector3 KnockbackDirection(Vector3 shellDirection, Vector3 normal, HitType type)
        {
            if ((type == HitType.Ricochet || type == HitType.Bounce) && normal.sqrMagnitude > 0f)
            {
                return (-normal).normalized;
            }
            return shellDirection.normalized;
        }

        public static void ApplyHitKnockback(
            BattleTank tank,
            Vector3 position,
            Vector3 normal,
            Vector3 shellDirection,
            float caliber,
            HitType type)
        {
            if (tank == null) return;
            var mode = tank.GetComponent<GravityModeTank>();
            if (mode == null) return;
            if (mode._body == null) mode._body = tank.GetComponent<Rigidbody>();
            if (mode._body == null) return;

            var push = KnockbackDirection(shellDirection, normal, type);
            if (push.sqrMagnitude <= 0f) return;
            var deltaV = KnockbackDeltaV(caliber, type, mode.ShotPowerMultiplier);
            if (deltaV <= 0f) return;

            mode._body.AddForceAtPosition(push * (deltaV * mode._body.mass), position, ForceMode.Impulse);
            if (tank.Suspension != null)
            {
                tank.Suspension.Stagger(Mathf.Max(deltaV * HitStaggerPerDeltaV, HitStaggerMin));
            }
        }

        private void FixedUpdate()
        {
            if (!_configured) Configure();
            if (_body == null) _body = GetComponent<Rigidbody>();
            if (_body != null && !_body.isKinematic)
            {
                _body.AddForce(
                    Vector3.up * (_body.mass * TankSuspension.SourceGravity * (1f - GravityScale)),
                    ForceMode.Force);
            }
            if (JetJump != null) JetJump.Tick(Time.time);
        }
    }
}
