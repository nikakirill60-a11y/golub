using System;
using BlitzLegacy.Core.Ui;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class AccountCardTextTests
    {
        [Test]
        public void Battles_IsAPlainCount()
        {
            Assert.AreEqual("42", AccountCardText.Battles(42));
        }

        [Test]
        public void WinRate_KeepsOneDecimalLikeGdscriptStr()
        {
            Assert.AreEqual("65.0%", AccountCardText.WinRate(65f));
        }

        [Test]
        public void WinRate_RoundsToOneDecimal()
        {
            Assert.AreEqual("66.7%", AccountCardText.WinRate(66.666f));
        }

        [Test]
        public void WinRate_ZeroBattlesReadsZero()
        {
            Assert.AreEqual("0.0%", AccountCardText.WinRate(0f));
        }

        [Test]
        public void TimePlayed_TruncatesToWholeHours()
        {
            Assert.AreEqual("2h", AccountCardText.TimePlayed(TimeSpan.FromMinutes(152)));
        }

        [Test]
        public void TimePlayed_UnderAnHourReadsZero()
        {
            Assert.AreEqual("0h", AccountCardText.TimePlayed(TimeSpan.FromMinutes(59)));
        }
    }
}
