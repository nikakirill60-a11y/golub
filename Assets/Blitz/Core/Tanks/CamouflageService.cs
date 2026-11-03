using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Save;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>What the Apply button of the camouflage shop does for a slot and a pick.</summary>
    public enum CamouflageAction
    {
        None,

        /// <summary>The camouflage is not owned yet: PURCHASE for the price ("Cost:"), installed at once.</summary>
        Buy,

        /// <summary>An owned camouflage the slot does not wear: INSTALL, free.</summary>
        Install,

        /// <summary>The installed camouflage is picked: REMOVE, free; it stays owned.</summary>
        Remove,
    }

    /// <summary>
    /// The economy of the camouflage shop. The 4.8.0 camouflages were
    /// disposable; here a camouflage is bought once per vehicle for the
    /// tank's price and is the vehicle's for good: it goes in and out of
    /// every slot that takes it for free, battles do not spend it and
    /// removing it refunds nothing.
    /// </summary>
    public sealed class CamouflageService
    {
        public const string ErrorNotEnoughCredits = LoadoutService.ErrorNotEnoughCredits;

        private readonly AccountData _account;
        private readonly CamouflageCatalog _catalog;

        public CamouflageService(AccountData account, CamouflageCatalog catalog)
        {
            _account = account;
            _catalog = catalog ?? CamouflageCatalog.Empty;
        }

        /// <summary>The camouflage installed into the slot of a kind, or the empty string.</summary>
        public static string Installed(TankSaveData save, CamouflageKind kind)
        {
            var slot = (int)kind;
            return save == null || slot < 0 || slot >= save.Camouflages.Count
                ? string.Empty
                : save.Camouflages[slot] ?? string.Empty;
        }

        /// <summary>Whether any slot of the tank holds a camouflage.</summary>
        public static bool AnyInstalled(TankSaveData save)
        {
            if (save == null) { return false; }
            for (var i = 0; i < save.Camouflages.Count; i++)
            {
                if (!string.IsNullOrEmpty(save.Camouflages[i])) { return true; }
            }

            return false;
        }

        /// <summary>Whether the camouflage was bought for the tank.</summary>
        public static bool Owns(TankSaveData save, string camouflage)
        {
            if (save == null || string.IsNullOrEmpty(camouflage)) { return false; }
            for (var i = 0; i < save.OwnedCamouflages.Count; i++)
            {
                if (string.Equals(save.OwnedCamouflages[i], camouflage, StringComparison.Ordinal)) { return true; }
            }

            return false;
        }

        /// <summary>
        /// The red badge of the hangar entry: the tank takes a camouflage,
        /// wears none and its shop was never opened.
        /// </summary>
        public bool ShowsBadge(TankSaveData save)
        {
            return save != null && !save.CamouflageSeen && _catalog.Supports(save.Name)
                && !AnyInstalled(save);
        }

        public CamouflageAction Action(TankSaveData save, CamouflageKind kind, string camouflage)
        {
            if (save == null || !_catalog.CanInstall(save.Name, kind, camouflage))
            {
                return CamouflageAction.None;
            }

            if (string.Equals(Installed(save, kind), camouflage, StringComparison.Ordinal))
            {
                return CamouflageAction.Remove;
            }

            return Owns(save, camouflage) ? CamouflageAction.Install : CamouflageAction.Buy;
        }

        /// <summary>Credits the action takes: the price of a purchase, nothing otherwise.</summary>
        public int ActionCost(TankSaveData save, CamouflageKind kind, string camouflage)
        {
            return save != null && Action(save, kind, camouflage) == CamouflageAction.Buy
                ? _catalog.Price(save.Name, camouflage)
                : 0;
        }

        /// <summary>
        /// Runs the action: a purchase pays and owns the camouflage, then
        /// it is installed like an owned one; a removal only bares the slot.
        /// </summary>
        public TankActionResult Apply(TankSaveData save, CamouflageKind kind, string camouflage)
        {
            var action = Action(save, kind, camouflage);
            if (action == CamouflageAction.None)
            {
                return TankActionResult.Fail("invalid camouflage action");
            }

            var cost = ActionCost(save, kind, camouflage);
            if (cost > 0 && (_account == null || _account.Credits < cost))
            {
                return TankActionResult.Fail(ErrorNotEnoughCredits);
            }

            if (_account != null && cost > 0)
            {
                _account.Credits = AccountData.AddCurrency(_account.Credits, -cost);
            }

            save.Cleanup();
            if (action == CamouflageAction.Buy)
            {
                save.OwnedCamouflages.Add(camouflage);
            }

            save.Camouflages[(int)kind] = action == CamouflageAction.Remove
                ? string.Empty
                : camouflage;
            return TankActionResult.Success(cost);
        }

        /// <summary>
        /// The camouflage the vehicle wears on the hangar stand: the first
        /// slot, in shop order, holding one the tank can still take; empty
        /// when none does.
        /// </summary>
        public static string HangarCamouflage(CamouflageCatalog catalog, TankSaveData save)
        {
            if (catalog == null || save == null) { return string.Empty; }
            for (var slot = 0; slot < CamouflageCatalog.SlotCount; slot++)
            {
                var kind = CamouflageCatalog.KindOfSlot(slot);
                var installed = Installed(save, kind);
                if (catalog.CanInstall(save.Name, kind, installed)) { return installed; }
            }

            return string.Empty;
        }

        /// <summary>
        /// The camouflage a tank fights in on a map: the one in the slot of
        /// the map's kind, provided the tank still takes it.
        /// </summary>
        public static string BattleCamouflage(CamouflageCatalog catalog, TankSaveData save, string mapId)
        {
            return save != null
                ? BattleCamouflage(catalog, save.Name, save.Camouflages, mapId)
                : string.Empty;
        }

        /// <summary>
        /// <see cref="BattleCamouflage(CamouflageCatalog, TankSaveData, string)"/>
        /// for a seat known only by its tank and the slots its hello carried.
        /// </summary>
        public static string BattleCamouflage(
            CamouflageCatalog catalog, string tank, IReadOnlyList<string> slots, string mapId)
        {
            CamouflageKind kind;
            if (catalog == null || slots == null || !catalog.TryKindOfMap(mapId, out kind)
                || (int)kind >= slots.Count)
            {
                return string.Empty;
            }

            var installed = slots[(int)kind] ?? string.Empty;
            return catalog.CanInstall(tank, kind, installed) ? installed : string.Empty;
        }

        /// <summary>
        /// The concealment a camouflage adds to a tank in battle. Any id the
        /// tank cannot wear - an unknown one off the network included - adds
        /// nothing.
        /// </summary>
        public static float ConcealmentBonus(CamouflageCatalog catalog, string tank, string camouflage)
        {
            if (catalog == null || catalog.Get(camouflage) == null) { return 0f; }
            var info = catalog.Tank(tank);
            return info != null && info.Has(camouflage) ? catalog.ConcealmentBonus : 0f;
        }
    }
}
