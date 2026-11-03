using UnityEngine;

namespace BlitzLegacy.Runtime.Battle.Ai
{
    public enum AtgmAiMode
    {
        None = 0,
        Artillery = 1,
        Assault = 2,
    }

    public enum AtgmMovement
    {
        Hold = 0,
        Approach = 1,
        Circle = 2,
        Retreat = 3,
    }

    /// <summary>Pure thresholds shared by live ATGM bots and EditMode tests.</summary>
    public static class AtgmAiTactics
    {
        public const float ArtilleryChance = .55f;
        public const float DeliberateMissChance = .12f;
        public const float ArtilleryMinRange = 75f;
        public const float ArtilleryMaxRange = 560f;
        public const float AssaultMaxRange = 120f;
        public const float AssaultCircleRange = 48f;
        public const float AssaultRetreatRange = 30f;
        public const float MissRadiusMin = 7f;
        public const float MissRadiusMax = 14f;

        public static AtgmAiMode ChooseMode(float roll)
        {
            return Mathf.Clamp01(roll) < ArtilleryChance
                ? AtgmAiMode.Artillery
                : AtgmAiMode.Assault;
        }

        public static bool ShouldDeliberatelyMiss(float roll)
        {
            return Mathf.Clamp01(roll) < DeliberateMissChance;
        }

        public static float MissRadius(float roll)
        {
            return Mathf.Lerp(MissRadiusMin, MissRadiusMax, Mathf.Clamp01(roll));
        }

        public static AtgmAiMode EffectiveMode(AtgmAiMode preferred, float distance)
        {
            if (preferred == AtgmAiMode.Artillery && distance < ArtilleryMinRange)
                return AtgmAiMode.Assault;
            return preferred;
        }

        public static bool CanFire(AtgmAiMode mode, float distance)
        {
            if (mode == AtgmAiMode.Artillery)
                return distance >= ArtilleryMinRange && distance <= ArtilleryMaxRange;
            return mode == AtgmAiMode.Assault && distance <= AssaultMaxRange;
        }

        public static AtgmMovement Movement(AtgmAiMode mode, float distance)
        {
            if (mode == AtgmAiMode.Artillery)
            {
                if (distance < 115f) return AtgmMovement.Retreat;
                if (distance > 430f) return AtgmMovement.Approach;
                return AtgmMovement.Hold;
            }

            if (distance < AssaultRetreatRange) return AtgmMovement.Retreat;
            if (distance <= AssaultMaxRange) return AtgmMovement.Circle;
            return AtgmMovement.Approach;
        }

        public static float LeadSeconds(float distance, float rocketSpeed)
        {
            if (distance <= 0f || rocketSpeed <= 0f) return 0f;
            return Mathf.Clamp(distance / rocketSpeed * .12f, .08f, .8f);
        }
    }
}
