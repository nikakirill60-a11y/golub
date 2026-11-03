using System.Collections.Generic;
using BlitzLegacy.Core.Battle;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>Live per-seat counters used by map intro and battle results.</summary>
    public sealed class BattleParticipantStats
    {
        private readonly HashSet<int> _hitShotIds = new HashSet<int>();

        public BattleTank Tank { get; internal set; }
        public string Nickname { get; internal set; }
        public bool IsPlayer { get; internal set; }
        public int Team { get; internal set; }
        public int Spots { get; internal set; }
        public int BaseTicks { get; internal set; }
        public int Damage { get; internal set; }
        public int Healing { get; internal set; }
        public int DamageTaken { get; internal set; }
        public int DamageBlocked { get; internal set; }
        public int Kills { get; internal set; }
        public int Shots { get; internal set; }
        public int Hits { get; internal set; }
        public int AmmoCost { get; internal set; }
        public int RepairCost { get; internal set; }
        public int PiercerCurrent { get; internal set; }
        public int PiercerStreaks { get; internal set; }
        public int KolobanovKills { get; internal set; }
        public int ModuleCrits { get; internal set; }
        public int TarczayKills { get; internal set; }
        public int BaseTicksDefendedMax { get; internal set; }
        public readonly Dictionary<BattleTank, BattleXpInteraction> Interactions =
            new Dictionary<BattleTank, BattleXpInteraction>();

        internal BattleXpInteraction Interaction(BattleTank target)
        {
            BattleXpInteraction record;
            if (!Interactions.TryGetValue(target, out record))
            {
                record = new BattleXpInteraction { TargetLevel = target.Card.Level };
                Interactions[target] = record;
            }
            return record;
        }

        internal void RecordHit(int shotId)
        {
            if (shotId <= 0 || _hitShotIds.Add(shotId))
            {
                Hits++;
            }
        }

        public BattleParticipantResult Snapshot()
        {
            var card = Tank == null ? null : Tank.Card;
            return new BattleParticipantResult
            {
                Name = Nickname ?? string.Empty,
                TankId = card == null ? string.Empty : card.Name,
                TankName = card == null ? string.Empty : card.DisplayNameShort,
                Team = Team,
                IsPlayer = IsPlayer,
                Survived = Tank != null && !Tank.Dead &&
                    (Tank.Health == null || Tank.Health.Health > 0),
                Spots = Spots,
                BaseTicks = BaseTicks,
                Damage = Damage,
                Healing = Healing,
                DamageTaken = DamageTaken,
                DamageBlocked = DamageBlocked,
                Kills = Kills,
                Shots = Shots,
                Hits = Hits,
            };
        }
    }
}
