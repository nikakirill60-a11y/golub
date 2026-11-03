using System;

namespace BlitzLegacy.Core.Tanks.Ai
{
    /// <summary>
    /// The parts of ai_controller.gd that are arithmetic rather than scene
    /// queries: which play style a tank fits, how dangerous an enemy is, how a
    /// target scores, which state the tactical update switches to and whether
    /// a jet jump is worth it. The controller keeps the raycasts and the
    /// rosters; everything here takes plain numbers so it can be tested
    /// without a battle, a map or a tank.
    /// </summary>
    public static class AiTactics
    {
        /// <summary>MAP_SECTORS.</summary>
        public const int MapSectors = 16;

        /// <summary>AI_TACTICAL_INTERVAL.</summary>
        public const float TacticalInterval = 2f;

        /// <summary>AI_TACTICAL_PHASE_BUCKETS.</summary>
        public const int TacticalPhaseBuckets = 8;

        /// <summary>DISTANT_MARKER_CACHE_INTERVAL.</summary>
        public const float DistantMarkerCacheInterval = 3f;

        /// <summary>DISTANT_MARKER_MAX_PATH_PROBES.</summary>
        public const int DistantMarkerMaxPathProbes = 4;

        /// <summary>NO_AIMTIME_SHOOT_DIST.</summary>
        public const float NoAimTimeShootDistance = 20f;

        /// <summary>LESS_AIMTIME_SHOOT_DIST.</summary>
        public const float LessAimTimeShootDistance = 75f;

        /// <summary>JUMP_CHASE_DIST.</summary>
        public const float JumpChaseDistance = 130f;

        /// <summary>JUMP_PEEK_DIST.</summary>
        public const float JumpPeekDistance = 90f;

        /// <summary>JUMP_MIN_SPEED_FRAC.</summary>
        public const float JumpMinSpeedFraction = 0.5f;

        /// <summary>The "near" radius get_target_tank_rating scores within.</summary>
        public const float TargetRatingNearDistance = 400f;

        /// <summary>The ratings sample_safe_spots gives a candidate point.</summary>
        public const int SafeSpotVisibleSingleRating = 1;
        public const int SafeSpotNotVisibleRating = 10;
        public const int SafeSpotNotElevatedRating = 5;

        /// <summary>The sampling grid of sample_safe_spots.</summary>
        public const int SafeSpotRows = 8;
        public const int SafeSpotSamplesPerRow = 8;
        public const float SafeSpotRowDistance = 100f;
        public const float SafeSpotArcDegrees = 180f;

        /// <summary>What choose_play_style reads off the tank.</summary>
        public struct PlayStyleInputs
        {
            /// <summary>tank_info.tank_class == Class.TD.</summary>
            public bool ClassIsTd;

            /// <summary>tank_info.tank_class == Class.LT.</summary>
            public bool ClassIsLight;

            /// <summary>tank_info.lvl.</summary>
            public int Level;

            /// <summary>tank_info.health.</summary>
            public float Health;

            /// <summary>The hull's average armour.</summary>
            public float AvgArmor;

            /// <summary>gun_info.shells[0].pen.</summary>
            public float ApPenetration;

            /// <summary>gun_info.shells[0].dmg.</summary>
            public float ApDamage;

            /// <summary>gun_info.deviation_r.x, the left traverse limit.</summary>
            public float DeviationLeft;

            /// <summary>tank_info.speed_limits.x.</summary>
            public float SpeedLimitForward;

            /// <summary>engine_info.power.</summary>
            public float EnginePower;

            /// <summary>mass * 0.001, so tonnes.</summary>
            public float MassTons;

            /// <summary>track_info.rot_speed.</summary>
            public float TrackRotationSpeed;
        }

