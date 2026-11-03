using UnityEngine;

namespace BlitzLegacy.Runtime.Battle.Ai
{
    /// <summary>
    /// Port of <c>ai_state_fight_distant.gd</c> (<c>AIStateCombatDistant</c>):
    /// hold a distant or sniping marker and reposition inside it. The source's
    /// dead <c>think_old</c> and <c>think_old_old</c> are not ported.
    /// </summary>
    public sealed class AiStateDistant : AiStateCombat
    {
        private float _enemyLastSeen;

        /// <summary><c>seconds_since_enemy_seen</c>.</summary>
        private float SecondsSinceEnemySeen()
        {
            return Time.time - _enemyLastSeen;
        }

        /// <summary><c>roam_within_marker</c>.</summary>
        private void RoamWithinMarker()
        {
            var marker = Ai.LastChosenMarker;
            var reaction = Ai.ReactionTime();
            if (SecondsSinceEnemySeen() > reaction * 30f
                || (marker != null && !marker.IsSniper()
                    && Ai.DelayElapsedSet("reposition_wihin_marker", reaction * 25f)))
            {
                Ai.TargetPosition = Ai.GetRandomPosAtMarker(marker);

                // The source pushes the stamp into the future so a reposition
                // buys ten quiet seconds before the marker is judged again.
                _enemyLastSeen = Time.time + 10f;
            }

            var tank = Tank();
            if (tank != null && !tank.Spotted && (Ai.DriveTargetIsClose() || EnemyIsSeen()))
            {
                Ai.Drive(0f, 0f);
            }
            else
            {
                Ai.DriveToPos(Ai.TargetPosition, true);
            }
        }

        protected override void ResetTarget(bool ignoreUnspotted = false)
        {
            Ai.SetTargetToMarker(Ai.GetDistantMarker());
            _enemyLastSeen = Time.time + 10f;
        }

        protected override void Think()
        {
            base.Think();

            if (EnemyIsSeen())
            {
                _enemyLastSeen = Time.time;
            }

            var marker = Ai.LastChosenMarker;
            if (marker != null && Ai.DistantMarkerIsValid(marker)
                && SecondsSinceEnemySeen() < Ai.ReactionTime() * 30f)
            {
                RoamWithinMarker();
            }
            else
            {
                ResetTarget();
            }
        }
    }
}
