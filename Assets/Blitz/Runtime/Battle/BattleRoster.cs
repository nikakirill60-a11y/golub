using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Battle autoload slice: teams, score, clock and terminal outcome.  The
    /// timeout path preserves winner -1, which is the Godot draw contract.
    /// </summary>
    public sealed class BattleRoster
    {
        public const float DefaultTimeLimitSeconds = 420f;
        public static BattleRoster Current { get; private set; }

        private readonly List<BattleTank> _tanksA = new List<BattleTank>();
        private readonly List<BattleTank> _tanksB = new List<BattleTank>();
        private readonly List<BattleAiMarker> _markers = new List<BattleAiMarker>();
        private readonly List<BattleAiCover> _covers = new List<BattleAiCover>();
        private readonly Dictionary<BattleTank, Action> _deathHandlers =
            new Dictionary<BattleTank, Action>();
        private readonly HashSet<BattleTank> _destroyed = new HashSet<BattleTank>();
        private float _startedAt = -1f;
        private bool _endedPublished;

        public List<BattleTank> TanksA { get { return _tanksA; } }
        public List<BattleTank> TanksB { get { return _tanksB; } }
        public List<BattleAiMarker> AiMarkers { get { return _markers; } }
        public List<BattleAiCover> AiCovers { get { return _covers; } }
        public int PlayerTeam { get; set; }
        public bool IsOngoing { get; private set; }
        public int TeamAScore { get; private set; }
        public int TeamBScore { get; private set; }
        public int WinnerTeam { get; private set; } = -1;
        public BattleFinishReason FinishReason { get; private set; } =
            BattleFinishReason.Forced;
        public float TimeLimitSeconds { get; set; } = DefaultTimeLimitSeconds;
        public BigBossMatch BigBoss { get; set; }

        public event Action<BattleTank> TankGotUnspotted;
        public event Action<BattleTank> TankGotDestroyed;
        public event Action<BattleRoster> BattleEnded;

        /// <summary>
        /// save_settings "MAX BATTLE TIME" as a clock limit in seconds. The row
        /// was saved and drawn but nothing read it, so every battle ran on the
        /// default seven minutes. INF means no countdown at all, which this
        /// class already spells as a limit of zero.
        /// </summary>
        public static float TimeLimitFor(SettingsData settings)
        {
            if (settings == null)
            {
                return DefaultTimeLimitSeconds;
            }

            switch (settings.BattleMaxTime)
            {
                case SettingsData.Time5: return 300f;
                case SettingsData.Time15: return 900f;
                case SettingsData.TimeInf: return 0f;
                default: return DefaultTimeLimitSeconds;
            }
        }

        public static BattleRoster Begin(
            IEnumerable<BattleAiMarker> markers,
            int playerTeam,
            IEnumerable<BattleAiCover> covers = null)
        {
            var roster = new BattleRoster { PlayerTeam = playerTeam };
            if (markers != null)
            {
                roster._markers.AddRange(markers);
            }
            if (covers != null)
            {
                roster._covers.AddRange(covers);
            }
            Current = roster;
            return roster;
        }

        public void Add(BattleTank tank)
        {
            if (tank == null)
            {
                return;
            }
            var side = tank.Team == 0 ? _tanksA : _tanksB;
            if (side.Contains(tank))
            {
                return;
            }
            side.Add(tank);
            if (tank.Health != null)
            {
                Action deathHandler = delegate { RegisterDestroyed(tank); };
                _deathHandlers[tank] = deathHandler;
                tank.Health.OnDeath += deathHandler;
            }
        }

        public List<BattleTank> GetEnemyTanks(int team)
        {
            return team == 0 ? _tanksB : _tanksA;
        }

        public List<BattleTank> GetAllyTanks(int team)
        {
            return team == 0 ? _tanksA : _tanksB;
        }

        public void StartBattle()
        {
            if (IsOngoing)
            {
                return;
            }
            BigBoss?.Reset();
            TeamAScore = BigBoss == null ? 0 : BigBoss.TeamA;
            TeamBScore = BigBoss == null ? 0 : BigBoss.TeamB;
            WinnerTeam = -1;
            FinishReason = BattleFinishReason.Forced;
            _destroyed.Clear();
            _endedPublished = false;
            _startedAt = Time.time;
            IsOngoing = true;
        }

        public float GetTime()
        {
            return _startedAt < 0f ? 0f : Mathf.Max(0f, Time.time - _startedAt);
        }

        /// <summary>Called by BattleSession.Update so timeout works without a node.</summary>
        public void Tick()
        {
            if (BigBoss != null && IsOngoing)
            {
                TeamAScore = BigBoss.TeamA; TeamBScore = BigBoss.TeamB;
                var timeout = TimeLimitSeconds > 0f && GetTime() >= TimeLimitSeconds;
                // A client never calls the time: the winner on points is the host's verdict.
                if (timeout && BattleNetHooks.PuppetWorld) return;
                if (BigBoss.Verdict(timeout, out var winner)) Finish(winner, timeout ? BattleFinishReason.Timeout : BattleFinishReason.Elimination);
                return;
            }
            if (IsOngoing && TimeLimitSeconds > 0f && GetTime() >= TimeLimitSeconds)
            {
                Finish(-1, BattleFinishReason.Timeout);
            }
        }

        public void ForceFinish(int winnerTeam = -1)
        {
            Finish(winnerTeam, BattleFinishReason.Forced);
        }

        public void NotifyUnspotted(BattleTank tank)
        {
            var handler = TankGotUnspotted;
            if (handler != null)
            {
                handler(tank);
            }
        }

        private void RegisterDestroyed(BattleTank tank)
        {
            if (!IsOngoing || tank == null || !_destroyed.Add(tank))
            {
                return;
            }
            if (BigBoss != null && tank.BigBoss != null)
            {
                var state = tank.BigBoss.State;
                // Online, the host's ledger is the only one: a client mirrors its tickets.
                if (!BattleNetHooks.PuppetWorld) BigBoss.Death(tank.BigBoss.Slot, state.LifeSerial, tank.Team, state.Boss);
                TeamAScore = BigBoss.TeamA; TeamBScore = BigBoss.TeamB;
                TankGotDestroyed?.Invoke(tank); return;
            }
            if (tank.Team == 0)
            {
                TeamBScore++;
            }
            else
            {
                TeamAScore++;
            }

            var teamAAlive = HasLivingTank(_tanksA);
            var teamBAlive = HasLivingTank(_tanksB);
            var ended = !teamAAlive || !teamBAlive;
            if (ended)
            {
                IsOngoing = false;
                WinnerTeam = teamAAlive == teamBAlive ? -1 : (teamAAlive ? 0 : 1);
                FinishReason = BattleFinishReason.Elimination;
            }

            var destroyedHandler = TankGotDestroyed;
            if (destroyedHandler != null)
            {
                destroyedHandler(tank);
            }
            if (ended)
            {
                PublishEnded();
            }
        }

        private void Finish(int winnerTeam, BattleFinishReason reason)
        {
            if (!IsOngoing || _endedPublished)
            {
                return;
            }
            IsOngoing = false;
            WinnerTeam = winnerTeam < 0 ? -1 : winnerTeam;
            FinishReason = reason;
            PublishEnded();
        }

        private void PublishEnded()
        {
            if (_endedPublished)
            {
                return;
            }
            _endedPublished = true;
            var handler = BattleEnded;
            if (handler != null)
            {
                handler(this);
            }
        }

        public void NotifyRespawn(BattleTank tank) { _destroyed.Remove(tank); }

        private bool HasLivingTank(List<BattleTank> tanks)
        {
            for (var i = 0; i < tanks.Count; i++)
            {
                var tank = tanks[i];
                var uprising = tank == null ? null : tank.GetComponent<UprisingTank>();
                if (uprising != null && uprising.State.Respawning) return true;
                if (tank != null && !_destroyed.Contains(tank) && !tank.Dead
                    && (tank.Health == null || tank.Health.Health > 0))
                {
                    return true;
                }
            }
            return false;
        }

        public void End()
        {
            IsOngoing = false;
            foreach (var pair in _deathHandlers)
            {
                if (pair.Key != null && pair.Key.Health != null)
                {
                    pair.Key.Health.OnDeath -= pair.Value;
                }
            }
            _deathHandlers.Clear();
            _destroyed.Clear();
            BattleEnded = null;
            if (Current == this)
            {
                Current = null;
            }
        }
    }
}
