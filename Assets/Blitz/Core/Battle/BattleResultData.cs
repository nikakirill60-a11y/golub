using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Battle
{
    public enum BattleOutcome
    {
        Draw = 0,
        Victory = 1,
        Defeat = 2,
    }

    public enum BattleFinishReason
    {
        Elimination = 0,
        Timeout = 1,
        Forced = 2,
    }

    public static class BattleOutcomeRules
    {
        public static BattleOutcome FromWinner(int winnerTeam, int playerTeam)
        {
            if (winnerTeam < 0)
            {
                return BattleOutcome.Draw;
            }
            return winnerTeam == playerTeam ? BattleOutcome.Victory : BattleOutcome.Defeat;
        }
    }

    public sealed class BattleRewardData
    {
        public string Id = string.Empty;
        public int Gold;
        public int Rarity;

        public JsonNode ToJson()
        {
            return JsonNode.NewObject()
                .Set("id", Id)
                .Set("gold", Gold)
                .Set("rarity", Rarity);
        }

        public static BattleRewardData FromJson(JsonNode node)
        {
            return new BattleRewardData
            {
                Id = node["id"].AsString(),
                Gold = node["gold"].AsInt(),
                Rarity = node["rarity"].AsInt(),
            };
        }
    }

    public sealed class BattleParticipantResult
    {
        public string Name = string.Empty;
        public string TankId = string.Empty;
        public string TankName = string.Empty;
        public int Team;
        public bool IsPlayer;
        public bool Survived;
        public int Spots;
        public int BaseTicks;
        public int Damage;
        public int Healing;
        public int DamageTaken;
        public int DamageBlocked;
        public int Kills;
        public int Shots;
        public int Hits;
        public int Xp;
        public int XpRaw;

        public JsonNode ToJson()
        {
            return JsonNode.NewObject()
                .Set("name", Name)
                .Set("tank_id", TankId)
                .Set("tank_name", TankName)
                .Set("team", Team)
                .Set("is_player", IsPlayer)
                .Set("survived", Survived)
                .Set("spots", Spots)
                .Set("base_ticks", BaseTicks)
                .Set("damage", Damage)
                .Set("healing", Healing)
                .Set("damage_taken", DamageTaken)
                .Set("damage_blocked", DamageBlocked)
                .Set("kills", Kills)
                .Set("shots", Shots)
                .Set("hits", Hits)
                .Set("xp", Xp)
                .Set("xp_raw", XpRaw);
        }

        public static BattleParticipantResult FromJson(JsonNode node)
        {
            return new BattleParticipantResult
            {
                Name = node["name"].AsString(),
                TankId = node["tank_id"].AsString(),
                TankName = node["tank_name"].AsString(),
                Team = node["team"].AsInt(),
                IsPlayer = node["is_player"].AsBool(),
                Survived = node["survived"].AsBool(),
                Spots = node["spots"].AsInt(),
                BaseTicks = node["base_ticks"].AsInt(),
                Damage = node["damage"].AsInt(),
                Healing = node["healing"].AsInt(),
                DamageTaken = node["damage_taken"].AsInt(),
                DamageBlocked = node["damage_blocked"].AsInt(),
                Kills = node["kills"].AsInt(),
                Shots = node["shots"].AsInt(),
                Hits = node["hits"].AsInt(),
                Xp = node["xp"].AsInt(),
                XpRaw = node.Has("xp_raw") ? node["xp_raw"].AsInt() : node["xp"].AsInt(),
            };
        }
    }

    /// <summary>Migration-safe, engine-free battle result saved on AccountData.</summary>
    public sealed class BattleResultData
    {
        public string Id = string.Empty;
        public string MapId = string.Empty;
        public string TankId = string.Empty;
        public double FinishedAt;
        public float DurationSeconds;
        public string ModeId = string.Empty;
        public int RemainingAllyPoints, RemainingEnemyPoints;
        public BattleOutcome Outcome;
        public BattleFinishReason FinishReason;
        public bool PremiumAccount;
        public int CreditsEarned;
        public int CreditCosts;
        public int Credits;
        public int Gold;
        public int Xp;
        public int FreeXp;
        public readonly List<BattleRewardData> Rewards = new List<BattleRewardData>();
        public readonly List<BattleParticipantResult> Participants =
            new List<BattleParticipantResult>();

        public BattleParticipantResult Player
        {
            get
            {
                for (var i = 0; i < Participants.Count; i++)
                {
                    if (Participants[i].IsPlayer)
                    {
                        return Participants[i];
                    }
                }
                return null;
            }
        }

        public JsonNode ToJson()
        {
            var rewards = JsonNode.NewArray();
            for (var i = 0; i < Rewards.Count; i++)
            {
                rewards.Add(Rewards[i].ToJson());
            }
            var participants = JsonNode.NewArray();
            for (var i = 0; i < Participants.Count; i++)
            {
                participants.Add(Participants[i].ToJson());
            }
            return JsonNode.NewObject()
                .Set("id", Id)
                .Set("map_id", MapId)
                .Set("tank_id", TankId)
                .Set("finished_at", FinishedAt)
                .Set("duration", DurationSeconds)
                .Set("mode_id", ModeId)
                .Set("ally_points", RemainingAllyPoints)
                .Set("enemy_points", RemainingEnemyPoints)
                .Set("outcome", (int)Outcome)
                .Set("finish_reason", (int)FinishReason)
                .Set("premium_account", PremiumAccount)
                .Set("credits_earned", CreditsEarned)
                .Set("credit_costs", CreditCosts)
                .Set("credits", Credits)
                .Set("gold", Gold)
                .Set("xp", Xp)
                .Set("free_xp", FreeXp)
                .Set("rewards", rewards)
                .Set("participants", participants);
        }

        public static BattleResultData FromJson(JsonNode node)
        {
            var result = new BattleResultData();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return result;
            }
            result.Id = node["id"].AsString();
            result.MapId = node["map_id"].AsString();
            result.TankId = node["tank_id"].AsString();
            result.FinishedAt = node["finished_at"].AsDouble();
            result.DurationSeconds = node["duration"].AsFloat();
            result.Outcome = (BattleOutcome)node["outcome"].AsInt();
            result.FinishReason = (BattleFinishReason)node["finish_reason"].AsInt();
            result.PremiumAccount = node["premium_account"].AsBool();
            result.CreditsEarned = node["credits_earned"].AsInt();
            result.CreditCosts = node["credit_costs"].AsInt();
            result.Credits = node["credits"].AsInt();
            result.Gold = node["gold"].AsInt();
            result.Xp = node["xp"].AsInt();
            result.FreeXp = node["free_xp"].AsInt();
            foreach (var reward in node["rewards"].Items)
            {
                result.Rewards.Add(BattleRewardData.FromJson(reward));
            }
            result.ModeId=node["mode_id"].AsString();result.RemainingAllyPoints=node["ally_points"].AsInt();result.RemainingEnemyPoints=node["enemy_points"].AsInt();
            foreach (var participant in node["participants"].Items)
            {
                result.Participants.Add(BattleParticipantResult.FromJson(participant));
            }
            return result;
        }
    }
}
