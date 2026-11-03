namespace BlitzLegacy.Runtime.Battle.Ai
{
    /// <summary>
    /// A charge. A Big Boss bomber whose blast is ready drives flat out into
    /// the densest group of spotted enemies - the blast goes off once they are
    /// in reach (see BigBossTank) - and an Uprising rammer drives through its
    /// target. The gun keeps firing on the way and the hull never halts; once
    /// the charge has nothing left to aim at, the tactics pick the next state
    /// (a spent bomber breaks away).
    /// </summary>
    public sealed class AiStateRush : AiStateCombat
    {
        /// <summary>A charge never parks to settle a shot.</summary>
        protected override bool HaltsToShoot
        {
            get { return false; }
        }

        protected override void Think()
        {
            base.Think();
            var target = Ai.RushTarget();
            if (target == null)
            {
                Ai.RequestTacticalSoon();
                return;
            }

            if (Ai.TargetTank != target && Ai.TankIsValid(target))
            {
                Ai.TargetTank = target;
                LastAimPoint = null;
            }

            Ai.NoteDriveIntent(false);
            Ai.DriveToPos(target.Pos(), true, false, false, false, true);
        }
    }
}
