using System;
using BlitzLegacy.Core.Hangar;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class HangarScheduleTests
    {
        [TestCase(false, HangarKind.Regular)]
        [TestCase(true, HangarKind.Premium)]
        public void DayBeforeHalloweenKeepsAccountHangar(bool premium, HangarKind expected)
        {
            Assert.AreEqual(expected, HangarSchedule.Resolve(new DateTime(2026, 10, 28, 23, 59, 59), premium));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HalloweenIsShownToEveryAccountForTheWholeWindow(bool premium)
        {
            Assert.AreEqual(HangarKind.Halloween2026, HangarSchedule.Resolve(new DateTime(2026, 10, 29, 0, 0, 0), premium));
            Assert.AreEqual(HangarKind.Halloween2026, HangarSchedule.Resolve(new DateTime(2026, 10, 31, 12, 0, 0), premium));
            Assert.AreEqual(HangarKind.Halloween2026, HangarSchedule.Resolve(new DateTime(2026, 11, 13, 23, 59, 59), premium));
        }

        [TestCase(false, HangarKind.Regular)]
        [TestCase(true, HangarKind.Premium)]
        public void AccountHangarReturnsOnNovemberFourteenth(bool premium, HangarKind expected)
        {
            Assert.AreEqual(expected, HangarSchedule.Resolve(new DateTime(2026, 11, 14, 0, 0, 0), premium));
            Assert.AreEqual(expected, HangarSchedule.Resolve(new DateTime(2027, 10, 30, 12, 0, 0), premium));
        }

        [Test]
        public void WindowFollowsTheLocalCalendarDay()
        {
            var moscow = TimeZoneInfo.CreateCustomTimeZone("Test+03", TimeSpan.FromHours(3), "Test+03", "Test+03");
            var honolulu = TimeZoneInfo.CreateCustomTimeZone("Test-10", TimeSpan.FromHours(-10), "Test-10", "Test-10");

            // 2026-10-28 21:00:00 UTC is already October 29 in Moscow but still October 28 in Honolulu.
            var opening = new DateTimeOffset(2026, 10, 28, 21, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            Assert.AreEqual(HangarKind.Halloween2026, HangarSchedule.ResolveAt(opening, moscow, false));
            Assert.AreEqual(HangarKind.Regular, HangarSchedule.ResolveAt(opening, honolulu, false));
            Assert.AreEqual(HangarKind.Regular, HangarSchedule.ResolveAt(opening - 1, moscow, false));

            // 2026-11-13 21:00:00 UTC is November 14 in Moscow but November 13 in Honolulu.
            var closing = new DateTimeOffset(2026, 11, 13, 21, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            Assert.AreEqual(HangarKind.Premium, HangarSchedule.ResolveAt(closing, moscow, true));
            Assert.AreEqual(HangarKind.Halloween2026, HangarSchedule.ResolveAt(closing - 1, moscow, true));
            Assert.AreEqual(HangarKind.Halloween2026, HangarSchedule.ResolveAt(closing, honolulu, true));
        }

        [Test]
        public void UnixSecondsInvertsLocalTime()
        {
            var moscow = TimeZoneInfo.CreateCustomTimeZone("Test+03", TimeSpan.FromHours(3), "Test+03", "Test+03");
            var midnight = new DateTime(2026, 10, 29);
            var unix = HangarSchedule.UnixSeconds(midnight, moscow);
            Assert.AreEqual(new DateTimeOffset(2026, 10, 28, 21, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), unix, 1e-6);
            Assert.AreEqual(midnight, HangarSchedule.LocalTime(unix, moscow));
            Assert.AreEqual(HangarKind.Halloween2026, HangarSchedule.ResolveAt(unix, moscow, false));
            Assert.AreEqual(HangarKind.Regular, HangarSchedule.ResolveAt(unix - 1d, moscow, false));
        }

        [TestCase(false, HangarKind.Regular)]
        [TestCase(true, HangarKind.Premium)]
        public void HalloweenWindowKeepsAccountHangarWhereItCannotBeDrawn(bool premium, HangarKind expected)
        {
            // A headless player (the dedicated server) has no device for the event scene.
            Assert.AreEqual(expected, HangarSchedule.Resolve(new DateTime(2026, 10, 31, 12, 0, 0), premium, false));
            var utc = TimeZoneInfo.Utc;
            var opening = HangarSchedule.UnixSeconds(HangarSchedule.Halloween2026FirstDay, utc);
            Assert.AreEqual(expected, HangarSchedule.ResolveAt(opening, utc, premium, false));
            Assert.AreEqual(HangarKind.Halloween2026, HangarSchedule.ResolveAt(opening, utc, premium, true));
        }

        [Test]
        public void MalformedClockFallsBackToAccountHangar()
        {
            var utc = TimeZoneInfo.Utc;
            Assert.AreEqual(HangarKind.Premium, HangarSchedule.ResolveAt(double.NaN, utc, true));
            Assert.AreEqual(HangarKind.Regular, HangarSchedule.ResolveAt(double.PositiveInfinity, utc, false));
            Assert.AreEqual(HangarKind.Regular, HangarSchedule.ResolveAt(double.NegativeInfinity, utc, false));
        }
    }
}
