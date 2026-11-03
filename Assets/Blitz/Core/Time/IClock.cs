using System;

namespace BlitzLegacy.Core.Time
{
    /// <summary>Mirrors Godot <c>Util.curtime_sys()</c> (unix seconds).</summary>
    public interface IClock
    {
        double NowUnixSeconds { get; }
    }

    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();

        public double NowUnixSeconds
        {
            get { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d; }
        }
    }

    public sealed class FixedClock : IClock
    {
        public FixedClock(double nowUnixSeconds)
        {
            NowUnixSeconds = nowUnixSeconds;
        }

        public double NowUnixSeconds { get; set; }
    }
}
