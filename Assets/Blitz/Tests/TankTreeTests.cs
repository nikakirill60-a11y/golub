using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Guards the complete researchable Blitz 4.8.0 database. Expected values
    /// below come from the client XML, so exporter or data-model drift fails in
    /// EditMode instead of on the device.
    /// </summary>
    public sealed class TankTreeTests
    {
        private static TanksDatabase LoadDatabase()
        {
            var text = Resources.Load<TextAsset>("Data/tanks");
            Assert.IsNotNull(text, "Data/tanks.json is missing from Resources");
            return TanksDatabase.Parse(text.text);
        }

        /// <summary>
        /// The re-laid USA and German trees (Tools/tech_tree_layout.py): no two
        /// cards share a slot, and every research arrow, including the 11.20
        /// additional links, moves at most one row. Only the tier I starter
        /// fans out further.
        /// </summary>
        [TestCase(Nation.Usa, 0)]
        [TestCase(Nation.Germany, 1)]
        public void HybridTrees_KeepEveryResearchArrowWithinOneRow(Nation nation, int starterFanOut)
        {
            var database = LoadDatabase();
            var slots = new System.Collections.Generic.HashSet<string>();
            var wideStarterArrows = 0;
            foreach (var tank in database.TreeTanks(nation))
            {
                if (tank.Premium) { continue; }
                Assert.IsTrue(slots.Add(tank.Level + "/" + tank.TreeBranch), tank.Name + ": shared tree slot");
                foreach (var name in tank.UnlockTanks.Keys)
                {
                    var next = database.Get(name.ToLowerInvariant());
                    Assert.IsNotNull(next, tank.Name + " -> " + name);
                    var levels = next.Level - tank.Level;
                    var rows = Mathf.Abs(next.TreeBranch - tank.TreeBranch);
                    Assert.That(levels, Is.InRange(0, 1), tank.Name + " -> " + name);
                    if (levels == 0)
                    {
                        Assert.AreEqual(1, rows, tank.Name + " -> " + name + ": same-tier arrow");
                    }
                    else if (rows > 1 && tank.Level == 1 && rows == 2)
                    {
                        wideStarterArrows++;
                    }
                    else
                    {
                        Assert.LessOrEqual(rows, 1, tank.Name + " -> " + name);
                    }
                }
            }

            Assert.AreEqual(starterFanOut, wideStarterArrows);
            Assert.AreEqual(-2, database.Get("a100_t49").TreeBranch);
            Assert.AreEqual(database.Get("a100_t49").TreeBranch, database.Get("a99_t92e1").TreeBranch);
        }

        [Test]
        public void Database_ContainsTheCompleteBlitz480ResearchTrees()
        {
            var database = LoadDatabase();
            Assert.AreEqual(522, database.Count);

            // Tree slots hold the researchable cards plus the 24 premiums the
            // client sells for gold from inside the tree itself.
            var expectedTree = new[] { 50, 67, 57, 42, 13, 33, 10 };
            var expectedResearchable = new[] { 43, 60, 52, 38, 13, 32, 10 };
            for (var nationId = 0; nationId < expectedTree.Length; nationId++)
            {
                var nation = (Nation)(nationId + 1);
                var tree = database.TreeTanks(nation);
                Assert.AreEqual(expectedTree[nationId], tree.Count, nation.ToString());
                var researchable = 0;
                foreach (var card in tree)
                {
                    Assert.AreEqual(nation, card.Nation, card.Name);
                    Assert.IsTrue(card.TreeTank, card.Name);
                    Assert.That(card.Level, Is.InRange(1, 10), card.Name);
                    if (!card.Premium)
                    {
                        researchable++;
                    }
                }

                Assert.AreEqual(expectedResearchable[nationId], researchable, nation.ToString());
            }
        }

        /// <summary>
        /// The 24 premiums the client places inside the tree have to read as
        /// the gold cards they are, not as free researched vehicles.
        /// </summary>
        [Test]
        public void TreePremiums_RenderAsGoldCardsPricedInGold()
        {
            var database = LoadDatabase();
            var premiums = 0;
            for (var nationId = 1; nationId <= 7; nationId++)
            {
                foreach (var card in database.TreeTanks((Nation)nationId))
                {
                    if (!card.Premium)
                    {
                        Assert.AreEqual(
                            "tree_tank_bg6",
                            HangarUiKit.TreeCardRegion(ResearchStatus.Researched, false),
                            card.Name);
                        continue;
                    }

                    premiums++;
                    Assert.Greater(card.CostGold, 0, card.Name);
                    Assert.AreEqual(card.CostGold, TankResearchService.PurchasePrice(card), card.Name);
                    Assert.AreEqual(
                        "tree_tank_bg3",
                        HangarUiKit.TreeCardRegion(ResearchStatus.Researched, true),
                        card.Name);
                }
            }

            Assert.AreEqual(24, premiums);
        }

        [Test]
        public void ResearchGraph_MatchesTheSourceUnlockCosts()
        {
            var database = LoadDatabase();

            Assert.AreEqual(550, database.ResearchCost("bt-2"));
            Assert.AreEqual(2700, database.ResearchCost("bt-7"));
            Assert.AreEqual(5410, database.ResearchCost("a-20"));
            Assert.AreEqual(12990, database.ResearchCost("t-34"));
            Assert.AreEqual(27825, database.ResearchCost("t-34-85"));
            Assert.AreEqual(26200, database.ResearchCost("t-43"));
            Assert.AreEqual(93653, database.ResearchCost("t-44"));
            Assert.AreEqual(142000, database.ResearchCost("t-54"));
            Assert.AreEqual(275000, database.ResearchCost("t62a"));

            // MS-1 is the root: nothing unlocks it and it is free.
            Assert.AreEqual(0, database.ResearchCost("ms-1"));
            Assert.AreEqual(0, database.Get("ms-1").CostXp);
            Assert.AreEqual(0, database.Get("ms-1").Cost);

            // The complete tree keeps both side branches as normal cards.
            var root = database.Get("ms-1");
            Assert.AreEqual(3, root.UnlockTanks.Count);
            Assert.AreEqual(510, root.UnlockTanks["at-1"]);
            Assert.AreEqual(600, root.UnlockTanks["t-26"]);
            Assert.IsNotNull(database.Get("at-1"));
            Assert.IsNotNull(database.Get("t-26"));

            // A-20 has one canonical edge to T-34.
            Assert.AreEqual(1, database.Get("a-20").UnlockTanks.Count);
            Assert.AreEqual(1, database.PrecedingTanks("t-34").Count);
            Assert.AreEqual("a-20", database.PrecedingTanks("t-34")[0].Name);
        }

        [Test]
        public void Cards_KeepTheSourceStatsAndPurchaseCosts()
        {
            var database = LoadDatabase();

            var bt2 = database.Get("bt-2");
            Assert.AreEqual("БТ-2", bt2.DisplayName);
            Assert.AreEqual(208, bt2.Health);
            Assert.AreEqual(TankClass.Light, bt2.Class);
            Assert.AreEqual(3500, bt2.Cost);
            Assert.AreEqual(50f, bt2.SpeedForward, 0.0001f);
            Assert.AreEqual(20f, bt2.SpeedBackward, 0.0001f);
            Assert.AreEqual(5500f, bt2.HullWeight, 0.0001f);
            Assert.AreEqual(1f, bt2.FarmCoefficient, 0.0001f);

            var t62a = database.Get("t62a");
            Assert.AreEqual("Т-62А", t62a.DisplayName);
            Assert.AreEqual(1520, t62a.Health);
            Assert.AreEqual(TankClass.Medium, t62a.Class);
            Assert.AreEqual(6100000, t62a.Cost);
            Assert.AreEqual(0, t62a.UnlockTanks.Count);
        }

        [Test]
        public void Modules_KeepTheSourceOrderCostsAndShells()
        {
            var database = LoadDatabase();

            // The lists are the ones the Blitz client 4.8.0 authors: BT-2 has
            // two turrets, four guns and a single engine and chassis.
            var bt2 = database.Get("bt-2");
            Assert.AreEqual(2, bt2.Turrets.Count);
            Assert.AreEqual(4, bt2.Guns.Count);
            Assert.AreEqual(1, bt2.Engines.Count);
            Assert.AreEqual(1, bt2.Tracks.Count);

            Assert.AreEqual(0, bt2.Engines[0].CostXp);
            Assert.AreEqual("M-17T", bt2.Engines[0].Name);
            Assert.AreEqual(450f, bt2.Engines[0].Power, 0.0001f);

            Assert.AreEqual(0, bt2.Tracks[0].CostXp);
            Assert.AreEqual("BT-2bis", bt2.Tracks[0].Name);
            Assert.AreEqual(50f, bt2.Tracks[0].RotationSpeed, 0.0001f);

            // Stock turret and its stock barrel; the upgrades carry the cost.
            Assert.AreEqual(0, bt2.Turrets[0].CostXp);
            Assert.AreEqual("BT-2_cylindrical", bt2.Turrets[0].Name);
            Assert.AreEqual(150, bt2.Turrets[1].CostXp);
            Assert.AreEqual("BT-2_bis", bt2.Turrets[1].Name);

            var stockGun = bt2.Guns[0];
            Assert.AreEqual("_37mm_B-3", stockGun.Name);
            Assert.AreEqual(3, stockGun.Shells.Count);
            Assert.AreEqual(ShellType.Ap, stockGun.Shells[0].Type);
            Assert.AreEqual(40f, stockGun.Shells[0].Damage, 0.0001f);
            Assert.AreEqual(40f, stockGun.Shells[0].Penetration, 0.0001f);
            Assert.IsTrue(stockGun.Shells[1].Premium);
            Assert.AreEqual(ShellType.Apcr, stockGun.Shells[1].Type);
            Assert.AreEqual(ShellType.He, stockGun.Shells[2].Type);

            // MS-1 keeps its autoloader gun with the source burst pattern.
            var burstGun = database.Get("ms-1").Guns[2];
            Assert.AreEqual("_20mm_TNSH", burstGun.Name);
            Assert.IsTrue(burstGun.Burst.HasBursts);
            Assert.AreEqual(4, burstGun.Burst.Bursts.Count);
            Assert.AreEqual(4, burstGun.Burst.Bursts[0]);
            Assert.AreEqual(0.1f, burstGun.Burst.IntraClip, 0.0001f);
        }

        /// <summary>
        /// db_tanks.gd get_tank_name_color: collector light blue beats premium
        /// gold, tech tree tanks stay white. carousel_tank.tscn and
        /// ui_tech_tree_tank.tscn share this one colour source.
        /// </summary>
        [Test]
        public void TankNames_WearTheSourceStatusColours()
        {
            var database = LoadDatabase();
            Assert.AreEqual(UiTheme.Label, HangarUiKit.TankNameColor(null));

            var collectors = 0;
            var premiums = 0;
            var regular = 0;
            foreach (var card in database.Tanks)
            {
                var color = HangarUiKit.TankNameColor(card);
                if (card.Collector)
                {
                    Assert.AreEqual(HangarUiKit.TankNameCollector, color, card.Name);
                    collectors++;
                }
                else if (card.Premium)
                {
                    Assert.AreEqual(HangarUiKit.TankNamePremium, color, card.Name);
                    premiums++;
                }
                else
                {
                    Assert.AreEqual(UiTheme.Label, color, card.Name);
                    regular++;
                }
            }

            Assert.Greater(collectors, 0);
            Assert.Greater(premiums, 0);
            Assert.Greater(regular, 0);
            Assert.AreEqual(database.Count, collectors + premiums + regular);
        }

        /// <summary>
        /// apply_tank_level_icon and apply_tank_class_icon: a premium wears the
        /// gold glyph variants, a collector keeps the white glyph and tints it
        /// blue, and collector wins over premium.
        /// </summary>
        [Test]
        public void TankGlyphs_FollowTheSourcePremiumAndCollectorRules()
        {
            var database = LoadDatabase();
            var golden = 0;
            var tinted = 0;
            foreach (var card in database.Tanks)
            {
                var level = HangarUiKit.TankLevelRegion(card);
                var tankClass = HangarUiKit.TankClassRegion(card);
                var tint = HangarUiKit.TankGlyphTint(card);
                var plainLevel = HangarUiKit.LevelRegion(card.Level);
                var plainClass = HangarUiKit.ClassRegion(card.Class);

                if (card.Collector)
                {
                    Assert.AreEqual(plainLevel, level, card.Name);
                    Assert.AreEqual(plainClass, tankClass, card.Name);
                    Assert.AreEqual(HangarUiKit.TankNameCollector, tint, card.Name);
                    tinted++;
                }
                else if (card.Premium)
                {
                    Assert.AreEqual(plainLevel + "_prem", level, card.Name);
                    Assert.AreEqual(plainClass + "_prem", tankClass, card.Name);
                    Assert.AreEqual(Color.white, tint, card.Name);
                    golden++;
                }
                else
                {
                    Assert.AreEqual(plainLevel, level, card.Name);
                    Assert.AreEqual(plainClass, tankClass, card.Name);
                    Assert.AreEqual(Color.white, tint, card.Name);
                }
            }

            Assert.Greater(golden, 0);
            Assert.Greater(tinted, 0);
        }

        /// <summary>Every glyph a card can ask for has to exist in the atlas.</summary>
        [Test]
        public void TankGlyphRegions_ExistInTheHangarAtlas()
        {
            var text = Resources.Load<TextAsset>("Data/atlas_regions");
            Assert.IsNotNull(text, "Data/atlas_regions.json is missing");
            var regions = text.text;
            foreach (var card in LoadDatabase().Tanks)
            {
                StringAssert.Contains(
                    "\"" + HangarUiKit.TankLevelRegion(card) + "\"", regions, card.Name);
                StringAssert.Contains(
                    "\"" + HangarUiKit.TankClassRegion(card) + "\"", regions, card.Name);
            }
        }
    }
}
