using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Rng;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;

namespace BlitzLegacy.Core.Shop
{
    /// <summary>What one granted container row turned into on the account.</summary>
    public sealed class GrantedReward
    {
        public GrantedReward(ContainerReward reward, bool duplicate, int compensationCredits)
        {
            Reward = reward;
            Duplicate = duplicate;
            CompensationCredits = compensationCredits;
        }

        public ContainerReward Reward { get; private set; }

        /// <summary>The tank was already owned and was paid out in credits.</summary>
        public bool Duplicate { get; private set; }

        public int CompensationCredits { get; private set; }
    }

    public sealed class ShopResult
    {
        private ShopResult()
        {
            Rewards = new List<GrantedReward>();
        }

        public bool Ok { get; private set; }

        /// <summary>lang.json key; currency errors take the price as <c>%s</c>.</summary>
        public string ErrorKey { get; private set; }

        public int Price { get; private set; }

        public ShopCurrency Currency { get; private set; }

        /// <summary>Container rewards in Godot order (credits, free XP, tank, gold).</summary>
        public List<GrantedReward> Rewards { get; private set; }

        public bool GuaranteedFired { get; private set; }

        public static ShopResult Success(int price, ShopCurrency currency)
        {
            return new ShopResult { Ok = true, Price = price, Currency = currency };
        }

        public static ShopResult Fail(string errorKey, int price, ShopCurrency currency)
        {
            return new ShopResult { Ok = false, ErrorKey = errorKey, Price = price, Currency = currency };
        }

        internal ShopResult WithRewards(List<GrantedReward> rewards, bool guaranteed)
        {
            Rewards = rewards ?? new List<GrantedReward>();
            GuaranteedFired = guaranteed;
            return this;
        }
    }

    /// <summary>
    /// Godot <c>Shop.Entry.buy()</c> plus the per-entry <c>on_buy</c>
    /// callbacks of <c>shop.gd</c>, minus the UI. Prices are checked and paid
    /// first, then the item is granted, as in the source.
    /// </summary>
    public sealed class ShopService
    {
        public const string ErrorNotEnoughGold = TankResearchService.ErrorNotEnoughGold;
        public const string ErrorNotEnoughCredits = TankResearchService.ErrorNotEnoughCredits;
        public const string ErrorTankOwned = "shop:owned_error";
        public const string ErrorInvalid = "invalid shop item";

        private readonly AccountData _account;
        private readonly TanksDatabase _database;
        private readonly IRandomSource _random;

        public ShopService(AccountData account, TanksDatabase database, IRandomSource random)
        {
            if (account == null)
            {
                throw new ArgumentNullException("account");
            }

            if (random == null)
            {
                throw new ArgumentNullException("random");
            }

            _account = account;
            _database = database;
            _random = random;
        }

        public static string NotEnoughKey(ShopCurrency currency)
        {
            return currency == ShopCurrency.Gold ? ErrorNotEnoughGold : ErrorNotEnoughCredits;
        }

        public long Balance(ShopCurrency currency)
        {
            return currency == ShopCurrency.Gold ? _account.Gold : _account.Credits;
        }

        public bool CanAfford(ShopCurrency currency, int cost)
        {
            return Balance(currency) >= cost;
        }

        public bool Owns(string tank)
        {
            return _account.HasTank(tank);
        }

        /// <summary>Godot premium entry: <c>add_prem_account(0, days)</c>.</summary>
        public ShopResult BuyPremium(PremiumOffer offer, double nowUnixSeconds)
        {
            if (offer == null || offer.Days <= 0)
            {
                return ShopResult.Fail(ErrorInvalid, 0, ShopCurrency.Gold);
            }

            if (!Pay(ShopCurrency.Gold, offer.CostGold))
            {
                return ShopResult.Fail(ErrorNotEnoughGold, offer.CostGold, ShopCurrency.Gold);
            }

            _account.AddPremAccount(0d, offer.Days, nowUnixSeconds);
            return ShopResult.Success(offer.CostGold, ShopCurrency.Gold);
        }

