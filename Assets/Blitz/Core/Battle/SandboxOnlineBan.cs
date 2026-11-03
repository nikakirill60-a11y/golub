using System;
using System.IO;

namespace BlitzLegacy.Core.Battle
{
    /// <summary>
    /// Raised when online data (a battle plan, a connect request, a room) names
    /// the offline-only Sandbox. Derives from <see cref="IOException"/> so every
    /// generic "bad packet" catch keeps rejecting it, while the receivers that
    /// must leave the session can tell it apart via <see cref="SandboxOnlineBan.IsBan"/>.
    /// </summary>
    public sealed class SandboxOnlineException : IOException
    {
        public SandboxOnlineException(string context)
            : base("The Sandbox is offline-only; refusing it online (" + context + ")")
        {
        }
    }

    /// <summary>
    /// Hard client-side ban of the Sandbox in networked battles. The Sandbox lets
    /// a player rewrite health, damage, reload, ammunition and enable aim cheats,
    /// so an honest client never builds, joins or stays in an online battle that
    /// carries it: the moment a host (or a tampered build) moves an online battle
    /// into the Sandbox, the honest side disconnects itself.
    /// </summary>
    public static class SandboxOnlineBan
    {
        /// <summary>Localization key of the notice shown when we leave for this reason.</summary>
        public const string ErrorKey = "online:error/sandbox";

        /// <summary>True for any mode that may not travel over the network.</summary>
        public static bool Violates(BattleMode mode)
        {
            return !BattleModeCatalog.AllowsOnline(mode);
        }

        /// <summary>
        /// Raw wire value: the explicit ids of the offline-only modes (the
        /// Sandbox and the War of Ducks event) count as this violation.
        /// </summary>
        public static bool IsSandbox(int rawMode)
        {
            return rawMode == (int)BattleMode.Sandbox || rawMode == (int)BattleMode.WorldOfDucks;
        }

        /// <summary>Throws <see cref="SandboxOnlineException"/> for an offline-only mode.</summary>
        public static void Ensure(BattleMode mode, string context)
        {
            if (BattleModeCatalog.OfflineOnly(mode))
            {
                throw new SandboxOnlineException(context ?? "online");
            }
        }

        /// <summary>
        /// Whether an exception (or anything it wraps) is a Sandbox rejection.
        /// </summary>
        public static bool IsBan(Exception error)
        {
            for (var e = error; e != null; e = e.InnerException)
            {
                if (e is SandboxOnlineException) { return true; }
            }
            return false;
        }
    }
}
