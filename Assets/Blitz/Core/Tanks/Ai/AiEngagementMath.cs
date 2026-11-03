using System;

namespace BlitzLegacy.Core.Tanks.Ai
{
    /// <summary>
    /// What one side brings to a gunfight, reduced to the numbers that decide
    /// it: how much it has left, how hard and how often it hits, and whether
    /// its next shell is already in the breech.
    /// </summary>
    public struct AiCombatant
    {
        /// <summary>Hit points left.</summary>
        public float Health;

        /// <summary>Damage of one shell that lands and penetrates.</summary>
        public float Alpha;

        /// <summary>Sustained seconds per shell, magazine cycle included.</summary>
        public float SecondsPerShot;

        /// <summary>Seconds until the next shell is ready; 0 when loaded.</summary>
        public float ReloadRemaining;

        /// <summary>Seconds the first aimed shot needs to settle.</summary>
        public float AimSeconds;

        /// <summary>Chance a shell lands on the other hull, 0..1.</summary>
        public float HitChance;

        /// <summary>Chance a landed shell gets through the armour, 0..1.</summary>
        public float PenetrationChance;

        /// <summary>Share of the alpha a shell still deals when it fails to penetrate.</summary>
        public float NonPenetrationShare;

        /// <summary>A Sandbox HARMLESS hull never pulls the trigger.</summary>
        public bool Harmless;
    }

    /// <summary>The engagements the bot layers over the ported states.</summary>
    public enum AiEngagement
    {
        None = 0,
        Duel = 1,
        Withdraw = 2,
        Chase = 3,
    }

    /// <summary>What a bot knows when it decides how to fight.</summary>
    public struct AiEngagementInputs
    {
        public AiEngagement Current;

        /// <summary>Seconds spent in <see cref="Current"/>.</summary>
        public float CurrentFor;

        /// <summary>False for a hull that may not drive (BOTS HOLD POSITION).</summary>
        public bool Mobile;

        /// <summary>The ATGM controller owns the movement of a rocket carrier.</summary>
        public bool Atgm;

        /// <summary>Green army and better weighs the whole picture.</summary>
        public bool Smart;

        /// <summary>A valid, spotted target is chosen.</summary>
        public bool HasTarget;

        /// <summary>Chance to win the one on one against the target.</summary>
        public float DuelOdds;

        /// <summary>Chance the local fight goes our way, allies included.</summary>
        public float ForceOdds;

        /// <summary>Enemies that can shoot at the bot right now.</summary>
        public int EnemiesEngaging;

        public float TargetDistance;

        /// <summary>How far the play style commits to a one on one.</summary>
        public float DuelRange;

        public float HealthFraction;

        /// <summary>A lost target is still worth hunting.</summary>
        public bool QuarryFresh;

        public float QuarryOdds;
        public float QuarryDistance;

        /// <summary>Shells arrive from a hull nobody on the team can see.</summary>
        public bool HitByUnseen;

        /// <summary>A withdrawn bot keeps hiding a while after the contact ends.</summary>
        public bool HoldingAfterWithdraw;
    }

    /// <summary>What makes one target better to shoot at than another.</summary>
    public struct AiTargetPriorityInputs
    {
        /// <summary>The bot already fights this target.</summary>
        public bool Current;

        /// <summary>Shells of the bot's alpha the target can still take.</summary>
        public float ShotsToKill;

        /// <summary>The bot's chance to penetrate it.</summary>
        public float PenetrationChance;

        /// <summary>Its reload left in seconds; negative when unknown.</summary>
        public float ReloadRemaining;

        /// <summary>Its damage output on the bot against the worst threat, 0..1.</summary>
        public float ThreatShare;

        public float Distance;
    }

    /// <summary>
    /// The arithmetic behind the bot's fight-or-flight judgement. It is kept
    /// free of the engine so the decisions can be locked by EditMode tests.
    /// A fight is judged the way a player sizes one up: how long each side
    /// needs to finish the other (alpha, reload, accuracy, penetration and
    /// whose gun is loaded), and, for a crowd, Lanchester's square law, where
    /// numbers count twice because every hull both shoots and soaks.
    /// </summary>
    public static class AiEngagementMath
    {
        /// <summary>No gun cycles faster than this, whatever a Sandbox slider says.</summary>
        public const float MinSecondsPerShot = 0.1f;

