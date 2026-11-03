namespace BlitzLegacy.Runtime.Battle.Ai
{
    /// <summary>
    /// Port of <c>ai_state_rollout.gd</c> (<c>AIStateRollout</c>): drive at the
    /// next marker while there is nothing to fight.
    /// </summary>
    public sealed class AiStateRollout : AiStateCombat
    {
        protected override void Think()
        {
            base.Think();

            // Authored map corridors survive combat-state interruptions, but
            // rudniki ships none, so this always falls through to the marker.
            if (Ai.FollowMapGoldenPath())
            {
                return;
            }

            // A goal with no corridor to it, or one the hull has made no
            // headway to for a while (a wall the probe keeps turning away
            // from, a queue behind a teammate), is swapped for another.
            var repick = !Ai.DriveTargetIsValid()
                || Ai.DriveTargetIsClose()
                || (Ai.UnreachableSeconds > 1.2f && Ai.DelayElapsedSet("repick_unreachable", 4f))
                || (Ai.IsMobile() && Ai.Stalled());
            if (repick)
            {
                ResetTarget();
                Ai.NoteProgress();
            }

            Ai.DriveToPos(Ai.TargetPosition, true, false, true);
        }
    }
}
