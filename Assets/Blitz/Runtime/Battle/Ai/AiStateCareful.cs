namespace BlitzLegacy.Runtime.Battle.Ai
{
    /// <summary>
    /// Port of <c>ai_state_fight_careful.gd</c> (<c>AIStateCombatCareful</c>):
    /// back off whenever the fight looks like it is turning bad.
    /// </summary>
    public sealed class AiStateCareful : AiStateCombat
    {
        private float _hideTime;

        protected override void Think()
        {
            base.Think();
            if (!Ai.EnemyTargetIsValid())
            {
                return;
            }

            var targetVisible = LastAimPoint != null;
            var allyToEnemy = Ai.AllyToEnemyNearCoeff(200f);
            var tanksLooking = Ai.GetTanksLookingAtMe();
            var itsDangerous = (Ai.GetEnemiesDangerLevel() >= 2 && tanksLooking >= 2)
                || (allyToEnemy < 1f && Ai.DistSqrToEnemies() < 100f * 100f);
            itsDangerous = itsDangerous
                || (Ai.IsTankLookingAtMe(Ai.TargetTank) && !Ai.IsDumb());

            if (itsDangerous)
            {
                Retreat();
                return;
            }

            _hideTime = 0f;
            if ((Ai.CanShoot() && !targetVisible) || TargetIsPrey())
            {
                Push();
            }
            else
            {
                Retreat();
            }
        }
    }
}
