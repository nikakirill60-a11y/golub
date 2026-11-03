using System;
using BlitzLegacy.Core.Tanks;

namespace BlitzLegacy.Core.Battle
{
    /// <summary>
    /// Online team-damage record of one account (the "blue" team-killer
    /// mechanic of World of Tanks Blitz). Persisted with the account.
    /// Times are Unix seconds.
    /// </summary>
    public sealed class TeamKillRecord
    {
        /// <summary>Team damage since the last offence, towards <see cref="TeamKillPolicy.BlueThreshold"/>.</summary>
        public int Pending;
        /// <summary>All-time team damage.</summary>
        public int Total;
        /// <summary>How many times the account turned blue.</summary>
        public int Offences;
        /// <summary>End of the current blue status; ignored while <see cref="Permanent"/>.</summary>
        public double BlueUntil;
        public bool Permanent;
        /// <summary>End of the strict week: more than 200 team damage in one battle is a kick.</summary>
        public double WatchUntil;

        public bool IsBlue(double now) { return Permanent || BlueUntil > now; }
        public bool Watched(double now) { return WatchUntil > now; }

        public TeamKillRecord Clone()
        {
            return (TeamKillRecord)MemberwiseClone();
        }
    }

    /// <summary>
    /// Pure rules of online friendly fire and of the blue team-killer status.
    /// The host applies the damage factors and counts team damage; each peer
    /// folds its own battle total into its account after the battle.
    /// </summary>
    public static class TeamKillPolicy
    {
        /// <summary>Online: shells, splash, fire and abilities hurt a team-mate four times less.</summary>
        public const float FriendlyFireFactor = 0.25f;
        /// <summary>Online: ramming a team-mate deals a tenth of the normal damage.</summary>
        public const float FriendlyRamFactor = 0.1f;
        /// <summary>Accumulated team damage that turns the account blue.</summary>
        public const int BlueThreshold = 1500;
        /// <summary>All-time team damage after which the strict week starts.</summary>
        public const int WatchTotal = 4000;
        /// <summary>During the strict week more team damage than this in one battle is a kick.</summary>
        public const int KickBattleDamage = 200;
        public const double Day = 24 * 60 * 60;
        public const double WatchSeconds = 7 * Day;

        /// <summary>
        /// Blue duration of the n-th offence: 2 days, then a week, then a
        /// month; after that the status is permanent (<c>PositiveInfinity</c>).
        /// </summary>
        public static double BlueSeconds(int offence)
        {
            switch (offence)
            {
                case 1: return 2 * Day;
                case 2: return 7 * Day;
                case 3: return 30 * Day;
                default: return offence <= 0 ? 0 : double.PositiveInfinity;
            }
        }

        /// <summary>Damage one team-mate deals to another, after the online cut.</summary>
        public static int FriendlyDamage(int damage, DamageType type, bool online)
        {
            if (!online || damage <= 0) { return damage; }
            if (type == DamageType.Environment || type == DamageType.Drowning) { return damage; }
            var factor = type == DamageType.Collision ? FriendlyRamFactor : FriendlyFireFactor;
            return (int)Math.Round(damage * factor, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Whether damage dealt counts as team damage: a team-mate other than
        /// yourself who is not blue already. Damage to a blue player is free.
        /// </summary>
        public static bool Counts(bool sameTeam, bool self, bool victimBlue)
        {
            return sameTeam && !self && !victimBlue;
        }

        /// <summary>Blue in this battle: already blue, or the pending damage crossed the threshold.</summary>
        public static bool BlueInBattle(bool blueAtStart, int pending, int battleDamage)
        {
            return blueAtStart || (long)pending + battleDamage >= BlueThreshold;
        }

        /// <summary>Strict week: more than <see cref="KickBattleDamage"/> in one battle is a kick.</summary>
        public static bool ShouldKick(bool watched, int battleDamage)
        {
            return watched && battleDamage > KickBattleDamage;
        }

        /// <summary>
        /// Folds one battle's team damage into the record. Every time the
        /// pending damage crosses the threshold is one more offence, and each
        /// offence escalates the blue status from now. Returns true when the
        /// record changed.
        /// </summary>
        public static bool ApplyBattle(TeamKillRecord record, int battleDamage, double now)
        {
            if (record == null || battleDamage <= 0) { return false; }
            record.Total = (int)Math.Min(int.MaxValue, (long)record.Total + battleDamage);
            record.Pending = (int)Math.Min(int.MaxValue, (long)record.Pending + battleDamage);
            while (record.Pending >= BlueThreshold)
            {
                record.Pending -= BlueThreshold;
                record.Offences++;
                var seconds = BlueSeconds(record.Offences);
                if (double.IsPositiveInfinity(seconds)) { record.Permanent = true; }
                else { record.BlueUntil = Math.Max(record.BlueUntil, now + seconds); }
            }
            if (record.Total >= WatchTotal)
            {
                record.WatchUntil = Math.Max(record.WatchUntil, now + WatchSeconds);
            }
            return true;
        }

        public static double Now()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        }
    }
}