        /// <summary>
        /// choose_play_style. The source walks a dictionary keyed
        /// GENERIC, HEAVY, SNIPER, LIGHT and keeps the first strict maximum, so
        /// a tie goes to the earlier style and a tank with no points at all
        /// keeps the style it already had.
        /// </summary>
        public static AiPlayStyle ChoosePlayStyle(
            PlayStyleInputs inputs,
            AiSkillLevel skill,
            AiPlayStyle current,
            AiPlayStyle[] blacklist = null)
        {
            var genericPoints = 1f;
            var sniperPoints = 0f;
            var heavyPoints = 0f;
            var lightPoints = 0f;

            var armorToPen = inputs.ApPenetration == 0f
                ? 0f
                : inputs.AvgArmor / inputs.ApPenetration;
            var green = AiSkillTable.SkillReached(skill, AiSkillLevel.GreenArmy);
            var blue = AiSkillTable.SkillReached(skill, AiSkillLevel.BlueArmy);
            var isTd = inputs.ClassIsTd && (armorToPen < 0.25f || !green);

            if (isTd || !green)
            {
                sniperPoints += 0.5f;
            }

            if ((isTd || !blue)
                && inputs.Health != 0f
                && inputs.ApDamage / inputs.Health > 0.35f)
            {
                sniperPoints += 1f;
            }

            if ((isTd || !blue) && Math.Abs(inputs.DeviationLeft) < 180f)
            {
                sniperPoints += 1.5f;
            }

            if (inputs.ClassIsLight)
            {
                lightPoints += 0.5f;
            }

            if (inputs.SpeedLimitForward > 60f && green)
            {
                lightPoints += 1f;
                if (inputs.MassTons != 0f && inputs.EnginePower / inputs.MassTons >= 20f)
                {
                    lightPoints += 0.5f;
                }

                if (inputs.TrackRotationSpeed >= 38f)
                {
                    lightPoints += 0.5f;
                }
            }

            if (inputs.MassTons > AiSkillTable.HeavyWeightForLevel(inputs.Level))
            {
                heavyPoints += 1f;
                lightPoints -= 1f;
            }

            if (armorToPen >= 0.5f)
            {
                heavyPoints += 3f;
            }

            if (inputs.SpeedLimitForward < 35f)
            {
                heavyPoints += 3f;
            }

            var styles = new[]
            {
                AiPlayStyle.Generic, AiPlayStyle.Heavy, AiPlayStyle.Sniper, AiPlayStyle.Light,
            };
            var points = new[] { genericPoints, heavyPoints, sniperPoints, lightPoints };

            var best = 0f;
            var chosen = current;
            for (var i = 0; i < styles.Length; i++)
            {
                if (Blacklisted(blacklist, styles[i]))
                {
                    continue;
                }

                if (points[i] > best)
                {
                    best = points[i];
                    chosen = styles[i];
                }
            }

            return chosen;
        }

        /// <summary>What get_tank_danger_level compares between two tanks.</summary>
        public struct DangerInputs
        {
            /// <summary>their lvl - my lvl.</summary>
            public int LevelDifference;

            /// <summary>their health / my health.</summary>
            public float HealthCoeff;

            /// <summary>their shell damage / my health.</summary>
            public float DamageToMyHealth;

            /// <summary>my shell damage / their health.</summary>
            public float MyDamageToTheirHealth;

            /// <summary>their damage per minute / my health.</summary>
            public float DpmToMyHealth;

            /// <summary>my damage per minute / their health.</summary>
            public float MyDpmToTheirHealth;

            /// <summary>their penetration / my average armour, 0 when I have none.</summary>
            public float PenetrationToMyArmor;

            /// <summary>my penetration / their average armour, 0 when they have none.</summary>
            public float MyPenetrationToTheirArmor;
        }

        /// <summary>
        /// get_tank_danger_level. The source's second level branch reads
        /// <c>elif lvl_diff &lt;= 1</c>, which the first branch already covers at
        /// 1, so it only ever fires at zero or below; the port keeps it as
        /// written.
        /// </summary>
        public static int DangerLevel(DangerInputs inputs)
        {
            var danger = 1;

            if (inputs.LevelDifference >= 1)
            {
                danger += 1;
            }
            else if (inputs.LevelDifference <= 1)
            {
                danger -= 1;
            }

            if (inputs.HealthCoeff >= 2f)
            {
                danger += 4;
            }
            else if (inputs.HealthCoeff <= 0.5f)
            {
                danger -= 2;
            }

            if (inputs.DamageToMyHealth >= 0.33f)
            {
                danger += 2;
            }

            if (inputs.MyDamageToTheirHealth >= 0.33f)
            {
                danger -= 2;
            }

            if (inputs.DpmToMyHealth >= 2f)
            {
                danger += 2;
            }

            if (inputs.MyDpmToTheirHealth >= 2f)
            {
                danger -= 2;
            }

            if (inputs.DamageToMyHealth >= 0.8f)
            {
                danger += 5;
            }

            if (inputs.MyDamageToTheirHealth >= 0.8f)
            {
                danger -= 5;
            }

            if (inputs.PenetrationToMyArmor >= 7f)
            {
                danger += 3;
            }

            if (inputs.MyPenetrationToTheirArmor >= 7f)
            {
                danger -= 3;
            }

            return danger;
        }

