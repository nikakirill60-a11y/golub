using BlitzLegacy.Core.Sandbox;
using BlitzLegacy.Core.Tanks;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Per-tank Sandbox profile: base_tank.training_damage_mul, training_harmless
    /// and training_stationary, plus apply_training_overrides on the battle-local
    /// modules. Never touches the catalogue or the garage save.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SandboxTank : MonoBehaviour
    {
        public BattleTank Tank { get; private set; }
        public SandboxTankFactors Factors { get; private set; }
        public bool PlayerProfile { get; private set; }
        public bool Harmless { get; private set; }
        public bool Stationary { get; private set; }
        public float DamageMultiplier { get { return Factors.Damage; } }

        /// <summary>
        /// Applies the seat's overrides once. Call after Gravity mode configured
        /// the suspension and before Uprising captures its base mobility, the
        /// order apply_battle_mode runs them in.
        /// </summary>
        public static SandboxTank Apply(BattleTank tank, SandboxConfig config, SandboxMultipliers local,
            bool playerProfile, bool harmless, bool stationary)
        {
            if (tank == null || config == null) { return null; }
            if (tank.GetComponent<SandboxTank>() != null) { return tank.GetComponent<SandboxTank>(); }
            var profile = tank.gameObject.AddComponent<SandboxTank>();
            profile.Tank = tank;
            profile.PlayerProfile = playerProfile;
            profile.Harmless = !playerProfile && harmless;
            profile.Stationary = !playerProfile && stationary;
            profile.Factors = SandboxTankFactors.For(config, local);
            profile.Overrides();
            return profile;
        }

        private void Overrides()
        {
            var f = Factors;
            if (Tank.Health != null)
            {
                // The setter refills the pool, as set_max_health does.
                Tank.Health.MaxHealth = SandboxTuning.Health(Tank.Health.MaxHealth, f.Hp);
            }

            var gun = Tank.GunInfo;
            if (gun != null)
            {
                for (var i = 0; i < gun.Shells.Count; i++)
                {
                    var shell = gun.Shells[i];
                    if (shell == null) { continue; }
                    var copy = shell.Clone();
                    copy.Penetration = SandboxTuning.Penetration(shell.Penetration, f.Penetration);
                    gun.Shells[i] = copy;
                }

                // Main magazine reload and single-shot reload are independent controls.
                var magazine = UsesMagazine(gun);
                var reloadFactor = magazine ? f.MagazineReload : f.Reload;
                gun.Reload = SandboxTuning.Reload(gun.Reload, reloadFactor);
                if (gun.AutoReloadTimes.Count > 0)
                {
                    // 11.20 pump guns load round by round on these timers: they are
                    // that gun's reload, so the same slider drives them.
                    var timers = new float[gun.AutoReloadTimes.Count];
                    for (var i = 0; i < timers.Length; i++) { timers[i] = SandboxTuning.Reload(gun.AutoReloadTimes[i], reloadFactor); }
                    gun.AutoReloadTimes.Clear();
                    for (var i = 0; i < timers.Length; i++) { gun.AutoReloadTimes.Add(timers[i]); }
                }
                gun.AimTime = SandboxTuning.Timing(gun.AimTime, f.AimTime);
                if (magazine && gun.Burst != null)
                {
                    gun.Burst.IntraClip = SandboxTuning.Timing(gun.Burst.IntraClip, f.Intraclip);
                    gun.Burst.IntraBurst = SandboxTuning.Timing(gun.Burst.IntraBurst, f.Intraclip);
                }
            }

            var suspension = Tank.Suspension;
            if (suspension != null)
            {
                suspension.MaxSpeedForward = SandboxTuning.Mobility(suspension.MaxSpeedForward, f.Speed);
                suspension.MaxSpeedBack = SandboxTuning.Mobility(suspension.MaxSpeedBack, f.Speed);
                suspension.EnginePower = SandboxTuning.Mobility(suspension.EnginePower, f.Engine);
                // Not in Godot: the hull traverse, which the track animation follows.
                suspension.RotationSpeed = SandboxTuning.TurnRate(suspension.RotationSpeed, f.TurnRate);
                Tank.ScaleTrackTurn(f.TurnRate);
            }
        }

        /// <summary>ModuleGun.uses_magazine: BURST and AUTOLOAD.</summary>
        public static bool UsesMagazine(GunInfo gun)
        {
            return gun != null && (gun.FireMode == 1 || gun.FireMode == 2);
        }

        public static SandboxTank Of(BattleTank tank)
        {
            return tank != null && SandboxBattle.Active ? tank.GetComponent<SandboxTank>() : null;
        }

        public static bool IsHarmless(BattleTank tank) { var p = Of(tank); return p != null && p.Harmless; }

        public static bool IsStationary(BattleTank tank) { var p = Of(tank); return p != null && p.Stationary; }

        /// <summary>receive_damage: scale the hit record by the attacker's outgoing multiplier.</summary>
        public static void ScaleOutgoing(DamageInfo damage)
        {
            if (damage == null) { return; }
            var attacker = Of(damage.AttackerTank as BattleTank);
            if (attacker == null) { return; }
            var multiplier = attacker.DamageMultiplier;
            damage.Dmg = SandboxTuning.Damage(damage.Dmg, multiplier);
            if (damage.DmgBase > 0) { damage.DmgBase = SandboxTuning.Damage(damage.DmgBase, multiplier); }
        }
    }
}
