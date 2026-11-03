using UnityEngine;

namespace BlitzLegacy.Runtime.Battle.Ai
{
    /// <summary>
    /// A target the bot was winning against slipped out of sight. Instead of
    /// rolling back to a random marker, drive to where it should be by now -
    /// its last position carried on along its last velocity - with the turret
    /// pre-aimed at the spot it will reappear from. The trail goes cold when
    /// the bot arrives and still sees nothing, or after the chase memory runs
    /// out; any new contact hands the decision back to the odds.
    /// </summary>
    public sealed class AiStateChase : AiStateCombat
    {
        public const float GoalInterval = 0.75f;
        public const float ColdTrailRadius = 20f;
        public const float ColdTrailSeconds = 3f;

        private Vector3 _goal;
        private float _goalAt = -1f;

        public override void Enter(AiTankController ai)
        {
            base.Enter(ai);
            _goal = Vector3.zero;
            _goalAt = -1f;
        }

        protected override void Think()
        {
            base.Think();
            var tank = Tank();
            if (tank == null)
            {
                return;
            }

            var quarry = Ai.Quarry;
            if (quarry == null || quarry.Dead)
            {
                GiveUp();
                return;
            }

            if (Ai.EnemyTargetIsValid())
            {
                // Contact: let the odds pick the next move.
                Ai.RequestTacticalSoon();
            }

            var now = Time.time;
            var predicted = Ai.PredictedQuarryPosition();
            if (now >= _goalAt || _goal == Vector3.zero)
            {
                _goalAt = now + GoalInterval;
                _goal = Ai.NavigationGoal(predicted);
                if (_goal == Vector3.zero)
                {
                    _goal = Ai.NavigationGoal(Ai.QuarryLastSeen);
                }
            }

            if (_goal == Vector3.zero)
            {
                GiveUp();
                return;
            }

            var close = (_goal - Ai.Pos()).sqrMagnitude < ColdTrailRadius * ColdTrailRadius;
            if (close && now - Ai.QuarryLastSeenAt > ColdTrailSeconds && !Ai.EnemyTargetIsValid())
            {
                GiveUp();
                return;
            }

            if (!Ai.EnemyTargetIsValid())
            {
                tank.SetAimTarget(predicted + (Vector3.up * 1.5f), true, false);
            }

            Ai.NoteDriveIntent(false);
            Ai.DriveToPos(_goal, true, false, false, false, true);
        }

        private void GiveUp()
        {
            Ai.EndChase();
            Ai.RequestTacticalSoon();
            Ai.Drive(0f, 0f);
        }
    }
}