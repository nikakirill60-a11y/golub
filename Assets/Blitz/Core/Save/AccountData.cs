using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Save
{
    /// <summary>Port of Godot <c>Account</c> (scripts/account.gd).</summary>
    public sealed class AccountData
    {
        public const int DefaultCredits = 250000;
        public const int DefaultGold = 250;
        public const int DefaultBlacklistSlotsMap = 1;
        public const int DefaultBlacklistSlotsTank = 3;

        public string Name = string.Empty;

        // 64-bit like Godot's int, so balances are not capped at int.MaxValue.
        public long Credits = DefaultCredits;
        public long Gold = DefaultGold;
        public long FreeXp;

        public int SelectedTankId;
        public string SelectedTankName = string.Empty;

        public readonly List<TankSaveData> Tanks = new List<TankSaveData>();

        /// <summary>Completed battles, also used as the idempotence ledger.</summary>
        public readonly List<BattleResultData> BattleResults =
            new List<BattleResultData>();

        public int BlacklistSlotsMap = DefaultBlacklistSlotsMap;
        public int BlacklistSlotsTank = DefaultBlacklistSlotsTank;

        public bool KnyazGiven;

        public readonly List<string> ResearchedModules = new List<string>();
        public readonly Dictionary<string, int> Rewards =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// Account warehouse: item id (EquipmentCatalog / ConsumableCatalog)
        /// to the number of spare copies owned but not mounted on any tank.
        /// Mounted items stay on TankSaveData; this is the stock they are
        /// drawn from and returned to. Zero or negative counts are dropped
        /// on save, so an empty entry never survives a round trip.
        /// </summary>
        public readonly Dictionary<string, int> Inventory =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Spare copies of an item currently in the warehouse.</summary>
        public int InventoryCount(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) { return 0; }
            int count;
            return Inventory.TryGetValue(itemId, out count) && count > 0 ? count : 0;
        }

        /// <summary>Drops empty and negative stacks so saves stay minimal.</summary>
        public static void PruneInventory(Dictionary<string, int> inventory)
        {
            if (inventory == null || inventory.Count == 0) { return; }
            var empty = new List<string>();
            foreach (var pair in inventory)
            {
                if (pair.Value <= 0 || string.IsNullOrEmpty(pair.Key))
                {
                    empty.Add(pair.Key);
                }
            }

            for (var i = 0; i < empty.Count; i++)
            {
                inventory.Remove(empty[i]);
            }
        }
        public readonly List<string> AchievementsDone = new List<string>();
        public readonly Dictionary<string, int> ShopGuaranteedRewards =
            new Dictionary<string, int>(StringComparer.Ordinal);

        public double TimeIngame;
        public double LastLogin;
        public double CreationTime;
        public double PremAccountExpiration;

        public string Login = string.Empty;
        public string Password = string.Empty;

        public int StatBattles;
        public int StatWins;
        public int StatKills;
        public int StatMaxKills;
        public int StatDamageDealt;
        public int StatDamageTaken;
        public int StatShellsShot;
        public int StatShellsHits;

        /// <summary>Online team damage and the blue team-killer status.</summary>
        public readonly TeamKillRecord TeamKill = new TeamKillRecord();

        /// <summary>Hidden online reputation; never shown in the statistics.</summary>
        public readonly ReputationRecord Reputation = new ReputationRecord();

        public bool LoggedIn { get; private set; }

        /// <summary>
        /// Adds a currency delta without wrapping around: a balance already
        /// near <see cref="long.MaxValue"/> saturates instead of turning
        /// negative (and the same at the bottom of the range).
        /// </summary>
        public static long AddCurrency(long balance, long delta)
        {
            if (delta > 0 && balance > long.MaxValue - delta)
            {
                return long.MaxValue;
            }

            if (delta < 0 && balance < long.MinValue - delta)
            {
                return long.MinValue;
            }

            return balance + delta;
        }

        public static string MakeKey(string login, string password)
        {
            return (login ?? string.Empty) + ":" + (password ?? string.Empty);
        }

        public string Key
        {
            get { return MakeKey(Login, Password); }
        }

        public bool HasTank(string name, bool includeResearched = false)
        {
            for (var i = 0; i < Tanks.Count; i++)
            {
                var tank = Tanks[i];
                if (string.Equals(tank.Name, name, StringComparison.Ordinal) &&
                    (tank.Bought || includeResearched))
                {
                    return true;
                }
            }

            return false;
        }

        public TankSaveData GetTank(string name)
        {
            for (var i = 0; i < Tanks.Count; i++)
            {
                if (string.Equals(Tanks[i].Name, name, StringComparison.Ordinal))
                {
                    return Tanks[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Pure part of Godot <c>Account.add_tank()</c>. Premium-duplicate credit
        /// compensation needs TanksDataBase and lands with the hangar slice.
        /// </summary>
        public TankSaveData AddTank(string name, bool bought = false)
        {
            if (string.IsNullOrEmpty(name) || name == TankSaveData.DefaultName)
            {
                return null;
            }

            var existing = GetTank(name);
            if (existing != null)
            {
                if (!existing.Bought)
                {
                    existing.Bought = bought;
                }

                return existing;
            }

            var tank = TankSaveData.WithName(name);
            tank.Bought = bought;
            Tanks.Add(tank);
            return tank;
        }

        /// <summary>
        /// Godot <c>should_get_guaranteed_reward(id, max_points)</c>: every
        /// opening adds a point; reaching <paramref name="maxPoints"/> resets
        /// the counter and guarantees the tank row of the container.
        /// </summary>
        public bool ShouldGetGuaranteedReward(string id, int maxPoints)
        {
            var key = id ?? string.Empty;
            int points;
            ShopGuaranteedRewards.TryGetValue(key, out points);
            points++;
            var guaranteed = points >= maxPoints;
            ShopGuaranteedRewards[key] = guaranteed ? 0 : points;
            return guaranteed;
        }

        /// <summary>Godot <c>get_guaranteed_reward_points(id)</c>.</summary>
        public int GuaranteedRewardPoints(string id)
        {
            int points;
            return ShopGuaranteedRewards.TryGetValue(id ?? string.Empty, out points) ? points : 0;
        }

        public int TanksBoughtCount
        {
            get
            {
                var count = 0;
                for (var i = 0; i < Tanks.Count; i++)
                {
                    if (Tanks[i].Bought)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>Win rate in percent, as shown on the login account card.</summary>
        public float WinRatePercent
        {
            get { return StatBattles <= 0 ? 0f : StatWins * 100f / StatBattles; }
        }

        public TimeSpan TimePlayed
        {
            get { return TimeSpan.FromSeconds(Math.Max(0d, TimeIngame)); }
        }

        /// <summary>
        /// Godot <c>prem_account_validate()</c>: an expired premium account
        /// restarts from now instead of from the moment it ran out, so added
        /// time is never swallowed by the gap.
        /// </summary>
        public bool HasPremium(double nowUnixSeconds)
        {
            return !double.IsNaN(PremAccountExpiration) &&
                !double.IsInfinity(PremAccountExpiration) &&
                PremAccountExpiration > nowUnixSeconds;
        }

        public void ValidatePremAccount(double nowUnixSeconds)
        {
            if (PremAccountExpiration < nowUnixSeconds)
            {
                PremAccountExpiration = nowUnixSeconds;
            }
        }

        /// <summary>
        /// Godot <c>add_prem_account(hours, days)</c>. The source also pushes a
        /// <c>news/prem_receive</c> hangar notification; the notification list
        /// is not ported yet, so only the expiration moves here.
        /// </summary>
        public void AddPremAccount(double hours, double days, double nowUnixSeconds)
        {
            ValidatePremAccount(nowUnixSeconds);
            PremAccountExpiration += (hours * 3600d) + (days * 24d * 3600d);
        }

        /// <summary>Pure part of Godot <c>Account.log_in()</c>.</summary>
        public void LogIn(double nowUnixSeconds)
        {
            for (var i = 0; i < Tanks.Count; i++)
            {
                Tanks[i].Cleanup();
            }

            LastLogin = nowUnixSeconds;
            LoggedIn = true;
        }

        public JsonNode ToJson()
        {
            var tanks = JsonNode.NewArray();
            for (var i = 0; i < Tanks.Count; i++)
            {
                tanks.Add(Tanks[i].ToJson());
            }

            var battleResults = JsonNode.NewArray();
            for (var i = 0; i < BattleResults.Count; i++)
            {
                battleResults.Add(BattleResults[i].ToJson());
            }

            return JsonNode.NewObject()
                .Set("name", Name)
                .Set("credits", Credits)
                .Set("gold", Gold)
                .Set("free_xp", FreeXp)
                .Set("selected_tank_id", SelectedTankId)
                .Set("selected_tank_name", SelectedTankName)
                .Set("tanks", tanks)
                .Set("battle_results", battleResults)
                .Set("blacklist_slots_map", BlacklistSlotsMap)
                .Set("blacklist_slots_tank", BlacklistSlotsTank)
                .Set("knyaz_given", KnyazGiven)
                .Set("researched_modules", JsonMap.FromStrings(ResearchedModules))
                .Set("rewards", JsonMap.FromIntMap(Rewards))
                .Set("inventory", JsonMap.FromIntMap(Inventory))
                .Set("achievements_done", JsonMap.FromStrings(AchievementsDone))
                .Set("shop_guaranteed_rewards", JsonMap.FromIntMap(ShopGuaranteedRewards))
                .Set("time_ingame", TimeIngame)
                .Set("last_login", LastLogin)
                .Set("creation_time", CreationTime)
                .Set("prem_account_expiration", PremAccountExpiration)
                .Set("login", Login)
                .Set("password", Password)
                .Set("stat_battles", StatBattles)
                .Set("stat_wins", StatWins)
                .Set("stat_kills", StatKills)
                .Set("stat_max_kills", StatMaxKills)
                .Set("stat_damage_dealt", StatDamageDealt)
                .Set("stat_damage_taken", StatDamageTaken)
                .Set("stat_shells_shot", StatShellsShot)
                .Set("stat_shells_hits", StatShellsHits)
                .Set("team_kill", JsonNode.NewObject()
                    .Set("pending", TeamKill.Pending)
                    .Set("total", TeamKill.Total)
                    .Set("offences", TeamKill.Offences)
                    .Set("blue_until", TeamKill.BlueUntil)
                    .Set("permanent", TeamKill.Permanent)
                    .Set("watch_until", TeamKill.WatchUntil))
                .Set("net_reputation", JsonNode.NewObject()
                    .Set("started", Reputation.Started)
                    .Set("clean", Reputation.Clean)
                    .Set("abandoned", Reputation.Abandoned)
                    .Set("ping_sum", Reputation.PingSum)
                    .Set("ping_battles", Reputation.PingBattles)
                    .Set("open", Reputation.Open));
        }

        public static AccountData FromJson(JsonNode node)
        {
            var account = new AccountData();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return account;
            }

            account.Name = node["name"].AsString();
            account.Credits = node["credits"].AsLong(DefaultCredits);
            account.Gold = node["gold"].AsLong(DefaultGold);
            account.FreeXp = node["free_xp"].AsLong();
            account.SelectedTankId = node["selected_tank_id"].AsInt();
            account.SelectedTankName = node["selected_tank_name"].AsString();

            account.Tanks.Clear();
            foreach (var tankNode in node["tanks"].Items)
            {
                account.Tanks.Add(TankSaveData.FromJson(tankNode));
            }

            account.BattleResults.Clear();
            foreach (var resultNode in node["battle_results"].Items)
            {
                account.BattleResults.Add(BattleResultData.FromJson(resultNode));
            }

            account.BlacklistSlotsMap = node["blacklist_slots_map"].AsInt(DefaultBlacklistSlotsMap);
            account.BlacklistSlotsTank = node["blacklist_slots_tank"].AsInt(DefaultBlacklistSlotsTank);
            account.KnyazGiven = node["knyaz_given"].AsBool();
            JsonMap.ReadStrings(node["researched_modules"], account.ResearchedModules);
            JsonMap.ReadIntMap(node["rewards"], account.Rewards);
            JsonMap.ReadIntMap(node["inventory"], account.Inventory);
            PruneInventory(account.Inventory);
            JsonMap.ReadStrings(node["achievements_done"], account.AchievementsDone);
            JsonMap.ReadIntMap(node["shop_guaranteed_rewards"], account.ShopGuaranteedRewards);
            account.TimeIngame = node["time_ingame"].AsDouble();
            account.LastLogin = node["last_login"].AsDouble();
            account.CreationTime = node["creation_time"].AsDouble();
            account.PremAccountExpiration = node["prem_account_expiration"].AsDouble();
            account.Login = node["login"].AsString();
            account.Password = node["password"].AsString();
            account.StatBattles = node["stat_battles"].AsInt();
            account.StatWins = node["stat_wins"].AsInt();
            account.StatKills = node["stat_kills"].AsInt();
            account.StatMaxKills = node["stat_max_kills"].AsInt();
            account.StatDamageDealt = node["stat_damage_dealt"].AsInt();
            account.StatDamageTaken = node["stat_damage_taken"].AsInt();
            account.StatShellsShot = node["stat_shells_shot"].AsInt();
            account.StatShellsHits = node["stat_shells_hits"].AsInt();
            var teamKill = node["team_kill"];
            account.TeamKill.Pending = Math.Max(0, teamKill["pending"].AsInt());
            account.TeamKill.Total = Math.Max(0, teamKill["total"].AsInt());
            account.TeamKill.Offences = Math.Max(0, teamKill["offences"].AsInt());
            account.TeamKill.BlueUntil = teamKill["blue_until"].AsDouble();
            account.TeamKill.Permanent = teamKill["permanent"].AsBool();
            account.TeamKill.WatchUntil = teamKill["watch_until"].AsDouble();
            var reputation = node["net_reputation"];
            account.Reputation.Started = Math.Max(0, reputation["started"].AsInt());
            account.Reputation.Clean = Math.Max(0, reputation["clean"].AsInt());
            account.Reputation.Abandoned = Math.Max(0, reputation["abandoned"].AsInt());
            account.Reputation.PingSum = Math.Max(0d, reputation["ping_sum"].AsDouble());
            account.Reputation.PingBattles = Math.Max(0, reputation["ping_battles"].AsInt());
            account.Reputation.Open = reputation["open"].AsBool();
            return account;
        }
    }
}
