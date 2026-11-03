using System;
using System.Collections;
using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle.Ai;
using BlitzLegacy.Runtime.Telemetry;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>Per-tank Uprising controller. No particle assets from Godot are used.</summary>
    [DisallowMultipleComponent]
    public sealed class UprisingTank : MonoBehaviour
    {
        public UprisingState State { get; private set; }
        public BattleTank Tank { get; private set; }
        public BattleSession Session { get; private set; }
        public event Action Respawned;
        public event Action<Vector3,Vector3> ChainRequested;
        public bool HasStatus(string id){return _statuses.ContainsKey(id);}
        public float StatusSeconds(string id){Status status;return _statuses.TryGetValue(id,out status)?Mathf.Max(0,status.Until-Time.time):0;}
        public float PassiveCooldown(string id){return Mathf.Max(0,(id=="splash"?_splashAt:id.StartsWith("lightning",StringComparison.Ordinal)?_chainAt:Time.time)-Time.time);}
        public event Action<string, Vector3> EffectRequested;
        private float _power, _forward, _back, _nextAi, _splashAt, _chainAt;
        private float _rewindUseAt = -1f, _damageWindowAt = -1f;
        private int _damageWindow;
        private Vector3 _moonwalkGoal;
        private readonly List<int> _ammo = new List<int>();
        private readonly Dictionary<string, Status> _statuses = new Dictionary<string, Status>();
        private readonly List<string> _statusKeys = new List<string>(2);
        private readonly List<Snapshot> _history = new List<Snapshot>();
        private readonly HashSet<BattleTank> _thanatosKnownTo = new HashSet<BattleTank>();
        private float _nextSnapshot;
        public bool Rewinding { get; private set; }
        public float BaseForwardSpeed {get {return _forward;}}
        private Coroutine _rewind, _spin;
        public const float DupletShotDelay = .3f;
        private struct PendingDuplet { public float At; public int Slot, Life; }
        private readonly List<PendingDuplet> _dupletShots = new List<PendingDuplet>(8);
        private sealed class Status { public BattleTank Caster; public float Until, Fraction; public Vector3 Position; }
        private struct Snapshot { public float Time; public Vector3 Position; public Quaternion Rotation; public int Health; }
        public bool Concealed { get { return !Tank.Dead && (State.CloakUntil > Time.time || (State.ActiveId == "moonwalk" && State.Running(Time.time))); } }
        public bool ShowsZeroHealthVisual { get { return PretendDeadActive; } }
        public bool PretendDeadActive { get { return State != null && State.ActiveId == "pretendDead" && State.Running(Time.time); } }
        public bool ShowsZeroHealthVisualTo(BattleTank observer)
        {
            return PretendDeadActive && observer != null && observer.Team != Tank.Team;
        }
        /// <summary>
        /// Play Dead is a full simulation of a destroyed tank for an enemy that
        /// has not seen through it: no armour paint, no outline, no lit reticle
        /// and a wreck everywhere in the HUD. Bots that already know (see
        /// <see cref="KnowsThanatos"/>) keep treating the tank as alive.
        /// </summary>
        public bool LooksDeadTo(BattleTank observer)
        {
            return PretendDeadActive && observer != null && observer != Tank
                && observer.Team != Tank.Team && !KnowsThanatos(observer);
        }
        /// <summary>Shadow (and the respawn cloak) leave no ground marks for the other team.</summary>
        public bool HidesTrackMarksFrom(int observerTeam)
        {
            return Tank != null && observerTeam != Tank.Team && Concealed;
        }
        public static bool InThanatos(BattleTank tank)
        {
            return tank != null && !tank.Dead && tank.Uprising != null && tank.Uprising.PretendDeadActive;
        }
        /// <summary>
        /// Host: a collision drops Play Dead without any damage to the tank
        /// that carries it. The cooldown armed on activation keeps running.
        /// </summary>
        public static void BreakThanatos(BattleTank tank)
        {
            if (BattleNetHooks.PuppetWorld || !InThanatos(tank)) return;
            tank.Uprising.EndActive();
        }
        public static UprisingTank Attach(BattleTank tank, BattleSession session)
        {
            var c = tank.gameObject.AddComponent<UprisingTank>(); c.Tank = tank; c.Session = session; tank.Uprising = c;
            tank.HudMode = new UprisingHudMode(c);
            var catalog = new UprisingCatalog(BlitzResources.LoadText("uprising_abilities"));
            c.State = new UprisingState(catalog, catalog.ForTank(tank.Card.Name, tank.Card.Class), session == null ? null : session.UprisingRules);
            c._ammo.AddRange(tank.ShellAmounts); c._power = tank.Suspension.EnginePower;
            c._forward = tank.Suspension.MaxSpeedForward; c._back = tank.Suspension.MaxSpeedBack;
            tank.Health.OnDeath += c.OnDeath; tank.Health.OnDamageInfoTaken += c.OnDamageTaken; tank.ProjectileFired += c.OnShot; tank.PrimaryProjectileFired += c.OnPrimaryShot;
            UprisingVfx.Attach(c); UprisingAudio.Attach(c);
            return c;
        }
        private void OnDestroy()
        {
            ClearDuplet();
            if (Tank != null && Tank.Uprising == this) Tank.Uprising = null;
            if (Tank != null && Tank.HudMode is UprisingHudMode mode && mode.Owner == this) Tank.HudMode = null;
            if (Tank != null) { Tank.Health.OnDeath -= OnDeath; Tank.Health.OnDamageInfoTaken -= OnDamageTaken; Tank.ProjectileFired -= OnShot; Tank.PrimaryProjectileFired -= OnPrimaryShot; }
        }
        private void Fx(string id, Vector3 point)
        {
            EffectRequested?.Invoke(id, point);
            if (!BattleNetHooks.PuppetWorld) { BattleNetHooks.RaiseUprisingEffect(this, id, point); }
        }
        private void ChainFx(Vector3 from, Vector3 to)
        {
            ChainRequested?.Invoke(from, to);
            if (!BattleNetHooks.PuppetWorld) { BattleNetHooks.RaiseUprisingChain(this, from, to); }
        }
        /// <summary>Host: everything a peer needs to draw and drive this tank's Uprising state.</summary>
        public UprisingSnapshot CaptureNet()
        {
            var now = Time.time; var s = State.Capture(now);
            s.CurseIn = StatusSeconds("curse"); s.MarkIn = StatusSeconds("huntersMark");
            s.SplashIn = Mathf.Max(0, _splashAt - now); s.ChainIn = Mathf.Max(0, _chainAt - now);
            return s;
        }
        /// <summary>
        /// Client puppet: mirror the host's state (UI, VFX, concealment, roster)
        /// and replay the host's respawn locally; pose and health still arrive
        /// through <see cref="BattleTank.ApplyNetState"/>.
        /// </summary>
        public void ApplyNet(UprisingSnapshot s)
        {
            if (State == null || Tank == null || !Tank.NetPuppet) return;
            var now = Time.time; var previousLife = State.LifeSerial;
            State.Apply(s, now);
            NetStatus("curse", s.CurseIn, now); NetStatus("huntersMark", s.MarkIn, now);
            _splashAt = now + Mathf.Max(0, s.SplashIn); _chainAt = now + Mathf.Max(0, s.ChainIn);
            if (Tank.Dead && s.AliveLife && s.LifeSerial != previousLife) NetRestore();
        }
        private void NetStatus(string id, float remaining, float now)
        {
            Status status;
            if (remaining <= 0f) { _statuses.Remove(id); return; }
            if (!_statuses.TryGetValue(id, out status)) { status = new Status { Position = transform.position }; _statuses[id] = status; }
            status.Until = now + remaining;
        }
        private void NetRestore()
        {
            ClearDuplet(); _history.Clear(); _thanatosKnownTo.Clear(); CancelRewind();
            Tank.RestoreLife(null); Tank.SetShellAmounts(_ammo);
            Session.Roster.NotifyRespawn(Tank); Session.NotifyUprisingRespawn(Tank);
            RefreshRespawnHud();
        }
        private void RefreshRespawnHud()
        {
            if (Tank==Session.Player) { Session.View.Tank = Tank; Session.SetCombatHudVisible(true); }
            var enemySide = Tank.Team != Session.Roster.PlayerTeam;
            Session.Hud.SetTeamTankAlive(enemySide, (enemySide ? Session.Enemies : Session.Allies).IndexOf(Tank), true);
            for (var i=0; i<Session.Hud.TankPlateCount; i++)
            { var plate=Session.Hud.GetTankPlate(i); if (plate.Tank==Tank) plate.RefreshLife(); }
            Respawned?.Invoke();
        }
        public void ReplayNetEffect(string id, Vector3 point) { EffectRequested?.Invoke(id, point); }
        public void ReplayNetChain(Vector3 from, Vector3 to) { ChainRequested?.Invoke(from, to); }
        private void OnDeath()
        {
            ClearDuplet(); StopSpin();
            _statuses.Clear(); _history.Clear(); _thanatosKnownTo.Clear(); CancelRewind();
            _rewindUseAt = _damageWindowAt = -1f; _damageWindow = 0; _moonwalkGoal = Vector3.zero;
            if (BattleNetHooks.PuppetWorld) return;
            if (!State.OnDeath(Time.time, n => UnityEngine.Random.Range(0,n))) return;
            // Remote humans pick their own offer through the host (BattleRemoteDriver).
            if (Tank != Session.Player && GetComponent<BattleRemoteDriver>() == null)
            {
                // The perk that suits the hull, not merely the dearest one.
                var ai = GetComponent<AiTankController>();
                var best = UprisingAiDecision.ChoosePerk(State.Offer,
                    Tank.Card != null ? Tank.Card.Class : TankClass.Medium,
                    ai != null && ai.PlayStyleIs(BlitzLegacy.Core.Tanks.Ai.AiPlayStyle.Sniper),
                    State.CanBuy, State.Cost);
                if (!string.IsNullOrEmpty(best)) State.Toggle(best, Time.time, Session.Started);
            }
        }
        private void OnDamageTaken(DamageInfo damage)
        {
            var amount = damage == null ? 0 : damage.Dmg;
            if (Tank == Session.Player || State.ActiveId != "returnInTime" || amount <= 0 || Tank.Dead) return;
            var now = Time.time;
            if (now - _damageWindowAt > 1.5f) _damageWindow = 0;
            _damageWindowAt = now;
            _damageWindow += amount;
            var max = Mathf.Max(1, Tank.Health.MaxHealth);
            if (amount < max * .18f && _damageWindow < max * .25f) return;
            var useAt = now + UnityEngine.Random.Range(.2f, 1.0001f);
            if (_rewindUseAt < now || useAt < _rewindUseAt) _rewindUseAt = useAt;
        }
        public bool Toggle(string id)
        {
            if (BattleNetHooks.PuppetWorld && Tank == Session.Player)
            {
                var index = State.Offer.IndexOf(id);
                if (index < 0 || index > 2) return false;
                BattleNetHooks.RequestUprisingAction((byte)(index + 2));
                return true;
            }
            var ok = State.Toggle(id, Time.time, Session.Started); if (ok) Mobility(); return ok;
        }
        private void OnShot(bool first, int shell)
        {
            State.CloakUntil = -1;
            if (PretendDeadActive) RevealThanatosToVisibleEnemyBots();
            if (State.ActiveId == "moonwalk") EndActive();
        }
        private void OnPrimaryShot(int shell)
        {
            if (!State.Has("duplet") || Tank.Dead || Session == null || !Session.Started) return;
            // Value-only deadlines: one echo per actual primary, no coroutine allocation or recursion.
            _dupletShots.Add(new PendingDuplet { At = Tank.Gun.Ct() + DupletShotDelay, Slot = shell, Life = State.LifeSerial });
            Tank.PendingDupletShots = _dupletShots.Count;
        }
        public void TickDuplet()
        {
            if (Tank == null || State == null || Session == null || !Session.Started || Tank.Dead || !State.Has("duplet"))
            { ClearDuplet(); return; }
            var now = Tank.Gun.Ct();
            while (_dupletShots.Count > 0 && _dupletShots[0].At <= now)
            {
                var shot = _dupletShots[0];
                _dupletShots.RemoveAt(0);
                Tank.PendingDupletShots = _dupletShots.Count;
                if (shot.Life != State.LifeSerial || State.Respawning) continue;
                Tank.LaunchExtraShell(shot.Slot);
            }
        }
        private void ClearDuplet()
        {
            _dupletShots.Clear();
            if (Tank != null) Tank.PendingDupletShots = 0;
        }
        private void EndActive() { State.ActiveUntil = -1; State.ShieldArmed = false; _thanatosKnownTo.Clear(); Mobility(); }
        public bool UseActive(BattleTank preferredTarget = null)
        {
            if (BattleNetHooks.PuppetWorld && Tank == Session.Player)
            {
                BattleNetHooks.RequestUprisingAction(1);
                return true;
            }
            var now = Time.time;
            if (Time.timeScale<=0 || !Session.Started || Tank.Dead || Tank.Blocked || State.Respawning || State.ReadyAt > now || State.Running(now) || Rewinding) return false;
            var id = State.ActiveId;
            var target = preferredTarget != null ? preferredTarget : Tank.GunTraceTank;
            if (id == "curse" || id == "huntersMark" || id == "vortex")
            {
                var other = target == null ? null : target.GetComponent<UprisingTank>();
                var observer = Session != null && Tank != Session.Player ? Tank : null;
                if (other == null || target.Dead || target.Team == Tank.Team || !target.Spotted || other.Concealed || !IsTargetable(target, observer)) return false;
                if (id == "vortex") other.StartSpin(UnityEngine.Random.Range(120f,180f) * (UnityEngine.Random.value < .5f ? -1 : 1));
                else
                {
                    if (other._statuses.ContainsKey(id)) return false;
                    other._statuses[id] = new Status { Caster = Tank, Until = now + 10, Position = target.transform.position };
                }
                if (id == "huntersMark") { State.CloakUntil = -1; Tank.SetSpotted(true); }
                other.Fx(id, target.transform.position);
            }
            else if (id == "returnInTime")
            {
                if (_history.Count == 0) return false;
                _statuses.Clear(); _rewind = StartCoroutine(Rewind());
            }
            else if (id == "pretendDead") { ClearNegativeEffects(); State.ShieldArmed = true; }
            State.ActiveUntil = State.Number(id,"duration",0) > 0 ? now + State.Number(id,"duration",0) : -1;
            State.ReadyAt = now + State.Number(id,"cooldown",45); Mobility();
            if(id!="curse" && id!="huntersMark" && id!="vortex")Fx(id,Tank.transform.position); return true;
        }
        private void StartSpin(float degrees) { StopSpin(); _spin = StartCoroutine(Spin(degrees)); }
        private void StopSpin() { if (_spin != null) StopCoroutine(_spin); _spin = null; }
        /// <summary>
        /// Play Dead sheds every hostile effect: the Uprising statuses (curse,
        /// hunter's mark), a vortex spin in progress and damaged modules.
        /// </summary>
        private void ClearNegativeEffects()
        {
            _statuses.Clear(); StopSpin();
            Tank.RepairEverything();
        }
        private IEnumerator Spin(float degrees)
        {
            var rb = Tank.GetComponent<Rigidbody>(); var start = rb.rotation; var end = Quaternion.AngleAxis(degrees,Vector3.up)*start;
            var until = Time.time + .6f;
            while (Time.time < until && !Tank.Dead && Session.Started)
            { rb.MoveRotation(Quaternion.Slerp(start,end,1-(until-Time.time)/.6f)); yield return new WaitForFixedUpdate(); }
            _spin = null;
        }
        private bool _wasKinematic, _suspensionEnabled, _wasBlocked;
        private IEnumerator Rewind()
        {
            Rewinding = true; var rb = Tank.GetComponent<Rigidbody>();
            _wasKinematic = rb.isKinematic; _wasBlocked = Tank.Blocked; _suspensionEnabled = Tank.Suspension.enabled;
            var snapshots = new List<Snapshot>(_history); _history.Clear();
            var cutoff = Time.time - 4; var first = snapshots[0];
            var hp = first.Health;
            if (snapshots.Count > 1 && first.Time < cutoff)
            {
                var second = snapshots[1]; var weight = Mathf.Clamp01((cutoff-first.Time)/Mathf.Max(.00001f,second.Time-first.Time));
                first.Position = Vector3.Lerp(first.Position,second.Position,weight);
                first.Rotation = Quaternion.Slerp(first.Rotation,second.Rotation,weight);
            }
            var path = new List<Snapshot>();
            for (var i=snapshots.Count-1;i>=0;i--) if (snapshots[i].Time > cutoff) path.Add(snapshots[i]);
            path.Add(first);
            Tank.Blocked = true; Tank.Suspension.StopDrive(); Tank.Suspension.enabled = false;
            if (!rb.isKinematic) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            rb.isKinematic = true;
            var index = 0;
            while (index < path.Count && !Tank.Dead && Session.Started)
            {
                var remaining = 200f/3.6f*Time.fixedDeltaTime;
                while (index < path.Count)
                {
                    var target = path[index]; var distance = Vector3.Distance(rb.position,target.Position);
                    if (distance <= remaining + .00001f)
                    { rb.position=target.Position; rb.rotation=target.Rotation; remaining=Mathf.Max(0,remaining-distance); index++; }
                    else
                    { var weight=remaining/distance; rb.position=Vector3.Lerp(rb.position,target.Position,weight); rb.rotation=Quaternion.Slerp(rb.rotation,target.Rotation,weight); break; }
                }
                if(index<path.Count) yield return new WaitForFixedUpdate();
            }
            if (!Tank.Dead && Session.Started)
            { Tank.Health.Restore(Mathf.Clamp(hp,1,Tank.Health.MaxHealth)); Tank.Modules?.RepairEverything(); }
            RestoreRewindPhysics(Tank.Dead || !Session.Started);
        }
        private void RestoreRewindPhysics(bool cancelled)
        {
            if (!Rewinding) return;
            var rb=Tank.GetComponent<Rigidbody>(); rb.isKinematic=_wasKinematic;
            if (!rb.isKinematic) { rb.linearVelocity=Vector3.zero; rb.angularVelocity=Vector3.zero; }
            Tank.Suspension.enabled=_suspensionEnabled;
            if(!cancelled) Tank.Blocked=_wasBlocked;
            Rewinding=false; _rewind=null; _history.Clear();
        }
        private void CancelRewind()
        {
            if (_rewind != null) StopCoroutine(_rewind);
            RestoreRewindPhysics(true); _rewind=null;
        }
        private void Update()
        {
            using var slowScope = BlitzPerfMonitor.Slow("uprising.update");
            TickDuplet();
            if (State == null || Session == null || !Session.Started || BattleNetHooks.PuppetWorld) return;
            var now = Time.time; State.Accrue(Time.deltaTime);
            if (State.Respawning)
            {
                if (State.Respawn(now,true)) Restore();
                return;
            }
            if (Tank.Dead) return;
            if (Concealed) Tank.SetSpotted(false);
            if (State.ActiveUntil > 0 && !State.Running(now)) EndActive();
            if (PretendDeadActive && ThanatosMotionCueActive()) RevealThanatosToVisibleEnemyBots();
            Mobility();
            var reload = State.ReloadMultiplier(now);
            if (Tank.Gun != null && reload < 1) Tank.Gun.AdvanceReload(Time.deltaTime * (1f / reload - 1f));
            if (State.Running(now) && State.ActiveId == "berserk") Tank.TightenDispersion();
            TickStatuses();
            if (Tank != Session.Player)
            {
                var ai = Tank.GetComponent<AiTankController>();
                ApplyAiActiveMovement(ai);
                if (now >= _nextAi)
                {
                    _nextAi = now + UnityEngine.Random.Range(.2f, .45f);
                    using (BattlePerfMarkers.UprisingActive.Auto())
                    {
                        TryUseActive(ai, now);
                    }
                }
            }
        }
        private void TryUseActive(AiTankController ai, float now)
        {
            if (ai == null || State.ReadyAt > now || State.Running(now) || Rewinding) return;
            if (State.ActiveId == "returnInTime")
            {
                if (_rewindUseAt >= 0 && now >= _rewindUseAt && _history.Count > 1)
                {
                    _rewindUseAt = -1;
                    _damageWindow = 0;
                    UseActive();
                }
                return;
            }
            var target = ai.EnemyTargetIsValid() ? ai.TargetTank : null;
            if (target == null || !IsTargetable(target, Tank)) return;
            var offset = target.transform.position - Tank.transform.position;
            var distance = offset.magnitude;
            var direction = distance > .01f ? offset / distance : Vector3.zero;
            var relative = target.LinearVelocity - Tank.LinearVelocity;
            var plan = ai.ShotPlanFor(target);
            var context = new UprisingAiDecision.Context
            {
                Ability = State.ActiveId,
                SelfClass = Tank.Card.Class,
                TargetClass = target.Card.Class,
                State = ai.CurrentStateKind,
                HealthFraction = (float)Tank.Health.Health / Mathf.Max(1, Tank.Health.MaxHealth),
                Distance = distance,
                ClosingSpeed = -Vector3.Dot(relative, direction),
                SelfSpeed = Tank.LinearVelocity.magnitude,
                TargetSpeed = target.LinearVelocity.magnitude,
                SelfArmor = Tank.AvgArmor,
                TargetArmor = target.AvgArmor,
                HasTarget = true,
                HasRamming = State.Has("ramming1") || State.Has("ramming2"),
                CanPenetrate = plan.Valid && !plan.Hopeless
            };
            if (!UprisingAiDecision.ShouldUse(context)) return;
            if (State.ActiveId == "moonwalk" && context.HealthFraction <= .42f)
            {
                _moonwalkGoal = ai.FindHidingSpot(target.transform.position);
                if (_moonwalkGoal == Vector3.zero)
                    _moonwalkGoal = Tank.transform.position - direction * 50f;
            }
            UseActive(target);
        }
        private void ApplyAiActiveMovement(AiTankController ai)
        {
            if (ai == null || !State.Running(Time.time)) return;
            if (State.ActiveId == "pretendDead")
            {
                var target = ai.TargetTank;
                var ramming = target != null && (target.transform.position - Tank.transform.position).sqrMagnitude <= 32f * 32f && Tank.LinearVelocity.magnitude >= 8f;
                if (!ramming) { Tank.Suspension.StopDrive(); ai.Drive(0f, 0f, true); }
            }
            else if (State.ActiveId == "moonwalk" && _moonwalkGoal != Vector3.zero)
            {
                ai.TargetPosition = _moonwalkGoal;
                ai.DriveToPos(_moonwalkGoal, true, true);
            }
        }
        public static bool IsTargetable(BattleTank target, BattleTank observer = null)
        {
            if (target == null || target.Dead) return false;
            var uprising = target.GetComponent<UprisingTank>();
            return uprising == null || !uprising.PretendDeadActive || uprising.KnowsThanatos(observer);
        }

        private bool KnowsThanatos(BattleTank observer)
        {
            if (!PretendDeadActive) return true;
            if (observer == null || observer == Tank || observer.Team == Tank.Team) return false;
            if (observer.GetComponent<AiTankController>() == null) return false;
            if (Session != null && Session.Roster != null
                && Session.Roster.TeamAScore + Session.Roster.TeamBScore == 0)
            {
                return true;
            }
            return _thanatosKnownTo.Contains(observer);
        }

        private bool ThanatosMotionCueActive()
        {
            return Mathf.Abs(Tank.ForwardAxis) > 0.01f
                || Mathf.Abs(Tank.RightAxis) > 0.01f
                || Mathf.Abs(Tank.TurretAngularVelocity) > 0.1f;
        }

        private void RevealThanatosToVisibleEnemyBots()
        {
            if (!PretendDeadActive || Session == null || Session.Roster == null || Tank == null) return;
            var enemies = Session.Roster.GetEnemyTanks(Tank.Team);
            for (var i = 0; i < enemies.Count; i++)
            {
                var enemy = enemies[i];
                if (enemy == null || enemy.Dead) continue;
                var ai = enemy.GetComponent<AiTankController>();
                if (ai == null || !ai.TankCheckVisibility(Tank, false)) continue;
                _thanatosKnownTo.Add(enemy);
                if (!ai.EnemyTargetIsValid()) ai.TargetTank = Tank;
            }
        }
        private void FixedUpdate()
        {
            using var slowScope = BlitzPerfMonitor.Slow("uprising.fixedUpdate");
            if (State == null || !Session.Started || Tank.Dead || Rewinding || BattleNetHooks.PuppetWorld || State.ActiveId != "returnInTime") return;
            if (Time.time < _nextSnapshot) return;
            _nextSnapshot = Time.time+.05f;
            _history.Add(new Snapshot { Time=Time.time, Position=transform.position, Rotation=transform.rotation, Health=Tank.Health.Health });
            while (_history.Count > 2 && Time.time-_history[1].Time >= 4) _history.RemoveAt(0);
        }
        private void TickStatuses()
        {
            _statusKeys.Clear(); _statusKeys.AddRange(_statuses.Keys);
            foreach (var id in _statusKeys)
            {
                var status = _statuses[id];
                // Shadow hides the tank (and the mark's visuals) from the enemy,
                // it does not cleanse the mark: only a real loss of spotting does.
                if (Time.time >= status.Until || status.Caster == null || (id == "huntersMark" && !Tank.Spotted && !Concealed)) { _statuses.Remove(id); continue; }
                if (id != "curse" || Rewinding) continue;
                var delta=transform.position-status.Position; delta.y=0; status.Position=transform.position;
                status.Fraction += delta.magnitude * Tank.Health.MaxHealth * .01f;
                var amount=Mathf.FloorToInt(status.Fraction+1e-5f); status.Fraction=Mathf.Max(0,status.Fraction-amount);
                if (amount>0) AbilityDamage(status.Caster,Tank,amount,transform.position,true);
                if (Tank.Dead) break;
            }
        }
        public static void Mediate(BattleTank victim, DamageInfo damage)
        {
            if (damage.Dmg <= 0) return;
            var attacker=damage.AttackerTank as BattleTank; var source=attacker == null ? null : attacker.GetComponent<UprisingTank>();
            // Wolf Strike (ramming1/2) and the other boosts never amplify a hit on a team-mate.
            if (source != null && attacker != victim && attacker.Team != victim.Team) damage.Dmg=Mathf.RoundToInt(damage.Dmg*source.State.Outgoing(damage.DmgType,Time.time));
            var c=victim.GetComponent<UprisingTank>(); if (c==null) return;
            var s=c.State; float factor=1;
            if (damage.DmgType==DamageType.Collision) factor*=s.Has("ramming2")?s.Number("ramming2","ram_in_mul",.35f):s.Has("ramming1")?s.Number("ramming1","ram_in_mul",.65f):1;
            Status mark;
            if (c._statuses.TryGetValue("huntersMark",out mark) && mark.Until>Time.time && mark.Caster!=null && attacker!=null && attacker.Team==mark.Caster.Team && attacker.Team!=victim.Team) factor*=1.6f;
            if (s.Running(Time.time) && s.ActiveId=="berserk") factor*=1.4f;
            damage.Dmg=s.Absorb(Mathf.RoundToInt(damage.Dmg*factor),victim.Health.Health,Time.time);
            s.CloakUntil=-1; if (s.ActiveId=="moonwalk" || s.ActiveId=="pretendDead") c.EndActive();
        }
        private void Mobility()
        {
            float engine=1,speed=1; int add=0;
            if (State.CloakUntil>Time.time) { engine*=2; speed*=1.4f; }
            if (State.Has("engineBoost2")) { engine*=State.Number("engineBoost2","engine_mul",3.1f); add+=(int)State.Number("engineBoost2","speed_add",20); }
            else if (State.Has("engineBoost1")) { engine*=State.Number("engineBoost1","engine_mul",2.1f); add+=(int)State.Number("engineBoost1","speed_add",10); }
            if (State.Running(Time.time) && State.ActiveId=="moonwalk") { engine*=4; add+=30; }
            Tank.Suspension.EnginePower=(int)(_power*engine); Tank.Suspension.MaxSpeedForward=(int)(_forward*speed)+add; Tank.Suspension.MaxSpeedBack=(int)(_back*speed);
        }
        private void Restore()
        {
            ClearDuplet();
            var spawns=Tank.Team==0 ? Session.World.PlayerSpawns:Session.World.BotSpawns;
            Transform best=null; float bestDist=-1;
            foreach (var spawn in spawns)
            {
                var distance=float.PositiveInfinity;
                foreach (var enemy in Session.Roster.GetEnemyTanks(Tank.Team)) if (enemy!=null && !enemy.Dead) distance=Mathf.Min(distance,(enemy.transform.position-spawn.position).sqrMagnitude);
                if (best==null || distance>bestDist) { best=spawn; bestDist=distance; }
            }
            Tank.RestoreLife(best); Tank.SetShellAmounts(_ammo); Session.Roster.NotifyRespawn(Tank); Session.NotifyUprisingRespawn(Tank);
            _splashAt=_chainAt=Time.time+5; _statuses.Clear(); _history.Clear(); _thanatosKnownTo.Clear();
            _rewindUseAt = _damageWindowAt = -1f; _damageWindow = 0; _moonwalkGoal = Vector3.zero;
            _nextAi = Time.time + UnityEngine.Random.Range(.6f, 1.2f); Mobility();
            var ai=Tank.GetComponent<AiTankController>(); if (ai!=null) ai.BeginNewLife();
            RefreshRespawnHud();
        }
        public static void AbilityDamage(BattleTank owner,BattleTank victim,int amount,Vector3 point,bool silent=false)
        {
            if (victim==null || victim.Dead || amount<=0) return;
            victim.ReceiveDamage(new DamageInfo { AttackerTank=owner, Dmg=amount, DmgBase=amount, DmgType=DamageType.Ability, Pos=new Vec3(point.x,point.y,point.z),SuppressFeedback=silent });
        }
        public void Impact(Vector3 point,int life,int splashDamage)
        {
            if (!Valid(life) || !State.Has("splash") || Time.time<_splashAt) return;
            _splashAt=Time.time+30; var splashRadius=State.Number("splash","radius",25f);
            foreach(var target in Session.Roster.GetEnemyTanks(Tank.Team))
                if(target!=null && !target.Dead && (target.transform.position-point).sqrMagnitude<=splashRadius*splashRadius) AbilityDamage(Tank,target,splashDamage,point);
            Fx("splash",point);
        }
        // Puppets only replay the host's passives (RPC), never trigger them locally.
        private bool Valid(int life) { return !BattleNetHooks.PuppetWorld && Session.Started && !Tank.Dead && State.LifeSerial==life; }
        public void Penetration(BattleTank victim,int life,int damageBase)
        {
            var id=State.Has("lightning2")?"lightning2":State.Has("lightning1")?"lightning1":"";
            if(id=="" || !Valid(life) || victim==null || victim.Team==Tank.Team || Time.time<_chainAt) return;
            _chainAt=Time.time+6; var radius=State.Number(id,"radius",id=="lightning2"?70f:60f);
            var factors=State.Numbers(id,"damage_factors",id=="lightning2"?new[]{.45f,.35f,.25f}:new[]{.25f,.15f,.1f});
            var targets=new List<BattleTank>{victim};
            for(var i=1;i<3;i++)
            {
                BattleTank nearest=null; float distance=radius*radius;
                foreach(var candidate in Session.Roster.GetEnemyTanks(Tank.Team))
                {
                    if(candidate==null || candidate.Dead || targets.Contains(candidate))continue;
                    var d=(candidate.transform.position-targets[i-1].transform.position).sqrMagnitude;
                    if(d<=distance){nearest=candidate;distance=d;}
                }
                if(nearest==null)break;targets.Add(nearest);
            }
            for(var i=0;i<targets.Count;i++)
            {
                var recipient=targets[i];var c=recipient.GetComponent<UprisingTank>();
                var previous=i>0?targets[i-1].GetComponent<UprisingTank>():null;
                StartCoroutine(ChainHit(targets,i,life,c==null?-1:c.State.LifeSerial,previous==null?-1:previous.State.LifeSerial,Mathf.RoundToInt(damageBase*(i<factors.Length?factors[i]:0f)),radius));
            }
        }
        private IEnumerator ChainHit(List<BattleTank> targets,int index,int life,int recipientLife,int previousLife,int amount,float radius)
        {
            if(index>0)yield return new WaitForSeconds(index*.2f);
            var target=targets[index]; if(!Valid(life) || target==null || target.Dead)yield break;
            var c=target.GetComponent<UprisingTank>(); if(c!=null && c.State.LifeSerial!=recipientLife)yield break;
            if(index>0){var previous=targets[index-1];if(previous==null || (previous.transform.position-target.transform.position).sqrMagnitude>radius*radius)yield break;var previousController=previous.GetComponent<UprisingTank>();if(previousController!=null && previousController.State.LifeSerial!=previousLife)yield break;}
            AbilityDamage(Tank,target,amount,target.transform.position);if(index>0) ChainFx(targets[index-1].transform.position+Vector3.up,target.transform.position+Vector3.up);
        }
    }
}