        /// <summary>
        /// Godot tank entry: <c>add_tank(tank, true)</c>. Godot lets the player
        /// buy an owned premium again for a credit refund; the weekly shop
        /// refuses instead, so nobody pays gold for credits by accident.
        /// </summary>
        public ShopResult BuyTank(TankOffer offer)
        {
            if (offer == null || offer.Tank == null || (_database != null && !_database.Has(offer.Name)))
            {
                return ShopResult.Fail(ErrorInvalid, 0, ShopCurrency.Gold);
            }

            if (Owns(offer.Name))
            {
                return ShopResult.Fail(ErrorTankOwned, offer.CostGold, ShopCurrency.Gold);
            }

            if (!Pay(ShopCurrency.Gold, offer.CostGold))
            {
                return ShopResult.Fail(ErrorNotEnoughGold, offer.CostGold, ShopCurrency.Gold);
            }

            GrantTank(offer.Name);
            return ShopResult.Success(offer.CostGold, ShopCurrency.Gold);
        }

        /// <summary>The container table without cards this database lacks.</summary>
        public ContainerLoot UsableLoot(ContainerOffer offer)
        {
            if (offer == null || offer.Loot == null)
            {
                return null;
            }

            return _database == null ? offer.Loot : offer.Loot.Where(_database.Has);
        }

        public int GuaranteedPoints(ContainerOffer offer)
        {
            return offer == null || offer.Loot == null ? 0 : _account.GuaranteedRewardPoints(offer.Loot.GuaranteedRewardId);
        }

        /// <summary>Godot container <c>on_buy</c>: roll, then grant every row.</summary>
        public ShopResult OpenContainer(ContainerOffer offer)
        {
            var loot = UsableLoot(offer);
            if (loot == null)
            {
                return ShopResult.Fail(ErrorInvalid, 0, ShopCurrency.Gold);
            }

            if (!Pay(offer.Currency, offer.Cost))
            {
                return ShopResult.Fail(NotEnoughKey(offer.Currency), offer.Cost, offer.Currency);
            }

            // Tanks already owned are excluded, unless every tank is owned.
            var owned = new List<string>();
            foreach (var tank in loot.Tanks)
            {
                if (_account.HasTank(tank))
                {
                    owned.Add(tank);
                }
            }

            if (owned.Count == loot.TanksAmount)
            {
                owned.Clear();
            }

            var guaranteed = offer.HasGuarantee && _account.ShouldGetGuaranteedReward(
                loot.GuaranteedRewardId, offer.GuaranteedRewardPoints);
            var rolled = loot.GetRandomRewards(guaranteed, owned, _random);
            var granted = new List<GrantedReward>(rolled.Count);
            for (var i = 0; i < rolled.Count; i++)
            {
                granted.Add(Grant(rolled[i]));
            }

            return ShopResult.Success(offer.Cost, offer.Currency).WithRewards(granted, guaranteed);
        }

        private GrantedReward Grant(ContainerReward reward)
        {
            switch (reward.Type)
            {
                case ContainerRewardType.Tank:
                    var compensation = GrantTank(reward.Tank);
                    return new GrantedReward(reward, compensation >= 0, Math.Max(compensation, 0));
                case ContainerRewardType.Gold:
                    _account.Gold = AccountData.AddCurrency(_account.Gold, reward.Amount);
                    break;
                case ContainerRewardType.Credits:
                    _account.Credits = AccountData.AddCurrency(_account.Credits, reward.Amount);
                    break;
                default:
                    _account.FreeXp = AccountData.AddCurrency(_account.FreeXp, reward.Amount);
                    break;
            }

            return new GrantedReward(reward, false, 0);
        }

        /// <summary>
        /// Godot <c>add_tank(name, true)</c>. Returns -1 for a new or newly
        /// bought card, otherwise the duplicate refund in credits: the gold
        /// price times <see cref="ShopCatalog.GoldPrice"/> for premiums, as in
        /// Godot, and the credit price for a researchable card (Godot paid
        /// nothing there, which reads as a lost reward).
        /// </summary>
        public int GrantTank(string tank)
        {
            var save = _account.GetTank(tank);
            if (save == null || !save.Bought)
            {
                _account.AddTank(tank, true);
                return -1;
            }

            var info = _database != null && _database.Has(tank) ? _database.Get(tank) : null;
            var refund = 0;
            if (info != null)
            {
                refund = info.Premium
                    ? Math.Max(info.CostGold, 0) * ShopCatalog.GoldPrice
                    : Math.Max(info.Cost, 0);
            }

            _account.Credits = AccountData.AddCurrency(_account.Credits, refund);
            return refund;
        }

        private bool Pay(ShopCurrency currency, int cost)
        {
            if (cost < 0 || !CanAfford(currency, cost))
            {
                return false;
            }

            if (currency == ShopCurrency.Gold)
            {
                _account.Gold -= cost;
            }
            else
            {
                _account.Credits -= cost;
            }

            return true;
        }
    }
}