        /// <summary>Reaction slack added to both kill times so equal guns tie.</summary>
        public const float DuelTimeBias = 0.5f;

        /// <summary>How sharply a kill-time lead turns into winning odds.</summary>
        public const float DuelSharpness = 2.5f;

        public const float OddsFloor = 0.02f;
        public const float OddsCeiling = 0.98f;

        public const float DuelEnterOdds = 0.45f;
        public const float DuelStayOdds = 0.33f;
        public const int DuelMaxEngaging = 2;

        /// <summary>A crowd that is clearly losing still lets a strong bot duel.</summary>
        public const float DuelCrowdForceOdds = 0.7f;

        public const float WithdrawForceEnter = 0.36f;
        public const float WithdrawForceExit = 0.5f;
        public const float WithdrawDuelEnter = 0.28f;
        public const float WithdrawDuelExit = 0.42f;
        public const int WithdrawCrowd = 3;
        public const float PanicHealth = 0.25f;

        public const float ChaseOdds = 0.55f;
        public const float ChaseForceOdds = 0.45f;

        /// <summary>A fresh duel or withdrawal is not re-judged inside this window.</summary>
        public const float MinDwellSeconds = 2.5f;

        /// <summary>The front-plate angle against a gun that may or may not get through.</summary>
        public const float AngledFront = 25f;

        /// <summary>Nearly square-on against a gun that always or never penetrates.</summary>
        public const float SquareFront = 8f;

        public static float Clamp01(float value)
        {
            return value < 0f ? 0f : (value > 1f ? 1f : value);
        }

