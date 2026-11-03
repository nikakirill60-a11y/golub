using System;

namespace BlitzLegacy.Core.Hangar
{
    /// <summary>Hangar environments the client can show behind the tank.</summary>
    public enum HangarKind
    {
        /// <summary>4.8 <c>hangar</c>, shown to accounts without premium.</summary>
        Regular,

        /// <summary>4.8 <c>hangar_prem_2018</c>, shown to premium accounts.</summary>
        Premium,

        /// <summary>11.21 <c>hangar_halloween_2026</c>, seasonal and shown to everyone.</summary>
        Halloween2026,

        /// <summary>
        /// <c>hangar_miku_void</c>, "Miku hangar in the void" (Lesta f0 mod scene); only on the player's choice.
        /// </summary>
        Miku,
    }

    /// <summary>
    /// Decides which hangar is shown. A seasonal hangar replaces the account-based selection for every
    /// account while its window is open; outside the window the premium state decides, exactly as before.
    /// Windows are whole calendar days on the device's local clock, both ends inclusive, so the switch
    /// happens at local midnight.
    /// </summary>
    public static class HangarSchedule
    {
        /// <summary>First local day of the Halloween 2026 hangar.</summary>
        public static readonly DateTime Halloween2026FirstDay = new DateTime(2026, 10, 29);

        /// <summary>Last local day (inclusive) of the Halloween 2026 hangar.</summary>
        public static readonly DateTime Halloween2026LastDay = new DateTime(2026, 11, 13);

        /// <summary>Whether the given local wall-clock time falls inside the Halloween 2026 window.</summary>
        public static bool IsHalloween2026(DateTime localTime)
        {
            var day = localTime.Date;
            return day >= Halloween2026FirstDay && day <= Halloween2026LastDay;
        }

        /// <summary>
        /// The hangar to show at the given local wall-clock time. <paramref name="seasonalDrawable"/> tells
        /// whether this device can draw the seasonal hangar at all: where it cannot (a headless player such as
        /// the dedicated server, or a GPU without its shaders) the account hangar stays up through the window.
        /// </summary>
        public static HangarKind Resolve(DateTime localTime, bool premium, bool seasonalDrawable = true)
        {
            if (seasonalDrawable && IsHalloween2026(localTime))
            {
                return HangarKind.Halloween2026;
            }

            return premium ? HangarKind.Premium : HangarKind.Regular;
        }

        /// <summary>The hangar to show at a unix time, judged on the calendar of <paramref name="zone"/>.</summary>
        public static HangarKind ResolveAt(
            double unixSeconds, TimeZoneInfo zone, bool premium, bool seasonalDrawable = true)
        {
            return Resolve(LocalTime(unixSeconds, zone), premium, seasonalDrawable);
        }

        /// <summary>The hangar to show at a unix time on the device's local calendar.</summary>
        public static HangarKind ResolveLocal(double unixSeconds, bool premium, bool seasonalDrawable = true)
        {
            return ResolveAt(unixSeconds, TimeZoneInfo.Local, premium, seasonalDrawable);
        }

        /// <summary>
        /// Unix seconds of a wall-clock time in <paramref name="zone"/> (local when null), the inverse of
        /// <see cref="LocalTime"/>. A time the zone skips at a daylight-saving change has no such instant.
        /// </summary>
        public static double UnixSeconds(DateTime wallClock, TimeZoneInfo zone)
        {
            var utc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified), zone ?? TimeZoneInfo.Local);
            return new DateTimeOffset(utc).ToUnixTimeMilliseconds() / 1000d;
        }

        /// <summary>Converts unix seconds to wall-clock time in <paramref name="zone"/> (local when null).</summary>
        public static DateTime LocalTime(double unixSeconds, TimeZoneInfo zone)
        {
            // One day inside DateTime's range so no zone offset can push the conversion out of it.
            const double MinSeconds = -62135596800d + 86400d;
            const double MaxSeconds = 253402300799d - 86400d;
            var clamped = double.IsNaN(unixSeconds) ? 0d : Math.Max(MinSeconds, Math.Min(MaxSeconds, unixSeconds));
            var utc = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Floor(clamped * 1000d)).UtcDateTime;
            return TimeZoneInfo.ConvertTimeFromUtc(utc, zone ?? TimeZoneInfo.Local);
        }
    }
}
