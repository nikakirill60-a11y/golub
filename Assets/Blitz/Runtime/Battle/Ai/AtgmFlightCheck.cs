using UnityEngine;

namespace BlitzLegacy.Runtime.Battle.Ai
{
    /// <summary>
    /// Flies a guided rocket ahead of time with the very steering the real
    /// one uses (<see cref="BattleProjectile.GuidedVelocity"/>) and reports
    /// the first piece of map it would hit. The overhead launcher used to fire
    /// as soon as its barrel reached the lob pitch, so a launcher parked
    /// behind a wall or under a roof kept putting its rocket into it.
    /// </summary>
    public static class AtgmFlightCheck
    {
        /// <summary>Steps between two traces of the walked path.</summary>
        public const int StepsPerTrace = 5;

        /// <summary>A rocket this close to its aim point has arrived.</summary>
        public const float ArrivalRadius = 4f;

        /// <summary>The longest walk, whatever the shell's lifetime says.</summary>
        public const int MaxSteps = 1200;

        /// <summary>What stops a rocket and does not move: the map and its props.</summary>
        public static int ObstacleMask
        {
            get { return (1 << BattleLayers.Static) | (1 << BattleLayers.Destructibles); }
        }

        /// <summary>
        /// True when the rocket launched from <paramref name="muzzle"/> with
        /// <paramref name="launchVelocity"/> and steered at
        /// <paramref name="aim"/> hits the map before it gets there.
        /// </summary>
        public static bool Obstructed(
            Vector3 muzzle,
            Vector3 launchVelocity,
            Vector3 aim,
            float turnSpeed,
            bool loft,
            float gravity,
            float maxSeconds,
            float step,
            out Vector3 hitPoint)
        {
            hitPoint = Vector3.zero;
            if (launchVelocity.sqrMagnitude <= 0.0001f || maxSeconds <= 0f)
            {
                return false;
            }

            if (step <= 0f)
            {
                step = 0.02f;
            }

            var steps = Mathf.Min(MaxSteps, Mathf.CeilToInt(maxSeconds / step));
            var arrival = ArrivalRadius * ArrivalRadius;
            var position = muzzle;
            var velocity = launchVelocity;
            var traceFrom = muzzle;
            for (var i = 1; i <= steps; i++)
            {
                if (turnSpeed > 0f)
                {
                    velocity = BattleProjectile.GuidedVelocity(
                        velocity, position, aim, turnSpeed, step, loft, muzzle, launchVelocity);
                }

                velocity.y -= gravity * step;
                position += velocity * step;
                var arrived = (position - aim).sqrMagnitude <= arrival;
                if (!arrived && i % StepsPerTrace != 0 && i != steps)
                {
                    continue;
                }

                RaycastHit hit;
                if (Trace(traceFrom, position, out hit))
                {
                    // Ground at the target's own feet is the target, not a wall.
                    if ((hit.point - aim).sqrMagnitude <= arrival)
                    {
                        return false;
                    }

                    hitPoint = hit.point;
                    return true;
                }

                if (arrived)
                {
                    return false;
                }

                traceFrom = position;
            }

            return false;
        }

        private static bool Trace(Vector3 from, Vector3 to, out RaycastHit hit)
        {
            var segment = to - from;
            var distance = segment.magnitude;
            if (distance <= 0.0001f)
            {
                hit = default(RaycastHit);
                return false;
            }

            return Physics.Raycast(
                from, segment / distance, out hit, distance, ObstacleMask,
                QueryTriggerInteraction.Ignore);
        }
    }
}