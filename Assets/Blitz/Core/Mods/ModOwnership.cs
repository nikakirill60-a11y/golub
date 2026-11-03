using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;

namespace BlitzLegacy.Core.Mods
{
    /// <summary>
    /// A mod vehicle is never researched and never sold, so nothing in the
    /// shop or the tech tree can ever hand the player a save for it. The
    /// garage only lists vehicles with a bought save, which is why installed
    /// mods used to stay invisible. Installing one is the purchase.
    /// </summary>
    public static class ModOwnership
    {
        /// <summary>Gives the account a bought save for every installed mod.</summary>
        public static int Grant(AccountData account, TanksDatabase database)
        {
            if (account == null || database == null) { return 0; }

            var granted = 0;
            var cards = database.Tanks;
            for (var i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                if (card == null || !card.Mod) { continue; }

                var save = Find(account, card.Name);
                if (save == null)
                {
                    account.Tanks.Add(new TankSaveData { Name = card.Name, Bought = true });
                    granted++;
                }
                else if (!save.Bought)
                {
                    save.Bought = true;
                    granted++;
                }
            }

            return granted;
        }

        private static TankSaveData Find(AccountData account, string name)
        {
            for (var i = 0; i < account.Tanks.Count; i++)
            {
                var save = account.Tanks[i];
                if (save != null && string.Equals(
                        save.Name, name, System.StringComparison.OrdinalIgnoreCase))
                {
                    return save;
                }
            }

            return null;
        }
    }
}

