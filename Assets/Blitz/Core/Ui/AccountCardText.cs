using System;
using System.Globalization;

namespace BlitzLegacy.Core.Ui
{
    /// <summary>
    /// Text formatting for the login account card, ported from account.tscn's
    /// setup(). Pure, so the exact strings are pinned by tests instead of
    /// drifting from the Godot original.
    /// </summary>
    public static class AccountCardText
    {
        /// <summary>Godot: str(account.battle_results.size()).</summary>
        public static string Battles(int battles)
        {
            return battles.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Godot: str(Util.round_to_dec(account.get_wr(), 1)) + "%".
        /// round_to_dec is round(x * 10) / 10, and GDScript's str() always
        /// prints a float with at least one decimal, so 65 reads "65.0".
        /// </summary>
        public static string WinRate(float percent)
        {
            var rounded = Math.Round(percent * 10d, MidpointRounding.AwayFromZero) / 10d;
            return rounded.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>
        /// Godot: str(account.get_time_played()) + "h", where get_time_played()
        /// is int(time_ingame * sec_to_hour), i.e. whole hours, truncated.
        /// </summary>
        public static string TimePlayed(TimeSpan played)
        {
            var hours = (int)played.TotalHours;
            if (hours < 0)
            {
                hours = 0;
            }

            return hours.ToString(CultureInfo.InvariantCulture) + "h";
        }
    }
}
