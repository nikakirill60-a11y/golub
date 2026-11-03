using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Tanks.Ai;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Battle.Ai;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Play Mode bot activity probe (Tools/unity.sh bot-probe): live battles in every offline
    /// mode report how far the bots travel from their spawns, how long they stand still, how
    /// often they shoot and use mode abilities, whether a returned bot drives again, and how
    /// fast a bot answers an enemy that appears at point-blank range behind it. The numbers are
    /// tuning evidence; only coarse floors fail the run.
    /// </summary>
    [InitializeOnLoad]
    public static class BotActivityProbeCli
    {
        private const string Key = "Blitz.BotProbe";
        private const float ScenarioSeconds = 75f, SampleSeconds = .5f, Speed = 2f, PointBlankSeconds = 8f;
        private static readonly BattleMode[] Modes = { BattleMode.Normal, BattleMode.BigBoss, BattleMode.Uprising };

        private sealed class Track
        {
            public BattleTank Tank; public AiTankController Ai;
            public Vector3 LifeSpawn, Last; public bool WasDead, LifeMoved;
            public float MaxAway, Idle, Alive, FirstShot = -1; public int Shots, Lives = 1, MovedLives, Abilities;
            public bool UprisingRunning;
            // Alive seconds spent past each gate of the fire path, in order.
            public float Targeted, Seen, Planned, OnTarget, Ready;
            public readonly Dictionary<AiStateKind, int> States = new Dictionary<AiStateKind, int>();
        }

        private static BattleSession _session;
        private static int _mode = -1, _errors, _failures, _trial;
        private static float _start, _next, _trialAt;
        private static DateTime _deadline;
        private static bool _hooked, _pointBlank;
        private static BattleTank _probe;
        private static float _probeShotAt, _acquiredAt, _alignedAt;
        private static Vector3 _probeSpot;
        private static readonly List<Track> Tracks = new List<Track>();
        private static readonly List<float> Reactions = new List<float>();

        static BotActivityProbeCli() { if (SessionState.GetBool(Key, false)) Hook(); }

        public static void Run()
        {
            SessionState.SetBool(Key, true); SessionState.SetInt(Key + ".result", -1); Hook();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); EditorApplication.isPlaying = true;
        }

        private static void Hook()
        {
            if (_hooked) return; _hooked = true; _deadline = DateTime.UtcNow.AddMinutes(12);
            EditorApplication.playModeStateChanged += Changed; EditorApplication.update += Tick; Application.logMessageReceived += Message;
        }

        private static void Message(string message, string stack, LogType type) { if (type == LogType.Exception) _errors++; }

        private static void Changed(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode) EditorApplication.delayCall += NextScenario;
            else if (state == PlayModeStateChange.EnteredEditMode) { var result = SessionState.GetInt(Key + ".result", -1); if (result >= 0) Finish(result); }
        }

        private static void NextScenario()
        {
            try
            {
                Close();
                _mode++;
                while (_mode <= Modes.Length && !Selected(_mode == Modes.Length ? "PointBlank" : Modes[_mode].ToString())) _mode++;
                if (_mode > Modes.Length) { Done(_errors == 0 && _failures == 0 ? 0 : 1); return; }
                _pointBlank = _mode == Modes.Length;
                var mode = _pointBlank ? BattleMode.Normal : Modes[_mode];
                Debug.Log("[Blitz] bot probe: settings hold position=" + AiTankController.BotsStationary);
                _session = BattleSession.Create(null, false, tankId: "t-34", autoStart: true, mode: mode);
                AiTankController.BotsStationary = false;
                Time.timeScale = Speed; _start = Time.time; _next = _start; _trial = 0; _trialAt = _start + 2f; _probe = null;
                Tracks.Clear();
                foreach (var bot in _session.Bots)
                {
                    if (bot == null || bot.Tank == null) continue;
                    var track = new Track { Tank = bot.Tank, Ai = bot, LifeSpawn = bot.Tank.transform.position, Last = bot.Tank.transform.position };
                    bot.Tank.ProjectileFired += (a, b) => { track.Shots++; if (track.FirstShot < 0) track.FirstShot = Time.time - _start; if (bot.Tank == _probe && _probeShotAt < 0) _probeShotAt = Time.time; };
                    if (bot.Tank.BigBoss != null) bot.Tank.BigBoss.Effect += (id, p) => { if (id != "heal" && id != "levelUp" && id != "permanentDamageShield" && id != "railgunShot") track.Abilities++; };
                    Tracks.Add(track);
                }
                Debug.Log("[Blitz] bot probe: " + (_pointBlank ? "point-blank" : mode.ToString()) + " bots=" + Tracks.Count);
            }
            catch (Exception e) { Debug.LogException(e); Done(1); }
        }

        private static void Tick()
        {
            if (DateTime.UtcNow > _deadline) { Debug.LogError("[Blitz] bot probe deadline exceeded"); Done(1); return; }
            if (!EditorApplication.isPlaying || _session == null) return;
            try
            {
                var now = Time.time;
                if (_pointBlank) { PointBlank(now); return; }
                while (now >= _next) { Sample(SampleSeconds); _next += SampleSeconds; }
                if (now - _start >= ScenarioSeconds || !_session.Started) { Report(Modes[_mode], now - _start); NextScenario(); }
            }
            catch (Exception e) { Debug.LogException(e); Done(1); }
        }

        private static void Sample(float dt)
        {
            foreach (var t in Tracks)
            {
                var pos = t.Tank.transform.position;
                if (t.Tank.Dead) { t.WasDead = true; continue; }
                if (t.WasDead) { t.WasDead = false; t.Lives++; t.LifeSpawn = pos; t.Last = pos; t.LifeMoved = false; }
                t.Alive += dt;
                if (Flat(pos - t.Last) < .4f) t.Idle += dt;
                t.Last = pos;
                var away = Flat(pos - t.LifeSpawn); t.MaxAway = Mathf.Max(t.MaxAway, away);
                if (!t.LifeMoved && away > 25f) { t.LifeMoved = true; t.MovedLives++; }
                int count; t.States.TryGetValue(t.Ai.CurrentStateKind, out count); t.States[t.Ai.CurrentStateKind] = count + 1;
                if (t.Ai.CanShoot()) t.Ready += dt;
                if (t.Ai.EnemyTargetIsValid())
                {
                    var target = t.Ai.TargetTank; t.Targeted += dt;
                    if (target.Spotted) t.Seen += dt;
                    if (t.Ai.ShotPlanFor(target).Valid) t.Planned += dt;
                    if (t.Tank.GunTraceTank == target) t.OnTarget += dt;
                }
                var uprising = t.Tank.Uprising;
                if (uprising != null && uprising.State != null)
                {
                    var running = uprising.State.Running(Time.time);
                    if (running && !t.UprisingRunning) t.Abilities++;
                    t.UprisingRunning = running;
                }
            }
        }

        private static void Report(BattleMode mode, float seconds)
        {
            float away = 0, idle = 0, alive = 0, shots = 0, targeted = 0, seen = 0, planned = 0, onTarget = 0, ready = 0;
            int silent = 0, lives = 0, moved = 0, abilities = 0, damage = 0, healing = 0;
            var states = new Dictionary<AiStateKind, int>(); var samples = 0;
            foreach (var t in Tracks)
            {
                away += t.MaxAway; idle += t.Idle; alive += t.Alive; shots += t.Shots; if (t.Shots == 0) silent++;
                lives += t.Lives; moved += t.MovedLives; abilities += t.Abilities;
                targeted += t.Targeted; seen += t.Seen; planned += t.Planned; onTarget += t.OnTarget; ready += t.Ready;
                var stats = _session.StatsFor(t.Tank); if (stats != null) { damage += stats.Damage; healing += stats.Healing; }
                foreach (var pair in t.States) { int c; states.TryGetValue(pair.Key, out c); states[pair.Key] = c + pair.Value; samples += pair.Value; }
            }
            var n = Mathf.Max(1, Tracks.Count);
            var line = new StringBuilder();
            line.AppendFormat(CultureInfo.InvariantCulture,
                "[Blitz] bot probe {0}: t={1:F0}s bots={2} meanMaxAway={3:F1}m idle={4:P0} shotsPerBotMin={5:F2} silentBots={6} livesMoved={7}/{8} abilities={9} damage={10} healing={11} states=",
                mode, seconds, Tracks.Count, away / n, alive > 0 ? idle / alive : 0, alive > 0 ? shots / (alive / 60f) : 0, silent, moved, lives, abilities, damage, healing);
            foreach (var pair in states) line.AppendFormat(CultureInfo.InvariantCulture, "{0}:{1:P0} ", pair.Key, samples > 0 ? (float)pair.Value / samples : 0);
            Debug.Log(line.ToString());
            // Where a silent mode loses its shots: the share of alive time with an enemy target,
            // a spotted one, an armour plan against it, the gun trace on it, and a loaded gun.
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[Blitz] bot probe {0} fire gates: target={1:P0} spotted={2:P0} plan={3:P0} onTarget={4:P0} loaded={5:P0}",
                mode, Share(targeted, alive), Share(seen, alive), Share(planned, alive), Share(onTarget, alive), Share(ready, alive)));
            PlanDiagnostics(mode);
            if (away / n < 20f) Fail(mode + ": bots barely leave their spawns");
            if (moved < lives * .6f) Fail(mode + ": returned bots do not drive again");
        }

        /// <summary>An enemy bot finds the idle player hull 15 m behind its back.</summary>
        private static void PointBlank(float now)
        {
            var player = _session.Player; player.Health.Restore(player.Health.MaxHealth);
            if (_probe != null)
            {
                var ai = _probe.GetComponent<AiTankController>();
                if (_acquiredAt < 0 && ai.TargetTank == player) _acquiredAt = now;
                if (_alignedAt < 0 && ai.IsTankLookingAtPos(_probe, player.transform.position + Vector3.up, .98f)) _alignedAt = now;
                if (_probeShotAt >= 0 || now - _trialAt >= PointBlankSeconds)
                {
                    var reaction = _probeShotAt >= 0 ? _probeShotAt - _trialAt : float.PositiveInfinity;
                    Reactions.Add(reaction);
                    var target = ai.TargetTank == null ? "none" : ai.TargetTank == player ? "player" : ai.TargetTank.name;
                    Debug.Log(string.Format(CultureInfo.InvariantCulture,
                        "[Blitz] bot probe point-blank trial {0}: {1} skill={2} tank={3} state={4} target={5} acquired={6} aligned={7} playerSpotted={8} plan={9} canShoot={10} distance={11:F1} gunError={12:F0} hullError={13:F0} moved={14:F1}",
                        _trial, float.IsPositiveInfinity(reaction) ? "no shot" : reaction.ToString("F2", CultureInfo.InvariantCulture) + " s", ai.Skill,
                        _probe.Card != null ? _probe.Card.Name : "?", ai.CurrentStateKind, target, Since(_acquiredAt), Since(_alignedAt), player.Spotted,
                        ai.ShotPlanFor(player).Valid, ai.CanShoot(), Flat(player.transform.position - _probe.transform.position),
                        FlatAngle(_probe.Turret != null && _probe.Turret.GunPivot != null ? _probe.Turret.GunPivot.forward : _probe.transform.forward, player.transform.position - _probe.transform.position),
                        FlatAngle(_probe.transform.forward, player.transform.position - _probe.transform.position), Flat(_probe.transform.position - _probeSpot)));
                    _probe = null; _trial++; _trialAt = now + 1f;
                }
                return;
            }
            if (now < _trialAt) return;
            if (_trial >= 4)
            {
                var answered = 0; foreach (var r in Reactions) if (!float.IsPositiveInfinity(r)) answered++;
                Debug.Log("[Blitz] bot probe point-blank: answered " + answered + "/" + Reactions.Count);
                if (answered < 3) Fail("point-blank enemies are ignored");
                NextScenario(); return;
            }
            var enemies = new List<AiTankController>();
            foreach (var bot in _session.Bots) if (bot != null && bot.Tank != null && !bot.Tank.Dead && bot.Tank.Team != player.Team) enemies.Add(bot);
            var chosen = enemies.Count > 0 ? enemies[Reactions.Count % enemies.Count] : null;
            if (chosen == null) { Fail("no enemy bot for the point-blank trial"); NextScenario(); return; }
            // The bot is placed 15 m in front of the player, facing away from it.
            var forward = player.transform.forward; forward.y = 0; forward.Normalize();
            var spot = player.transform.position + forward * 15f;
            RaycastHit ground; if (Physics.Raycast(spot + Vector3.up * 60f, Vector3.down, out ground, 200f, ~0, QueryTriggerInteraction.Ignore)) spot = ground.point + Vector3.up * .6f;
            var body = chosen.Tank.GetComponent<Rigidbody>();
            body.position = spot; body.rotation = Quaternion.LookRotation(forward, Vector3.up); body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero;
            chosen.Tank.transform.SetPositionAndRotation(spot, body.rotation);
            chosen.Tank.Gun?.Reload(0);
            _probe = chosen.Tank; _probeShotAt = -1; _acquiredAt = -1; _alignedAt = -1; _trialAt = now; _probeSpot = spot;
        }

        /// <summary>BLITZ_BOT_PROBE_ONLY narrows the run to a comma-separated list of scenarios
        /// (Normal, BigBoss, Uprising, PointBlank) while tuning.</summary>
        private static bool Selected(string scenario)
        {
            var only = Environment.GetEnvironmentVariable("BLITZ_BOT_PROBE_ONLY");
            if (string.IsNullOrEmpty(only)) return true;
            foreach (var part in only.Split(',')) if (string.Equals(part.Trim(), scenario, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void Fail(string label) { _failures++; Debug.LogError("[Blitz] bot probe: " + label); }
        private static string Since(float at) { return at < 0 ? "never" : (at - _trialAt).ToString("F2", CultureInfo.InvariantCulture) + "s"; }
        private static float FlatAngle(Vector3 a, Vector3 b) { a.y = 0; b.y = 0; return Vector3.Angle(a, b); }
        private static float Flat(Vector3 v) { v.y = 0; return v.magnitude; }
        private static float Share(float part, float whole) { return whole > 0 ? part / whole : 0; }

        /// <summary>What the armour planner sees for a few targeting bots: its probe count, the
        /// probes the world walls off, plates without a collision mesh, and the first colliders
        /// on the straight line from the muzzle to the target's gun.</summary>
        private static void PlanDiagnostics(BattleMode mode)
        {
            var planner = typeof(ArmorShotPlanner); const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            var shown = 0;
            foreach (var t in Tracks)
            {
                if (shown >= 4 || t.Tank.Dead || !t.Ai.EnemyTargetIsValid()) continue;
                shown++;
                var target = t.Ai.TargetTank; var plan = ArmorShotPlanner.Choose(t.Tank, target);
                var samples = (int)planner.GetField("_sampleCount", flags).GetValue(null);
                var rays = (int[])planner.GetField("_sampleRay", flags).GetValue(null);
                var walled = 0; for (var i = 0; i < samples; i++) if (rays[i] < 0) walled++;
                var meshless = 0; foreach (var plate in target.Plates) if (plate == null || plate.Shape == null || plate.Shape.sharedMesh == null) meshless++;
                var origin = t.Tank.GunShootPoint(); var aim = target.GunShootPoint(); var line = aim - origin;
                var hits = Physics.RaycastAll(origin, line.normalized, line.magnitude + 5f, BattleLayers.ProjectileMask, QueryTriggerInteraction.Collide);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                var path = new StringBuilder();
                for (var i = 0; i < hits.Length && i < 4; i++)
                {
                    var plate = hits[i].collider.GetComponent<BattleArmorPlate>();
                    var owner = plate == null ? "" : plate.Tank == target ? ",target" : plate.Tank == t.Tank ? ",own" : ",other:" + (plate.Tank != null ? plate.Tank.name : "null");
                    path.AppendFormat(CultureInfo.InvariantCulture, "{0}@{1:F0}[{2}{3}] ", hits[i].collider.name, hits[i].distance, LayerMask.LayerToName(hits[i].collider.gameObject.layer), owner);
                }
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[Blitz] bot probe {0} plan: {1} -> {2} distance={3:F0} valid={4} hopeless={5} samples={6} walled={7} plates={8} meshless={9} line={10}",
                    mode, t.Tank.name, target.name, line.magnitude, plan.Valid, plan.Hopeless, samples, walled, target.Plates.Count, meshless, path));
            }
        }

        private static void Close()
        {
            Time.timeScale = 1;
            if (_session != null) { Object.Destroy(_session.gameObject); _session = null; }
        }

        private static void Done(int code)
        {
            Close(); _mode = Modes.Length + 1; SessionState.SetInt(Key + ".result", code);
            Debug.Log("[Blitz] bot probe finished: code=" + code + " failures=" + _failures + " exceptions=" + _errors);
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else Finish(code);
        }

        private static void Finish(int code)
        {
            SessionState.SetBool(Key, false); EditorApplication.update -= Tick; EditorApplication.playModeStateChanged -= Changed;
            Application.logMessageReceived -= Message; _hooked = false; EditorApplication.Exit(code);
        }
    }
}
