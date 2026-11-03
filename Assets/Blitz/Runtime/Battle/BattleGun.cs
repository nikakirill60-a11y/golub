using System;
using BlitzLegacy.Core.Tanks;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Port of <c>assets/tanks/base/gun_component.gd</c>: the reload clock that
    /// sits between a fire command and the shell leaving the barrel. The two
    /// Godot signals become plain C# events, and the hull subscribes to
    /// <see cref="Shoot"/> the way <c>base_tank.tscn</c> wires <c>shoot</c> to
    /// <c>shoot_shell</c>.
    ///
    /// The duplet ability and the carried shell counts the source also tests in
    /// <c>can_shoot</c> arrive with later slices; what is left is the clock plus
    /// the tank's own <c>can_launch_shell</c>. Magazine guns extend this class
    /// exactly as <c>GunComponentBurst</c> extends the script above.
    /// </summary>
    [DisallowMultipleComponent]
    public class BattleGun : MonoBehaviour
    {
        /// <summary>
        /// <c>Util.curtime()</c> behind <c>ct()</c>. EditMode tests cannot step
        /// the engine clock, so they replace this the way the source's tools
        /// scripts drive <c>next_shoot</c> by hand.
        /// </summary>
        public static Func<float> Clock;

        /// <summary><c>signal shoot(first_shot)</c>.</summary>
        public event Action<bool> Shoot;

        /// <summary><c>signal on_reload</c>.</summary>
        public event Action Reloaded;

        /// <summary><c>gun_info</c>.</summary>
        public GunInfo Info { get; private set; }

        /// <summary><c>tank</c>.</summary>
        public BattleTank Tank { get; private set; }

        /// <summary><c>last_shot</c>.</summary>
        public float LastShot { get; protected set; }

        /// <summary><c>next_shoot</c>.</summary>
        private BlitzLegacy.Core.Security.ObscuredFloat _nextShoot;
        public float NextShoot { get { return _nextShoot.Get("gun.next_shoot"); } private set { _nextShoot = value; } }

        private bool _reloadPending;

        /// <summary><c>ct()</c>.</summary>
        public float Ct()
        {
            return Clock != null ? Clock() : Time.time;
        }

        /// <summary>
        /// <c>get_reload_total()</c>, times the ammo rack penalty of
        /// `ammo_bay_damaged`.
        /// </summary>
        public virtual float ReloadTotal
        {
            get
            {
                var reload = Info != null ? Info.Reload : 0f;
                return Tank != null ? reload * Tank.ReloadPenalty * Tank.AdrenalineReloadMultiplier : reload;
            }
        }

        /// <summary><c>setup</c>; the source's UI base has no counterpart here.</summary>
        public virtual void Setup(GunInfo info, BattleTank tank)
        {
            Info = info;
            Tank = tank;
        }

        /// <summary><c>can_shoot()</c>.</summary>
        public virtual bool CanShoot()
        {
            if (Tank != null && (Tank.PendingDupletShots > 0 || !Tank.CanLaunchShell(Tank.ShellId)))
            {
                return false;
            }

            return GunMath.CanShoot(Ct(), NextShoot);
        }

        /// <summary>
        /// <c>get_reload_remaining()</c>. The source asks the full
        /// <c>can_shoot</c> here, so a gun that is loaded but cannot fire - a
        /// dead or blocked tank - reports a stale clock rather than a full
        /// reload, and the HUD ring follows.
        /// </summary>
        public virtual float ReloadRemaining()
        {
            if (Tank != null && Tank.AvailableShellCount == 0) { return 0f; }
            return CanShoot() ? ReloadTotal : Mathf.Max(0f, NextShoot - Ct());
        }

        /// <summary><c>get_reload_coeff()</c>.</summary>
        public virtual float ReloadCoefficient()
        {
            return GunMath.ReloadCoefficient(ReloadRemaining(), ReloadTotal);
        }

        /// <summary><c>just_shot()</c>.</summary>
        public bool JustShot()
        {
            return GunMath.JustShot(Ct(), LastShot);
        }

        /// <summary><c>shoot_attempt()</c>.</summary>
        public virtual void ShootAttempt()
        {
            if (Tank == null || Tank.Dead || !CanShoot())
            {
                return;
            }

            if (WaterBlocksShot()) { return; }

            RaiseShoot(true);
            LastShot = Ct();
            Reload(ReloadTotal);
        }

        /// <summary>
        /// Blitz 4.8.0 <c>cant_shoot_gun_under_water</c>: a loaded gun whose
        /// muzzle is under the water line keeps its shell.
        /// </summary>
        protected bool WaterBlocksShot()
        {
            return Tank != null && Tank.Water != null && Tank.Water.BlocksShot();
        }

        /// <summary><c>shoot.emit(first_shot)</c>.</summary>
        protected void RaiseShoot(bool firstShot)
        {
            if (Shoot != null) { Shoot(firstShot); }
        }

        /// <summary>Advance active loader work, including an autoreloader's separate clock.</summary>
        public virtual void AdvanceReload(float seconds)
        {
            if (seconds > 0f && !float.IsNaN(seconds) && !float.IsInfinity(seconds)
                && _reloadPending && NextShoot > Ct())
                NextShoot = Mathf.Max(Ct(), NextShoot - seconds);
        }

        /// <summary>Preserve completed work when the loader speed changes mid-reload.</summary>
        public virtual void ScaleReloadTime(float factor)
        {
            if (factor <= 0f || float.IsNaN(factor) || float.IsInfinity(factor)) { return; }
            var now = Ct();
            if (_reloadPending && NextShoot > now)
                NextShoot = now + (NextShoot - now) * factor;
        }

        protected void CancelReload()
        {
            _reloadPending = false;
            NextShoot = Ct();
        }

        /// <summary>Drop empty loading work without emitting a false reload-complete cue.</summary>
        public virtual void RefreshAmmunition()
        {
            if (Tank != null && !Tank.EnsureShellAvailable()) { CancelReload(); }
        }

        public void ShiftLastShot(float seconds)
        {
            LastShot += seconds;
        }

        /// <summary><c>reload(next_shoot)</c>.</summary>
        public void Reload(float seconds)
        {
            if (Tank != null && !Tank.EnsureShellAvailable())
            {
                CancelReload();
                return;
            }
            NextShoot = Ct() + seconds;
            _reloadPending = true;
        }

        /// <summary>
        /// <c>_process</c>: the source finishes a reload off this one clock
        /// rather than a timer, so a deadline that moves mid-reload still ends
        /// the reload once and at the right moment.
        /// </summary>
        public virtual void Tick()
        {
            RefreshAmmunition();
            if (!_reloadPending || Ct() < NextShoot)
            {
                return;
            }

            _reloadPending = false;
            OnReload();
            if (Reloaded != null) { Reloaded(); }
        }

        /// <summary><c>_on_reload()</c>, which the source runs before the signal.</summary>
        protected virtual void OnReload()
        {
        }

        private void Update()
        {
            Tick();
        }
    }
}
