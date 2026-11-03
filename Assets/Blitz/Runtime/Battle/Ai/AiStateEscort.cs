using UnityEngine;

namespace BlitzLegacy.Runtime.Battle.Ai
{
    /// <summary>
    /// Big Boss support roles. The healer and the graviton keep to their boss
    /// (a graviton without one pairs with the nearest intoxicator): they hold
    /// a station behind it, as seen from the enemy, and fight from there. A
    /// healer whose mending shell is wanted - the boss below nine tenths, a
    /// tank near the boss below three quarters, anyone else below half - drives
    /// into range and shoots that ally instead: every friendly hit heals it and
    /// hands over the energy shield when one is armed.
    /// </summary>
    public sealed class AiStateEscort : AiStateCombat
    {
        /// <summary>How often the station behind the anchor is recomputed.</summary>
        public const float StationRefresh = 1f;

        /// <summary>How far off its station an escort may drift before it drives back.</summary>
        public const float StationSlack = 9f;

        /// <summary>Within this distance of the station the hull may reverse onto it.</summary>
        public const float ReverseOntoStation = 30f;

        /// <summary>How close to a patient the healer drives when it has no clear shot.</summary>
        public const float HealApproach = 25f;

        private BattleTank _patient;
        private Vector3 _station;
        private float _stationAt = -1f;

        /// <summary>The ally the healer is mending; null while it escorts.</summary>
        public BattleTank Patient
        {
            get { return _patient; }
        }

        public override void Enter(AiTankController ai)
        {
            base.Enter(ai);
            _patient = null;
            _station = Vector3.zero;
            _stationAt = -1f;
        }

        protected override void Think()
        {
            // The enemy target and its aim point stay fresh for the fight.
            base.Think();
            var tank = Tank();
            if (tank == null)
            {
                return;
            }

            _patient = Ai.HealTarget();
            if (_patient != null)
            {
                ThinkHealing(tank, _patient);
                return;
            }

            var anchor = Ai.EscortAnchor();
            if (anchor == null)
            {
                // Nobody left to keep to: the ported tactics take over.
                Ai.RequestTacticalSoon();
                Ai.Drive(0f, 0f);
                return;
            }

            var now = Time.time;
            if (now >= _stationAt)
            {
                _station = Ai.EscortStation(anchor);
                _stationAt = now + StationRefresh;
            }

            var goal = _station != Vector3.zero ? _station : anchor.Pos();
            var offset = goal - tank.Pos();
            offset.y = 0f;
            Ai.NoteDriveIntent(false);
            if (offset.sqrMagnitude > StationSlack * StationSlack)
            {
                Ai.DriveToPos(goal, true, offset.sqrMagnitude < ReverseOntoStation * ReverseOntoStation);
                return;
            }

            // On station: front plate and gun towards the fight.
            if (Ai.EnemyTargetIsValid())
            {
                Ai.HoldFacing(Ai.TargetTank, Vector3.zero, 0f);
                return;
            }

            var front = Ai.FrontAnchor();
            if (front != Vector3.zero)
            {
                Ai.HoldFacing(null, front, 0f);
            }
            else
            {
                Ai.Drive(0f, 0f);
            }
        }

        /// <summary>Into range and line of the patient, then hold the gun on it.</summary>
        private void ThinkHealing(BattleTank tank, BattleTank patient)
        {
            Ai.NoteDriveIntent(false);
            var aim = PatientAimPoint(patient);
            var distance = Vector3.Distance(patient.Pos(), tank.Pos());
            if (distance > AiTankController.HealRange
                || !Ai.CheckPosToPosVisibility(tank.GunShootPoint(), aim))
            {
                var away = tank.Pos() - patient.Pos();
                away.y = 0f;
                var approach = away.sqrMagnitude > 1f
                    ? patient.Pos() + (away.normalized * HealApproach)
                    : patient.Pos();
                var goal = Ai.NavigationGoal(approach);
                Ai.DriveToPos(goal != Vector3.zero ? goal : patient.Pos(), true);
                return;
            }

            Ai.HoldFacing(patient, Vector3.zero, 0f);
        }

        protected override void Tick(float delta)
        {
            var tank = Tank();
            if (tank != null && _patient != null && !_patient.Dead)
            {
                TickHealing(tank, _patient);
                return;
            }

            base.Tick(delta);
        }

        /// <summary>Lay the gun on the patient and fire the moment the trace meets it.</summary>
        private static void TickHealing(BattleTank tank, BattleTank patient)
        {
            tank.SetAimTarget(PatientAimPoint(patient), false, false);
            var roster = BattleRoster.Current;
            if (!tank.CanShoot() || tank.Gun == null || SandboxTank.IsHarmless(tank)
                || roster == null || !roster.IsOngoing)
            {
                return;
            }

            tank.UpdateGunTrace(true);
            if (tank.GunTraceTank == patient)
            {
                tank.Gun.ShootAttempt();
            }
        }

        private static Vector3 PatientAimPoint(BattleTank patient)
        {
            var turret = patient.Turret;
            return turret != null && turret.TurretPivot != null
                ? turret.TurretPivot.position
                : patient.Pos() + (Vector3.up * 1.5f);
        }
    }
}
