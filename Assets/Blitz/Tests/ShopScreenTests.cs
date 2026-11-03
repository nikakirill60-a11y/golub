using System;
using System.Linq;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Rng;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Shop;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Time;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Tests
{
    public sealed class ShopScreenTests
    {
        private const double Now = 1750032000d;
        private RectTransform _canvas;
        private AccountData _account;
        private TanksDatabase _tanks;
        private OnlineClock _network;
        private FixedClock _device;
        private HangarUiKit _kit;
        private ShopScreen _shop;

        private sealed class OfflineSource : INetworkTimeSource
        {
            public bool TryQuery(string host, int timeout, out double unix) { unix = 0d; return false; }
        }

        private void Build(bool modern)
        {
            _canvas = UiFactory.Node("shop_test", null);
            _canvas.sizeDelta = new Vector2(1386.67f, 733.33f);
            _canvas.gameObject.AddComponent<Canvas>();
            var atlas = AtlasRegionTable.Parse(Resources.Load<TextAsset>("Data/atlas_regions").text);
            if (modern) atlas.Skin = ModernSkin.Instance;
            var lang = LocalizationTable.Parse(Resources.Load<TextAsset>("Data/lang").text);
            lang.Language = "ru";
            _kit = new HangarUiKit(atlas, lang,
                BlitzResources.LoadFont(BlitzResources.RegularFontName),
                BlitzResources.LoadFont(BlitzResources.BoldFontName), null);
            _tanks = TanksDatabase.Parse(Resources.Load<TextAsset>("Data/tanks").text);
            _account = new AccountData { Gold = 100000, Credits = 1000000, FreeXp = 0 };
            _network = new OnlineClock(new OfflineSource(), new[] { "test" }, () => 1d);
            _network.SetSynced(Now);
            _device = new FixedClock(Now);
            _shop = new ShopScreen(_kit, _tanks, _account, _network, _device, modern, new SeededRandomSource(48));
            _shop.ModalLayer = _canvas;
            _shop.Build(_canvas);
            _shop.Show();
            Canvas.ForceUpdateCanvases();
        }

        [TearDown]
        public void TearDown()
        {
            if (_shop != null) _shop.Hide();
            if (_canvas != null) UnityEngine.Object.DestroyImmediate(_canvas.gameObject);
            _shop = null;
            _canvas = null;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BothStyles_HaveThreeTabsAndBuyOnlyAfterConfirmation(bool modern)
        {
            Build(modern);
            Assert.AreEqual(ShopAvailability.Online, _shop.Availability);
            Assert.AreEqual(8, _shop.CardCount(ShopScreen.TabPremium));
            Assert.AreEqual(3, _shop.CardCount(ShopScreen.TabContainers));
            Assert.Greater(_shop.CardCount(ShopScreen.TabTanks), 5);
            Assert.IsFalse(_shop.Root.GetComponentsInChildren<Text>(true).Any(t => t.text.StartsWith("shop:")));
            var changed = 0;
            _shop.Changed = tank => changed++;
            _shop.PressBuy(ShopScreen.TabPremium, 0);
            Assert.IsNotNull(_shop.ActivePopup);
            Assert.AreEqual(100000, _account.Gold, "opening CONFIRM does not spend");
            _shop.ActivePopup.Accept();
            Assert.AreEqual(99750, _account.Gold);
            Assert.AreEqual(Now + 86400d, _account.PremAccountExpiration);
            Assert.AreEqual(1, changed);
            _shop.Hide();
            Assert.IsFalse(_shop.Root.gameObject.activeSelf);
            _shop.Show();
            Assert.IsTrue(_shop.Root.gameObject.activeSelf);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TankPurchase_MarksOwnedAndMondayUpdatesCardsNotDeviceTime(bool modern)
        {
            Build(modern);
            _shop.ShowTab(ShopScreen.TabTanks);
            var week = _shop.Week;
            var name = _shop.TankOffers[0].Name;
            _shop.PressBuy(ShopScreen.TabTanks, 0);
            _shop.ActivePopup.Accept();
            Assert.IsTrue(_account.HasTank(name));
            Assert.IsTrue(_shop.CardOwned(ShopScreen.TabTanks, 0));
            Assert.IsFalse(_shop.CardButton(ShopScreen.TabTanks, 0).interactable);
            _device.NowUnixSeconds = Now + 100d * WeeklyTankRotation.WeekSeconds;
            _shop.Refresh(true);
            Assert.AreEqual(week, _shop.Week);
            _network.SetSynced(Now + WeeklyTankRotation.WeekSeconds);
            _shop.Refresh(true);
            Assert.AreEqual(week + 1, _shop.Week);
            Assert.IsNotEmpty(_shop.RotationText);
        }

        [Test]
        public void ConfirmationFromPreviousWeek_CannotBuyAStaleTank()
        {
            Build(false);
            _shop.PressBuy(ShopScreen.TabTanks, 0);
            var stale = _shop.TankOffers[0].Name;
            _network.SetSynced(Now + WeeklyTankRotation.WeekSeconds);
            _shop.ActivePopup.Accept();
            Assert.AreEqual(100000, _account.Gold);
            Assert.IsFalse(_account.HasTank(stale));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Offline_BlocksPurchaseIncludingAnAlreadyOpenConfirmation(bool modern)
        {
            Build(modern);
            _shop.PressBuy(ShopScreen.TabPremium, 0);
            _network.SetOffline();
            _shop.ActivePopup.Accept();
            Assert.AreEqual(100000, _account.Gold);
            Assert.AreEqual(ShopAvailability.Offline, _shop.Availability);
            Assert.IsTrue(_shop.RetryVisible);
            Assert.AreEqual(_kit.Str("shop:offline/title"), _shop.StatusTitle);
            _shop.PressBuy(ShopScreen.TabContainers, 0);
            Assert.IsNull(_shop.ActivePopup);
            Assert.IsNull(_shop.OpenedContainer);
            _network.SetSynced(Now);
            _shop.Refresh(true);
            Assert.AreEqual(ShopAvailability.Online, _shop.Availability);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Container_ShowsRewardsSkipsWithoutGrantingTwiceAndCanOpenMore(bool modern)
        {
            Build(modern);
            _shop.ShowTab(ShopScreen.TabContainers);
            _shop.PressBuy(ShopScreen.TabContainers, 0);
            _shop.ActivePopup.Accept();
            var window = _shop.OpenedContainer;
            Assert.IsNotNull(window);
            Assert.AreEqual(ContainerOpenScreen.Phase.Intro, window.CurrentPhase);
            Assert.IsFalse(window.OpenMoreButton.interactable);
            Assert.GreaterOrEqual(window.RewardCount, 1, "a container is never empty");
            Assert.AreEqual(_kit.Str("shop:container/small"), window.HeaderText);
            Assert.IsFalse(window.ProgressShown, "the small container has no guaranteed tank");
            Assert.IsFalse(window.CoveredTankShown);
            var credits = _account.Credits;
            window.PressOpenMore();
            Assert.AreEqual(credits, _account.Credits, "animation locks the purchase button");
            window.AdvanceAnimation(0.6f);
            Assert.AreEqual(ContainerOpenScreen.Phase.Charge, window.CurrentPhase);
            window.AdvanceAnimation(2.0f);
            Assert.AreEqual(ContainerOpenScreen.Phase.Reveal, window.CurrentPhase);
            window.SkipToRewards();
            window.SkipToRewards();
            Assert.AreEqual(credits, _account.Credits);
            Assert.AreEqual(ContainerOpenScreen.Phase.Settled, window.CurrentPhase);
            Assert.IsTrue(window.OpenMoreButton.interactable);
            Assert.IsNotEmpty(window.RewardType(0));
            Assert.IsNotEmpty(window.RewardValue(0));
            window.PressOpenMore();
            Assert.AreSame(window, _shop.OpenedContainer, "open again reuses its modal");
            window.SkipToRewards();
            _account.Credits = 0;
            window.RefreshBalances();
            Assert.AreEqual(UiTheme.ErrorText, window.OpenMoreCostColor);
            window.PressOpenMore();
            Assert.IsNull(_shop.OpenedContainer);
            Assert.IsNotNull(_shop.ActivePopup);
            Assert.AreEqual(0, _account.Credits);
            Assert.AreEqual(0, _shop.Service.GuaranteedPoints(ShopCatalog.ContainerOffers()[0]));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Container_RewardsFlyOutOfTheBoxOneByOneThenTextFadesIn(bool modern)
        {
            Build(modern);
            _shop.ShowTab(ShopScreen.TabContainers);
            _shop.PressBuy(ShopScreen.TabContainers, 0);
            _shop.ActivePopup.Accept();
            var window = _shop.OpenedContainer;
            Assert.IsNotNull(window.Scene, "the opening is the 3D chest scene");
            Assert.AreEqual(0f, window.HeaderAlpha);
            Assert.AreEqual(0f, window.RewardAlpha(0));
            Assert.IsFalse(window.RewardTile(0).gameObject.activeSelf);

            window.AdvanceAnimation(ContainerOpenScreen.RevealAt + ContainerOpenScreen.TileFlyTime * 0.5f);
            Assert.AreEqual(ContainerOpenScreen.Phase.Reveal, window.CurrentPhase);
            Assert.That(window.RewardAlpha(0), Is.InRange(0.1f, 0.9f), "the first reward is in flight");
            for (var i = 1; i < window.RewardCount; i++)
                Assert.AreEqual(0f, window.RewardAlpha(i), "the next rewards wait for their turn");
            Assert.Less(window.RewardTile(0).localScale.x, 1f);
            Assert.AreEqual(0f, window.HeaderAlpha, "text stays hidden while rewards fly");

            window.AdvanceAnimation(window.SettleTime);
            Assert.AreEqual(ContainerOpenScreen.Phase.Settled, window.CurrentPhase);
            Assert.AreEqual(1f, window.HeaderAlpha);
            for (var i = 0; i < window.RewardCount; i++)
            {
                Assert.AreEqual(1f, window.RewardAlpha(i));
                Assert.AreEqual(1f, window.RewardTile(i).localScale.x, 1e-5f);
            }
            Assert.AreEqual(ContainerOpenScreen.SettledDarkness, window.Scene.Darkness, 1e-5f);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BothStyles_TitleTheShopNextToTheBackArrow(bool modern)
        {
            Build(modern);
            var title = _shop.Root.GetComponentsInChildren<Text>(true)
                .Single(t => t.name == "title" && t.transform.parent == _shop.Root);
            Assert.AreEqual("Магазин", title.text);
            var back = (RectTransform)_shop.Root.Find("btn_menu");
            var rt = title.rectTransform;
            var titleCentre = rt.TransformPoint(rt.rect.center);
            var backCentre = back.TransformPoint(back.rect.center);
            Assert.AreEqual(backCentre.y, titleCentre.y, 0.5f, "the title is centred on the back arrow's height");
            Assert.Greater(rt.TransformPoint(rt.rect.min).x, back.TransformPoint(back.rect.max).x,
                "the title starts right of the arrow");
            if (!modern)
            {
                // The hangar nickname block it replaces: x = 119, bold 28.
                Assert.AreEqual(ShopScreen.ClassicTitleSize, title.fontSize);
                Assert.AreEqual(ShopScreen.ClassicTitleX, rt.anchoredPosition.x, 1e-3f);
                Assert.AreSame(_kit.Bold, title.font);
            }
        }

        [Test]
        public void Modern_ShopBackButtonMatchesTheHangarTopButton()
        {
            Build(true);
            var back = _canvas.GetComponentsInChildren<Button>(true).First(b => b.name == "btn_menu");
            var parent = (RectTransform)back.transform.parent;
            var reference = (RectTransform)_kit.ModernTopButton("reference", parent, "icon_back").transform;
            var rt = (RectTransform)back.transform;
            Assert.AreEqual(reference.anchorMin, rt.anchorMin);
            Assert.AreEqual(reference.anchorMax, rt.anchorMax);
            Assert.AreEqual(reference.pivot, rt.pivot);
            Assert.AreEqual(reference.anchoredPosition, rt.anchoredPosition);
            Assert.AreEqual(reference.sizeDelta, rt.sizeDelta);
            Assert.AreEqual(new Vector2(HangarUiKit.ModernTopButtonWidth, HangarUiKit.ModernTopButtonHeight), rt.rect.size);
            UnityEngine.Object.DestroyImmediate(reference.gameObject);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Container_DuplicateShowsCompensationAndTheBackArrowReturnsToTheShop(bool modern)
        {
            Build(modern);
            var homeCalls = 0;
            _shop.HomePressed = () => homeCalls++;
            _shop.ShowTab(ShopScreen.TabContainers);
            var offer = ShopCatalog.ContainerOffers()[2];
            foreach (var tank in offer.Loot.Tanks) _account.AddTank(tank, true);
            _account.ShopGuaranteedRewards[offer.Loot.GuaranteedRewardId] = 9;
            _shop.PressBuy(ShopScreen.TabContainers, 2);
            _shop.ActivePopup.Accept();
            var window = _shop.OpenedContainer;
            window.SkipToRewards();
            var tankIndex = Enumerable.Range(0, window.RewardCount)
                .First(i => window.RewardType(i) == _kit.Str("shop:reward/tank"));
            Assert.IsNotEmpty(window.RewardNote(tankIndex), "duplicate tank shows its compensation");
            Assert.IsTrue(window.ProgressShown, "the huge container keeps its guaranteed tank bar");
            Assert.AreEqual("0 / 10", window.ProgressCaption);

            var buttons = window.GetComponentsInChildren<Button>(true);
            Assert.IsFalse(buttons.Any(b => b.name == "btn_close"), "the results have no CLOSE button");
            Assert.AreEqual(1, buttons.Count(b => b.name == "btn_menu"), "one back arrow is the way out");
            Assert.AreSame(window.BackButton, buttons.First(b => b.name == "btn_menu"));
            window.BackButton.onClick.Invoke();
            Assert.AreEqual(0, homeCalls, "the back arrow returns to the shop, not to the hangar");
            Assert.IsTrue(_shop.Root.gameObject.activeSelf, "the shop stays open");
            Assert.AreEqual(ShopScreen.TabContainers, _shop.Tab, "on the containers tab");
            Assert.IsNull(_shop.OpenedContainer);

            window.Close();
            _shop.Hide();
            Assert.AreEqual(0, homeCalls, "closing twice and a parent-driven hide do not navigate");
            Assert.AreEqual(0, _canvas.GetComponentsInChildren<ContainerOpenScreen>(true).Length);
        }

        [Test]
        public void PoorAccount_UsesErrorPopupAndClosingDoesNotAdvanceGuarantee()
        {
            Build(false);
            _account.Credits = 0;
            _shop.PressBuy(ShopScreen.TabContainers, 0);
            Assert.IsNotNull(_shop.ActivePopup);
            _shop.ActivePopup.Accept();
            Assert.IsNull(_shop.OpenedContainer);
            Assert.AreEqual(0, _shop.Service.GuaranteedPoints(ShopCatalog.ContainerOffers()[0]));
        }
    }
}