        /// <summary>What get_target_tank_rating weighs for one candidate.</summary>
        public struct TargetRatingInputs
        {
            /// <summary>should_notice_health_change and a damage record exist.</summary>
            public bool NoticedDamage;

            /// <summary>The hardest hitter so far is this candidate.</summary>
            public bool IsWorstAttacker;

            /// <summary>That attacker's damage over my maximum health.</summary>
            public float WorstAttackerShare;

            /// <summary>skill_to_dmg_temper_penalty for my skill.</summary>
            public float DamageTemperPenalty;

            /// <summary>skip_vis, or a visibility trace that came back clear.</summary>
            public bool Visible;

            /// <summary>The skip_vis argument itself.</summary>
            public bool SkipVisibility;

            /// <summary>my lvl - their lvl, counted only when they are lower.</summary>
            public int LevelAdvantage;

            /// <summary>skill >= GREEN_ARMY.</summary>
            public bool SkillAtLeastGreen;

            /// <summary>their health / my health.</summary>
            public float HealthCoeff;

            /// <summary>is_tank_looking_at_me.</summary>
            public bool LookingAtMe;

            /// <summary>The squared distance between the two hulls.</summary>
            public float DistanceSquared;

            /// <summary>ally_to_enemy_near_coeff(400).</summary>
            public float AllyToEnemyNear;

            /// <summary>get_tank_danger_level for the candidate.</summary>
            public int DangerLevel;
        }

        /// <summary>get_target_tank_rating.</summary>
        public static float TargetRating(TargetRatingInputs inputs)
        {
            var rating = 0f;
            var near = TargetRatingNearDistance;

            if (inputs.NoticedDamage
                && inputs.IsWorstAttacker
                && inputs.WorstAttackerShare > 0.25f)
            {
                rating += inputs.DamageTemperPenalty * 1000f;
            }

            var seen = false;
            if (inputs.Visible)
            {
                seen = true;
                rating += 8f;
                if (inputs.LevelAdvantage > 0)
                {
                    rating += 2f * inputs.LevelAdvantage;
                }
            }

            if (inputs.SkillAtLeastGreen)
            {
                if (inputs.HealthCoeff > 1f)
                {
                    rating += 4f;
                }
                else if ((inputs.HealthCoeff < 1f && !inputs.SkipVisibility)
                    || (inputs.HealthCoeff < 1f && inputs.SkipVisibility && seen))
                {
                    rating -= 1f;
                }
            }

            if (inputs.LookingAtMe)
            {
                rating += 4f;
            }
            else
            {
                rating -= 4f;
            }

            if (inputs.AllyToEnemyNear < 1f && inputs.DistanceSquared >= near * near)
            {
                rating -= 10f;
            }
            else
            {
                if (inputs.DistanceSquared <= near * near)
                {
                    rating += 3f;
                }

                if (inputs.DistanceSquared <= 20f * 20f)
                {
                    rating += 7f;
                }
            }

            if (inputs.DangerLevel < -3)
            {
                rating += 8f;
            }

            return rating;
        }

        /// <summary>
        /// The one-off bonus try_switching_to_relevant_state adds to the
        /// distant state the first time it runs for a tank.
        /// </summary>
        public static int FirstRunDistantPoints(
            bool sniper,
            bool dumb,
            bool heavy,
            float armorCoeff,
            int level,
            float speedLimitForward)
        {
            var points = 0;
            if (sniper || dumb)
            {
                points += 10;
            }

            if (armorCoeff < 15f && level > 5)
            {
                points += 1;
            }

            if ((!heavy && speedLimitForward < 40f) || speedLimitForward > 60f)
            {
                points += 1;
            }

            return points;
        }

        /// <summary>What try_switching_to_relevant_state scores a state with.</summary>
        public struct StateInputs
        {
            public bool Heavy;
            public bool Light;
            public int InitialCareful;
            public int InitialBrawl;
            public int InitialDistant;
            public int InitialSingle;
            public int InitialRollout;

            /// <summary>health_coeff().</summary>
            public float HealthCoeff;

            /// <summary>get_tanks_looking_at_me().</summary>
            public int TanksLooking;

            /// <summary>ally_to_enemy_near_coeff(150).</summary>
            public float AllyToEnemy;

            /// <summary>get_enemies_danger_level(250).</summary>
            public int EnemiesDanger;

            /// <summary>get_tanks_around_pos_team(pos(), 150, true).</summary>
            public int EnemiesNear;

