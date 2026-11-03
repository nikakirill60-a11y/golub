using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Tanks;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Port of <c>assets/tanks/base/gun_gomponent_burst.gd</c>: the magazine
    /// clock of every BURST and AUTOLOAD gun. One <c>shoot_attempt</c> pulls a
    /// whole burst out of the clip, the shells inside it leave on absolute
    /// deadlines, and the main reload only starts behind the last of them.
    ///
    /// The source hangs <c>ui_gun_burst.tscn</c> off this component; here the
    /// HUD owns its canvas, so the magazine only raises
    /// <see cref="BurstsChanged"/> and <see cref="BattleGunBurstUi"/> listens.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleGunBurst : BattleGun
    {
        /// <summary><c>bursts</c>: the rounds still in the clip.</summary>
        public readonly List<int> Bursts = new List<int>();

        /// <summary><c>bursting</c>.</summary>
        public bool Bursting { get; private set; }

        /// <summary><c>ui_update()</c>, which the HUD indicator follows.</summary>
        public event Action<int> BurstsChanged;

        // Obscured (anti memory-editing): the deadlines of the magazine clock.
        private BlitzLegacy.Core.Security.ObscuredFloat _burstStartedSecure;
        private BlitzLegacy.Core.Security.ObscuredFloat _burstIntraburstSecure;
        private BlitzLegacy.Core.Security.ObscuredFloat _autoDeadlineSecure = float.PositiveInfinity;
        private float _burstStarted { get { return _burstStartedSecure.Get("gun.burst_started"); } set { _burstStartedSecure = value; } }
        private float _burstIntraburst { get { return _burstIntraburstSecure.Get("gun.intraburst_live"); } set { _burstIntraburstSecure = value; } }
        private float _autoDeadline { get { return _autoDeadlineSecure.Get("gun.autoreload_deadline"); } set { _autoDeadlineSecure = value; } }
        private int _burstShells;
        private int _burstEmitted;
        private bool _mainReloadPending;
        private bool AutoReload { get { return Info != null && Info.AutoReload; } }
        public float NextShellSeconds
        {
            get { return AutoReload && Bursts.Count < LoadableMagazineSize && !float.IsPositiveInfinity(_autoDeadline)
                ? Mathf.Max(0f, _autoDeadline - Ct()) : 0f; }
        }

        private float AutoSeconds(int loaded)
        {
            var seconds = Info.AutoReloadTimes[Mathf.Clamp(loaded, 0, Info.AutoReloadTimes.Count - 1)];
            return seconds * (Tank != null ? Tank.ReloadPenalty * Tank.AdrenalineReloadMultiplier : 1f);
        }

        private BurstInfo BurstInfo
        {
            get { return Info != null ? Info.Burst : null; }
        }

        /// <summary>The magazine the gun is loaded with when full.</summary>
        public int MagazineSize
        {
            get { return BurstInfo != null ? BurstInfo.Bursts.Count : 0; }
        }

        private int LoadableMagazineSize
        {
            get { return Tank != null ? Mathf.Min(MagazineSize, Tank.AvailableShellCount) : MagazineSize; }
        }

        /// <summary><c>can_shoot()</c>: a live burst blocks the trigger.</summary>
        public override bool CanShoot()
        {
            return base.CanShoot() && !Bursting && Bursts.Count > 0;
        }

        /// <summary><c>setup</c>.</summary>
        public override void Setup(GunInfo info, BattleTank tank)
        {
            base.Setup(info, tank);
            ResetBursts();

            if (tank != null && tank.Health != null)
            {
                // on_death hides the indicator and drops the live burst.
                tank.Health.OnDeath += OnDeath;
            }
        }

        private void OnDeath()
        {
            Bursting = false;
        }

        private float ClipInterval
        {
            get { return (BurstInfo != null ? BurstInfo.IntraClip : 0f)
                * (Tank != null ? Tank.ReloadPenalty : 1f); }
        }

        /// <summary><c>get_intraclip()</c>.</summary>
        public float IntraClip()
        {
            if (BurstInfo == null) { return 0f; }

            return Bursting ? BurstInfo.IntraClip : BurstInfo.IntraBurst;
        }

        /// <summary><c>get_reload_remaining()</c>.</summary>
        public override float ReloadRemaining()
        {
            if (Tank != null && Tank.AvailableShellCount == 0) { return 0f; }
            if (CanShoot()) { return ReloadTotal; }
            if (AutoReload)
            { return Bursts.Count == 0 ? NextShellSeconds : Mathf.Max(0f, NextShoot - Ct()); }
            if (Bursting)
            {
                // Keep the ring blocked throughout a live multi-shell burst.
                // After its first shell, display the actual next-shell interval.
                if (_burstEmitted == 0 || _burstIntraburst <= 0f) return ReloadTotal;
                return Mathf.Max(0f, _burstStarted + _burstEmitted * _burstIntraburst - Ct());
            }
            return Mathf.Max(0f, NextShoot - Ct());
        }

        /// <summary>
        /// Duration of the active reload phase: inter-shell, intra-clip or
        /// whole-magazine. Never infer a main reload from one round remaining.
        /// </summary>
        public override float ReloadTotal
        {
            get
            {
                if (AutoReload)
                { return Bursts.Count == 0 ? AutoSeconds(0) : ClipInterval; }
                if (Bursting && _burstIntraburst > 0f) return _burstIntraburst;
                // One round still loaded means an intra-clip cooldown, NOT a
                // whole-magazine reload. The old <=1 test prefilled the ring.
                return _mainReloadPending || Bursts.Count == 0 ? base.ReloadTotal : ClipInterval;
            }
        }

        /// <summary><c>get_reload_coeff()</c>.</summary>
        public override float ReloadCoefficient()
        {
            return GunMath.ReloadCoefficient(ReloadRemaining(), ReloadTotal);
        }

        /// <summary><c>_process</c>.</summary>
        public override void Tick()
        {
            base.Tick();
            if (AutoReload)
            {
                // Resolve absolute deadlines so a long frame does not lose a
                // shell or shorten the next loading interval. Death cancels it.
                if (Tank == null || Tank.Dead) { return; }
                while (Bursts.Count < LoadableMagazineSize && Ct() >= _autoDeadline)
                {
                    Bursts.Add(1);
                    UiUpdate();
                    _autoDeadline = Bursts.Count < LoadableMagazineSize
                        ? _autoDeadline + AutoSeconds(Bursts.Count)
                        : float.PositiveInfinity;
                }
                return;
            }
            AdvanceBurst();
        }

        /// <summary>
        /// <c>_advance_burst()</c>. A fresh timer is charged the whole delta of
        /// the frame that created it, so one long frame fired an entire burst
        /// early and compressed both its cadence and the reload measured from
        /// its last shell. These deadlines are absolute instead and resolve
        /// from the same clock as the main reload: a hitch can only delay a
        /// projectile, never shorten the interval in front of it.
        /// </summary>
        private void AdvanceBurst()
        {
            if (!Bursting || Tank == null) { return; }

            while (_burstEmitted < _burstShells
                && Ct() >= _burstStarted + _burstEmitted * _burstIntraburst)
            {
                EmitBurstShell();
            }

            // Finishing from the final projectile, not from a separate
            // deadline, keeps the main reload strictly behind the last shell.
            if (_burstEmitted >= _burstShells)
            {
                FinishBurst();
            }
        }

        private void EmitBurstShell()
        {
            var shellIndex = _burstEmitted;
            _burstEmitted += 1;

            // A suppressed projectile (battle over, dead crew) still consumes
            // its slot so the burst completes on schedule instead of hanging.
            // A scene without a roster - the smoke stands and the tests - is
            // the source's ongoing battle.
            if (BattleRoster.Current != null && !BattleRoster.Current.IsOngoing) { return; }
            if (Tank.Dead) { return; }

            RaiseShoot(shellIndex == 0 || _burstIntraburst > 0.25f);
        }

        private void FinishBurst()
        {
            Bursting = false;
            RefreshAmmunition();
            if (Tank == null || Tank.AvailableShellCount == 0) { return; }
            if (Bursts.Count == 0)
            {
                ForceMainReload(true);
            }
            else
            {
                // One-shell bursts are autoloader rounds. Every remaining round
                // must respect the magazine's intra-clip delay instead of
                // becoming ready on the next frame; multi-projectile bursts use
                // the same inter-burst gap.
                Reload(ClipInterval);
            }
        }

        /// <summary><c>shoot_attempt()</c>.</summary>
        public override void ShootAttempt()
        {
            if (Tank == null) { return; }
            if (Tank.Dead || !CanShoot()) { return; }
            if (WaterBlocksShot()) { return; }

            if (AutoReload)
            {
                Bursts.RemoveAt(0);
                LastShot = Ct();
                // Firing cancels the in-progress shell, not the loaded ones.
                _autoDeadline = Ct() + AutoSeconds(Bursts.Count);
                Reload(ClipInterval);
                UiUpdate();
                RaiseShoot(true);
                RefreshAmmunition();
                return;
            }

            if (Bursts.Count == 0)
            {
                ResetBursts();
                return;
            }

            Bursting = true;
            LastShot = Ct();

            _burstShells = Bursts[0];
            Bursts.RemoveAt(0);
            _burstEmitted = 0;
            _burstStarted = LastShot;
            _burstIntraburst = BurstInfo != null ? BurstInfo.IntraBurst : 0f;
            UiUpdate();
        }

        /// <summary>
        /// <c>force_main_reload()</c>, the <c>reload_burst</c> action: dump the
        /// rest of the clip and load a fresh one.
        /// </summary>
        public void ForceMainReload(bool force = false)
        {
            // Automatic magazines load missing rounds in the background; the
            // manual drum reload action must never discard loaded rounds.
            if (AutoReload) { return; }
            RefreshAmmunition();
            if (Tank == null || Tank.AvailableShellCount == 0) { return; }
            if (force || (Bursts.Count != MagazineSize && CanShoot()))
            {
                Bursts.Clear();
                UiUpdate();
                _mainReloadPending = true;
                Reload(base.ReloadTotal);
            }
        }

        public override void RefreshAmmunition()
        {
            base.RefreshAmmunition();
            if (Tank == null) { return; }
            if (Tank.AvailableShellCount == 0)
            {
                Bursting = false;
                _mainReloadPending = false;
                _autoDeadline = float.PositiveInfinity;
                if (Bursts.Count > 0) { Bursts.Clear(); UiUpdate(); }
                return;
            }
            if (AutoReload)
            {
                var capacity = LoadableMagazineSize;
                if (Bursts.Count > capacity)
                {
                    Bursts.RemoveRange(capacity, Bursts.Count - capacity);
                    UiUpdate();
                }
                if (Bursts.Count >= capacity) { _autoDeadline = float.PositiveInfinity; }
                else if (float.IsPositiveInfinity(_autoDeadline))
                    _autoDeadline = Ct() + AutoSeconds(Bursts.Count);
            }
        }

        public override void ScaleReloadTime(float factor)
        {
            if (factor <= 0f || float.IsNaN(factor) || float.IsInfinity(factor)) { return; }
            // Adrenaline accelerates loading, not the fixed intra-clip firing gap.
            if (_mainReloadPending) { base.ScaleReloadTime(factor); }
            var now = Ct();
            if (AutoReload && !float.IsPositiveInfinity(_autoDeadline) && _autoDeadline > now)
                _autoDeadline = now + (_autoDeadline - now) * factor;
        }

        public override void AdvanceReload(float seconds)
        {
            base.AdvanceReload(seconds);
            if (seconds <= 0f || float.IsNaN(seconds) || float.IsInfinity(seconds)) { return; }
            var now = Ct();
            if (AutoReload && !float.IsPositiveInfinity(_autoDeadline) && _autoDeadline > now)
                _autoDeadline = Mathf.Max(now, _autoDeadline - seconds);
        }

        /// <summary><c>_on_reload()</c>.</summary>
        protected override void OnReload()
        {
            if (!_mainReloadPending) { return; }

            _mainReloadPending = false;
            ResetBursts();
            UiUpdate();
        }

        /// <summary><c>ui_update()</c>.</summary>
        private void UiUpdate()
        {
            if (BurstsChanged != null) { BurstsChanged(Bursts.Count); }
        }

        /// <summary><c>reset_bursts()</c>.</summary>
        public void ResetBursts()
        {
            if (Tank == null) { return; }

            Bursting = false;
            _mainReloadPending = false;
            _autoDeadline = float.PositiveInfinity;
            Bursts.Clear();
            var available = Tank.AvailableShellCount;
            if (BurstInfo == null) { return; }
            foreach (var shells in BurstInfo.Bursts)
            {
                if (available <= 0) { break; }
                var loaded = Mathf.Min(shells, available);
                Bursts.Add(loaded);
                available -= loaded;
            }
        }
    }
}
