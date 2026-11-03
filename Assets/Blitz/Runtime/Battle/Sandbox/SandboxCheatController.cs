using System.Collections.Generic;
using BlitzLegacy.Core.Sandbox;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// training_cheat_controller.gd: the battle-local aimbot, wallhack and
    /// triggerbot of the Sandbox cheat page. It is attached only to the local
    /// player's tank of an offline Sandbox battle and reads the battle's own
    /// preset copy, so nothing here can reach an account or an online room.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SandboxCheatController : MonoBehaviour
    {
        /// <summary>WALLHACK_GREEN: the target is in line of sight.</summary>
        public static readonly Color WallhackGreen = new Color(0.12f, 1f, 0.18f, 1f);

        /// <summary>WALLHACK_RED: the target is behind cover.</summary>
        public static readonly Color WallhackRed = new Color(1f, 0.025f, 0.015f, 1f);

        private BattleTank _tank;
        private SandboxBattle _sandbox;
        private BattleTank _aimTarget;
        private Vector3 _smoothedPoint;
        private readonly List<BattleTank> _wallhackTargets = new List<BattleTank>();
        private readonly List<BattleTank> _active = new List<BattleTank>();
        private float _nextWallhack;
        private BattleTank _triggerTarget;
        private float _triggerSince;

        /// <summary>The tank currently held by the aimbot, if any.</summary>
        public BattleTank AimTarget { get { return _aimTarget; } }

        /// <summary>Tanks currently traced by the wallhack.</summary>
        public IReadOnlyList<BattleTank> WallhackTargets { get { return _wallhackTargets; } }

        /// <summary><c>setup_training_cheats</c>: attaches to the player's tank of a Sandbox battle.</summary>
        public static SandboxCheatController Attach(BattleTank tank, SandboxBattle sandbox)
        {
            if (tank == null || sandbox == null) { return null; }
            var controller = tank.GetComponent<SandboxCheatController>();
            if (controller == null) { controller = tank.gameObject.AddComponent<SandboxCheatController>(); }
            controller._tank = tank;
            controller._sandbox = sandbox;
            return controller;
        }

        public static SandboxCheatController Of(BattleTank tank)
        {
            return tank == null ? null : tank.GetComponent<SandboxCheatController>();
        }

        /// <summary>cam_trace's <c>cheat_aiming</c>: the aimbot holds a target for this tank.</summary>
        public static bool Aiming(BattleTank tank)
        {
            var controller = Of(tank);
            return controller != null && controller.AimbotEnabled;
        }

        private SandboxConfig Config
        {
            get
            {
                // The controller only ever acts inside its own live Sandbox.
                if (_sandbox == null || SandboxBattle.Current != _sandbox) { return null; }
                return _sandbox.Config;
            }
        }

        /// <summary><c>aimbot_enabled</c>.</summary>
        public bool AimbotEnabled
        {
            get
            {
                var config = Config;
                return config != null && config.CheatAimbot && _aimTarget != null;
            }
        }

        private Vector3 ViewOrigin()
        {
            var camera = _tank != null && _tank.View != null ? _tank.View.Active : null;
            if (camera != null) { return camera.transform.position; }
            return GunPosition();
        }

        private Vector3 GunPosition()
        {
            var turret = _tank != null ? _tank.Turret : null;
            if (turret == null) { return _tank != null ? _tank.transform.position : Vector3.zero; }
            return turret.GunPosition;
        }

        private Vector3 GunForward()
        {
            var turret = _tank != null ? _tank.Turret : null;
            return turret != null && turret.GunPivot != null ? turret.GunPivot.forward : Vector3.forward;
        }

        /// <summary><c>_target_point</c>: the configured aim point, optionally led.</summary>
        public Vector3 TargetPoint(BattleTank target, bool withLead)
        {
            var config = Config;
            if (config == null || target == null) { return Vector3.zero; }
            Vector3 point;
            switch (config.AimbotAimPoint)
            {
                case SandboxAimPoint.Turret:
                    point = target.Turret != null && target.Turret.TurretPivot != null
                        ? target.Turret.TurretPivot.position : target.AutoAimPoint();
                    break;
                case SandboxAimPoint.Hull:
                    point = target.transform.position;
                    break;
                case SandboxAimPoint.Tracks:
                    point = target.transform.position + Vector3.up * Mathf.Max(0.25f, target.AutoAimOffset * 0.18f);
                    break;
                default:
                    point = target.AutoAimPoint();
                    break;
            }

            if (!withLead || config.AimbotLead <= 0f || _tank == null) { return point; }
            var shell = _tank.CurrentShell();
            if (shell == null || shell.Speed <= 0f) { return point; }
            var travel = SandboxCheatMath.LeadTime(Vector3.Distance(GunPosition(), point), shell.Speed, config.AimbotLead);
            return point + target.LinearVelocity * travel;
        }

        private bool LineOfSight(BattleTank target, Vector3 origin)
        {
            if (_tank == null || target == null) { return false; }
            return _tank.AutoAim.HasLineOfSight(target, origin);
        }

        private static float AngleDegrees(Vector3 forward, Vector3 offset)
        {
            if (offset.sqrMagnitude <= 0f || forward.sqrMagnitude <= 0f) { return 180f; }
            var cosine = Mathf.Clamp(Vector3.Dot(forward.normalized, offset.normalized), -1f, 1f);
            return Mathf.Acos(cosine) * Mathf.Rad2Deg;
        }

        private bool IsEnemy(BattleTank target)
        {
            return target != null && _tank != null && target != _tank && target.Team != _tank.Team
                && !target.Dead && UprisingTank.IsTargetable(target);
        }

        private bool TargetValid(BattleTank target, Vector3 origin, Vector3 forward)
        {
            var config = Config;
            if (config == null || !IsEnemy(target)) { return false; }
            var offset = TargetPoint(target, false) - origin;
            var distance = offset.magnitude;
            if (distance <= SandboxCheatMath.MinTargetDistance || distance > config.AimbotMaxDistance) { return false; }
            if (!SandboxCheatMath.InsideFov(AngleDegrees(forward, offset), config.AimbotFov)) { return false; }
            if (config.AimbotRequireSpotted && !target.Spotted) { return false; }
            if (!config.AimbotThroughWalls && !LineOfSight(target, origin)) { return false; }
            return true;
        }

        private float TargetScore(BattleTank target, Vector3 origin, Vector3 forward)
        {
            var offset = TargetPoint(target, false) - origin;
            var health = target.Health != null ? target.Health.Health : 0;
            var max = target.Health != null ? target.Health.MaxHealth : 1;
            return SandboxCheatMath.TargetScore(Config.AimbotPriority,
                AngleDegrees(forward, offset), offset.magnitude, health, max);
        }

        private BattleTank PickTarget(Vector3 origin, Vector3 forward)
        {
            var config = Config;
            var roster = BattleRoster.Current;
            if (config == null || _tank == null || roster == null) { return null; }
            if (config.AimbotSticky && TargetValid(_aimTarget, origin, forward)) { return _aimTarget; }
            BattleTank best = null;
            var bestScore = float.PositiveInfinity;
            var enemies = roster.GetEnemyTanks(_tank.Team);
            for (var i = 0; i < enemies.Count; i++)
            {
                var candidate = enemies[i];
                if (!TargetValid(candidate, origin, forward)) { continue; }
                var score = TargetScore(candidate, origin, forward);
                if (best == null || score < bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }
            return best;
        }

        private void ClearAim()
        {
            if (_tank != null && _aimTarget != null && _tank.AutoAim.Captured == _aimTarget)
            {
                _tank.AutoAim.Release(false, 0f);
            }
            _aimTarget = null;
            _smoothedPoint = Vector3.zero;
        }

        /// <summary>
        /// <c>aim_override</c>, called by the camera's aim trace after the
        /// ordinary reticle and auto-aim resolution, so the same gun,
        /// ballistic compensation and turret limits apply as to manual aim.
        /// </summary>
        public Vector3 AimOverride(Vector3 origin, Vector3 forward, Vector3 fallback, float delta)
        {
            var config = Config;
            if (config == null || !config.CheatAimbot || _tank == null || _tank.Dead)
            {
                ClearAim();
                return fallback;
            }

            var selected = PickTarget(origin, forward);
            if (selected == null)
            {
                ClearAim();
                return fallback;
            }

            var desired = TargetPoint(selected, true);
            if (selected != _aimTarget || !IsFinite(_smoothedPoint) || _smoothedPoint == Vector3.zero)
            {
                var lerp = _tank.Turret != null ? _tank.Turret.AimTargetLerp : Vector3.zero;
                _smoothedPoint = IsFinite(lerp) && lerp != Vector3.zero ? lerp : desired;
            }
            _aimTarget = selected;
            if (_tank.AutoAim.Captured != selected)
            {
                _tank.AutoAim.Capture(selected, desired, null);
            }
            else
            {
                _tank.AutoAim.MarkSeen();
            }

            var alpha = SandboxCheatMath.SmoothingAlpha(config.AimbotSmoothing, delta);
            _smoothedPoint = Vector3.Lerp(_smoothedPoint, desired, alpha);
            return _smoothedPoint;
        }

        private void ClearWallhackTarget(BattleTank target)
        {
            if (target == null) { return; }
            target.SetOutlineWidth(SandboxCheatMath.NormalOutlineWidth);
            target.SwitchOutline(false);
            target.OutlineSetup(true);
            target.ForceModelVisible(false);
        }

        private void ClearWallhack()
        {
            for (var i = 0; i < _wallhackTargets.Count; i++)
            {
                ClearWallhackTarget(_wallhackTargets[i]);
            }
            _wallhackTargets.Clear();
        }

        private void UpdateWallhack(float now)
        {
            var config = Config;
            var roster = BattleRoster.Current;
            if (config == null || !config.CheatWallhack || _tank == null || _tank.Dead || roster == null)
            {
                ClearWallhack();
                return;
            }
            if (now < _nextWallhack) { return; }
            _nextWallhack = now + SandboxCheatMath.WallhackRefresh;

            _active.Clear();
            var origin = ViewOrigin();
            var enemies = roster.GetEnemyTanks(_tank.Team);
            for (var i = 0; i < enemies.Count; i++)
            {
                var target = enemies[i];
                if (target == null || target.Dead) { continue; }
                var distance = Vector3.Distance(_tank.transform.position, target.transform.position);
                if (!SandboxCheatMath.InWallhackBand(distance, config.WallhackCloseDistance, config.WallhackMaxDistance))
                {
                    ClearWallhackTarget(target);
                    continue;
                }
                target.ForceModelVisible(true);
                target.SwitchOutline(true);
                target.SetOutlineColor(LineOfSight(target, origin) ? WallhackGreen : WallhackRed);
                target.SetOutlineWidth(SandboxCheatMath.WallhackOutlineWidth);
                _active.Add(target);
            }

            for (var i = 0; i < _wallhackTargets.Count; i++)
            {
                if (!_active.Contains(_wallhackTargets[i])) { ClearWallhackTarget(_wallhackTargets[i]); }
            }
            _wallhackTargets.Clear();
            _wallhackTargets.AddRange(_active);
        }

        /// <summary><c>trace_can_penetrate</c> against the latest gun trace.</summary>
        private bool TraceCanPenetrate()
        {
            var collider = _tank.GunTraceCollider;
            var plate = collider != null ? collider.GetComponent<BattleArmorPlate>() : null;
            var shell = _tank.CurrentShell();
            var normal = _tank.GunTraceNormal;
            var incidence = -1f;
            if (plate != null && normal != Vector3.zero)
            {
                incidence = SandboxCheatMath.Incidence(Vector3.Dot(-GunForward().normalized, normal.normalized));
            }
            return SandboxCheatMath.CanPenetrate(shell, plate != null, plate != null ? plate.Armor : 0f, incidence);
        }

        private BattleTank TriggerCandidate(Vector3 origin)
        {
            var config = Config;
            if (config == null || _tank == null) { return null; }
            var direct = _tank.GunTraceTank;
            var candidate = config.TriggerbotUseAimbot && _aimTarget != null ? _aimTarget : direct;
            if (!IsEnemy(candidate)) { return null; }
            if (config.TriggerbotRequireDirectHit && direct != candidate) { return null; }
            var point = TargetPoint(candidate, false);
            var gun = GunPosition();
            if (Vector3.Distance(gun, point) > config.TriggerbotMaxDistance) { return null; }
            if (config.TriggerbotRequireSpotted && !candidate.Spotted) { return null; }
            if (config.TriggerbotRequireVisible && !LineOfSight(candidate, origin)) { return null; }
            var direction = point - gun;
            if (direction.sqrMagnitude <= 0.001f) { return null; }
            if (AngleDegrees(GunForward(), direction) > config.TriggerbotToleranceDeg) { return null; }
            if (!config.TriggerbotFireWhileMoving && _tank.Suspension != null
                && Mathf.Abs(_tank.Suspension.Speed) > SandboxCheatMath.StillSpeed)
            {
                return null;
            }
            if (config.TriggerbotRequirePenetration && (direct != candidate || !TraceCanPenetrate())) { return null; }
            return candidate;
        }

        private void ResetTrigger()
        {
            _triggerTarget = null;
            _triggerSince = 0f;
        }

        private void UpdateTriggerbot(float now)
        {
            var config = Config;
            if (config == null || !config.CheatTriggerbot || _tank == null || _tank.Dead || _tank.Blocked)
            {
                ResetTrigger();
                return;
            }
            var candidate = TriggerCandidate(ViewOrigin());
            if (candidate == null)
            {
                ResetTrigger();
                return;
            }
            if (candidate != _triggerTarget)
            {
                _triggerTarget = candidate;
                _triggerSince = now;
                return;
            }
            if (now - _triggerSince < config.TriggerbotDelay) { return; }
            if (_tank.Gun != null && _tank.CanShoot())
            {
                _tank.Gun.ShootAttempt();
                _triggerSince = now;
            }
        }

        private void FixedUpdate()
        {
            if (_tank == null) { return; }
            var roster = BattleRoster.Current;
            if (Config == null || roster == null || !roster.IsOngoing)
            {
                ClearAim();
                ClearWallhack();
                return;
            }
            var now = Time.time;
            UpdateWallhack(now);
            UpdateTriggerbot(now);
        }

        private void OnDestroy()
        {
            ClearAim();
            ClearWallhack();
        }

        private static bool IsFinite(Vector3 value)
        {
            return !(float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z)
                || float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z));
        }
    }
}
