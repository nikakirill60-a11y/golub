using BlitzLegacy.Core.Tanks.Ai;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle.Ai
{
    /// <summary>
    /// Port of <c>ai_state_fight.gd</c> (<c>AIStateCombat</c>): the shared
    /// fighting behaviour every combat state builds on. It keeps the target,
    /// leads the aim point and pulls the trigger once the gun has settled on
    /// the target for long enough.
    /// </summary>
    public abstract class AiStateCombat : AiState
    {
        /// <summary><c>last_aim_point</c>.</summary>
        protected Transform LastAimPoint;

        /// <summary><c>seconds_on_target</c>.</summary>
        protected float SecondsOnTarget;

        /// <summary><c>enemy_is_seen</c>.</summary>
        protected bool EnemyIsSeen()
        {
            return LastAimPoint != null;
        }

        /// <summary>
        /// Whether a casemate tank destroyer halts its hull to fire, as the
        /// source always does. A withdrawal must not stop in the open.
        /// </summary>
        protected virtual bool HaltsToShoot
        {
            get { return true; }
        }

        /// <summary>
        /// Whether a turreted hull should stop to let its aim settle before
        /// a shot at this range; the source never does.
        /// </summary>
        protected virtual bool SettleBeforeShot(float distance)
        {
            return false;
        }

        /// <summary><c>push</c>: drive straight at the target.</summary>
        protected void Push()
        {
            var tank = Tank();
            if (tank == null || Ai.TargetTank == null)
            {
                return;
            }

            Ai.NoteDriveIntent(false);
            Ai.DriveToPos(
                Ai.TargetTank.Pos(),
                true,
                false,
                false,
                false,
                false,
                tank.IsTdNoFullRotation);
        }

        /// <summary>
        /// <c>retreat(ignore_hiding_spots)</c>. Prefer the nearest valid source
        /// map cover; when none is safe, back away from the enemy average by
        /// three hull lengths as the original fallback does.
        /// </summary>
        protected void Retreat(bool ignoreHidingSpots = false)
        {
            var tank = Tank();
            if (tank == null)
            {
                return;
            }

            Ai.NoteDriveIntent(true);

            var threat = Ai.TargetTank != null ? Ai.TargetTank.Pos() : Vector3.zero;
            var hidingSpot = threat != Vector3.zero
                ? Ai.FindHidingSpot(threat)
                : Vector3.zero;

            // Most of the map is not an authored hide object: a crew that can
            // read terrain also backs behind a ridge or a house nearby.
            if (hidingSpot == Vector3.zero && threat != Vector3.zero
                && Ai.SkillReached(AiSkillLevel.GreenArmy))
            {
                hidingSpot = Ai.FindCoverFrom(threat, 45f);
            }

            var hide = hidingSpot != Vector3.zero
                && !ignoreHidingSpots
                && (!tank.IsTdNoFullRotation || !Ai.CanShoot());
            if (hide)
            {
                var reach = tank.HullLength * 0.75f;
                if ((hidingSpot - Ai.Pos()).sqrMagnitude > reach * reach)
                {
                    Ai.DriveToPos(hidingSpot, true, true);
                }
                else
                {
                    Ai.Drive(0f, 0f);
                }

                return;
            }

            var dir = Ai.DirToEnemies();
            var away = tank.Pos() - (new Vector3(dir.x, 0f, dir.y) * tank.HullLength * 3f);
            Ai.DriveToPos(away, true, true);
        }

        /// <summary><c>reset_target(ignore_unspotted)</c>.</summary>
        protected virtual void ResetTarget(bool ignoreUnspotted = false)
        {
            var remembered = Ai.TanksUnspottedThere;
            if (remembered.Count == 0 || ignoreUnspotted)
            {
                Ai.SetTargetToRandomMarker();
                return;
            }

            Ai.TargetPosition = Ai.TakeUnspottedThere(Random.Range(0, remembered.Count));
        }

        /// <summary><c>target_is_prey</c>.</summary>
        protected bool TargetIsPrey()
        {
            return Ai.TargetTank != null && Ai.GetTankDangerLevel(Ai.TargetTank) <= -2;
        }

        protected override void Think()
        {
            if (!Ai.EnemyTargetIsValid())
            {
                LastAimPoint = null;
                Ai.SearchForTargetTankRating(true);
                return;
            }

            if (Ai.DelayElapsedSet("aim_at_target_points", Ai.ReactionTime() * 0.75f))
            {
                LastAimPoint = Ai.TankGetVisibleAimPoint(Ai.TargetTank);
            }

            if (Ai.DelayElapsedSet("switch_target", Random.Range(1.8f, 3f)))
            {
                Ai.SearchForTargetTankRating();
            }
        }

        protected override void Tick(float delta)
        {
            var tank = Tank();
            if (tank == null)
            {
                return;
            }
            if (Ai.TickAtgmCombat(delta))
            {
                return;
            }
            if (!Ai.EnemyTargetIsValid())
            {
                return;
            }

            var target = Ai.TargetTank;
            var shootPos = tank.GunShootPoint();
            Vector3 targetPos;
            if (LastAimPoint != null)
            {
                var shell = tank.CurrentShell();
                var speed = shell != null ? shell.Speed : 0f;
                var distance = (target.Pos() - shootPos).magnitude;
                var travelTime = speed > 0f ? distance / speed : 0f;
                var lead = target.LinearVelocity * travelTime;
                if (Ai.IsDumb())
                {
                    lead *= Random.Range(0.3f, 0.8f);
                }

                targetPos = LastAimPoint.position + lead;
            }
            else
            {
                targetPos = target.GunShootPoint();
            }

            tank.SetAimTarget(targetPos);

            // A casemate tank destroyer has to turn its hull before a stored
            // navigation command keeps driving past a point-blank threat that
            // sits outside the gun's arc.
            if (Ai.AnswerCloseFixedGunThreat(targetPos))
            {
                SecondsOnTarget = 0f;
                return;
            }

            if (LastAimPoint == null || !Ai.CanShoot())
            {
                return;
            }

            var onTarget = tank.GunTraceTank == target
                || Ai.IsTankLookingAtPos(tank, targetPos, 0.98f);
            if (!onTarget)
            {
                return;
            }

            if (!Ai.CheckPosToPosVisibility(shootPos, targetPos))
            {
                return;
            }

            if ((tank.IsTdNoFullRotation && HaltsToShoot)
                || SettleBeforeShot((target.Pos() - tank.Pos()).magnitude))
            {
                Ai.StopToShoot(true);
            }

            SecondsOnTarget += delta;
            if (SecondsOnTarget < Ai.ReactionTime() * 2f)
            {
                return;
            }

            Ai.Shoot();
            SecondsOnTarget = 0f;
        }
    }
}
