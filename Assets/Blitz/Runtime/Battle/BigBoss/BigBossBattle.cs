using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>Battle orchestration: roles, the ticket ledger and the gravity traps. Missing
    /// server formulae stay in the compatibility profile; none are represented as recovered
    /// 11.20 behaviour. Online, only the simulating peer draws roles and counts deaths; a
    /// client mirrors both (<see cref="ApplyNet"/>, <see cref="BigBossTank.ApplyNet"/>).</summary>
    [DefaultExecutionOrder(150)]
    public sealed class BigBossBattle : MonoBehaviour
    {
        public BattleSession Session { get; private set; }
        public BigBossRules Rules { get; private set; }
        public System.Random Random { get; private set; }
        public BigBossMatch Match { get; private set; }
        private bool _begun;
        private readonly List<Trap> _traps = new List<Trap>(14);
        private sealed class Trap { public BigBossTank Owner; public Vector3 Point; public float Until, Radius, Acceleration, PullSpeed; public DavaFx Fx; }
        /// <summary>Ground grip the suspension applies against sideways sliding (0.75 g at the
        /// source gravity of 10). A pure acceleration below it barely moves a parked tank.</summary>
        public const float TrapGripAcceleration = 7.5f;
        /// <param name="rules">The battle's tables; the shipped ones when null.</param>
        public static BigBossBattle Create(BattleSession session, int seed = 1120, BigBossRules rules = null)
        {
            var c = session.gameObject.AddComponent<BigBossBattle>(); c.Session = session;
            c.Rules = rules ?? new BigBossRules(BlitzResources.LoadText(BigBossRules.Resource)); c.Random = new System.Random(seed);
            c.Match = new BigBossMatch(c.Rules); session.Roster.BigBoss = c.Match;
            var slot = 0;
            foreach (var team in new[] {session.Roster.TanksA, session.Roster.TanksB})
            {
                // A client draws no boss: every seat waits for the host's role as an ordinary crew.
                var boss = BattleNetHooks.PuppetWorld ? -1 : c.Random.Next(team.Count);
                for (var i = 0; i < team.Count; i++) team[i].BigBoss.Assign(c.Rules, i == boss ? 1 : c.Rules.OrdinaryRole(c.Random, 0), slot++);
            }
            return c;
        }
        public void AddTrap(BigBossTank owner, string id, Vector3 point)
        {
            if (owner == null || !IsTrap(id) || !float.IsFinite(point.x) || !float.IsFinite(point.y) || !float.IsFinite(point.z)) return;
            // At most one live trap per caster: source cooldown exceeds duration.
            for (var i = _traps.Count - 1; i >= 0; i--) if (_traps[i].Owner == owner) RemoveTrap(i);
            var mass = Rules.Number(id, "attractorMass");
            var trap = new Trap {Owner = owner, Point = point, Until = Time.time + Rules.Number(id, "effectDuration"), Radius = Rules.Number(id, "radius"), Acceleration = mass * Rules.P("gravityShotAccelerationPerMass"), PullSpeed = mass * Rules.P("gravityShotPullSpeedPerMass")};
            if (!Session.Dedicated)
            {
                trap.Fx = DavaFx.CreateBigBoss(transform, owner.Tank.Team == Session.Player.Team ? "Boss_mode_Graviton" : "Boss_mode_Graviton_red", Rules.Number(id, "effectDuration"));
                trap.Fx.transform.position = point; trap.Fx.ViewCamera = Session.View?.Active;
            }
            _traps.Add(trap);
            if (!BattleNetHooks.PuppetWorld) BattleNetHooks.RaiseBigBossTrap(owner, id, point);
        }
        /// <summary>A gravity trap ability of these tables; anything else from the wire is dropped.</summary>
        public bool IsTrap(string id)
        {
            return !string.IsNullOrEmpty(id) && id.Length <= 32 && Rules.Data["abilities"].Has(id) && Rules.Ability(id)["family"].AsString() == "gravityShot";
        }
        /// <summary>Client: the host placed a trap; it is drawn and timed here but never pulls.</summary>
        public void ReplayTrap(BigBossTank owner, string id, Vector3 point) { AddTrap(owner, id, point); }
        /// <summary>Client: the host's ticket ledger.</summary>
        public void ApplyNet(int teamA, int teamB) { Match.ApplyNet(teamA, teamB); }
        /// <summary>Seconds the caster's live trap still pulls, 0 without one.</summary>
        public float TrapSeconds(BigBossTank owner)
        {
            for (var i = 0; i < _traps.Count; i++) if (_traps[i].Owner == owner) return Mathf.Max(0, _traps[i].Until - Time.time);
            return 0;
        }
        private void RemoveTrap(int index) { if (_traps[index].Fx != null) _traps[index].Fx.Stop(); _traps.RemoveAt(index); }
        private void FixedUpdate()
        {
            if (!Session.Started) return;
            var core = Rules.P("gravityShotCoreRadius"); var dt = Time.fixedDeltaTime;
            for (var i = _traps.Count - 1; i >= 0; i--)
            {
                var t = _traps[i]; if (Time.time >= t.Until || t.Owner == null) { RemoveTrap(i); continue; }
                // The host's physics pulls the hulls; a client only shows the field.
                if (BattleNetHooks.PuppetWorld) continue;
                var enemies = Session.Roster.GetEnemyTanks(t.Owner.Tank.Team);
                for (var e = 0; e < enemies.Count; e++)
                {
                    var enemy = enemies[e];
                    if (enemy == null || enemy.Dead) continue;
                    var d = t.Point - enemy.transform.position; d.y = 0; var distance = d.magnitude;
                    if (distance <= core || distance > t.Radius) continue;
                    var body = enemy.GetComponent<Rigidbody>();
                    if (body == null || body.isKinematic) continue;
                    // Profile: steer the radial speed towards a mass-scaled approach speed that
                    // eases into the core, with enough authority to beat the track grip.
                    var dir = d / distance;
                    var target = Mathf.Min(t.PullSpeed, Mathf.Sqrt(2 * t.Acceleration * (distance - core)));
                    var change = Mathf.Min(target - Vector3.Dot(body.linearVelocity, dir), (t.Acceleration + TrapGripAcceleration) * dt);
                    if (change > 0) body.AddForce(dir * change, ForceMode.VelocityChange);
                }
            }
        }
        public void Begin()
        {
            if (_begun) return;
            _begun = true; foreach (var tank in Session.BattleTanks()) tank.BigBoss.Begin();
        }
        private void LateUpdate()
        {
            // One terminal decision after every nested explosion/death in this frame.
            Session.Roster.Tick();
            if (Session.Hud != null)
            {
                var playerA = Session.Roster.PlayerTeam == 0;
                Session.Hud.SetTeamScores(playerA ? Match.TeamA : Match.TeamB, playerA ? Match.TeamB : Match.TeamA);
            }
            if (_begun && !Session.Started) for (var i = _traps.Count - 1; i >= 0; i--) RemoveTrap(i);
        }
        private void OnDestroy() { for (var i = _traps.Count - 1; i >= 0; i--) RemoveTrap(i); }
    }
}
