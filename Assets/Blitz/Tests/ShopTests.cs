using System;
using System.Collections.Generic;
using System.Linq;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Rng;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Shop;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Tanks;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class ShopTests
    {
        private TanksDatabase _database;
        private AccountData _account;
        private ShopService _service;

        private sealed class MaximumRandom : IRandomSource
        {
            public int RangeInclusive(int from, int to) { return to; }
            public float Value() { return 0.9999f; }
        }

        private sealed class CountingRandom : IRandomSource
        {
            public int Roll;
            public int Ranges;
            public int Values;
            public int RangeInclusive(int from, int to)
            {
                Ranges++;
                return Mathf.Clamp(Roll, from, to);
            }
            public float Value() { Values++; return 0.75f; }
        }

        [SetUp]
        public void SetUp()
        {
            _database = TanksDatabase.Parse(Resources.Load<TextAsset>("Data/tanks").text);
            _account = new AccountData { Gold = 100000, Credits = 1000000, FreeXp = 0 };
            _service = new ShopService(_account, _database, new MaximumRandom());
        }

        [Test]
        public void Catalog_HasGodotPremiumPricesAndThreeRequestedContainers()
        {
            var premium = ShopCatalog.PremiumOffers();
            CollectionAssert.AreEqual(new[] { 1, 3, 7, 14, 30, 90, 180, 360 }, premium.Select(p => p.Days));
            foreach (var offer in premium) Assert.AreEqual(offer.Days * 250, offer.CostGold);
            var containers = ShopCatalog.ContainerOffers();
            Assert.AreEqual(3, containers.Count);
            Assert.AreEqual(65000, containers[0].Cost);
            Assert.AreEqual(ShopCurrency.Credits, containers[0].Currency);
            Assert.AreEqual(300, containers[1].Cost);
            Assert.AreEqual(600, containers[2].Cost);
            Assert.AreEqual(ShopCurrency.Gold, containers[1].Currency);
            Assert.AreEqual(ShopCurrency.Gold, containers[2].Currency);
            CollectionAssert.AreEqual(new[] { 0, 0, 10 }, containers.Select(c => c.GuaranteedRewardPoints));
            CollectionAssert.AreEqual(new[] { false, false, true }, containers.Select(c => c.HasGuarantee));
        }

        [Test]
        public void GodotPools_KeepTheirRowsChancesAndAliases()
        {
            var small = ShopCatalog.SmallLoot();
            var big = ShopCatalog.BigLoot();
            var huge = ShopCatalog.HugeLoot();
            foreach (var loot in new[] { small, big, huge })
            {
                // Credits and free XP are the common drops, gold is rarer and a tank is the rarest.
                Assert.Greater(loot.CreditsChance, loot.GoldChance);
                Assert.Greater(loot.FreeXpChance, loot.GoldChance);
                Assert.Greater(loot.GoldChance, loot.TankChance);
                Assert.Less(loot.GoldChance, 100f, "gold is a chance, not a certainty");
            }
            Assert.Greater(big.TankChance, small.TankChance);
            Assert.Greater(huge.TankChance, big.TankChance);
            Assert.Greater(huge.GoldChance, big.GoldChance);
            Assert.Greater(big.GoldChance, small.GoldChance);
            CollectionAssert.AreEqual(new[] { "t1_e6", "t2_lt", "ha_go", "r105_bt_7a" }, small.Tanks);
            CollectionAssert.AreEqual(new[] { "t26_e4_superpershing", "t34_hvy", "ke_ni_b", "ltp" }, big.Tanks);
            CollectionAssert.AreEqual(new[] { 1000, 750, 500, 200, 50 },
                small.Rewards.Where(r => r.Type == ContainerRewardType.Gold).Select(r => r.Amount));
            Assert.AreEqual(4, big.Rewards.Count(r => r.Type == ContainerRewardType.Gold && r.Amount == 3500));
            foreach (var offer in ShopCatalog.ContainerOffers())
            {
                foreach (var tank in offer.Loot.Tanks) Assert.IsTrue(_database.Has(tank), offer.Id + ": " + tank);
                Assert.AreEqual(offer.Loot.TanksAmount, _service.UsableLoot(offer).TanksAmount);
            }
        }

        [Test]
        public void WeightedRows_HaveInclusiveBoundariesAndClampWeightsToOne()
        {
            var first = ContainerReward.ForGold(100, 0);
            var second = ContainerReward.ForGold(200, 2);
            Assert.AreEqual(1, first.Weight);
            var rows = new List<ContainerReward> { first, second };
            Assert.AreSame(first, ContainerLoot.GetWeighted(rows, new CountingRandom { Roll = 1 }));
            Assert.AreSame(second, ContainerLoot.GetWeighted(rows, new CountingRandom { Roll = 2 }));
            Assert.AreSame(second, ContainerLoot.GetWeighted(rows, new CountingRandom { Roll = 3 }));
            var random = new CountingRandom();
            Assert.IsNull(ContainerLoot.GetWeighted(new List<ContainerReward>(), random));
            Assert.AreEqual(1, random.Ranges, "Godot rolls even an empty type table");
            Assert.AreEqual(1f / 3f, new ContainerLoot("test", 0f, 0f).Add(first).Add(second).RowShare(first), 0.00001f);
        }

        [Test]
        public void Loot_RollsFourTypesThenChancesAndKeepsGodotRewardOrder()
        {
            var random = new CountingRandom();
            var loot = new ContainerLoot("test", 100f, 100f).AddTanks("a")
                .Add(ContainerReward.ForGold(10, 1)).Add(ContainerReward.ForCredits(20, 1))
                .Add(ContainerReward.ForFreeXp(30, 1));
            var rewards = loot.GetRandomRewards(false, null, random);
            CollectionAssert.AreEqual(new[] { ContainerRewardType.Credits, ContainerRewardType.FreeXp,
                ContainerRewardType.Tank, ContainerRewardType.Gold }, rewards.Select(r => r.Type));
            Assert.AreEqual(4, random.Ranges);
            Assert.AreEqual(2, random.Values);
            random = new CountingRandom();
            loot.GetRandomRewards(true, null, random);
            Assert.AreEqual(1, random.Values, "guaranteed tank skips its chance roll");
            rewards = loot.GetRandomRewards(true, new[] { "a" }, new CountingRandom());
            Assert.IsFalse(rewards.Any(r => r.Type == ContainerRewardType.Tank));
        }

        [Test]
        public void Loot_IsNeverEmptyAndFollowsItsChances()
        {
            foreach (var offer in ShopCatalog.ContainerOffers())
            {
                var loot = offer.Loot;
                var random = new SeededRandomSource(7);
                var counts = new Dictionary<ContainerRewardType, int>();
                const int runs = 20000;
                for (var i = 0; i < runs; i++)
                {
                    var rewards = loot.GetRandomRewards(false, null, random);
                    Assert.Greater(rewards.Count, 0, offer.Id + ": empty container");
                    foreach (var type in rewards.Select(r => r.Type).Distinct())
                    {
                        int count;
                        counts.TryGetValue(type, out count);
                        counts[type] = count + 1;
                    }
                }

                int gold, credits, freeXp, tank;
                counts.TryGetValue(ContainerRewardType.Gold, out gold);
                counts.TryGetValue(ContainerRewardType.Credits, out credits);
                counts.TryGetValue(ContainerRewardType.FreeXp, out freeXp);
                counts.TryGetValue(ContainerRewardType.Tank, out tank);
                Assert.AreEqual(loot.GoldChance / 100f, gold / (float)runs, 0.02f, offer.Id + " gold");
                Assert.Greater(credits, gold, offer.Id);
                Assert.Greater(freeXp, gold, offer.Id);
                Assert.Less(tank, gold, offer.Id);
            }
        }

        [Test]
        public void Loot_FallsBackToCreditsWhenNothingDropped()
        {
            var loot = new ContainerLoot("test", 0f, 0f, 1f, 1f)
                .Add(ContainerReward.ForCredits(20, 1)).Add(ContainerReward.ForFreeXp(30, 1));
            var rewards = loot.GetRandomRewards(false, null, new CountingRandom());
            Assert.AreEqual(ContainerRewardType.Credits, rewards.Single().Type);
        }

        [Test]
        public void GuaranteedCounters_AreIndependentResetAndSurviveSaveReload()
        {
            Assert.IsFalse(_account.ShouldGetGuaranteedReward("small_container", 3));
            Assert.IsFalse(_account.ShouldGetGuaranteedReward("small_container", 3));
            Assert.IsFalse(_account.ShouldGetGuaranteedReward("huge_container", 2));
            var restored = AccountData.FromJson(JsonNode.Parse(_account.ToJson().ToString()));
            Assert.AreEqual(2, restored.GuaranteedRewardPoints("small_container"));
            Assert.IsTrue(restored.ShouldGetGuaranteedReward("small_container", 3));
            Assert.AreEqual(0, restored.GuaranteedRewardPoints("small_container"));
            Assert.AreEqual(1, restored.GuaranteedRewardPoints("huge_container"));
            Assert.AreEqual(0, AccountData.FromJson(JsonNode.Parse("{}")).GuaranteedRewardPoints("small_container"));
        }

        [Test]
        public void Premium_PaysGoldAndExtendsExistingOrExpiredTime()
        {
            const double now = 1750000000d;
            var offer = ShopCatalog.PremiumOffers()[0];
            _account.PremAccountExpiration = now + 100d;
            Assert.IsTrue(_service.BuyPremium(offer, now).Ok);
            Assert.AreEqual(now + 100d + 86400d, _account.PremAccountExpiration);
            Assert.AreEqual(100000 - 250, _account.Gold);
            _account.PremAccountExpiration = now - 100d;
            Assert.IsTrue(_service.BuyPremium(offer, now).Ok);
            Assert.AreEqual(now + 86400d, _account.PremAccountExpiration);
            _account.Gold = 0;
            var expiration = _account.PremAccountExpiration;
            Assert.AreEqual(ShopService.ErrorNotEnoughGold, _service.BuyPremium(offer, now).ErrorKey);
            Assert.AreEqual(expiration, _account.PremAccountExpiration);
        }

        [Test]
        public void TankPurchase_GrantsBoughtTankAndRefusesOwnedOrPoorAccount()
        {
            var tank = _database.Get("lowe");
            var offer = new TankOffer(tank, 6000, false);
            Assert.IsTrue(_service.BuyTank(offer).Ok);
            Assert.IsTrue(_account.HasTank(tank.Name));
            Assert.AreEqual(94000, _account.Gold);
            Assert.AreEqual(ShopService.ErrorTankOwned, _service.BuyTank(offer).ErrorKey);
            Assert.AreEqual(94000, _account.Gold);
            _account.Gold = 0;
            Assert.AreEqual(ShopService.ErrorNotEnoughGold,
                _service.BuyTank(new TankOffer(_database.Get("t34_hvy"), 6000, false)).ErrorKey);
            Assert.IsFalse(_account.HasTank("t34_hvy"));
        }

        [Test]
        public void Container_PaysCorrectCurrencyAndDoesNotAdvanceGuaranteeWhenPoor()
        {
            var small = ShopCatalog.ContainerOffers()[0];
            var credits = _account.Credits;
            var gold = _account.Gold;
            var result = _service.OpenContainer(small);
            Assert.IsTrue(result.Ok);
            Assert.AreEqual(gold, _account.Gold);
            Assert.AreEqual(credits - 65000 + result.Rewards.First(r => r.Reward.Type == ContainerRewardType.Credits).Reward.Amount,
                _account.Credits);
            Assert.AreEqual(0, _service.GuaranteedPoints(small), "the small container has no guarantee");
            _account.Credits = 64999;
            Assert.AreEqual(ShopService.ErrorNotEnoughCredits, _service.OpenContainer(small).ErrorKey);
            Assert.AreEqual(0, _service.GuaranteedPoints(small));
            Assert.AreEqual(64999, _account.Credits);
            var huge = ShopCatalog.ContainerOffers()[2];
            _account.Gold = 599;
            Assert.IsFalse(_service.OpenContainer(huge).Ok);
            Assert.AreEqual(0, _service.GuaranteedPoints(huge), "a refused purchase does not advance the guarantee");
        }

        [Test]
        public void GuaranteedTank_ExcludesOwnedThenRefundsWhenTheEntirePoolIsOwned()
        {
            var loot = new ContainerLoot("forced", 0f, 0f).AddTanks("lowe", "t34_hvy");
            var offer = new ContainerOffer("test", ShopCurrency.Gold, 300, loot, 1, "shop_chest_big");
            _account.AddTank("t34_hvy", true);
            var first = _service.OpenContainer(offer);
            Assert.IsTrue(first.GuaranteedFired);
            Assert.AreEqual("lowe", first.Rewards.Single().Reward.Tank);
            Assert.IsFalse(first.Rewards.Single().Duplicate);
            Assert.IsTrue(_account.HasTank("lowe"));
            var credits = _account.Credits;
            var second = _service.OpenContainer(offer).Rewards.Single();
            Assert.AreEqual("t34_hvy", second.Reward.Tank);
            Assert.IsTrue(second.Duplicate);
            Assert.AreEqual(_database.Get("t34_hvy").CostGold * 400, second.CompensationCredits);
            Assert.AreEqual(credits + second.CompensationCredits, _account.Credits);
            Assert.AreEqual(0, _service.GuaranteedPoints(offer));
        }

        [Test]
        public void HugeContainer_GuaranteesTheTenthTank()
        {
            var offer = ShopCatalog.ContainerOffers()[2];
            for (var i = 1; i <= 10; i++)
            {
                var result = _service.OpenContainer(offer);
                Assert.IsNotEmpty(result.Rewards);
                Assert.AreEqual(i == 10, result.GuaranteedFired);
                Assert.AreEqual(i == 10, result.Rewards.Any(r => r.Reward.Type == ContainerRewardType.Tank));
            }
            Assert.AreEqual(0, _service.GuaranteedPoints(offer));
        }

        [Test]
        public void Rotation_MondayBoundaryDoesNotUseLocalTimezone()
        {
            const double monday = 1750032000d; // 2025-06-16 00:00 UTC.
            Assert.AreEqual(monday, WeeklyTankRotation.WeekStartUnix(WeeklyTankRotation.WeekIndex(monday)));
            Assert.AreEqual(WeeklyTankRotation.WeekIndex(monday) - 1, WeeklyTankRotation.WeekIndex(monday - 0.001d));
            Assert.AreEqual(monday + WeeklyTankRotation.WeekSeconds, WeeklyTankRotation.NextRotationUnix(monday));
            Assert.AreEqual(TimeSpan.FromDays(7d), WeeklyTankRotation.TimeLeft(monday));
        }

        [Test]
        public void Rotation_IsDeterministicOrderIndependentAndChangesBetweenWeeks()
        {
            var tanks = new List<TankInfo>();
            for (var level = 6; level <= 10; level++)
                for (var i = 0; i < 10; i++)
                    tanks.Add(new TankInfo { Name = level + "_" + i, Level = level,
                        Premium = i < 6, Collector = i >= 6 });
            var first = WeeklyTankRotation.ForWeek(tanks, 1234);
            tanks.Reverse();
            CollectionAssert.AreEqual(first.Select(t => t.Name), WeeklyTankRotation.ForWeek(tanks, 1234).Select(t => t.Name));
            Assert.AreEqual(15, first.Count);
            Assert.AreEqual(15, first.Select(t => t.Name).Distinct().Count());
            Assert.IsFalse(first.Select(t => t.Name).SequenceEqual(WeeklyTankRotation.ForWeek(tanks, 1235).Select(t => t.Name)));
            foreach (var group in first.GroupBy(t => t.Level))
            {
                Assert.AreEqual(2, group.Count(t => !t.Collector));
                Assert.AreEqual(1, group.Count(t => t.Collector));
                Assert.Greater(group.First(t => t.Collector).CostGold, group.First(t => !t.Collector).CostGold);
            }
            Assert.IsFalse(WeeklyTankRotation.IsCandidate(new TankInfo { Name = "a", Level = 5, Premium = true }, null));
            Assert.IsFalse(WeeklyTankRotation.IsCandidate(new TankInfo { Name = "a", Level = 8, Premium = true, Hidden = true }, null));
            Assert.IsFalse(WeeklyTankRotation.IsCandidate(new TankInfo { Name = "a", Level = 8, Premium = true, Mod = true }, null));
            Assert.IsFalse(WeeklyTankRotation.IsCandidate(new TankInfo { Name = "oth1", Level = 8, Premium = true }, null));
            Assert.IsFalse(WeeklyTankRotation.IsCandidate(tanks[0], t => false));
        }

        [Test]
        public void EveryRealRotationCandidate_HasAnIconAndClientModel()
        {
            var atlas = AtlasRegionTable.Parse(Resources.Load<TextAsset>("Data/atlas_regions").text);
            var candidates = _database.Tanks.Where(t => WeeklyTankRotation.IsCandidate(t,
                info => atlas.Contains(HangarUiKit.TankIconRegion(info.Name)))).ToList();
            Assert.Greater(candidates.Count, 10);
            foreach (var tank in candidates) Assert.IsTrue(TankAssetSource.HasModel(tank), tank.Name);
            var offers = WeeklyTankRotation.ForWeek(candidates, 2900);
            Assert.Greater(offers.Count, 5);
            Assert.IsTrue(offers.All(t => t.Level >= 6 && t.Level <= 10));
            for (var level = 6; level <= 10; level++)
                Assert.Greater(ShopPricing.TankPrice(level, true), ShopPricing.TankPrice(level, false));
        }

        [Test]
        public void ShopArtworkAndDynamicKeys_ArePresentInBothLanguages()
        {
            var atlas = AtlasRegionTable.Parse(Resources.Load<TextAsset>("Data/atlas_regions").text);
            var lang = LocalizationTable.Parse(Resources.Load<TextAsset>("Data/lang").text);
            foreach (var offer in ShopCatalog.PremiumOffers()) Assert.IsNotNull(atlas.Get(offer.ImageRegion));
            foreach (var offer in ShopCatalog.ContainerOffers())
            {
                Assert.IsNotNull(atlas.Get(offer.ImageRegion));
                Assert.IsNotNull(atlas.Get(offer.OpeningImageRegion));
                foreach (var tank in offer.Loot.Tanks) Assert.IsNotNull(atlas.Get(HangarUiKit.TankIconRegion(tank)), tank);
                foreach (var key in new[] { offer.NameKey, offer.DescriptionKey })
                    foreach (var language in new[] { "en", "ru" }) Assert.AreNotEqual(key, lang.Get(key, language));
            }
            foreach (var region in new[] { "chest_background", "chest_fx_glow", "chest_fx_spot_light", "chest_fx_box_light",
                "chest_fx_shadow", "chest_fx_camm", "chest_fx_sparks", "shop_reward_slot", "shop_reward_freexp" })
                Assert.IsNotNull(atlas.Get(region), region);
            foreach (var key in new[] { "shop:premium/desc", "shop:guaranteed_left", "shop:offline/title", "shop:offline/text",
                "shop:received", "shop:guaranteed_reward", "shop:compensation", "shop:reward/tank", "shop:reward/gold",
                "shop:reward/credits", "shop:reward/freexp", "shop:owned", "shop:rotation", "shop:rotation/hint" })
                foreach (var language in new[] { "en", "ru" }) Assert.AreNotEqual(key, lang.Get(key, language));
        }

        [Test]
        public void PrintfAndCountdown_HandleEscapesAndTheLastMinute()
        {
            Assert.AreEqual("5 / 10 / 100%", ShopText.Fill("%d / %s / 100%%", "5", "10"));
            Assert.AreEqual("6 d 23 h", ShopText.Countdown(TimeSpan.FromHours(167d), "%d d %d h", "%d h %d min"));
            Assert.AreEqual("0 h 1 min", ShopText.Countdown(TimeSpan.FromSeconds(1d), "%d d %d h", "%d h %d min"));
            Assert.AreEqual("0 h 0 min", ShopText.Countdown(TimeSpan.FromSeconds(-1d), "%d d %d h", "%d h %d min"));
        }
    }
}
