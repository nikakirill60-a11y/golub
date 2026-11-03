using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Battle;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Team damage of one online battle. The host counts every point a human
    /// deals to a team-mate (never to a blue one), turns the attacker blue
    /// once its account crosses the threshold and asks for a kick during the
    /// strict week. Clients only receive their own total.
    /// </summary>
    public sealed class BattleTeamDamage
    {
        private sealed class Entry
        {
            public bool BlueAtStart;
            public int Pending;
            public bool Watched;
            public int Battle;
            public bool Kicked;
        }

        private readonly Dictionary<BattleTank, Entry> _entries = new Dictionary<BattleTank, Entry>();

        /// <summary>Host: a watched player went over the per-battle limit.</summary>
        public event Action<BattleTank> KickRequested;

        /// <summary>Host: a player got the blue team-killer status during this battle.</summary>
        public event Action<BattleTank> TurnedBlue;

        /// <summary>Client: the local player's battle total as the host replicates it.</summary>
        public int ReplicatedLocalDamage { get; set; }

        /// <summary>The local total was folded into the account already.</summary>
        public bool Committed { get; set; }

        /// <summary>Seats a human with the record its account brought.</summary>
        public void Register(BattleTank tank, bool blue, int pending, bool watched)
        {
            if (tank == null) { return; }
            _entries[tank] = new Entry { BlueAtStart = blue, Pending = Math.Max(0, pending), Watched = watched };
            tank.TeamKiller = blue;
        }

        public int BattleDamage(BattleTank tank)
        {
            Entry entry;
            return tank != null && _entries.TryGetValue(tank, out entry) ? entry.Battle : 0;
        }

        /// <summary>
        /// Host: <paramref name="dealt"/> HP left <paramref name="victim"/>
        /// because of <paramref name="attacker"/>. Returns true when it counted
        /// as team damage.
        /// </summary>
        public bool Record(BattleTank attacker, BattleTank victim, int dealt, bool attackerHuman)
        {
            if (attacker == null || victim == null || dealt <= 0 || !attackerHuman) { return false; }
            if (!TeamKillPolicy.Counts(attacker.Team == victim.Team, attacker == victim, victim.TeamKiller))
            {
                return false;
            }
            Entry entry;
            if (!_entries.TryGetValue(attacker, out entry))
            {
                // A seat taken over after a host migration: its account record
                // stayed with the old host, the blue flag came with the state.
                entry = new Entry { BlueAtStart = attacker.TeamKiller };
                _entries[attacker] = entry;
            }
            entry.Battle = (int)Math.Min(int.MaxValue, (long)entry.Battle + dealt);
            if (!attacker.TeamKiller && TeamKillPolicy.BlueInBattle(entry.BlueAtStart, entry.Pending, entry.Battle))
            {
                attacker.TeamKiller = true;
                if (TurnedBlue != null) { TurnedBlue(attacker); }
            }
            if (!entry.Kicked && TeamKillPolicy.ShouldKick(entry.Watched, entry.Battle))
            {
                entry.Kicked = true;
                if (KickRequested != null) { KickRequested(attacker); }
            }
            return true;
        }
    }
}