            /// <summary>Battle.get_time().</summary>
            public float BattleTime;

            /// <summary>get_distant_marker() != null.</summary>
            public bool HasDistantMarker;
        }

        /// <summary>
        /// The scoring half of try_switching_to_relevant_state. The caller
        /// still handles the "no spotted enemies means rollout" shortcut,
        /// because that one returns before any points are counted.
        ///
        /// Util.get_best_option sorts the points and takes the highest, and the
        /// source's dictionary literal lets a later entry overwrite an equal
        /// earlier key, so a tie goes to the state written last: rollout,
        /// careful, brawl, distant, single.
        /// </summary>
        public static AiStateKind ChooseState(StateInputs inputs)
        {
            var careful = inputs.InitialCareful;
            var brawl = inputs.InitialBrawl;
            var distant = inputs.InitialDistant;
            var single = inputs.InitialSingle;
            var rollout = inputs.InitialRollout;

            if (inputs.Heavy)
            {
                brawl += 4;
                careful -= 1;
                if (inputs.HealthCoeff > 0.5f)
                {
                    brawl += 1;
                }
            }

            var lookingTanksToRetreat = inputs.Heavy ? 4 : 2;

            // The source declares this threshold an int and assigns 0.5 to it,
            // which truncates: a tank that is not heavy never retreats on the
            // ally-to-enemy count alone. Ported as written.
            var allyToEnemyToRetreat = inputs.Heavy ? 2 : 0;
            var enemiesDangerToRetreat = inputs.Heavy ? 3 : 2;

            if (!inputs.Light)
            {
                if (inputs.TanksLooking >= lookingTanksToRetreat)
                {
                    careful += 1;
                }

                if (inputs.AllyToEnemy < allyToEnemyToRetreat)
                {
                    careful += 2;
                }

                if (inputs.EnemiesDanger > enemiesDangerToRetreat)
                {
                    careful += 2;
                }

                if (inputs.HealthCoeff < 0.5f)
                {
                    careful += 5;
                }

                if (inputs.EnemiesNear > 3)
                {
                    careful += 3;
                }
            }
            else
            {
                rollout += 8;
            }

            if (inputs.BattleTime < 50f && inputs.EnemiesNear < 1)
            {
                rollout += 5;
            }

            if (!inputs.HasDistantMarker)
            {
                distant -= 1000;
            }

            if (inputs.AllyToEnemy >= 1.5f && inputs.EnemiesNear < 3)
            {
                single += 2;
            }

            if (inputs.EnemiesDanger <= 1)
            {
                single += 1;
            }

            var best = AiStateKind.Rollout;
            var bestPoints = rollout;
            if (careful >= bestPoints)
            {
                bestPoints = careful;
                best = AiStateKind.Careful;
            }

            if (brawl >= bestPoints)
            {
                bestPoints = brawl;
                best = AiStateKind.Brawl;
            }

            if (distant >= bestPoints)
            {
                bestPoints = distant;
                best = AiStateKind.Distant;
            }

            if (single >= bestPoints)
            {
                best = AiStateKind.Single;
            }

            return best;
        }

        /// <summary>decide_jet_jump, kept for the gravity mode.</summary>
        public static AiJumpReason DecideJetJump(
            float distance,
            float speedFraction,
            bool facingTarget,
            bool targetVisible,
            bool retreating)
        {
            if (retreating)
            {
                return speedFraction >= JumpMinSpeedFraction
                    ? AiJumpReason.Escape
                    : AiJumpReason.None;
            }

            if (distance > JumpChaseDistance
                && facingTarget
                && speedFraction >= JumpMinSpeedFraction)
            {
                return AiJumpReason.Chase;
            }

            if (!targetVisible && distance < JumpPeekDistance)
            {
                return AiJumpReason.Peek;
            }

            return AiJumpReason.None;
        }

        /// <summary>
        /// marker_type_to_chance. The source declares it next to
        /// get_best_marker, which now picks a random vacant marker instead, so
        /// this is carried over for parity and is not read by the port either.
        /// </summary>
        public static float MarkerTypeChance(int markerType)
        {
            switch (markerType)
            {
                case 0: return 1f;
                case 1: return 0.5f;
                case 2: return 0.25f;
                default: return 0f;
            }
        }

        private static bool Blacklisted(AiPlayStyle[] blacklist, AiPlayStyle style)
        {
            if (blacklist == null)
            {
                return false;
            }

            for (var i = 0; i < blacklist.Length; i++)
            {
                if (blacklist[i] == style)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
