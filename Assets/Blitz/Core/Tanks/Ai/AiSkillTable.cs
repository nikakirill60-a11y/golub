using System;

namespace BlitzLegacy.Core.Tanks.Ai
{
    /// <summary>
    /// The static skill tables of ai_controller.gd. Every number is the
    /// source's; the randomness is handed in so a test can pin it down.
    /// </summary>
    public static class AiSkillTable
    {
        /// <summary>heavy_armor_for_level.</summary>
        public static readonly int[] HeavyArmorForLevelTable =
        {
            30, 50, 70, 80, 110, 130, 160, 200, 250, 300,
        };

        /// <summary>heavy_weight_for_level, in tonnes.</summary>
        public static readonly int[] HeavyWeightForLevelTable =
        {
            999, 999, 30, 40, 60, 70, 70, 70, 80, 80,
        };

        /// <summary>One entry of a choose_skill chance table.</summary>
        public struct SkillChance
        {
            public AiSkillLevel Skill;
            public float Chance;

            public SkillChance(AiSkillLevel skill, float chance)
            {
                Skill = skill;
                Chance = chance;
            }
        }

        /// <summary>
        /// skill_to_chance_lowt, in the source's key order. choose_skill keeps
        /// the last entry whose roll passes, so the order is part of the data.
        /// </summary>
        public static readonly SkillChance[] ChanceLowTier =
        {
            new SkillChance(AiSkillLevel.Idle, 0f),
            new SkillChance(AiSkillLevel.WhiteArmy, 45f),
            new SkillChance(AiSkillLevel.GreenArmy, 25f),
            new SkillChance(AiSkillLevel.BlueArmy, 1f),
            new SkillChance(AiSkillLevel.PurpleArmy, 0.1f),
        };

        /// <summary>skill_to_chance_medt, in the source's key order.</summary>
        public static readonly SkillChance[] ChanceMedTier =
        {
            new SkillChance(AiSkillLevel.Idle, 0.01f),
            new SkillChance(AiSkillLevel.BlueArmy, 75f),
            new SkillChance(AiSkillLevel.GreenArmy, 45f),
            new SkillChance(AiSkillLevel.WhiteArmy, 25f),
            new SkillChance(AiSkillLevel.PurpleArmy, 1f),
        };

        /// <summary>skill_to_chance_hight, in the source's key order.</summary>
        public static readonly SkillChance[] ChanceHighTier =
        {
            new SkillChance(AiSkillLevel.Idle, 0f),
            new SkillChance(AiSkillLevel.BlueArmy, 75f),
            new SkillChance(AiSkillLevel.GreenArmy, 65f),
            new SkillChance(AiSkillLevel.PurpleArmy, 25f),
            new SkillChance(AiSkillLevel.WhiteArmy, 10f),
        };

        /// <summary>skill_level_reached.</summary>
        public static bool SkillReached(
            AiSkillLevel skill, AiSkillLevel reference, bool above = false)
        {
            return above ? skill > reference : skill >= reference;
        }

        /// <summary>is_dumb: anything below the green army thinks poorly.</summary>
        public static bool IsDumb(AiSkillLevel skill)
        {
            return !SkillReached(skill, AiSkillLevel.GreenArmy);
        }

        public static int HeavyArmorForLevel(int level)
        {
            return HeavyArmorForLevelTable[Clamp(level) - 1];
        }

        public static int HeavyWeightForLevel(int level)
        {
            return HeavyWeightForLevelTable[Clamp(level) - 1];
        }

        /// <summary>skill_to_reaction_time, before the 0.85..1.15 jitter.</summary>
        public static float BaseReactionTime(AiSkillLevel skill)
        {
            switch (skill)
            {
                case AiSkillLevel.WhiteArmy: return 0.35f;
                case AiSkillLevel.GreenArmy: return 0.33f;
                case AiSkillLevel.BlueArmy: return 0.28f;
                case AiSkillLevel.PurpleArmy: return 0.25f;
                default: return 0.5f;
            }
        }

        /// <summary>skill_to_dmg_temper_penalty.</summary>
        public static float DamageTemperPenalty(AiSkillLevel skill)
        {
            switch (skill)
            {
                case AiSkillLevel.WhiteArmy: return 0.0025f;
                case AiSkillLevel.GreenArmy: return 0.01f;
                case AiSkillLevel.BlueArmy: return 0.0015f;
                default: return 0f;
            }
        }

        /// <summary>The aim_target_lerp_factor activate() writes on the tank.</summary>
        public static float AimTargetLerpFactor(AiSkillLevel skill)
        {
            switch (skill)
            {
                case AiSkillLevel.GreenArmy: return 4f;
                case AiSkillLevel.BlueArmy: return 5f;
                case AiSkillLevel.PurpleArmy: return 8f;
                default: return 3f;
            }
        }

        /// <summary>The disp_shoot_tres of shoot().</summary>
        public static float DispersionShootThreshold(AiSkillLevel skill)
        {
            switch (skill)
            {
                case AiSkillLevel.WhiteArmy: return 2.2f;
                case AiSkillLevel.GreenArmy: return 1.9f;
                case AiSkillLevel.BlueArmy: return 1.75f;
                case AiSkillLevel.PurpleArmy: return 1.2f;
                default: return 1f;
            }
        }

        /// <summary>The health fraction should_notice_health_change compares to.</summary>
        public static float HealthNoticeThreshold(AiSkillLevel skill)
        {
            switch (skill)
            {
                case AiSkillLevel.WhiteArmy: return 0.15f;
                case AiSkillLevel.GreenArmy: return 0.4f;
                case AiSkillLevel.BlueArmy: return 0.75f;
                case AiSkillLevel.PurpleArmy: return 0.95f;
                default: return 0f;
            }
        }

        /// <summary>
        /// choose_skill. The unfair roll is drawn once per battle in the source
        /// (a static), and the caller keeps it here. The dead
        /// <c>elif lvl >= 8</c> branch of the source is preserved: <c>lvl > 5</c>
        /// already answers for every level that could reach it.
        /// </summary>
        public static AiSkillLevel ChooseSkill(
            int level, bool enemyOfPlayer, bool unfairRoll, Func<float, bool> chance)
        {
            if (chance == null)
            {
                throw new ArgumentNullException("chance");
            }

            var isEnemy = enemyOfPlayer && unfairRoll;
            var table = isEnemy ? ChanceHighTier : ChanceLowTier;
            if (!isEnemy)
            {
                if (level > 5)
                {
                    table = ChanceMedTier;
                }
                else if (level >= 8)
                {
                    table = ChanceHighTier;
                }
            }

            var skill = AiSkillLevel.WhiteArmy;
            for (var i = 0; i < table.Length; i++)
            {
                if (chance(table[i].Chance))
                {
                    skill = table[i].Skill;
                }
            }

            return skill;
        }

        /// <summary>Util.random_chance: a percentage against a 0..1 roll.</summary>
        public static bool RandomChance(float chance, float roll)
        {
            return chance * 0.01f > roll;
        }

        private static int Clamp(int level)
        {
            if (level < 1)
            {
                return 1;
            }

            return level > HeavyWeightForLevelTable.Length
                ? HeavyWeightForLevelTable.Length
                : level;
        }
    }
}