        private static float Clamp(float value, float min, float max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        /// <summary>
        /// Sustained seconds per shell. A magazine spreads its reload over the
        /// clip; an auto-reloader is as fast as its average round timer.
        /// </summary>
        public static float SecondsPerShot(
            float reload, int clipShells, float intraClip, float autoReloadAverage)
        {
            if (autoReloadAverage > 0f)
            {
                return Math.Max(MinSecondsPerShot, autoReloadAverage);
            }

            if (clipShells <= 1)
            {
                return Math.Max(MinSecondsPerShot, reload);
            }

            var cycle = reload + ((clipShells - 1) * Math.Max(0f, intraClip));
            return Math.Max(MinSecondsPerShot, cycle / clipShells);
        }

        /// <summary>
        /// The share of shells a gun lands on a hull of the given radius. The
        /// aim circle radius is <paramref name="dispersion"/> metres at 100 m
        /// and the shells fall as a 2D Gaussian with the circle at two sigma.
        /// </summary>
        public static float HitChance(float dispersion, float distance, float targetRadius)
        {
            if (targetRadius <= 0f)
            {
                return 0.03f;
            }

            var circle = Math.Max(0.01f, dispersion * Math.Max(0f, distance) / 100f);
            var ratio = targetRadius / circle;
            var chance = 1f - (float)Math.Exp(-2f * ratio * ratio);
            return Clamp(chance, 0.03f, 0.99f);
        }

        /// <summary>
        /// Penetration odds against an averaged plate: armour is uneven and
        /// angled, so the transition is spread from 0.7x to 1.5x the armour.
        /// </summary>
        public static float PenetrationChance(float penetration, float armor)
        {
            if (armor <= 0f)
            {
                return 0.97f;
            }

            var ratio = penetration / armor;
            return Clamp((ratio - 0.7f) / 0.8f, 0.03f, 0.97f);
        }

        /// <summary>Expected damage of one shell fired.</summary>
        public static float ExpectedDamagePerShot(AiCombatant c)
        {
            if (c.Harmless || c.Alpha <= 0f)
            {
                return 0f;
            }

            var pen = Clamp01(c.PenetrationChance);
            var landed = pen + ((1f - pen) * Clamp01(c.NonPenetrationShare));
            return c.Alpha * Clamp01(c.HitChance) * landed;
        }

        /// <summary>Expected damage per second.</summary>
        public static float ExpectedDps(AiCombatant c)
        {
            return ExpectedDamagePerShot(c) / Math.Max(MinSecondsPerShot, c.SecondsPerShot);
        }

        /// <summary>
        /// Seconds <paramref name="attacker"/> needs to strip
        /// <paramref name="defenderHealth"/>: the shells of full alpha it takes,
        /// stretched by the misses and bounces, fired from the current reload.
        /// </summary>
        public static float TimeToKill(AiCombatant attacker, float defenderHealth)
        {
            if (defenderHealth <= 0f)
            {
                return 0f;
            }

            var perShot = ExpectedDamagePerShot(attacker);
            if (perShot <= 0.0001f)
            {
                return float.PositiveInfinity;
            }

            var success = perShot / attacker.Alpha;
            var needed = (float)Math.Ceiling(defenderHealth / attacker.Alpha);
            var shots = Math.Max(1f, needed / success);
            return Math.Max(0f, attacker.AimSeconds)
                + Math.Max(0f, attacker.ReloadRemaining)
                + ((shots - 1f) * Math.Max(MinSecondsPerShot, attacker.SecondsPerShot));
        }

        /// <summary>Shells of the attacker's alpha the defender can still take.</summary>
        public static float ShotsToKill(float alpha, float defenderHealth)
        {
            if (alpha <= 0f)
            {
                return float.PositiveInfinity;
            }

            return Math.Max(1f, (float)Math.Ceiling(defenderHealth / alpha));
        }

        /// <summary>
        /// The chance to win a one on one from both kill times: equal times
        /// tie at one half, and a lead counts more the larger it gets.
        /// </summary>
        public static float DuelOdds(float myTimeToKill, float theirTimeToKill)
        {
            var mineInfinite = float.IsInfinity(myTimeToKill) || float.IsNaN(myTimeToKill);
            var theirsInfinite = float.IsInfinity(theirTimeToKill) || float.IsNaN(theirTimeToKill);
            if (mineInfinite && theirsInfinite)
            {
                return 0.5f;
            }

            if (mineInfinite)
            {
                return OddsFloor;
            }

            if (theirsInfinite)
            {
                return OddsCeiling;
            }

            var ratio = (Math.Max(0f, myTimeToKill) + DuelTimeBias)
                / (Math.Max(0f, theirTimeToKill) + DuelTimeBias);
            var odds = 1f / (1f + (float)Math.Pow(ratio, DuelSharpness));
            return Clamp(odds, OddsFloor, OddsCeiling);
        }

        /// <summary>The odds of one combatant against another.</summary>
        public static float DuelOdds(AiCombatant me, AiCombatant them)
        {
            return DuelOdds(TimeToKill(me, them.Health), TimeToKill(them, me.Health));
        }

        /// <summary>
        /// Lanchester's square law: a side's strength is its summed damage
        /// output times its summed health.
        /// </summary>
        public static float ForceOdds(float ourDps, float ourHealth, float theirDps, float theirHealth)
        {
            var ours = Math.Max(0f, ourDps) * Math.Max(0f, ourHealth);
            var theirs = Math.Max(0f, theirDps) * Math.Max(0f, theirHealth);
            if (ours + theirs <= 0.0001f)
            {
                return 0.5f;
            }

            return Clamp(ours / (ours + theirs), OddsFloor, OddsCeiling);
        }

        /// <summary>
        /// How far the bot turns its hull off the enemy: angled against a gun
        /// that sits on the edge of penetrating, square-on when angling cannot
        /// change the outcome and would only show more side.
        /// </summary>
        public static float FrontalAngle(float enemyPenetrationChance)
        {
            return enemyPenetrationChance >= 0.2f && enemyPenetrationChance <= 0.9f
                ? AngledFront
                : SquareFront;
        }

        /// <summary>The range at which the play style commits to a one on one.</summary>
        public static float DuelRange(AiPlayStyle style)
        {
            switch (style)
            {
                case AiPlayStyle.Heavy: return 220f;
                case AiPlayStyle.Light: return 240f;
                case AiPlayStyle.Sniper: return 170f;
                default: return 260f;
            }
        }

        /// <summary>
        /// The perception beat: a sharper crew re-reads the battlefield more
        /// often. The beat only weighs numbers and a handful of cached sight
        /// lines, so even the fastest one is cheap.
        /// </summary>
        public static float PerceptionInterval(AiSkillLevel skill)
        {
            switch (skill)
            {
                case AiSkillLevel.PurpleArmy: return 0.3f;
                case AiSkillLevel.BlueArmy: return 0.4f;
                case AiSkillLevel.GreenArmy: return 0.5f;
                default: return 0.6f;
            }
        }

        /// <summary>
        /// The extra target rating of a smart bot: finish what it can finish,
        /// punish a reload, answer the gun that hurts it most, keep the target
        /// it already fights and leave alone what it cannot penetrate.
        /// </summary>
        public static float TargetPriorityBonus(AiTargetPriorityInputs inputs)
        {
            var bonus = 0f;
            if (inputs.Current)
            {
                bonus += 3f;
            }

            if (inputs.ShotsToKill <= 1f)
            {
                bonus += 6f;
            }
            else if (inputs.ShotsToKill <= 2f)
            {
                bonus += 2.5f;
            }

            if (inputs.PenetrationChance < 0.15f)
            {
                bonus -= 6f;
            }
            else if (inputs.PenetrationChance < 0.4f)
            {
                bonus -= 2f;
            }

            if (inputs.ReloadRemaining > 2.5f && inputs.Distance < 250f)
            {
                bonus += 2f;
            }

            return bonus + (3f * Clamp01(inputs.ThreatShare));
        }

        /// <summary>
        /// Duel, withdraw, chase or leave the fight to the ported states. The
        /// thresholds to leave an engagement are looser than to enter it, so a
        /// bot does not flap between pushing and running on a close call.
        /// </summary>
        public static AiEngagement ChooseEngagement(AiEngagementInputs inputs)
        {
            if (!inputs.Mobile)
            {
                return AiEngagement.None;
            }

            var chosen = Judge(inputs);
            var committed = inputs.Current == AiEngagement.Duel
                || inputs.Current == AiEngagement.Withdraw;
            if (committed
                && chosen != inputs.Current
                && chosen != AiEngagement.Withdraw
                && inputs.HasTarget
                && inputs.CurrentFor < MinDwellSeconds)
            {
                return inputs.Current;
            }

            return chosen;
        }

        private static AiEngagement Judge(AiEngagementInputs inputs)
        {
            if (ShouldWithdraw(inputs))
            {
                return AiEngagement.Withdraw;
            }

            if (inputs.Atgm)
            {
                return AiEngagement.None;
            }

            if (inputs.HasTarget)
            {
                var dueling = inputs.Current == AiEngagement.Duel;
                var odds = dueling ? DuelStayOdds : DuelEnterOdds;
                var range = dueling ? inputs.DuelRange * 1.25f : inputs.DuelRange;
                var crowdOk = inputs.EnemiesEngaging <= DuelMaxEngaging
                    || inputs.ForceOdds >= DuelCrowdForceOdds;
                if (!inputs.Smart)
                {
                    odds += 0.05f;
                }

                return inputs.DuelOdds >= odds && inputs.TargetDistance <= range && crowdOk
                    ? AiEngagement.Duel
                    : AiEngagement.None;
            }

            if (inputs.Smart
                && inputs.QuarryFresh
                && inputs.QuarryOdds >= ChaseOdds
                && inputs.ForceOdds >= ChaseForceOdds
                && inputs.QuarryDistance <= inputs.DuelRange)
            {
                return AiEngagement.Chase;
            }

            return AiEngagement.None;
        }

        private static bool ShouldWithdraw(AiEngagementInputs inputs)
        {
            if (!inputs.HasTarget)
            {
                // Nobody to shoot back at: keep the cover a withdrawal found,
                // and get out of the line of an unseen gun.
                return (inputs.Current == AiEngagement.Withdraw && inputs.HoldingAfterWithdraw)
                    || (inputs.Smart && inputs.HitByUnseen);
            }

            bool withdraw;
            if (inputs.Current == AiEngagement.Withdraw)
            {
                withdraw = inputs.ForceOdds < WithdrawForceExit
                    || (inputs.DuelOdds < WithdrawDuelExit && inputs.EnemiesEngaging >= 1);
            }
            else
            {
                withdraw = inputs.ForceOdds < WithdrawForceEnter
                    || (inputs.DuelOdds < WithdrawDuelEnter && inputs.EnemiesEngaging >= 1)
                    || (inputs.EnemiesEngaging >= WithdrawCrowd && inputs.ForceOdds < WithdrawForceExit)
                    || (inputs.HealthFraction < PanicHealth
                        && inputs.EnemiesEngaging >= 1
                        && inputs.DuelOdds < 0.6f);
            }

            if (!inputs.Smart)
            {
                // A white army crew only notices a fight it is plainly losing.
                withdraw = withdraw && (inputs.ForceOdds < 0.3f || inputs.HealthFraction < 0.3f);
            }

            return withdraw;
        }
    }
}