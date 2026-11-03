using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// The hello a joining peer introduces itself with: the signed-in
    /// account's name, tank and loadout. Shared by every online transport.
    /// </summary>
    public static class BattleNetHelloFactory
    {
        /// <summary>The tank the current account takes online (the default tank without an account).</summary>
        public static string SelectedTankId()
        {
            var bootstrap = BlitzBootstrap.Current;
            var account = bootstrap != null && bootstrap.Accounts != null
                ? bootstrap.Accounts.GetCurrentAccount() : null;
            return account != null && !string.IsNullOrEmpty(account.SelectedTankName)
                ? account.SelectedTankName : BattleSession.DefaultTankId;
        }

        public static BattleNetHello FromCurrentAccount(string build)
        {
            var hello = new BattleNetHello { Build = build, TankId = BattleSession.DefaultTankId };
            var bootstrap = BlitzBootstrap.Current;
            var account = bootstrap != null && bootstrap.Accounts != null
                ? bootstrap.Accounts.GetCurrentAccount() : null;
            if (account == null) { return hello; }
            hello.Name = account.Name != null && account.Name.Length > BattleNetHello.MaxNameLength
                ? account.Name.Substring(0, BattleNetHello.MaxNameLength) : account.Name;
            if (!string.IsNullOrEmpty(account.SelectedTankName)) { hello.TankId = account.SelectedTankName; }
            var now = TeamKillPolicy.Now();
            hello.TeamKiller = account.TeamKill.IsBlue(now);
            hello.TeamKillWatched = account.TeamKill.Watched(now);
            hello.TeamKillPending = account.TeamKill.Pending;
            OnlineReputation.SettleCrash(account.Reputation);
            hello.Reputation = OnlineReputation.Score(account.Reputation, account.TimeIngame);
            var tank = account.GetTank(hello.TankId);
            if (tank != null)
            {
                hello.Gun = tank.GunId;
                hello.Engine = tank.EngineId;
                hello.Tracks = tank.TrackId;
                hello.Turret = tank.TurretId;
                hello.Ammo.AddRange(tank.ShellsAmounts);
                // The host spends these for this crew, in the order the HUD shows them.
                hello.Consumables.AddRange(BattleNetConsumables.Sanitized(LoadoutService.ActiveConsumables(tank)));
                // Every slot travels: the host picks the one of the map's kind.
                hello.Camouflages.AddRange(tank.Camouflages);
            }
            return hello;
        }
    }
}
