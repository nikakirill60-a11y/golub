using System;
using System.Globalization;
using System.Text;

namespace BlitzLegacy.Core.Shop
{
    /// <summary>
    /// Text helpers of the shop screens. lang.json keeps the Godot printf
    /// placeholders (<c>%s</c>, <c>%d</c>), which <c>tr(key) % value</c> fills
    /// in order; <see cref="Fill"/> does the same for any number of values.
    /// </summary>
    public static class ShopText
    {
        /// <summary>Replaces each <c>%s</c> / <c>%d</c> in order; <c>%%</c> stays a percent sign.</summary>
        public static string Fill(string pattern, params string[] values)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(pattern.Length + 16);
            var next = 0;
            for (var i = 0; i < pattern.Length; i++)
            {
                var symbol = pattern[i];
                if (symbol == '%' && i + 1 < pattern.Length)
                {
                    var code = pattern[i + 1];
                    if (code == 's' || code == 'd')
                    {
                        builder.Append(values != null && next < values.Length ? values[next] : string.Empty);
                        next++;
                        i++;
                        continue;
                    }

                    if (code == '%')
                    {
                        builder.Append('%');
                        i++;
                        continue;
                    }
                }

                builder.Append(symbol);
            }

            return builder.ToString();
        }

        /// <summary>
        /// The rotation countdown: days and hours while a day or more is
        /// left (<c>shop:time/dh</c>), hours and minutes after that
        /// (<c>shop:time/hm</c>). Minutes round up, so the last minute never
        /// reads "0 h 0 min".
        /// </summary>
        public static string Countdown(TimeSpan left, string daysHoursPattern, string hoursMinutesPattern)
        {
            if (left < TimeSpan.Zero)
            {
                left = TimeSpan.Zero;
            }

            var totalMinutes = (long)Math.Ceiling(left.TotalMinutes);
            var days = totalMinutes / (24L * 60L);
            var hours = (totalMinutes / 60L) % 24L;
            var minutes = totalMinutes % 60L;
            if (days > 0)
            {
                return Fill(daysHoursPattern, Number(days), Number(hours));
            }

            return Fill(hoursMinutesPattern, Number(totalMinutes / 60L), Number(minutes));
        }

        private static string Number(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
