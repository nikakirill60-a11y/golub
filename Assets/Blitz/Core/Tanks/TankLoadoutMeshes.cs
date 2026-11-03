using BlitzLegacy.Core.Save;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// Turns an installed loadout into the two client mesh slots that describe
    /// it (<c>turret_01</c>, <c>gun_08</c> ...).
    ///
    /// The hangar screen and every headless stage that shows a tank have to
    /// agree on this, otherwise a frame taken outside the screen shows the
    /// stock pair while the player sees the researched one.
    /// </summary>
    public static class TankLoadoutMeshes
    {
        /// <summary>
        /// Resolves the mesh names of the pair the save installs. A missing
        /// card or save falls back to the stock turret and its first gun,
        /// which is what <c>TankNodeTable</c> resolves a null name to.
        /// </summary>
        public static void Resolve(
            TankInfo card, TankSaveData save, out string turretMesh, out string gunMesh)
        {
            turretMesh = null;
            gunMesh = null;
            if (card == null) { return; }

            var turretId = save == null ? 0 : save.TurretId;
            var turret = card.Turret(turretId);
            if (turret == null) { return; }

            turretMesh = turret.Mesh;
            var gun = card.GunFor(turretId, save == null ? 0 : save.GunId);
            if (gun == null) { return; }

            var slot = turret.Slot(gun.Name);
            if (slot != null) { gunMesh = slot.Mesh; }
        }

        /// <summary>The save of one card inside an account, or null.</summary>
        public static TankSaveData SaveOf(AccountData account, string cardName)
        {
            if (account == null || string.IsNullOrEmpty(cardName)) { return null; }

            for (var i = 0; i < account.Tanks.Count; i++)
            {
                if (account.Tanks[i].Name == cardName)
                {
                    return account.Tanks[i];
                }
            }

            return null;
        }
    }
}
