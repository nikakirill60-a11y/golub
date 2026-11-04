using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Shop;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Tier XI is a first-class level: cards keep it through cleanup, the
    /// database buckets it on its own, filters and prices know it, and the
    /// online queue widens its tier table by the one row.
    /// </summary>
    public sealed class TierElevenTests
    {
        [Test]
        public void Cleanup_KeepsLevelElevenAndClampsTwelveDownToIt()
        {
            var eleven = new TankInfo { Name = "tier_xi", Level = 11 };
            eleven.Cleanup();
            Assert.That(eleven.Level, Is.EqualTo(11));

            var twelve = new TankInfo { Name = "tier_xii", Level = 12 };
            twelve.Cleanup();
            Assert.That(twelve.Level, Is.EqualTo(TanksDatabase.MaxLevel));
        }

        [Test]
        public void Database_BucketsLevelElevenSeparately()
        {
            var database = new TanksDatabase();
            database.Add(new TankInfo { Name = "xi_tank", Level = 11 });
            database.Add(new TankInfo { Name = "x_tank", Level = 10 });

            Assert.That(database.ByLevel(11).Count, Is.EqualTo(1));
            Assert.That(database.ByLevel(11)[0].Name, Is.EqualTo("xi_tank"));
            Assert.That(database.ByLevel(10).Count, Is.EqualTo(1));
        }

        [Test]
        public void CarouselFilter_AcceptsLevelEleven()
        {
            var filter = new CarouselFilter();
            filter.ToggleLevel(11);
            Assert.That(filter.HasLevel(11), Is.True);

            var info = new TankInfo { Name = "xi_tank", Level = 11, Nation = Nation.Ussr };
            Assert.That(filter.Matches(info), Is.True);
        }

        [Test]
        public void Shop_KnowsTierElevenPrices()
        {
            Assert.That(ShopPricing.MaxLevel, Is.EqualTo(11));
            Assert.That(ShopPricing.TankPrice(11, false), Is.EqualTo(18000));
            Assert.That(ShopPricing.TankPrice(11, true), Is.EqualTo(24500));
        }

        [Test]
        public void OnlineQueue_WidensToElevenTiers()
        {
            Assert.That(OnlineQueue.Tiers, Is.EqualTo(11));
            Assert.That(OnlineQueue.MixLength, Is.EqualTo(11 + OnlineQueue.Classes));

            var tiers = new int[OnlineQueue.Tiers];
            var classes = new int[OnlineQueue.Classes];
            tiers[0] = 3;
            var decodedTiers = new int[OnlineQueue.Tiers];
            var decodedClasses = new int[OnlineQueue.Classes];
            Assert.That(
                OnlineQueue.TryDecode(OnlineQueue.Encode(tiers, classes), decodedTiers, decodedClasses),
                Is.True);
            Assert.That(decodedTiers[0], Is.EqualTo(3));
        }

        [Test]
        public void LevelRegion_NamesTheEleventhGlyphs()
        {
            Assert.That(HangarUiKit.LevelRegion(11), Is.EqualTo("tank_level_11"));
            Assert.That(
                HangarUiKit.TankLevelRegion(new TankInfo { Level = 11, Premium = true }),
                Is.EqualTo("tank_level_11_prem"));
        }

        [Test]
        public void BigBoss_AcceptsTierEleven()
        {
            Assert.That(BigBossRules.EligibleTier(11), Is.True);
            Assert.That(BigBossRules.EligibleTier(TanksDatabase.MaxLevel + 1), Is.False);
        }
    }
}
