using System;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Telemetry;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle.Ai
{
    /// <summary>
    /// Two complete ATGM bot styles: a stationary overhead launcher that keeps
    /// steering a lofted rocket, and a mobile close-range flanker.
    ///
    /// The overhead launcher walks the rocket's flight before it commits a
    /// shot. A path the map blocks is never fired: the launcher slides aside
    /// to a new spot, and after a few blocked tries drops to the flanker's
    /// direct fire for a while. A rocket that still dies on the way ends its
    /// guidance at once instead of steering a wreck for its whole lifetime.
    /// </summary>
    internal sealed class AtgmAiController : IDisposable
    {
        /// <summary>A walked path is trusted this long for the same aim.</summary>
        public const float PathCheckInterval = 0.5f;

        /// <summary>Blocked tries before the launcher switches to direct fire.</summary>
        public const int BlockedShotsBeforeAssault = 3;

        public const float AssaultFallbackSeconds = 15f;
        public const float RelocateSeconds = 6f;

        /// <summary>A rocket that dies further than this from its aim was blocked.</summary>
        public const float BlockedImpactDistance = 15f;

        private readonly AiTankController _ai;
        private readonly BattleTank _tank;
        private BattleTank _guidedTarget;
        private Vector3 _lastKnownAim;
        private Vector3 _shotOffset;
        private Vector3 _moveGoal;
        private float _fireAt = -1f;
        private float _guideUntil = -1f;
        private float _moveAt = -1f;
        private float _orbitSign = 1f;
        private AtgmAiMode _shotMode;
        private float _pathCheckedAt = -1f;
        private Vector3 _pathCheckedAim;
        private bool _pathClear = true;
        private int _blockedShots;
        private float _assaultUntil = -1f;
        private Vector3 _relocateGoal;
        private float _relocateUntil = -1f;
        private float _relocateSide = 1f;
        private BattleProjectile _rocket;
        private bool _rocketSearched;
        private Vector3 _rocketLastPosition;
        private Vector3 _rocketAim;
        private bool _rocketDeliberateMiss;
        private AtgmAiMode _rocketMode;
        private float _firedAt = -1f;

        public AtgmAiMode Mode { get; private set; }
        public bool Guiding { get { return Time.time < _guideUntil; } }

        /// <summary>True while blocked lobs have pushed the launcher onto direct fire.</summary>
        public bool AssaultFallback { get { return Time.time < _assaultUntil; } }

        /// <summary>True while the launcher drives to a new firing spot.</summary>
        public bool Relocating { get { return Time.time < _relocateUntil; } }

        public static AtgmAiController Create(AiTankController ai, BattleTank tank)
        {
            if (ai == null || tank == null || tank.GunInfo == null) return null;
            var found = false;
            for (var i = 0; i < tank.GunInfo.Shells.Count; i++)
                if (tank.GunInfo.Shells[i].Atgm) { found = true; break; }
            return found ? new AtgmAiController(ai, tank) : null;
        }

        private AtgmAiController(AiTankController ai, BattleTank tank)
        {
            _ai = ai;
            _tank = tank;
            _tank.ProjectileFired += OnProjectileFired;
        }

        public void StartLife()
        {
            Mode = AtgmAiTactics.ChooseMode(UnityEngine.Random.value);
            _orbitSign = UnityEngine.Random.value < .5f ? -1f : 1f;
            _guidedTarget = null;
            _lastKnownAim = _shotOffset = _moveGoal = Vector3.zero;
            _fireAt = _guideUntil = _moveAt = -1f;
            _shotMode = Mode;
            _pathCheckedAt = _assaultUntil = _relocateUntil = _firedAt = -1f;
            _pathClear = true;
            _blockedShots = 0;
            _rocket = null;
            _rocketSearched = false;
            if (_tank.Turret != null) _tank.Turret.LobAim = false;
        }

        public void Stop()
        {
            _fireAt = _guideUntil = -1f;
            _guidedTarget = null;
            _shotOffset = Vector3.zero;
            _relocateUntil = _firedAt = -1f;
            _rocket = null;
            if (_tank != null && _tank.Turret != null) _tank.Turret.LobAim = false;
        }

        /// <summary>The mode the launcher plays now: blocked lobs force direct fire.</summary>
        private AtgmAiMode Preferred(float now)
        {
            return now < _assaultUntil ? AtgmAiMode.Assault : Mode;
        }

        public bool TickCombat(float delta)
        {
            if (Mode == AtgmAiMode.None || _tank == null || _tank.Dead || _tank.GunInfo == null)
                return false;

            var now = Time.time;
            TrackRocket(now);
            if (_guideUntil >= 0f && now >= _guideUntil)
            {
                _guideUntil = -1f;
                _guidedTarget = null;
                _shotOffset = Vector3.zero;
            }
            var current = _ai.TankIsValid(_ai.TargetTank) ? _ai.TargetTank : null;
            var guiding = now < _guideUntil;
            var target = guiding && _guidedTarget != null ? _guidedTarget : current;
            var known = target != null && !target.Dead && target.Spotted;
            var shellIndex = MissileShell();
            if (shellIndex < 0)
            {
                Stop();
                return true;
            }
            _tank.SelectShell(shellIndex);
            var shell = _tank.GunInfo.Shells[shellIndex];

            if (known)
            {
                var distance = Vector3.Distance(_tank.transform.position, target.transform.position);
                _lastKnownAim = target.AutoAimPoint()
                    + target.LinearVelocity * AtgmAiTactics.LeadSeconds(distance, Mathf.Abs(shell.Speed));
            }
            else if (!guiding)
            {
                CancelPendingShot();
                if (_tank.Turret != null) _tank.Turret.LobAim = false;
                return true;
            }

            var range = Vector3.Distance(_tank.transform.position, _lastKnownAim);
            var effective = _fireAt >= 0f || guiding
                ? _shotMode
                : AtgmAiTactics.EffectiveMode(Preferred(now), range);
            if (_tank.Turret != null) _tank.Turret.LobAim = effective == AtgmAiMode.Artillery;

            var aim = _lastKnownAim + _shotOffset;
            _tank.SetAimTarget(aim, false, false);
            if (guiding)
            {
                _rocketAim = aim;
                return true;
            }

            if (current == null || current != target || !current.Spotted
                || !AtgmAiTactics.CanFire(effective, range))
            {
                CancelPendingShot();
                return true;
            }
            if (effective == AtgmAiMode.Assault
                && !_ai.CheckPosToPosVisibility(_tank.GunShootPoint(), aim))
            {
                CancelPendingShot();
                return true;
            }

            if (_fireAt < 0f)
            {
                if (!_tank.CanShoot() || !Aligned(aim, shell, effective)) return true;
                if (effective == AtgmAiMode.Artillery && !ArtilleryPathClear(aim, shell, now)) return true;
                _guidedTarget = current;
                _shotMode = effective;
                _shotOffset = ShotOffset();
                _fireAt = now + Mathf.Max(.18f, _ai.ReactionTime() * .55f);
                return true;
            }

            if (now < _fireAt) return true;
            if (!_tank.CanShoot() || !current.Spotted)
            {
                CancelPendingShot();
                return true;
            }

            _tank.Gun.ShootAttempt();
            _fireAt = -1f;
            return true;
        }

        public void ApplyMovement()
        {
            if (Mode == AtgmAiMode.None || _tank == null || _tank.Dead) return;
            if (Time.time < _relocateUntil)
            {
                var gap = _relocateGoal - _tank.transform.position;
                gap.y = 0f;
                if (gap.sqrMagnitude > 5f * 5f)
                {
                    _ai.NoteDriveIntent(false);
                    _ai.DriveToPos(_relocateGoal, true, true);
                    return;
                }

                // Arrived: walk the flight again from the new spot.
                _relocateUntil = -1f;
                _pathCheckedAt = -1f;
            }

            var target = _ai.TankIsValid(_ai.TargetTank) ? _ai.TargetTank : null;
            if (target == null) return;

            var delta = target.transform.position - _tank.transform.position;
            delta.y = 0f;
            var distance = delta.magnitude;
            if (distance <= .01f) return;
            var direction = delta / distance;
            var mode = AtgmAiTactics.EffectiveMode(Preferred(Time.time), distance);
            switch (AtgmAiTactics.Movement(mode, distance))
            {
                case AtgmMovement.Hold:
                    _ai.Drive(0f, 0f, true);
                    break;
                case AtgmMovement.Retreat:
                    _moveGoal = _tank.transform.position - direction * 65f;
                    _ai.DriveToPos(_moveGoal, true, true);
                    break;
                case AtgmMovement.Approach:
                    var standoff = mode == AtgmAiMode.Artillery ? 300f : 70f;
                    _moveGoal = target.transform.position - direction * standoff;
                    _ai.DriveToPos(_moveGoal, true, false, false, false, true);
                    break;
                case AtgmMovement.Circle:
                    if (Time.time >= _moveAt || (_moveGoal - _tank.transform.position).sqrMagnitude < 12f * 12f)
                    {
                        var side = Vector3.Cross(Vector3.up, direction) * _orbitSign;
                        _moveGoal = target.transform.position + side * 52f - direction * 12f;
                        _moveAt = Time.time + UnityEngine.Random.Range(1.6f, 2.8f);
                        if (UnityEngine.Random.value < .2f) _orbitSign = -_orbitSign;
                    }
                    _ai.DriveToPos(_moveGoal, true, true);
                    break;
            }
        }

        private int MissileShell()
        {
            for (var i = 0; i < _tank.GunInfo.Shells.Count; i++)
                if (_tank.GunInfo.Shells[i].Atgm && _tank.CanLaunchShell(i)) return i;
            return -1;
        }

        private bool Aligned(Vector3 aim, ShellInfo shell, AtgmAiMode mode)
        {
            if (_tank.Turret == null || _tank.Turret.GunPivot == null) return false;
            if (mode != AtgmAiMode.Artillery)
                return _ai.IsTankLookingAtPos(_tank, aim, .985f);

            var to = aim - _tank.Turret.GunPosition;
            var flatTo = Vector3.ProjectOnPlane(to, Vector3.up);
            var flatGun = Vector3.ProjectOnPlane(_tank.Turret.GunPivot.forward, Vector3.up);
            if (flatTo.sqrMagnitude <= .001f || flatGun.sqrMagnitude <= .001f
                || Vector3.Dot(flatTo.normalized, flatGun.normalized) < .99f) return false;
            var expected = -ArtyMath.GuidedLaunchPitch(flatTo.magnitude,
                _tank.GunInfo.DeviationUp, Mathf.Abs(shell.Speed), shell.TurnSpeed, to.y);
            return Mathf.Abs(Mathf.DeltaAngle(_tank.Turret.Pitch * Mathf.Rad2Deg, expected)) <= 4f;
        }

        /// <summary>
        /// Walk the lofted flight from the barrel as it stands. The walk is
        /// cached for half a second per aim point, so a launcher waiting on a
        /// blocked shot does not re-walk it every physics frame.
        /// </summary>
        private bool ArtilleryPathClear(Vector3 aim, ShellInfo shell, float now)
        {
            if (_pathCheckedAt >= 0f && now - _pathCheckedAt < PathCheckInterval
                && (aim - _pathCheckedAim).sqrMagnitude < 4f)
            {
                return _pathClear;
            }

            if (_tank.Turret == null || _tank.Turret.GunPivot == null)
            {
                return true;
            }

            _pathCheckedAt = now;
            _pathCheckedAim = aim;
            var pivot = _tank.Turret.GunPivot;
            var muzzle = _tank.Turret.Muzzle != null ? _tank.Turret.Muzzle.position : pivot.position;
            var speed = Mathf.Abs(shell.Speed);
            var range = Vector3.Distance(muzzle, aim);
            var seconds = Mathf.Min(
                GunMath.ShellLifetime(shell.MaxDistance, shell.Speed),
                (range / Mathf.Max(1f, speed) * 2.2f) + 1f);
            Vector3 hit;
            using (BattlePerfMarkers.AiSight.Auto())
            {
                _pathClear = !AtgmFlightCheck.Obstructed(
                    muzzle, pivot.forward * speed, aim, shell.TurnSpeed, true,
                    AimMath.ShellGravity(shell.GravityMultiplier, true),
                    seconds, Time.fixedDeltaTime, out hit);
            }

            if (_pathClear)
            {
                _blockedShots = 0;
            }
            else if (now >= _relocateUntil)
            {
                OnShotBlocked(hit, aim, now);
            }

            return _pathClear;
        }

        /// <summary>
        /// The map is in the way: slide aside from it - backing off a little
        /// when the wall is right in front - and after a few tries give the
        /// lob up for direct fire.
        /// </summary>
        private void OnShotBlocked(Vector3 hit, Vector3 aim, float now)
        {
            _blockedShots++;
            if (_blockedShots >= BlockedShotsBeforeAssault)
            {
                _blockedShots = 0;
                _assaultUntil = now + AssaultFallbackSeconds;
                _relocateUntil = -1f;
                return;
            }

            // A parked launcher cannot slide aside: the count alone moves it
            // to direct fire within a second or so.
            if (!_ai.IsMobile()) return;

            var me = _tank.transform.position;
            var toAim = aim - me;
            toAim.y = 0f;
            if (toAim.sqrMagnitude <= .01f) return;
            var direction = toAim.normalized;
            var close = hit != Vector3.zero && (hit - me).sqrMagnitude < 40f * 40f;
            var sideways = close ? 22f : 35f;
            var back = close ? 12f : 0f;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var side = Vector3.Cross(Vector3.up, direction) * _relocateSide;
                _relocateSide = -_relocateSide;
                var goal = _ai.NavigationGoal(me + side * sideways - direction * back);
                if (goal == Vector3.zero) continue;
                _relocateGoal = goal;
                _relocateUntil = now + RelocateSeconds;
                return;
            }
        }

        /// <summary>
        /// Follow the rocket in flight; when it dies early, stop guiding, and
        /// when it died far from its aim on a lob, count the shot as blocked.
        /// </summary>
        private void TrackRocket(float now)
        {
            if (_firedAt < 0f) return;
            if (!_rocketSearched || ReferenceEquals(_rocket, null))
            {
                _rocket = FindOwnRocket();
                _rocketSearched = _rocket != null || now - _firedAt > .5f;
                if (ReferenceEquals(_rocket, null))
                {
                    if (_rocketSearched) _firedAt = -1f;
                    return;
                }
            }

            if (_rocket != null && _rocket.Alive)
            {
                _rocketLastPosition = _rocket.transform.position;
                return;
            }

            _rocket = null;
            _firedAt = -1f;
            var early = _guideUntil >= 0f && now < _guideUntil - .3f;
            if (!early) return;
            _guideUntil = -1f;
            _guidedTarget = null;
            _shotOffset = Vector3.zero;
            var far = (_rocketLastPosition - _rocketAim).sqrMagnitude
                > BlockedImpactDistance * BlockedImpactDistance;
            if (far && !_rocketDeliberateMiss && _rocketMode == AtgmAiMode.Artillery)
            {
                _pathCheckedAt = -1f;
                OnShotBlocked(_rocketLastPosition, _rocketAim, now);
            }
        }

        private BattleProjectile FindOwnRocket()
        {
            var live = BattleProjectile.LiveGuided;
            for (var i = live.Count - 1; i >= 0; i--)
            {
                var rocket = live[i];
                if (rocket != null && rocket.Alive && rocket.Owner == _tank) return rocket;
            }

            return null;
        }

        private Vector3 ShotOffset()
        {
            if (!AtgmAiTactics.ShouldDeliberatelyMiss(UnityEngine.Random.value)) return Vector3.zero;
            var angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            var radius = AtgmAiTactics.MissRadius(UnityEngine.Random.value);
            return new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
        }

        private void CancelPendingShot()
        {
            if (_fireAt < 0f) return;
            _fireAt = -1f;
            _guidedTarget = null;
            _shotOffset = Vector3.zero;
        }

        private void OnProjectileFired(bool first, int shellId)
        {
            if (_tank.GunInfo == null || shellId < 0 || shellId >= _tank.GunInfo.Shells.Count) return;
            var shell = _tank.GunInfo.Shells[shellId];
            if (!shell.Atgm) return;
            _guideUntil = Time.time + GunMath.ShellLifetime(shell.MaxDistance, shell.Speed) + .25f;
            _firedAt = Time.time;
            _rocket = null;
            _rocketSearched = false;
            _rocketAim = _lastKnownAim + _shotOffset;
            _rocketLastPosition = _tank.transform.position;
            _rocketDeliberateMiss = _shotOffset != Vector3.zero;
            _rocketMode = _shotMode;
        }

        public void Dispose()
        {
            if (_tank != null) _tank.ProjectileFired -= OnProjectileFired;
            Stop();
        }
    }
}
