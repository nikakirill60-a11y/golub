using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Guards the nation plate the carousel paints behind every card. The
    /// client data knows eight buckets: the seven playable nations plus the
    /// crossover vehicles, which wear <c>TankInfoMain.nation_flags[0]</c>.
    /// </summary>
    public sealed class TankNationPlateTests
    {
        private static TanksDatabase LoadDatabase()
        {
            var text = Resources.Load<TextAsset>("Data/tanks");
            Assert.IsNotNull(text, "Data/tanks.json is missing from Resources");
            return TanksDatabase.Parse(text.text);
        }

        /// <summary>
        /// Every card has to resolve to the plate of its own nation. Nation 8
        /// used to fall out of the clamp and borrow the Soviet flag.
        /// </summary>
        [Test]
        public void EveryTank_ResolvesToThePlateOfItsOwnNation()
        {
            var database = LoadDatabase();
            var crossovers = 0;
            var tree = 0;
            foreach (var card in database.Tanks)
            {
                Assert.AreNotEqual(Nation.None, card.Nation, card.Name);
                Assert.AreEqual(
                    "tank_nation_" + (int)card.Nation,
                    HangarUiKit.CarouselNationRegion(card.Nation),
                    card.Name);
                if (card.Nation == Nation.Other)
                {
                    crossovers++;
                    if (card.TreeTank) { tree++; }
                }
            }

            // 11.20 mixed-nation roster: 82 vehicles, 7 of them the AC branch.
            Assert.AreEqual(82, crossovers);
            Assert.AreEqual(7, tree);
            Assert.AreEqual(
                "tank_nation_8", HangarUiKit.CarouselNationRegion(Nation.Other));
        }
    }
}
