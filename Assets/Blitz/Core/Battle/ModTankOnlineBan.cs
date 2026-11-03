using System;
using System.IO;

namespace BlitzLegacy.Core.Battle
{
    /// <summary>
    /// Raised when online data (the selected tank, a hello, a connect request,
    /// a battle plan) names a mod tank. Like <see cref="SandboxOnlineException"/>
    /// it derives from <see cref="IOException"/>, so every generic "bad packet"
    /// catch keeps rejecting it, while the receivers that must leave the session
    /// tell it apart via <see cref="OnlineBan.KeyOf"/>.
    /// </summary>
    public sealed class ModTankOnlineException : IOException
    {
        public ModTankOnlineException(string tankId, string context, string key)
            : base("Mod tanks are offline-only; refusing '" + (tankId ?? string.Empty)
                + "' online (" + (context ?? "online") + ")")
        {
            TankId = tankId ?? string.Empty;
            Key = string.IsNullOrEmpty(key) ? ModTankOnlineBan.ErrorKey : key;
        }

        /// <summary>The refused tank id as it was named.</summary>
        public string TankId { get; private set; }

        /// <summary>Localization key of the notice shown for this refusal.</summary>
        public string Key { get; private set; }
    }

    /// <summary>
    /// Hard ban of mod tanks in networked battles, as strict as the Sandbox ban.
    /// Online play only ever seats the tanks shipped with the game: the selected
    /// tank, every hello and every slot of a battle plan must be stock. Anything
    /// else (an installed mod, an id this build does not ship, a tampered peer)
    /// is refused, so an honest client never queues, joins or stays in a battle
    /// with a mod tank. Fails closed: until Runtime registers the stock list,
    /// nothing counts as stock.
    /// </summary>
    public static class ModTankOnlineBan
    {
        /// <summary>Notice when the player's own tank is a mod.</summary>
        public const string ErrorKey = "online:error/mod_tank";

        /// <summary>Notice when someone else brought a mod tank into the battle.</summary>
        public const string BattleErrorKey = "online:error/mod_tank_battle";

        private static Func<string, bool> _stock;

        /// <summary>
        /// True only for tanks shipped with the game. Registered by Runtime
        /// (Core cannot load the tank list itself); tests may swap it.
        /// </summary>
        public static Func<string, bool> StockResolver
        {
            get { return _stock; }
            set { _stock = value; }
        }

        /// <summary>Whether the id names a stock tank this build ships.</summary>
        public static bool IsStock(string tankId)
        {
            if (string.IsNullOrWhiteSpace(tankId)) { return false; }
            var stock = _stock;
            if (stock == null) { return false; }
            try { return stock(tankId); }
            catch (Exception) { return false; }
        }

        /// <summary>True for any tank that may not travel over the network.</summary>
        public static bool Violates(string tankId)
        {
            return !IsStock(tankId);
        }

        /// <summary>Throws for the player's own tank when it is not a stock tank.</summary>
        public static void Ensure(string tankId, string context)
        {
            if (Violates(tankId)) { throw new ModTankOnlineException(tankId, context, ErrorKey); }
        }

        /// <summary>Throws for a tank someone else brought into the battle.</summary>
        public static void EnsureBattle(string tankId, string context)
        {
            if (Violates(tankId)) { throw new ModTankOnlineException(tankId, context, BattleErrorKey); }
        }

        /// <summary>Index of the first plan slot whose tank is not stock, or -1.</summary>
        public static int FirstViolation(BattleNetPlan plan)
        {
            if (plan == null) { return -1; }
            for (var i = 0; i < plan.Slots.Count; i++)
            {
                if (Violates(plan.Slots[i].TankId)) { return i; }
            }
            return -1;
        }

        /// <summary>Whether an exception (or anything it wraps) is a mod tank rejection.</summary>
        public static bool IsBan(Exception error)
        {
            for (var e = error; e != null; e = e.InnerException)
            {
                if (e is ModTankOnlineException) { return true; }
            }
            return false;
        }
    }

    /// <summary>
    /// The hard online bans together (the Sandbox, mod tanks): what a receiver
    /// checks to leave a session instead of treating the data as a bad packet.
    /// </summary>
    public static class OnlineBan
    {
        /// <summary>Notice key of the hard ban an exception (or anything it wraps) carries, or null.</summary>
        public static string KeyOf(Exception error)
        {
            for (var e = error; e != null; e = e.InnerException)
            {
                if (e is SandboxOnlineException) { return SandboxOnlineBan.ErrorKey; }
                var mod = e as ModTankOnlineException;
                if (mod != null) { return mod.Key; }
            }
            return null;
        }

        public static bool IsBan(Exception error)
        {
            return KeyOf(error) != null;
        }
    }
}
