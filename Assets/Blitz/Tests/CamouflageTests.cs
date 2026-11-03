using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Hangar;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The Blitz 4.8.0 camouflages, bought once per vehicle here: the ported
    /// catalog, the shop economy, the battle that wears a camouflage and
    /// leaves it on the tank, the saves and network messages that carry the
    /// slots, and the paint on the hangar stand and on a battle tank.
    /// </summary>
    public sealed class CamouflageTests
    {
        private const string StonySoil = "the_stony_soil";
        private const string ResourcesRoot = "Assets/Blitz/Resources/";

        /// <summary>
        /// Two camouflages on a tiny roster: forest fits the summer slot only,
        /// rock fits all three, and "pricey" takes rock alone.
        /// </summary>
        private static readonly string TestJson = (
            "{'base_price':1000,'concealment_bonus':0.05,'slots':['summer','winter','desert'],"
            + "'map_kinds':{'green':'summer','snow':'winter','sand':'desert'},"
            + "'groups':{'standard':{'name':'group/standard','rarity':10}},"
            + "'camouflages':{"
            + "'forest':{'id':1,'name':'camo/forest','description':'camo/descr','group':'standard',"
            + "'kinds':['summer'],'category':'disposable-cheap','tile':'tile_forest','slot_icon':'slot_forest'},"
            + "'rock':{'id':2,'name':'camo/rock','description':'camo/descr','group':'standard',"
            + "'kinds':['summer','winter','desert'],'category':'disposable-cheap','tile':'tile_rock','slot_icon':'slot_rock'}},"
            + "'tanks':{"
            + "'cheap':{'pack':'480','price_factor':0.5,'price':500,'masks':{'CHEAP':'Masks/cheap'},"
            + "'camouflages':{'forest':PAINT,'rock':PAINT}},"
            + "'pricey':{'pack':'1120','price_factor':2,'price':2000,'masks':{'PRICEY':'Masks/pricey'},"
            + "'camouflages':{'rock':PAINT}}},"
            + "'unsupported':{'plain':'hull albedo material binds no decalmask'}}")
            .Replace("PAINT", "{'alpha':0.8,'scale':[0.5,0.25],'offset':[0.1,0.2],'texture':'Textures/common'}")
            .Replace('\'', '"');

        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                UnityEngine.Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        private static CamouflageCatalog Test()
        {
            return CamouflageCatalog.Parse(TestJson);
        }

        private static CamouflageCatalog Ported()
        {
            return CamouflageCatalog.Parse(BlitzResources.LoadText(CamouflageCatalog.DataResource));
        }

        private static TanksDatabase Roster()
        {
            return TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
        }

        private static JsonNode Json(string text)
        {
            return JsonNode.Parse(text.Replace('\'', '"'));
        }

        private static string Patched(string from, string to)
        {
            StringAssert.Contains(from, TestJson);
            return TestJson.Replace(from, to);
        }

        // ------------------------------------------------------------------
        // The ported catalog.
        // ------------------------------------------------------------------

        [Test]
        public void PortedCatalog_IsTheStonySoilOfThe480Shop()
        {
            var catalog = Ported();
            Assert.AreEqual(2800, catalog.BasePrice);
            Assert.AreEqual(0.02f, catalog.ConcealmentBonus, 1e-6f);
            Assert.AreEqual(1, catalog.All.Count);

            var info = catalog.Get(StonySoil);
            Assert.IsNotNull(info);
            Assert.AreEqual(31, info.SourceId);
            Assert.AreEqual("#vehicle_customization:camouflage/name/TheStonySoil", info.NameKey);
            Assert.AreEqual("disposable-cheap", info.Category);
            Assert.AreEqual("camo_tile_the_stony_soil", info.TileRegion);
            Assert.AreEqual("camo_slot_the_stony_soil", info.SlotIconRegion);
            CollectionAssert.AreEqual(
                new[] { CamouflageKind.Summer, CamouflageKind.Winter, CamouflageKind.Desert }, info.Kinds);
            Assert.IsNotNull(catalog.Group(info.Group));
        }

        [TestCase("t-34", 2800, "480")]
        [TestCase("ms-1", 560, "480")]
        [TestCase("r71_is_2b", 3920, "480")]
        [TestCase("a111_t25_pilot", 4480, "1120")]
        public void PortedPrices_ScaleTheShopPriceByTheVehicleFactor(string name, int price, string pack)
        {
            var catalog = Ported();
            var tank = catalog.Tank(name);
            Assert.IsNotNull(tank, name);
            Assert.AreEqual(price, tank.Price);
            Assert.AreEqual(price, catalog.Price(name, StonySoil));
            Assert.AreEqual(Mathf.RoundToInt(catalog.BasePrice * tank.PriceFactor), tank.Price);
            Assert.AreEqual(pack, tank.Pack);
            Assert.AreEqual(
                "Camouflages/Textures/" + pack + "/common/common_01", tank.PaintOf(StonySoil).Texture);
        }

        [TestCase("gb_mark_i", "decalmask")]
        [TestCase("oth10_warduck", "decalmask")]
        [TestCase("oth08_wh_vindicator", "eventCamouflage_user")]
        [TestCase("oth09_wh_predator", "eventCamouflage_user")]
        public void PortedCatalog_KeepsTheClientsUnsupportedTanksBare(string name, string reason)
        {
            var catalog = Ported();
            Assert.IsFalse(catalog.Supports(name));
            StringAssert.Contains(reason, catalog.UnsupportedReason(name));
            Assert.AreEqual(0, catalog.Price(name));
            for (var slot = 0; slot < CamouflageCatalog.SlotCount; slot++)
            {
                CollectionAssert.IsEmpty(catalog.Available(name, CamouflageCatalog.KindOfSlot(slot)));
            }
        }

        [Test]
        public void PortedCatalog_CoversEveryRosterTankOrSaysWhyNot()
        {
            var catalog = Ported();
            var unexplained = new List<string>();
            var supported = 0;
            foreach (var card in Roster().Tanks)
            {
                var takes = catalog.Supports(card.Name);
                var excused = !string.IsNullOrEmpty(catalog.UnsupportedReason(card.Name));
                if (takes == excused) { unexplained.Add(card.Name); }
                if (!takes) { continue; }
                supported++;
                Assert.IsTrue(catalog.Tank(card.Name).Has(StonySoil), card.Name);
            }

            CollectionAssert.IsEmpty(unexplained, "a tank either takes a camouflage or names why not");
            Assert.AreEqual(catalog.SupportedCount, supported, "camouflage data only for roster tanks");
        }

        [Test]
        public void PortedCatalog_PaintsEveryCardFromThePackItWasPortedFrom()
        {
            var catalog = Ported();
            var cards = JsonNode.Parse(BlitzResources.LoadText(BlitzResources.TanksData))["tanks"];
            var wrong = new List<string>();
            var packs = new HashSet<string>();
            foreach (var card in cards.Items)
            {
                var name = card["name"].AsString();
                var tank = catalog.Tank(name);
                if (tank == null) { continue; }
                var pack = card["source_version"].AsString() == "11.20" ? "1120" : "480";
                packs.Add(tank.Pack);
                if (tank.Pack != pack) { wrong.Add(name + " " + tank.Pack); }
                foreach (var mask in tank.Masks.Values)
                {
                    if (!mask.StartsWith("Camouflages/Masks/" + pack + "/", StringComparison.Ordinal))
                    {
                        wrong.Add(name + " " + mask);
                    }
                }

                if (!tank.PaintOf(StonySoil).Texture.StartsWith("Camouflages/Textures/" + pack + "/", StringComparison.Ordinal))
                {
                    wrong.Add(name + " " + tank.PaintOf(StonySoil).Texture);
                }
            }

            CollectionAssert.IsEmpty(wrong, "11.20 cards wear the 11.20 camouflage files");
            CollectionAssert.AreEquivalent(new[] { "480", "1120" }, packs);
        }

        [Test]
        public void PortedTextures_ShipWithTheirImportRules()
        {
            var catalog = Ported();
            var masks = new HashSet<string>();
            var patterns = new HashSet<string>();
            var hullless = new List<string>();
            foreach (var card in Roster().Tanks)
            {
                var tank = catalog.Tank(card.Name);
                if (tank == null) { continue; }
                if (tank.MaskOf(TankModelAssets.For(card).Id) == null) { hullless.Add(card.Name); }
                foreach (var mask in tank.Masks.Values) { masks.Add(mask); }
                patterns.Add(tank.PaintOf(StonySoil).Texture);
            }

            CollectionAssert.IsEmpty(hullless, "the hull albedo of every supported tank has a mask");
            var missing = masks.Concat(patterns).Where(path => !File.Exists(ResourcesRoot + path + ".png")).ToList();
            CollectionAssert.IsEmpty(missing);

            foreach (var mask in masks)
            {
                var importer = (TextureImporter)AssetImporter.GetAtPath(ResourcesRoot + mask + ".png");
                Assert.AreEqual(TextureImporterType.SingleChannel, importer.textureType, mask);
                Assert.IsFalse(importer.sRGBTexture, mask);
                Assert.AreEqual(TextureWrapMode.Repeat, importer.wrapMode, mask);
            }

            foreach (var pattern in patterns)
            {
                var importer = (TextureImporter)AssetImporter.GetAtPath(ResourcesRoot + pattern + ".png");
                Assert.IsFalse(importer.sRGBTexture, pattern);
                Assert.AreEqual(TextureWrapMode.Repeat, importer.wrapMode, pattern);
                Assert.AreEqual(TextureImporterCompression.Uncompressed, importer.textureCompression, pattern);
            }
        }

        [Test]
        public void EveryBattleMap_HasAClimate()
        {
            var catalog = Ported();
            var maps = new HashSet<string>(BattleMapCatalog.Ids);
            foreach (var file in Directory.GetFiles(ResourcesRoot + "Data", "map_*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(file).Substring("map_".Length);
                if (!id.EndsWith("_objects", StringComparison.Ordinal)) { maps.Add(id); }
            }

            CamouflageKind kind;
            var unknown = maps.Where(map => !catalog.TryKindOfMap(map, out kind)).ToList();
            CollectionAssert.IsEmpty(unknown);
            Assert.IsTrue(catalog.TryKindOfMap("moon", out kind));
            Assert.AreEqual(CamouflageKind.Winter, kind);
            Assert.IsTrue(catalog.TryKindOfMap("desert_train", out kind));
            Assert.AreEqual(CamouflageKind.Desert, kind);
            Assert.IsTrue(catalog.TryKindOfMap("rudniki", out kind));
            Assert.AreEqual(CamouflageKind.Summer, kind);
        }

        [Test]
        public void EveryCamouflageString_IsTranslated()
        {
            var catalog = Ported();
            var keys = new List<string>
            {
                CamouflageScreen.TitleKey, CamouflageScreen.NoAvailableKey,
                CamouflageScreen.BuyKey, CamouflageScreen.InstallKey, CamouflageScreen.RemoveKey,
                CamouflageScreen.CostKey, CamouflageScreen.BuyConfirmKey,
                CamouflageScreen.HiddenInBattleKey, CamouflageScreen.ConfirmBuyKey,
                CamouflageScreen.ConfirmCancelKey,
                CamouflageService.ErrorNotEnoughCredits, "CONFIRM", "ERROR",
                "#settings:Graphics/Camouflage/Title", "#settings:Graphics/Camouflage/Description",
                "#settings:Graphics/Camouflage/Off", "#settings:Graphics/Camouflage/ForPlayer",
                "#settings:Graphics/Camouflage/ForAll",
            };
            for (var slot = 0; slot < CamouflageCatalog.SlotCount; slot++)
            {
                keys.Add(CamouflageCatalog.SlotNameKey(CamouflageCatalog.KindOfSlot(slot)));
            }

            foreach (var info in catalog.All)
            {
                keys.Add(info.NameKey);
                keys.Add(info.DescriptionKey);
            }

            foreach (var group in catalog.Groups) { keys.Add(group.NameKey); }

            var lang = JsonNode.Parse(BlitzResources.LoadText("lang"))["keys"];
            var untranslated = new List<string>();
            foreach (var key in keys)
            {
                foreach (var language in new[] { "en", "ru" })
                {
                    if (string.IsNullOrEmpty(lang[key][language].AsString()))
                    {
                        untranslated.Add(language + ": " + key);
                    }
                }
            }

            CollectionAssert.IsEmpty(untranslated);
        }

        // ------------------------------------------------------------------
        // Catalog rules.
        // ------------------------------------------------------------------

        [Test]
        public void Catalog_RefusesDataTheShopCannotDraw()
        {
            Assert.Throws<InvalidOperationException>(() => CamouflageCatalog.Parse(string.Empty));
            Assert.Throws<InvalidOperationException>(() => CamouflageCatalog.Parse("[]"));
            Assert.Throws<InvalidOperationException>(() => CamouflageCatalog.Parse(
                Patched("\"slots\":[\"summer\",\"winter\",\"desert\"]", "\"slots\":[\"summer\",\"desert\",\"winter\"]")));
            Assert.Throws<InvalidOperationException>(() => CamouflageCatalog.Parse(
                Patched("\"kinds\":[\"summer\"]", "\"kinds\":[\"autumn\"]")));
            Assert.Throws<InvalidOperationException>(() => CamouflageCatalog.Parse(
                Patched("\"group\":\"standard\",\"kinds\":[\"summer\"]", "\"group\":\"rare\",\"kinds\":[\"summer\"]")));
            Assert.AreEqual(0, CamouflageCatalog.Empty.SupportedCount);
            Assert.IsFalse(CamouflageCatalog.Empty.Supports("t-34"));
        }

        [Test]
        public void Catalog_ListsWhatEachSlotOfATankTakes()
        {
            var catalog = Test();
            CollectionAssert.AreEqual(
                new[] { "forest", "rock" }, catalog.Available("cheap", CamouflageKind.Summer).Select(c => c.Id));
            CollectionAssert.AreEqual(
                new[] { "rock" }, catalog.Available("cheap", CamouflageKind.Winter).Select(c => c.Id));
            CollectionAssert.AreEqual(
                new[] { "rock" }, catalog.Available("pricey", CamouflageKind.Summer).Select(c => c.Id));
            CollectionAssert.IsEmpty(catalog.Available("plain", CamouflageKind.Summer));

            Assert.IsTrue(catalog.CanInstall("cheap", CamouflageKind.Summer, "forest"));
            Assert.IsFalse(catalog.CanInstall("cheap", CamouflageKind.Desert, "forest"));
            Assert.IsFalse(catalog.CanInstall("pricey", CamouflageKind.Summer, "forest"));
            Assert.IsFalse(catalog.CanInstall("cheap", CamouflageKind.Summer, "ghost"));
            Assert.AreEqual(500, catalog.Price("cheap"));
            Assert.AreEqual(0, catalog.Price("pricey", "forest"));
            Assert.AreEqual(0, catalog.Price("plain"));
            Assert.AreEqual("hull albedo material binds no decalmask", catalog.UnsupportedReason("plain"));
            Assert.IsNull(catalog.UnsupportedReason("cheap"));

            var tank = catalog.Tank("cheap");
            var paint = tank.PaintOf("forest");
            Assert.AreEqual(0.8f, paint.Alpha, 1e-6f);
            Assert.AreEqual("Textures/common", paint.Texture);
            Assert.AreEqual("Masks/cheap", tank.MaskOf("CHEAP"));
            Assert.IsNull(tank.MaskOf("PRICEY"));
            Assert.IsNull(tank.PaintOf("ghost"));

            // DAVA's V runs top-down: Unity samples (1 - v) * scale + offset.
            var offset = TankCamouflageView.UnityOffset(paint);
            Assert.AreEqual(0.1f, offset.x, 1e-6f);
            Assert.AreEqual(1f - 0.25f - 0.2f, offset.y, 1e-6f);

            CamouflageKind kind;
            Assert.IsTrue(catalog.TryKindOfMap("snow", out kind));
            Assert.AreEqual(CamouflageKind.Winter, kind);
            Assert.IsFalse(catalog.TryKindOfMap("void", out kind));
            Assert.IsFalse(catalog.TryKindOfMap(null, out kind));
        }

        // ------------------------------------------------------------------
        // The shop economy.
        // ------------------------------------------------------------------

        [Test]
        public void Shop_BuysEachCamouflageOnceAndMovesItForFree()
        {
            var account = new AccountData { Credits = 3000 };
            var save = TankSaveData.WithName("cheap");
            var shop = new CamouflageService(account, Test());

            Assert.AreEqual(CamouflageAction.Buy, shop.Action(save, CamouflageKind.Summer, "forest"));
            Assert.AreEqual(500, shop.ActionCost(save, CamouflageKind.Summer, "forest"));
            var bought = shop.Apply(save, CamouflageKind.Summer, "forest");
            Assert.IsTrue(bought.Ok);
            Assert.AreEqual(500, bought.Paid);
            Assert.AreEqual(2500, account.Credits);
            Assert.AreEqual("forest", CamouflageService.Installed(save, CamouflageKind.Summer));
            Assert.IsTrue(CamouflageService.Owns(save, "forest"));
            Assert.IsTrue(CamouflageService.AnyInstalled(save));

            // Another camouflage for the same slot is a purchase of its own.
            Assert.AreEqual(CamouflageAction.Buy, shop.Action(save, CamouflageKind.Summer, "rock"));
            Assert.AreEqual(500, shop.ActionCost(save, CamouflageKind.Summer, "rock"), "one tank price for every camouflage");
            Assert.IsTrue(shop.Apply(save, CamouflageKind.Summer, "rock").Ok);
            Assert.AreEqual(2000, account.Credits);
            Assert.AreEqual("rock", CamouflageService.Installed(save, CamouflageKind.Summer));
            CollectionAssert.AreEqual(new[] { "forest", "rock" }, save.OwnedCamouflages);

            // What the tank owns goes back in for free, into any slot that takes it.
            Assert.AreEqual(CamouflageAction.Install, shop.Action(save, CamouflageKind.Summer, "forest"));
            Assert.AreEqual(0, shop.ActionCost(save, CamouflageKind.Summer, "forest"));
            var installed = shop.Apply(save, CamouflageKind.Summer, "forest");
            Assert.IsTrue(installed.Ok);
            Assert.AreEqual(0, installed.Paid);
            Assert.AreEqual("forest", CamouflageService.Installed(save, CamouflageKind.Summer));
            Assert.AreEqual(CamouflageAction.Install, shop.Action(save, CamouflageKind.Winter, "rock"));
            Assert.IsTrue(shop.Apply(save, CamouflageKind.Winter, "rock").Ok);
            Assert.AreEqual("rock", CamouflageService.Installed(save, CamouflageKind.Winter));
            Assert.AreEqual(2000, account.Credits);

            // Removing refunds nothing, and the camouflage stays the tank's.
            Assert.AreEqual(CamouflageAction.Remove, shop.Action(save, CamouflageKind.Summer, "forest"));
            Assert.AreEqual(0, shop.ActionCost(save, CamouflageKind.Summer, "forest"));
            Assert.IsTrue(shop.Apply(save, CamouflageKind.Summer, "forest").Ok);
            Assert.IsTrue(shop.Apply(save, CamouflageKind.Winter, "rock").Ok);
            Assert.AreEqual(2000, account.Credits);
            Assert.IsFalse(CamouflageService.AnyInstalled(save));
            Assert.AreEqual(CamouflageAction.Install, shop.Action(save, CamouflageKind.Summer, "forest"));
            CollectionAssert.AreEqual(new[] { "forest", "rock" }, save.OwnedCamouflages);
        }

        [Test]
        public void Shop_RefusesWhatTheSlotCannotTakeOrTheAccountCannotPay()
        {
            var account = new AccountData { Credits = 499 };
            var save = TankSaveData.WithName("cheap");
            var shop = new CamouflageService(account, Test());

            var poor = shop.Apply(save, CamouflageKind.Winter, "rock");
            Assert.IsFalse(poor.Ok);
            Assert.AreEqual(CamouflageService.ErrorNotEnoughCredits, poor.ErrorKey);
            Assert.AreEqual(499, account.Credits);
            Assert.AreEqual(string.Empty, CamouflageService.Installed(save, CamouflageKind.Winter));

            Assert.AreEqual(CamouflageAction.None, shop.Action(save, CamouflageKind.Winter, "forest"), "forest is summer only");
            Assert.AreEqual(CamouflageAction.None, shop.Action(save, CamouflageKind.Summer, "ghost"));
            Assert.AreEqual(CamouflageAction.None, shop.Action(save, CamouflageKind.Summer, string.Empty));
            Assert.AreEqual(CamouflageAction.None, shop.Action(TankSaveData.WithName("plain"), CamouflageKind.Summer, "rock"));
            Assert.AreEqual(CamouflageAction.None, shop.Action(TankSaveData.WithName("pricey"), CamouflageKind.Summer, "forest"));
            Assert.IsFalse(shop.Apply(save, CamouflageKind.Winter, "forest").Ok);
            Assert.AreEqual(0, shop.ActionCost(null, CamouflageKind.Summer, "rock"));
            Assert.AreEqual(499, account.Credits);
        }

        [Test]
        public void HangarBadge_MarksASupportedBareTankUntilItsShopIsOpened()
        {
            var shop = new CamouflageService(new AccountData(), Test());
            var save = TankSaveData.WithName("cheap");
            Assert.IsTrue(shop.ShowsBadge(save));
            save.CamouflageSeen = true;
            Assert.IsFalse(shop.ShowsBadge(save));

            var dressed = TankSaveData.WithName("cheap");
            dressed.Camouflages[(int)CamouflageKind.Desert] = "rock";
            Assert.IsFalse(shop.ShowsBadge(dressed));
            Assert.IsFalse(shop.ShowsBadge(TankSaveData.WithName("plain")));
            Assert.IsFalse(shop.ShowsBadge(null));
        }

        [Test]
        public void StandAndBattle_WearTheSlotThatStillFits()
        {
            var catalog = Test();
            var save = TankSaveData.WithName("cheap");
            Assert.AreEqual(string.Empty, CamouflageService.HangarCamouflage(catalog, save));
            save.Camouflages[(int)CamouflageKind.Winter] = "rock";
            Assert.AreEqual("rock", CamouflageService.HangarCamouflage(catalog, save));
            save.Camouflages[(int)CamouflageKind.Summer] = "forest";
            Assert.AreEqual("forest", CamouflageService.HangarCamouflage(catalog, save), "the first slot in shop order");

            Assert.AreEqual("forest", CamouflageService.BattleCamouflage(catalog, save, "green"));
            Assert.AreEqual("rock", CamouflageService.BattleCamouflage(catalog, save, "snow"));
            Assert.AreEqual(string.Empty, CamouflageService.BattleCamouflage(catalog, save, "sand"));
            Assert.AreEqual(string.Empty, CamouflageService.BattleCamouflage(catalog, save, "void"));

            // Slots an old save or a forged hello filled with what the tank cannot take.
            var stale = TankSaveData.WithName("pricey");
            stale.Camouflages[(int)CamouflageKind.Summer] = "forest";
            stale.Camouflages[(int)CamouflageKind.Desert] = "ghost";
            Assert.AreEqual(string.Empty, CamouflageService.HangarCamouflage(catalog, stale));
            Assert.AreEqual(string.Empty, CamouflageService.BattleCamouflage(catalog, stale, "green"));
            Assert.AreEqual(string.Empty, CamouflageService.BattleCamouflage(catalog, stale, "sand"));

            var hello = new List<string> { "rock" };
            Assert.AreEqual("rock", CamouflageService.BattleCamouflage(catalog, "pricey", hello, "green"));
            Assert.AreEqual(string.Empty, CamouflageService.BattleCamouflage(catalog, "pricey", hello, "snow"), "a hello may carry fewer slots");
            Assert.AreEqual(string.Empty, CamouflageService.BattleCamouflage(catalog, "plain", hello, "green"));
            Assert.AreEqual(string.Empty, CamouflageService.BattleCamouflage(catalog, "pricey", null, "green"));
        }

        [Test]
        public void Concealment_GrowsOnlyForACamouflageTheTankCanWear()
        {
            var catalog = Test();
            Assert.AreEqual(0.05f, CamouflageService.ConcealmentBonus(catalog, "cheap", "forest"), 1e-6f);
            Assert.AreEqual(0f, CamouflageService.ConcealmentBonus(catalog, "pricey", "forest"));
            Assert.AreEqual(0f, CamouflageService.ConcealmentBonus(catalog, "cheap", "ghost"));
            Assert.AreEqual(0f, CamouflageService.ConcealmentBonus(catalog, "plain", "rock"));
            Assert.AreEqual(0f, CamouflageService.ConcealmentBonus(catalog, "cheap", string.Empty));
            Assert.AreEqual(0.02f, CamouflageService.ConcealmentBonus(Ported(), "t-34", StonySoil), 1e-6f);
        }

        // ------------------------------------------------------------------
        // The battle result.
        // ------------------------------------------------------------------

        [Test]
        public void Result_LeavesTheCamouflageOnTheTankAndChargesNothingForIt()
        {
            var result = BattleRewardCalculator.Calculate(new BattleRewardInput
            {
                Id = "camouflage-battle",
                MapId = "rudniki",
                TankId = "t-34",
                TankLevel = 1,
                Outcome = BattleOutcome.Defeat,
                HadFirstVictory = true,
                HadFirstBlood = true,
            });
            Assert.AreEqual(800, result.Credits);
            Assert.AreEqual(0, result.CreditCosts);

            var account = new AccountData { Credits = 0 };
            var tank = account.AddTank("t-34", true);
            tank.Camouflages[(int)CamouflageKind.Summer] = StonySoil;
            tank.OwnedCamouflages.Add(StonySoil);
            Assert.IsTrue(BattleResultApplier.Apply(account, result));
            Assert.AreEqual(800, account.Credits);
            Assert.AreEqual(StonySoil, tank.Camouflages[(int)CamouflageKind.Summer]);
            CollectionAssert.AreEqual(new[] { StonySoil }, tank.OwnedCamouflages);
        }

        // ------------------------------------------------------------------
        // Saves and settings.
        // ------------------------------------------------------------------

        [Test]
        public void TankSave_RoundTripsTheSlotsAndWhatTheTankOwns()
        {
            var save = TankSaveData.WithName("t-34");
            save.Camouflages[(int)CamouflageKind.Winter] = StonySoil;
            save.OwnedCamouflages.Add("forest");
            save.CamouflageSeen = true;
            var back = TankSaveData.FromJson(save.ToJson());
            CollectionAssert.AreEqual(new[] { string.Empty, StonySoil, string.Empty }, back.Camouflages);
            CollectionAssert.AreEqual(new[] { "forest", StonySoil }, back.OwnedCamouflages,
                "whatever a slot wears is owned");
            Assert.IsTrue(back.CamouflageSeen);

            var old = TankSaveData.FromJson(Json("{'name':'t-34'}"));
            CollectionAssert.AreEqual(new[] { string.Empty, string.Empty, string.Empty }, old.Camouflages);
            CollectionAssert.IsEmpty(old.OwnedCamouflages);
            Assert.IsFalse(old.CamouflageSeen);

            // A save of the disposable shop keeps what it wears, now for good.
            var disposable = TankSaveData.FromJson(Json(
                "{'name':'t-34','camouflages':['a','','a'],'camouflage_resupply':[false,true,true]}"));
            CollectionAssert.AreEqual(new[] { "a", string.Empty, "a" }, disposable.Camouflages);
            CollectionAssert.AreEqual(new[] { "a" }, disposable.OwnedCamouflages);

            var forged = TankSaveData.FromJson(Json(
                "{'name':'t-34','camouflages':['a','b','c','d','e'],'camouflages_owned':['b','','b','x']}"));
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, forged.Camouflages);
            CollectionAssert.AreEqual(new[] { "b", "x", "a", "c" }, forged.OwnedCamouflages,
                "no empty or repeated ids, and nothing for the slots that were cut off");
        }

        [Test]
        public void Settings_DrawEveryonesCamouflageByDefaultAndClamp()
        {
            var settings = new SettingsData();
            Assert.AreEqual(SettingsData.CamouflageForAll, settings.GraphicsCamouflage);
            Assert.AreEqual(SettingsData.CamouflageForAll, settings.GetInt("graphics_camouflage"));
            settings.SetInt("graphics_camouflage", 7);
            Assert.AreEqual(SettingsData.CamouflageForAll, settings.GraphicsCamouflage);
            settings.SetInt("graphics_camouflage", -2);
            Assert.AreEqual(SettingsData.CamouflageOff, settings.GraphicsCamouflage);
            settings.SetInt("graphics_camouflage", SettingsData.CamouflageForPlayer);
            Assert.AreEqual(
                SettingsData.CamouflageForPlayer, SettingsData.FromJson(settings.ToJson()).GraphicsCamouflage);
            Assert.AreEqual(SettingsData.CamouflageForAll, SettingsData.FromJson(Json("{}")).GraphicsCamouflage);
        }

        [TestCase(SettingsData.CamouflageForAll, true, true)]
        [TestCase(SettingsData.CamouflageForAll, false, true)]
        [TestCase(SettingsData.CamouflageForPlayer, true, true)]
        [TestCase(SettingsData.CamouflageForPlayer, false, false)]
        [TestCase(SettingsData.CamouflageOff, true, false)]
        [TestCase(SettingsData.CamouflageOff, false, false)]
        public void BattlePaint_FollowsTheCamouflageSetting(int setting, bool own, bool painted)
        {
            Assert.AreEqual(painted, CamouflageAssets.Painted(setting, own));
        }

        // ------------------------------------------------------------------
        // The network.
        // ------------------------------------------------------------------

        private static BattleNetPlan Plan()
        {
            var plan = new BattleNetPlan { MapId = "rudniki", Mode = BattleMode.Normal, Build = "1.0.0+1" };
            plan.Slots.Add(new BattleNetPlan.Slot { Team = 0, Owner = 0, TankId = "t-34", Name = "P0" });
            plan.Slots.Add(new BattleNetPlan.Slot { Team = 1, Owner = BattleNetPlan.BotOwner, TankId = "kv1", Name = "Bot" });
            return plan;
        }

        private static BattleNetHello Hello()
        {
            return new BattleNetHello { Build = "1.0.0+1", Name = "Tanker", TankId = "t-34" };
        }

        [Test]
        public void Plan_CarriesTheSeatCamouflageAndRefusesMalformedIds()
        {
            var plan = Plan();
            plan.Slots[0].Camouflage = StonySoil;
            var back = BattleNetPlan.FromBytes(plan.ToBytes());
            Assert.AreEqual(StonySoil, back.Slots[0].Camouflage);
            Assert.AreEqual(string.Empty, back.Slots[1].Camouflage);

            var spaced = Plan();
            spaced.Slots[0].Camouflage = "Stony Soil";
            Assert.Throws<InvalidDataException>(spaced.Validate);
            var longer = Plan();
            longer.Slots[0].Camouflage = new string('a', BattleNetCamouflage.MaxIdLength + 1);
            Assert.Throws<InvalidDataException>(() => longer.ToBytes());

            // A hostile host forging the id after its codec wrote it.
            var forged = Plan();
            forged.Slots[1].Camouflage = "zzzz";
            var bytes = forged.ToBytes();
            Assert.AreEqual("zzzz", BattleNetPlan.FromBytes(bytes).Slots[1].Camouflage);
            for (var i = bytes.Length - 4; i < bytes.Length; i++) { bytes[i] = (byte)'Z'; }
            Assert.Throws<InvalidDataException>(() => BattleNetPlan.FromBytes(bytes));
        }

        [Test]
        public void Hello_CarriesTheThreeSlotsAndRefusesForgedOnes()
        {
            Assert.AreEqual(5, BattleNetHello.FormatVersion);
            Assert.AreEqual(5, BattleNetPlan.FormatVersion);

            var hello = Hello();
            hello.Camouflages.AddRange(new[] { StonySoil, string.Empty, StonySoil });
            CollectionAssert.AreEqual(
                new[] { StonySoil, string.Empty, StonySoil }, BattleNetHello.FromBytes(hello.ToBytes()).Camouflages);

            var malformed = Hello();
            malformed.Camouflages.Add("Not An Id");
            CollectionAssert.AreEqual(
                new[] { string.Empty }, BattleNetHello.FromBytes(malformed.ToBytes()).Camouflages,
                "a client never sends what a host refuses");

            var bytes = Hello().ToBytes();
            CollectionAssert.IsEmpty(BattleNetHello.FromBytes(bytes).Camouflages);

            var tooMany = (byte[])bytes.Clone();
            BitConverter.GetBytes(CamouflageCatalog.SlotCount + 1).CopyTo(tooMany, tooMany.Length - 4);
            Assert.Throws<InvalidDataException>(() => BattleNetHello.FromBytes(tooMany));

            var padded = bytes.Concat(new byte[] { 0 }).ToArray();
            Assert.Throws<InvalidDataException>(() => BattleNetHello.FromBytes(padded));

            var older = (byte[])bytes.Clone();
            BitConverter.GetBytes(4).CopyTo(older, 0);
            Assert.Throws<InvalidDataException>(() => BattleNetHello.FromBytes(older), "a v4 client cannot join");
        }

        // ------------------------------------------------------------------
        // The paint.
        // ------------------------------------------------------------------

        private static List<Material> Painted(GameObject model, out int tracks)
        {
            var painted = new List<Material>();
            tracks = 0;
            foreach (var renderer in model.GetComponentsInChildren<MeshRenderer>(true))
            {
                var part = TankModuleView.TrimCloneSuffix(renderer.gameObject.name);
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || material.shader == null
                        || material.shader.name != CamouflageAssets.ShaderName)
                    {
                        continue;
                    }

                    painted.Add(material);
                    if (part == "trackL" || part == "trackR" || part == "tracks_crash") { tracks++; }
                }
            }

            return painted;
        }

        [TestCase("t-34", "480")]
        [TestCase("a111_t25_pilot", "1120")]
        public void HangarStand_WearsThePatternOfItsPack(string name, string pack)
        {
            var card = Roster().Get(name);
            Assert.IsNotNull(card, name);
            _root = new GameObject("CamouflageStandTest");
            var world = HangarWorld.Create(_root.transform, new SettingsData(), card, TankSaveData.WithName(name));
            Assert.IsNotNull(world.TankModel, name);
            int tracks;
            Assert.AreEqual(string.Empty, world.ShownCamouflage);
            CollectionAssert.IsEmpty(Painted(world.TankModel, out tracks));

            var count = world.ShowCamouflage(StonySoil);
            Assert.Greater(count, 0);
            Assert.AreEqual(StonySoil, world.ShownCamouflage);
            var materials = Painted(world.TankModel, out tracks);
            Assert.AreEqual(count, materials.Count);
            Assert.AreEqual(0, tracks, "the tracks keep their plain sheet");
            var pattern = CamouflageAssets.Texture("Camouflages/Textures/" + pack + "/common/common_01");
            Assert.IsNotNull(pattern);
            foreach (var material in materials)
            {
                Assert.AreSame(pattern, material.GetTexture("_CamoTex"), material.name);
                Assert.IsNotNull(material.GetTexture("_CamoMask"), material.name);
                Assert.IsNotNull(material.GetTexture("_BaseMap"), material.name);
            }

            Assert.AreEqual(0, world.ShowCamouflage(string.Empty));
            Assert.AreEqual(string.Empty, world.ShownCamouflage);
            CollectionAssert.IsEmpty(Painted(world.TankModel, out tracks));
            Assert.AreEqual(0, world.ShowCamouflage("ghost"));
            Assert.AreEqual(count, world.ShowCamouflage(StonySoil), "the twins are reused");
        }

        [Test]
        public void HangarStand_OfAnUnsupportedTankStaysPlain()
        {
            var card = Roster().Get("gb_mark_i");
            Assert.IsNotNull(card);
            _root = new GameObject("CamouflageStandTest");
            var world = HangarWorld.Create(_root.transform, new SettingsData(), card, TankSaveData.WithName(card.Name));
            Assert.AreEqual(0, world.ShowCamouflage(StonySoil));
            Assert.AreEqual(string.Empty, world.ShownCamouflage);
        }

        [Test]
        public void BattleTank_HidesInTheCamouflageWhetherOrNotItIsDrawn()
        {
            _root = new GameObject("CamouflageBattleTest");
            var origin = new Vector3(9000f, 10f, 0f);
            var spawn = new GameObject("Spawn").transform;
            spawn.SetParent(_root.transform, false);
            spawn.position = origin;
            var tank = BattleTank.Spawn(
                _root.transform, "Wearer", Roster().Get("t-34"), spawn,
                new Bounds(origin, new Vector3(1000f, 400f, 1000f)), 0);
            var plain = tank.CamoFactor();
            int tracks;

            tank.WearCamouflage(StonySoil, true);
            Assert.AreEqual(StonySoil, tank.CamouflageId);
            Assert.AreEqual(0.02f, tank.CamouflageBonus, 1e-6f);
            Assert.AreEqual(plain + 0.02f, tank.CamoFactor(), 1e-5f);
            Assert.Greater(Painted(tank.Model, out tracks).Count, 0);
            Assert.AreEqual(0, tracks);

            tank.RepaintCamouflage(false);
            CollectionAssert.IsEmpty(Painted(tank.Model, out tracks));
            Assert.AreEqual(0.02f, tank.CamouflageBonus, 1e-6f, "the bonus does not depend on the display");

            tank.WearCamouflage("forged_camouflage", true);
            Assert.AreEqual(string.Empty, tank.CamouflageId);
            Assert.AreEqual(0f, tank.CamouflageBonus);
            Assert.AreEqual(plain, tank.CamoFactor(), 1e-5f);
            CollectionAssert.IsEmpty(Painted(tank.Model, out tracks));
        }
    }
}
