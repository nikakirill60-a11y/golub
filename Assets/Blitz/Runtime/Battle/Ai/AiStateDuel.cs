using BlitzLegacy.Core.Tanks.Ai;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle.Ai
{
    /// <summary>
    /// A one on one the bot judged it can win. How it fights follows from
    /// what it brings: a heavy - or anything the target struggles to
    /// penetrate, or a hull buffed far past its opponent - closes in with its
    /// front angled at the gun; a light circles at close range towards the
    /// target's rear; a sniper or casemate destroyer holds, faces and settles
    /// its aim; a medium trades at mid range and ducks into nearby cover while
    /// it reloads, peeking back out from where it last fired.
    /// </summary>
    public sealed class AiStateDuel : AiStateCombat
    {
        public enum DuelRole
        {
            Brawler = 0,
            Skirmisher = 1,
            Flanker = 2,
            Marksman = 3,
        }

        public const float BrawlRange = 32f;
        public const float SkirmishRange = 95f;
        public const float PressRange = 60f;
        public const float FlankRadius = 45f;
        public const float MarksmanMinRange = 70f;
        public const float SettleRange = 110f;
        public const float DuckReload = 2.5f;
        public const float DuckCoverReach = 30f;
        public const float RoleInterval = 3f;
        public const float OrbitInterval = 0.8f;
        public const float OrbitStepDegrees = 40f;

        private DuelRole _role;
        private float _roleAt = -1f;
        private float _side = 1f;
        private float _sideAt;
        private float _orbit = 1f;
        private Vector3 _orbitGoal;
        private float _orbitAt = -1f;
        private Vector3 _firingSpot;
        private float _lastShot = float.NaN;
        private bool _ducking;

        /// <summary>How the bot plays this duel.</summary>
        public DuelRole Role
        {
            get { return _role; }
        }

        public override void Enter(AiTankController ai)
        {
            base.Enter(ai);
            _side = Random.value < 0.5f ? -1f : 1f;
            _orbit = Random.value < 0.5f ? -1f : 1f;
            _sideAt = Time.time + Random.Range(4f, 7f);
            _roleAt = -1f;
            _orbitAt = -1f;
            _orbitGoal = Vector3.zero;
            _firingSpot = Vector3.zero;
            _ducking = false;
            var tank = ai != null ? ai.Tank : null;
            _lastShot = tank != null && tank.Gun != null ? tank.Gun.LastShot : float.NaN;
        }

        protected override bool HaltsToShoot
        {
            get { return _role == DuelRole.Marksman; }
        }

        /// <summary>
        /// A player stops for a long shot so the reticle can close; a bot
        /// does the same, unless a second gun is close enough to punish it.
        /// </summary>
        protected override bool SettleBeforeShot(float distance)
        {
            if (_ducking || distance < SettleRange
                || (_role != DuelRole.Marksman && _role != DuelRole.Skirmisher)
                || !Ai.SkillReached(AiSkillLevel.GreenArmy))
            {
                return false;
            }

            var picture = Ai.Engagement;
            return picture.NearestEnemyDistance > 80f && picture.EnemiesEngaging <= 1;
        }

        protected override void Think()
        {
            base.Think();
            if (!Ai.EnemyTargetIsValid())
            {
                return;
            }

            var tank = Tank();
            var target = Ai.TargetTank;
            if (tank == null || target == null)
            {
                return;
            }

            var now = Time.time;
            if (tank.Gun != null && !Mathf.Approximately(tank.Gun.LastShot, _lastShot))
            {
                _lastShot = tank.Gun.LastShot;
                _firingSpot = tank.Pos();
            }

            if (now >= _roleAt)
            {
                _role = ChooseRole(tank, target);
                _roleAt = now + RoleInterval;
            }

            if (now >= _sideAt)
            {
                // Swap cheeks now and then, so the angled front is not a
                // still target for a gun that has already ranged it.
                _side = -_side;
                _sideAt = now + Random.Range(4f, 7f);
            }

            switch (_role)
            {
                case DuelRole.Brawler:
                    ThinkBrawler(tank, target);
                    break;
                case DuelRole.Flanker:
                    ThinkFlanker(tank, target);
                    break;
                case DuelRole.Marksman:
                    ThinkMarksman(tank, target);
                    break;
                default:
                    ThinkSkirmisher(tank, target);
                    break;
            }
        }

        private DuelRole ChooseRole(BattleTank tank, BattleTank target)
        {
            if (tank.IsTdNoFullRotation || Ai.PlayStyleIs(AiPlayStyle.Sniper))
            {
                return DuelRole.Marksman;
            }

            // An Uprising engine perk buys the speed a flank needs.
            if (Ai.PlayStyleIs(AiPlayStyle.Light) || Ai.HasUprisingPerk("engineBoost"))
            {
                return DuelRole.Flanker;
            }

            var picture = Ai.Engagement;
            if (Ai.PlayStyleIs(AiPlayStyle.Heavy)
                || picture.TargetFrontPenetration < 0.45f
                || picture.DuelOdds >= 0.8f)
            {
                return DuelRole.Brawler;
            }

            var mine = tank.Suspension != null ? tank.Suspension.MaxSpeedForward : 0f;
            var theirs = target.Suspension != null ? target.Suspension.MaxSpeedForward : 0f;
            if (mine > theirs * 1.25f && picture.DuelOdds >= 0.55f)
            {
                return DuelRole.Flanker;
            }

            return DuelRole.Skirmisher;
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

        /// <summary>Nose first into close range, then hold the angled front on the gun.</summary>
        private void ThinkBrawler(BattleTank tank, BattleTank target)
        {
            var picture = Ai.Engagement;
            var toTarget = Flat(target.Pos() - tank.Pos());
            var distance = toTarget.magnitude;
            var angle = AiEngagementMath.FrontalAngle(picture.TargetFrontPenetration) * _side;
            Ai.NoteDriveIntent(false);
            if (!EnemyIsSeen() || distance > BrawlRange + 12f)
            {
                var goal = distance > BrawlRange && distance > 0.01f
                    ? target.Pos() - (toTarget / distance * BrawlRange)
                    : target.Pos();
                Ai.DriveToPos(goal, true, false, false, false, false, tank.IsTdNoFullRotation);
                return;
            }

            // Too close and the enemy starts circling: give ground, nose on.
            Ai.HoldFacing(target, Vector3.zero, angle, distance < BrawlRange * 0.45f ? -1f : 0f);
        }

        /// <summary>Trade at mid range; duck into cover while the gun reloads.</summary>
        private void ThinkSkirmisher(BattleTank tank, BattleTank target)
        {
            var picture = Ai.Engagement;
            var toTarget = Flat(target.Pos() - tank.Pos());
            var distance = toTarget.magnitude;
            var desired = picture.DuelOdds >= 0.7f ? PressRange : SkirmishRange;
            if (picture.MyHitChance < 0.6f)
            {
                // The shells scatter at this range: close in until they land.
                desired = Mathf.Min(desired, distance * 0.7f);
            }

            desired = Mathf.Max(desired, 35f);
            var reloadLeft = tank.Gun != null && !tank.Gun.CanShoot()
                ? tank.Gun.ReloadRemaining()
                : 0f;

            if (reloadLeft > DuckReload && Ai.SkillReached(AiSkillLevel.GreenArmy))
            {
                var cover = Ai.FindCoverFrom(target.Pos(), DuckCoverReach);
                if (cover != Vector3.zero)
                {
                    _ducking = true;
                    Ai.NoteDriveIntent(true);
                    var reach = tank.HullLength * 0.75f;
                    if ((cover - tank.Pos()).sqrMagnitude > reach * reach)
                    {
                        Ai.DriveToPos(cover, true, true);
                    }
                    else
                    {
                        Ai.HoldFacing(target, Vector3.zero, 0f);
                    }

                    return;
                }
            }

            if (_ducking && reloadLeft > 1f)
            {
                Ai.HoldFacing(target, Vector3.zero, 0f);
                return;
            }

            _ducking = false;
            Ai.NoteDriveIntent(false);
            if (!EnemyIsSeen())
            {
                // Peek back out where the last shot went from, else find a line.
                var peek = _firingSpot != Vector3.zero
                    && (_firingSpot - tank.Pos()).sqrMagnitude > 9f
                        ? _firingSpot
                        : target.Pos();
                Ai.DriveToPos(peek, true, true);
                return;
            }

            if (distance > desired + 20f && distance > 0.01f)
            {
                Ai.DriveToPos(target.Pos() - (toTarget / distance * desired), true);
                return;
            }

            if (distance < desired - 30f)
            {
                Ai.NoteDriveIntent(true);
                Ai.HoldFacing(target, Vector3.zero, 0f, -1f);
                return;
            }

            var angle = AiEngagementMath.FrontalAngle(picture.TargetFrontPenetration) * _side * 0.6f;
            Ai.HoldFacing(target, Vector3.zero, angle);
        }

        /// <summary>Circle the target at close range, swinging towards its rear.</summary>
        private void ThinkFlanker(BattleTank tank, BattleTank target)
        {
            var now = Time.time;
            var center = target.Pos();
            var reached = _orbitGoal != Vector3.zero
                && (_orbitGoal - tank.Pos()).sqrMagnitude < 10f * 10f;
            if (now >= _orbitAt || reached || _orbitGoal == Vector3.zero)
            {
                _orbitAt = now + OrbitInterval;
                var from = Flat(tank.Pos() - center);
                var rear = -Flat(target.transform.forward);
                if (from.sqrMagnitude < 1f)
                {
                    from = rear;
                }

                if (rear.sqrMagnitude > 0.0001f
                    && Vector3.Dot(rear.normalized, from.normalized) < 0.5f)
                {
                    // Bearing grows clockwise, and so does a positive cross.
                    _orbit = Vector3.Cross(from, rear).y >= 0f ? 1f : -1f;
                }

                var radius = Mathf.Clamp(from.magnitude * 0.85f, FlankRadius * 0.7f, FlankRadius * 1.3f);
                var bearing = Mathf.Atan2(from.x, from.z);
                _orbitGoal = Vector3.zero;
                for (var attempt = 0; attempt < 2 && _orbitGoal == Vector3.zero; attempt++)
                {
                    var next = bearing + (OrbitStepDegrees * Mathf.Deg2Rad * _orbit);
                    var probe = center + (new Vector3(Mathf.Sin(next), 0f, Mathf.Cos(next)) * radius);
                    _orbitGoal = Ai.NavigationGoal(probe);
                    if (_orbitGoal == Vector3.zero)
                    {
                        _orbit = -_orbit;
                    }
                }
            }

            Ai.NoteDriveIntent(false);
            if (_orbitGoal == Vector3.zero)
            {
                Ai.DriveToPos(center, true);
                return;
            }

            Ai.DriveToPos(_orbitGoal, true, false, false, false, true);
        }

        /// <summary>Hold, face the gun and settle; give ground when rushed.</summary>
        private void ThinkMarksman(BattleTank tank, BattleTank target)
        {
            var distance = Flat(target.Pos() - tank.Pos()).magnitude;
            if (distance < MarksmanMinRange && Ai.Engagement.DuelOdds < 0.75f)
            {
                Ai.NoteDriveIntent(true);
                Ai.HoldFacing(target, Vector3.zero, 0f, -1f);
                return;
            }

            Ai.NoteDriveIntent(false);
            if (!EnemyIsSeen())
            {
                var spot = _firingSpot != Vector3.zero
                    && (_firingSpot - tank.Pos()).sqrMagnitude > 9f
                        ? _firingSpot
                        : target.Pos();
                Ai.DriveToPos(spot, true, true, false, false, false, tank.IsTdNoFullRotation);
                return;
            }

            Ai.HoldFacing(target, Vector3.zero, 0f);
        }
    }
}