using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Tanks;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>Runtime of the source-defined roles. Missing server formulae are intentionally
    /// isolated in the compatibility profile; see TASK_PLAN_BIG_BOSS.md. Online, the host (or
    /// the dedicated server) simulates every seat and a client only mirrors it: see
    /// <see cref="CaptureNet"/> and <see cref="ApplyNet"/>.</summary>
    [DisallowMultipleComponent]
    public sealed class BigBossTank : MonoBehaviour
    {
        public BattleTank Tank { get; private set; }
        public BattleSession Session { get; private set; }
        public BigBossState State { get; private set; }
        public int Slot { get; private set; }
        public float ShieldUntil { get; internal set; }
        public float HealedAt { get; private set; } = -100;
        public float PoisonUntil { get; private set; }
        public bool RailgunArmed { get; private set; }
        public bool ShieldArmed { get; private set; }
        public bool Poisoned { get { return Puppet ? _netPoisonedUntil > Time.time : _poisons.Count > 0; } }
        public bool HealingAuraActive { get { return Puppet ? _netAuraUntil > Time.time : _waves > 0; } }
        /// <summary>Seconds a received energy shield was granted for; drives its status tile.</summary>
        public float ShieldDuration { get; internal set; }
        /// <summary>The single button-driven ability of the current role and level
        /// (<see cref="BigBossState.ManualAbility"/>), cached per role and level.</summary>
        public string ManualAbility
        {
            get
            {
                if (State == null) return null;
                if (_manualRole != State.RoleId || _manualLevel != State.Level)
                {
                    _manualRole = State.RoleId; _manualLevel = State.Level; _manualId = State.ManualAbility(State.Level);
                }
                return _manualId;
            }
        }
        /// <summary>The shared fourth consumable slot (cons_jump) and its keyboard binding. An
        /// online client only asks the host, which spends the charge and replicates it.</summary>
        public bool UseRoleAbility()
        {
            var id = ManualAbility;
            if (id == null) return false;
            if (!Puppet) return Activate(id);
            if (Tank != Session.Player || !Session.Started || Tank.Dead || Tank.Blocked || State.Remaining(id, Time.time) > 0) return false;
            BattleNetHooks.RequestUprisingAction(BattleRemoteDriver.BigBossAbilityAction);
            return true;
        }
        /// <summary>Seconds left of a timed effect started by <paramref name="family"/>, 0 when idle.</summary>
        public float RunningSeconds(string family)
        {
            if (State == null || Tank.Dead) return 0;
            switch (family)
            {
                case "poisonedShell": return Mathf.Max(0, PoisonUntil - Time.time);
                case "gravityShot": return Session.BigBoss != null ? Session.BigBoss.TrapSeconds(this) : 0;
                case "healingAura":
                    if (Puppet) return Mathf.Max(0, _netAuraUntil - Time.time);
                    return _waves > 0 ? Mathf.Max(0, _waveAt - Time.time) + (_waves - 1) * Param("healingAura", "healWaveDelay") : 0;
                default: return 0;
            }
        }
        /// <summary>Authored length of the timed effect behind <see cref="RunningSeconds"/>.</summary>
        public float RunningTotal(string family)
        {
            switch (family)
            {
                case "poisonedShell": return Param(family, "duration", 1);
                case "gravityShot": return Param(family, "effectDuration", 1);
                case "healingAura": return Param(family, "healWaveDelay", 1) * Param(family, "healWavesCount", 1);
                default: return 1;
            }
        }
        /// <summary>Longest remaining poison applied to this tank and its full length.</summary>
        public float PoisonedSeconds(out float total)
        {
            if (Puppet) { total = _netPoisonedTotal; return Mathf.Max(0, _netPoisonedUntil - Time.time); }
            var seconds = 0f; total = 1;
            foreach (var p in _poisons)
            {
                var left = Mathf.Max(0, p.Next - Time.time) + Mathf.Max(0, p.Ticks - 1) * p.Delay;
                if (left > seconds) { seconds = left; total = Mathf.Max(p.Delay, p.Delay * p.TotalTicks); }
            }
            return seconds;
        }
        public event Action<string, Vector3> Effect;
        public event Action Respawned;
        private int _maxHp, _level;
        private Transform _returnSpawn;
        private float _power, _forward, _back, _reload, _aim, _reloadFactor = 1;
        private float _aiAt, _shieldDuration, _waveAt, _waveHeal, _waveRange, _selfReduction;
        private int _waves, _manualRole = -1, _manualLevel = -1;
        private string _manualId;
        private Ai.AiTankController _bot;
        private float _autoAt;
        private readonly List<int> _ammo = new List<int>();
        private readonly List<float> _autoTimes = new List<float>();
        private sealed class Poison { public BigBossTank Owner; public int Life, Ticks, TotalTicks; public float Next, Delay, Percent; }
        private readonly List<Poison> _poisons = new List<Poison>(7);
        // Online client: the host's statuses, the role the modifiers were built for and
        // the railgun flag of the next replayed round.
        private float _netPoisonedUntil, _netPoisonedTotal = 1, _netAuraUntil;
        private int _appliedRole = -1, _deadLife = -1, _lastRailgunShot;
        private bool _netSynced, _netRailgun;
        /// <summary>Online client: the host simulates every role, this hull only mirrors it.</summary>
        private static bool Puppet { get { return BattleNetHooks.PuppetWorld; } }
        public static BigBossTank Attach(BattleTank tank, BattleSession session)
        {
            var c = tank.gameObject.AddComponent<BigBossTank>(); c.Tank = tank; c.Session = session; tank.BigBoss = c;
            tank.HudMode = new BigBossHudMode(c);
            c.CaptureBaseline(); c._ammo.AddRange(tank.ShellAmounts);
            foreach (var time in tank.GunInfo.AutoReloadTimes) c._autoTimes.Add(time);
            tank.Health.OnDeath += c.OnDeath; tank.Health.OnDamageInfoTaken += c.OnDamage;
            return c;
        }
        /// <summary>The hull every role scales. Taken again for the first role, after the hangar's
        /// passive consumables (improved fuel, rations) are mounted, so every role keeps them.</summary>
        private void CaptureBaseline()
        {
            _maxHp = Tank.Health.MaxHealth; _power = Tank.Suspension.EnginePower;
            _forward = Tank.Suspension.MaxSpeedForward; _back = Tank.Suspension.MaxSpeedBack;
            _reload = Tank.GunInfo.Reload; _aim = Tank.GunInfo.AimTime;
        }
        public void Assign(BigBossRules rules, int role, int slot)
        {
            if (State == null) CaptureBaseline();
            State = new BigBossState(rules, role); Slot = slot; ApplyModifiers(true);
            // A dedicated server draws nothing: its peers replay the effects.
            if (!Session.Dedicated) BigBossVfx.Attach(this);
        }
        public void Begin() { State.StartLife(Time.time); _aiAt = Time.time + 1 + Slot * .11f; }
        public string FamilyAbility(string family)
        {
            if (State == null) return null;
            foreach (var id in State.Rules.Abilities(State.RoleId, State.Level)) if (State.Rules.Ability(id)["family"].AsString() == family) return id;
            return null;
        }
        public float Param(string family, string key, float fallback = 0)
        {
            var id = FamilyAbility(family); return id == null ? fallback : State.Rules.Number(id, key, fallback);
        }
        private void ApplyModifiers(bool refill)
        {
            _level = State.Level; _appliedRole = State.RoleId;
            var passive = State.Boss ? "bigBoss" : State.RoleId == 4 ? "killerPassive" : State.RoleId == 5 ? "kamikazePassive" : "";
            var previous = _reloadFactor; _reloadFactor = Param(passive, "gunReloadFactor", 1);
            Tank.GunInfo.Reload = _reload * _reloadFactor; Tank.GunInfo.AimTime = _aim * Param(passive, "gunAimingFactor", 1);
            Tank.Gun.ScaleReloadTime(_reloadFactor / previous);
            Tank.GunInfo.AutoReloadTimes.Clear();
            foreach (var time in _autoTimes) Tank.GunInfo.AutoReloadTimes.Add(time * Param(passive, "pumpGunReloadFactor", 1));
            var boosted = false;
            foreach (var item in Tank.ActiveConsumables)
                if (item.Info.Id == ConsumableCatalog.Afterburning && item.IsActive(Time.time)) boosted = true;
            Tank.Suspension.EnginePower = Mathf.RoundToInt(_power * Param(passive, "enginePowerFactor", 1) * (boosted ? 1.25f : 1));
            Tank.Suspension.MaxSpeedForward = Mathf.RoundToInt(_forward * Param(passive, "speedLimitsFactor", 1)) + (boosted ? 5 : 0);
            Tank.Suspension.MaxSpeedBack = Mathf.RoundToInt(_back * Param(passive, "speedLimitsFactor", 1)) + (boosted ? 2 : 0);
            // Profile: the boss pool is a plain multiple of the tank's own pool (user-requested
            // compatibility value); ordinary passives keep their source percentage bonus.
            var max = State.Boss ? Mathf.RoundToInt(_maxHp * State.Rules.P("bossMaxHealthFactor"))
                : Mathf.RoundToInt(_maxHp * (1 + Param(passive, "maxHealthPercentBonus") / 100));
            // Never resize a wreck: the MaxHealth setter refills the pool, which would revive it.
            if (refill || (max != Tank.Health.MaxHealth && !Tank.Dead)) { var hp = Tank.Health.Health; Tank.Health.MaxHealth = max; if (!refill) Tank.Health.Restore(Mathf.Min(hp, max)); }
        }
        public bool Activate(string id)
        {
            if (State == null || Puppet || !Session.Started || Tank.Dead || Tank.Blocked || Time.timeScale <= 0 || State.Rules.Ability(id)["kind"].AsString() != "active") return false;
            var family = State.Rules.Ability(id)["family"].AsString();
            if (family == "railgunCharger" && (RailgunArmed || (Tank.CurrentShell() != null && Tank.CurrentShell().Atgm))) return false;
            if (family == "shieldGiverAbility" && ShieldArmed) return false;
            if (!State.Activate(id, Time.time)) return false;
            switch (family)
            {
                case "gravityBurst": Blast(id, false); break;
                case "kamikazeAbility": Blast(id, true); break;
                case "railgunCharger": RailgunArmed = true; break;
                case "poisonedShell": PoisonUntil = Time.time + State.Rules.Number(id, "duration"); break;
                case "shieldGiverAbility": ShieldArmed = true; _shieldDuration = State.Rules.Number(id, "shieldDuration"); break;
                case "healingAura":
                    _waves = (int)State.Rules.Number(id, "healWavesCount"); _waveAt = Time.time + State.Rules.Number(id,"healWaveDelay");
                    _waveHeal = State.Rules.Number(id, "healFactorPercent") / 100; _waveRange = State.Rules.Number(id, "range"); _selfReduction = State.Rules.Number(id, "selfHealReducePercent") / 100;
                    break;
                case "gravityShot":
                    var point = Tank.GunTracePoint; var delta = point - Tank.GunShootPoint();
                    if (!Finite(point) || delta.sqrMagnitude < .01f) point = Tank.GunShootPoint() + transform.forward * State.Rules.P("gravityShotRange");
                    else point = Tank.GunShootPoint() + Vector3.ClampMagnitude(delta, State.Rules.P("gravityShotRange"));
                    Session.BigBoss.AddTrap(this, id, point); break;
            }
            Fx(family, transform.position); return true;
        }
        /// <summary>A one-shot effect of this seat; the host also hands it to the network.</summary>
        private void Fx(string id, Vector3 point)
        {
            Effect?.Invoke(id, point);
            if (!Puppet) BattleNetHooks.RaiseBigBossEffect(this, id, point);
        }
        /// <summary>Effect ids the host replays on its clients. The level-up and the railgun
        /// discharge are derived there from the mirrored state and the replayed round.</summary>
        public static bool IsNetEffect(string id)
        {
            switch (id)
            {
                case "gravityBurst": case "kamikazeAbility": case "railgunCharger": case "poisonedShell":
                case "shieldGiverAbility": case "healingAura": case "gravityShot":
                case "heal": case "permanentDamageShield": case "shieldGiven":
                    return true;
                default: return false;
            }
        }
        /// <summary>Client: replay one host effect on this seat (sound, VFX, the heal marker).</summary>
        public void ReplayNetEffect(string id, Vector3 point)
        {
            if (State == null || !IsNetEffect(id)) return;
            if (id == "heal") HealedAt = Time.time;
            Effect?.Invoke(id, point);
        }
        private static bool Finite(Vector3 p) { return float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z); }
        private void Blast(string id, bool bomber)
        {
            var radius = State.Rules.Number(id, bomber ? "range" : "burstRange");
            foreach (var target in Session.Roster.GetEnemyTanks(Tank.Team))
            {
                if (target == null || target.Dead || (target.transform.position - transform.position).sqrMagnitude > radius * radius) continue;
                var damage = bomber ? Tank.Health.MaxHealth * State.Rules.Number(id, "maxHealthDamageFactor") : target.Health.MaxHealth * State.Rules.Number(id, "burstDamagePercent") / 100;
                if (!bomber)
                {
                    var body = target.GetComponent<Rigidbody>(); var dir = target.transform.position - transform.position; dir.y = 0;
                    if (body != null && !body.isKinematic) body.AddForce((dir.normalized + Vector3.up * State.Rules.P("gravityBurstLift")) * State.Rules.Number(id, "burstForce") * State.Rules.P("gravityBurstVelocityPerForce"), ForceMode.VelocityChange);
                }
                AbilityDamage(target, Mathf.RoundToInt(damage));
            }
        }
        private void AbilityDamage(BattleTank target, int amount, bool poison = false)
        {
            target.ReceiveDamage(new DamageInfo { AttackerTank = Tank, Attacker = Tank, Dmg = amount, DmgBase = amount, DmgType = DamageType.Ability, BigBossPoisonTick = poison, SuppressFeedback = poison, Pos = new Vec3(target.transform.position.x, target.transform.position.y, target.transform.position.z) });
        }
        private void OnDeath()
        {
            if (State == null || !Session.Started) return;
            if (Puppet)
            {
                // The host decides the death, the next role and the bomber's blast; the
                // client only drops what the wreck no longer has.
                EndLifeConsumables();
                _poisons.Clear(); ClearActives(); _netPoisonedUntil = _netAuraUntil = 0; _deadLife = State.LifeSerial;
                return;
            }
            var bomber = FamilyAbility("kamikazeAbility");
            var blast = bomber != null && State.Remaining(bomber, Time.time) <= 0;
            if (blast) State.Activate(bomber, Time.time);
            EndLifeConsumables();
            // Draw the next ordinary role now so the respawn screen can present it.
            State.Die(Time.time, Session.BigBoss != null ? Session.BigBoss.Random : null); _poisons.Clear(); ClearActives();
            if (blast) { Blast(bomber, true); Fx("kamikazeAbility", transform.position); }
        }
        private void ClearActives() { RailgunArmed = ShieldArmed = false; ShieldUntil = PoisonUntil = 0; _waves = 0; }
        /// <summary>A wreck drops its running items and the next life starts with them ready. A
        /// passive one (improved fuel, a ration) is part of the hull for the whole battle: stopping
        /// it would let a press after the respawn apply it a second time.</summary>
        private void EndLifeConsumables()
        {
            foreach (var item in Tank.ActiveConsumables) if (!item.Passive) { item.Stop?.Invoke(); item.Reset(); }
        }
        private void OnDamage(DamageInfo info)
        {
            if (State == null || Puppet || info.Dmg <= 0) return;
            var attacker = info.AttackerTank as BattleTank; var c = attacker == null ? null : attacker.BigBoss;
            if (c == null || attacker.Team == Tank.Team) return;
            var actual = Mathf.Min(info.Dmg, Tank.Health.Health);
            c.State.Reward(actual, 0, info.Dmg >= Tank.Health.Health);
            if (info.BigBossPoisonPercent > 0 && info.BigBossLifeSerial == c.State.LifeSerial && !c.Tank.Dead)
                AddPoison(c, info.BigBossPoisonPercent, info.BigBossPoisonTicks, info.BigBossPoisonDelay);
        }
        private void AddPoison(BigBossTank caster, float percent, int ticks, float delay)
        {
            if (ticks <= 0 || delay <= 0) return;
            Poison effect = null;
            foreach (var p in _poisons) if (p.Owner == caster) { effect = p; break; }
            if (effect == null) { effect = new Poison(); _poisons.Add(effect); }
            effect.Owner = caster; effect.Life = State.LifeSerial; effect.Percent = percent; effect.Ticks = effect.TotalTicks = ticks; effect.Delay = delay; effect.Next = Time.time + delay;
        }
        public int Heal(BigBossTank source, int amount, bool cleanse)
        {
            if (Puppet || Tank.Dead || amount <= 0) return 0;
            var actual = Mathf.Min(amount, Tank.Health.MaxHealth - Tank.Health.Health);
            if (cleanse) _poisons.Clear();
            if (actual > 0) { Tank.Health.Restore(Tank.Health.Health + actual); if (source != null && source != this) { source.State.Reward(0, actual); var stats=Session.StatsFor(source.Tank); if(stats!=null)stats.Healing+=actual; } HealedAt = Time.time; Fx("heal", transform.position); }
            return actual;
        }
        public static void Mediate(BattleTank victim, DamageInfo info)
        {
            var c = victim.BigBoss; if (c == null || c.State == null || info.Dmg <= 0) return;
            var attacker = info.AttackerTank as BattleTank;
            if (attacker != null && attacker != victim && attacker.Team == victim.Team) { info.Dmg = 0; return; }
            var amount = info.Dmg * info.BigBossDamageFactor;
            if (c.ShieldUntil > Time.time && attacker != null && attacker.Team != victim.Team && info.DmgType != DamageType.Fire && !info.BigBossPoisonTick)
            { c.ShieldUntil = 0; info.Dmg = 0; return; }
            if (info.DmgType != DamageType.Fire && !info.BigBossPoisonTick)
            {
                var reduction = c.Param("permanentDamageShield", "damageReductionPercent");
                amount *= 1 - reduction / 100;
                // The graviton's passive field absorbs part of an enemy hit; BigBossAudio plays its hum.
                if (reduction > 0 && attacker != null && attacker.Team != victim.Team) c.Fx("permanentDamageShield", victim.transform.position);
            }
            if (c.State.Boss && (info.DmgType == DamageType.Collision || info.DmgType == DamageType.Environment)) amount = Mathf.Min(amount, victim.Health.MaxHealth * c.Param("bigBoss", "maxStaticsDamagePercent") / 100);
            info.Dmg = Mathf.Max(0, Mathf.RoundToInt(amount));
        }
        /// <summary>Snapshot launch parameters: a role change cannot alter an already flying shell.</summary>
        public BigBossShot Launch(ShellInfo shell) { return Launch(shell, 0); }
        /// <summary><see cref="Launch(ShellInfo)"/> for the round <paramref name="shotId"/>. On an
        /// online client the round is a replay: the host's railgun flag decides its flight.</summary>
        public BigBossShot Launch(ShellInfo shell, int shotId)
        {
            var shot = new BigBossShot(); if (State == null) return shot;
            shot.Life = State.LifeSerial; shot.Damage = Param(State.Boss ? "bigBoss" : "killerPassive", "commonDamageFactor", 1);
            shot.Heal = Param("healingShot", "healFactor"); shot.HealRandom = Param("healingShot", "healRandomFactor");
            if (Puppet)
            {
                if (_netRailgun) { shot.Railgun = true; shot.Speed = Param("railgunCharger", "projectileSpeed"); Effect?.Invoke("railgunShot", Tank.GunShootPoint()); }
                _netRailgun = false;
                return shot;
            }
            if (RailgunArmed && shell != null && !shell.Atgm)
            {
                shot.Damage *= Param("railgunCharger", "damageFactor", 1); shot.Speed = Param("railgunCharger", "projectileSpeed"); shot.Railgun = true; RailgunArmed = false;
                // The charge recharges from the shot that spent it, not from the moment it armed.
                var railgun = FamilyAbility("railgunCharger"); if (railgun != null) State.Restart(railgun, Time.time);
                _lastRailgunShot = shotId;
                Effect?.Invoke("railgunShot", Tank.GunShootPoint());
            }
            if (PoisonUntil > Time.time) { shot.Poison = Param("poisonedShell", "maxHealthDamagePercent"); shot.Ticks = (int)Param("poisonedShell", "poisonTicks"); shot.Delay = Param("poisonedShell", "poisonTickDelay"); }
            // The armed shield rides every shell until one of them lands on an ally: a miss or
            // an enemy hit keeps the charge (see GrantShield).
            if (ShieldArmed) shot.Shield = _shieldDuration;
            return shot;
        }
        /// <summary>Host: whether the round <paramref name="shotId"/> spent the railgun charge.</summary>
        public bool IsRailgunShot(int shotId) { return shotId != 0 && shotId == _lastRailgunShot; }
        /// <summary>Client: the next replayed round of this hull carries the host's railgun charge.</summary>
        public void PrimeNetShot(bool railgun) { _netRailgun = railgun; }
        /// <summary>Hands the armed energy shield to <paramref name="receiver"/> on a friendly hit.
        /// One charge shields exactly one ally; its cooldown restarts from the hit. Shells of an
        /// earlier life cannot spend the charge of the current one.</summary>
        public bool GrantShield(BigBossTank receiver, float duration, int life)
        {
            if (Puppet || !ShieldArmed || State == null || State.LifeSerial != life || Tank.Dead || receiver == null || receiver == this || receiver.Tank.Dead || duration <= 0) return false;
            ShieldArmed = false;
            var id = FamilyAbility("shieldGiverAbility"); if (id != null) State.Restart(id, Time.time);
            receiver.ShieldUntil = Time.time + duration; receiver.ShieldDuration = duration;
            // BigBossAudio confirms the hand-over to the giver; the receiver holds its own loop.
            Fx("shieldGiven", receiver.transform.position);
            return true;
        }
        /// <summary>Host: everything a client needs to draw and drive this seat's role.</summary>
        public BigBossSnapshot CaptureNet()
        {
            var now = Time.time; var s = State.Capture(now);
            if (RailgunArmed) s.Flags |= BigBossSnapshot.FlagRailgun;
            if (ShieldArmed) s.Flags |= BigBossSnapshot.FlagShield;
            s.ShieldIn = Mathf.Max(0, ShieldUntil - now); s.ShieldDuration = ShieldDuration;
            s.PoisonCastIn = Mathf.Max(0, PoisonUntil - now);
            s.PoisonedIn = PoisonedSeconds(out var total); s.PoisonedTotal = total;
            s.AuraIn = RunningSeconds("healingAura");
            return s;
        }
        /// <summary>Client puppet: mirror the host's role, progression, cooldowns and statuses,
        /// and replay its respawn locally. Pose and health still arrive through
        /// <see cref="BattleTank.ApplyNetState"/>, which must run after this.</summary>
        public void ApplyNet(BigBossSnapshot s)
        {
            if (State == null || Tank == null || !Tank.NetPuppet) return;
            var now = Time.time;
            if (!State.Apply(s, now)) return;
            RailgunArmed = (s.Flags & BigBossSnapshot.FlagRailgun) != 0; ShieldArmed = (s.Flags & BigBossSnapshot.FlagShield) != 0;
            ShieldUntil = s.ShieldIn > 0 ? now + Mathf.Min(s.ShieldIn, 3600) : 0; ShieldDuration = Mathf.Clamp(s.ShieldDuration, 0, 3600);
            PoisonUntil = s.PoisonCastIn > 0 ? now + Mathf.Min(s.PoisonCastIn, 3600) : 0;
            _netPoisonedUntil = s.PoisonedIn > 0 ? now + Mathf.Min(s.PoisonedIn, 3600) : 0;
            _netPoisonedTotal = s.PoisonedTotal > .01f && s.PoisonedTotal < 3600 ? s.PoisonedTotal : 1;
            _netAuraUntil = s.AuraIn > 0 ? now + Mathf.Min(s.AuraIn, 3600) : 0;
            var synced = _netSynced; _netSynced = true;
            if (Tank.Dead)
            {
                // A seat that is neither waiting nor out of lives runs a new life on the host.
                // No local spawn: the host's pose lands with the next state.
                if (Session.Started && !State.Respawning && !State.Exhausted && State.LifeSerial != _deadLife) { _returnSpawn = null; Restore(); }
                return;
            }
            var levelUp = synced && _appliedRole == State.RoleId && State.Level > _level;
            // Keeps the current health: the host's pool lands right after, as a restore or a hit.
            if (_appliedRole != State.RoleId || _level != State.Level) ApplyModifiers(false);
            if (levelUp) Effect?.Invoke("levelUp", transform.position);
        }
        /// <summary>Smoke diagnostics of this seat (English, logs only).</summary>
        public string Describe()
        {
            if (State == null) return "-";
            return "role=" + State.RoleId + " lv=" + State.Level + " pts=" + State.Points.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                + " lives=" + State.Lives + " life=" + State.LifeSerial + (State.Respawning ? " respawning" : "") + (State.Exhausted ? " out" : "");
        }
        private void Update()
        {
            if (State == null || !Session.Started || Time.timeScale <= 0) return;
            if (Puppet)
            {
                // Spotting runs on every peer: the boss is always on the map.
                if (!Tank.Dead && State.Boss) Tank.SetSpotted(true);
                return;
            }
            if (Tank.Dead)
            {
                if (State.Respawning && Time.time >= State.RespawnAt)
                {
                    _returnSpawn = FindSpawn();
                    if (_returnSpawn != null && State.Return(Time.time, Session.BigBoss.Random)) Restore();
                }
                return;
            }
            if (_level != State.Level) { ApplyModifiers(false); Effect?.Invoke("levelUp", transform.position); }
            if (State.Boss) Tank.SetSpotted(true);
            for (var i = _poisons.Count - 1; i >= 0; i--)
            {
                var p = _poisons[i];
                if (p.Life != State.LifeSerial || p.Owner == null) { _poisons.RemoveAt(i); continue; }
                if (Time.time < p.Next) continue;
                p.Next += p.Delay; p.Ticks--; p.Owner.AbilityDamage(Tank, Mathf.RoundToInt(Tank.Health.MaxHealth * p.Percent / 100), true);
                if (Tank.Dead) return;
                if (p.Ticks <= 0) _poisons.RemoveAt(i);
            }
            if (_waves > 0 && Time.time >= _waveAt)
            {
                _waves--; _waveAt += Param("healingAura", "healWaveDelay");
                foreach (var target in Session.Roster.GetAllyTanks(Tank.Team)) if (target != null && target.BigBoss != null && !target.Dead && (target.transform.position - transform.position).sqrMagnitude <= _waveRange * _waveRange)
                    target.BigBoss.Heal(this, Mathf.RoundToInt(Tank.Health.MaxHealth * _waveHeal * (target == Tank ? 1 - _selfReduction : 1)), false);
            }
            if (Time.time >= _autoAt) { _autoAt = Time.time + .2f; AutoAbilities(); }
            if (Tank != Session.Player && Time.time >= _aiAt) { _aiAt = Time.time + BotAbilityInterval; BotAbilities(); }
        }
        /// <summary>Self-activating families arm themselves as soon as their cooldown ends: the
        /// boss's charger and the healer's energy shield (spent by the next friendly hit). The
        /// aura branch serves profiles that still mark it self-activating.</summary>
        private void AutoAbilities()
        {
            var ids = State.Rules.Abilities(State.RoleId, State.Level);
            for (var i = 0; i < ids.Length; i++)
            {
                var id = ids[i];
                if (!State.AutoActivated(id) || State.Remaining(id, Time.time) > 0) continue;
                var family = State.Rules.Ability(id)["family"].AsString();
                if (family == "railgunCharger") { if (!RailgunArmed) Activate(id); }
                else if (family == "shieldGiverAbility") { if (!ShieldArmed) Activate(id); }
                else if (family == "healingAura" && _waves == 0 && WoundedAllyWithin(State.Rules.Number(id, "range"))) Activate(id);
            }
        }
        /// <summary>How often a bot crew reconsiders its role's button ability.</summary>
        public const float BotAbilityInterval = .5f;
        /// <summary>Range of the poisoned shell's bot trigger: the poison is spent on shots that land.</summary>
        public const float BotPoisonRange = 300;
        /// <summary>A bot crew spends its role's button ability (<see cref="ManualAbility"/>) where
        /// it pays: the aura while allies around (or the healer itself) are hurt, the boss's
        /// repulsion and the bomber's blast against spotted enemies in reach, the poison and the
        /// gravity trap on the enemy under the gun. Self-activating families stay with
        /// <see cref="AutoAbilities"/>.</summary>
        private void BotAbilities()
        {
            var ai = Bot;
            if (ai == null || !ai.Active || Tank.Dead || Tank.Blocked) return;
            var id = ManualAbility;
            if (id == null || State.Remaining(id, Time.time) > 0) return;
            if (BotWantsAbility(State.Rules.Ability(id)["family"].AsString(), id)) Activate(id);
        }
        /// <summary>Whether a bot should fire <paramref name="id"/> of <paramref name="family"/> now.</summary>
        public bool BotWantsAbility(string family, string id)
        {
            switch (family)
            {
                case "healingAura":
                    return _waves == 0 && (WoundedAllyWithin(State.Rules.Number(id, "range"), .85f, false) || Tank.Health.Health < Tank.Health.MaxHealth * .7f);
                case "gravityBurst": return SpottedEnemyWithin(State.Rules.Number(id, "burstRange"));
                case "kamikazeAbility": return SpottedEnemyWithin(State.Rules.Number(id, "range") * .9f);
                case "poisonedShell":
                    return EnemyUnderGun(BotPoisonRange) && Tank.Gun != null && (Tank.Gun.CanShoot() || Tank.Gun.ReloadRemaining() < 2);
                case "gravityShot": return EnemyUnderGun(State.Rules.P("gravityShotRange"));
                default: return false;
            }
        }
        private Ai.AiTankController Bot
        {
            get { if (_bot == null) _bot = GetComponent<Ai.AiTankController>(); return _bot; }
        }
        private bool SpottedEnemyWithin(float range)
        {
            foreach (var target in Session.Roster.GetEnemyTanks(Tank.Team))
                if (target != null && !target.Dead && target.Spotted && (target.transform.position - transform.position).sqrMagnitude <= range * range) return true;
            return false;
        }
        private bool EnemyUnderGun(float range)
        {
            var target = Tank.GunTraceTank;
            return target != null && !target.Dead && target.Team != Tank.Team && (target.transform.position - transform.position).sqrMagnitude <= range * range;
        }
        /// <summary>A living Big Boss tank of this team within <paramref name="range"/> below
        /// <paramref name="below"/> of its health pool; <paramref name="self"/> counts this hull.</summary>
        private bool WoundedAllyWithin(float range, float below = 1, bool self = true)
        {
            foreach (var ally in Session.Roster.GetAllyTanks(Tank.Team))
                if (ally != null && !ally.Dead && ally.BigBoss != null && (self || ally != Tank) && ally.Health.Health < ally.Health.MaxHealth * below && (ally.transform.position - transform.position).sqrMagnitude <= range * range) return true;
            return false;
        }
        private Transform FindSpawn()
        {
            Transform best = null; var farthest = -1f;
            foreach (var spawn in Tank.Team == 0 ? Session.World.PlayerSpawns : Session.World.BotSpawns)
            {
                var distance = float.PositiveInfinity; var occupied = false;
                foreach (var other in Session.BattleTanks()) if (other != null && !other.Dead)
                { var d = (other.transform.position - spawn.position).sqrMagnitude; if (d < 100) occupied = true; if (other.Team != Tank.Team) distance = Mathf.Min(distance, d); }
                if (!occupied && distance > farthest) { best = spawn; farthest = distance; }
            }
            return best;
        }
        /// <summary>A new life at the return spawn; an online client has none and takes the
        /// host's pose from the next state.</summary>
        private void Restore()
        {
            ClearActives(); _poisons.Clear(); _netPoisonedUntil = _netAuraUntil = 0; ApplyModifiers(true); Tank.RestoreLife(_returnSpawn); Tank.SetShellAmounts(_ammo);
            Session.Roster.NotifyRespawn(Tank); Session.NotifyUprisingRespawn(Tank);
            var enemy = Tank.Team != Session.Roster.PlayerTeam;
            Session.Hud.SetTeamTankAlive(enemy, (enemy ? Session.Enemies : Session.Allies).IndexOf(Tank), true);
            for (var i = 0; i < Session.Hud.TankPlateCount; i++) if (Session.Hud.GetTankPlate(i).Tank == Tank) Session.Hud.GetTankPlate(i).RefreshLife();
            if (Tank == Session.Player) { Session.View.Tank = Tank; Session.SetCombatHudVisible(true); }
            // The AI went idle with the wreck: a returning bot starts its brain over from the new spawn.
            else if (!Puppet && Bot != null) Bot.BeginNewLife();
            Respawned?.Invoke();
        }
        private void OnDestroy()
        {
            if (Tank == null) return;
            Tank.Health.OnDeath -= OnDeath; Tank.Health.OnDamageInfoTaken -= OnDamage;
            if (Tank.BigBoss == this) Tank.BigBoss = null;
            if (Tank.HudMode is BigBossHudMode mode && mode.Owner == this) Tank.HudMode = null;
            Tank.Suspension.EnginePower = Mathf.RoundToInt(_power); Tank.Suspension.MaxSpeedForward = Mathf.RoundToInt(_forward); Tank.Suspension.MaxSpeedBack = Mathf.RoundToInt(_back);
            Tank.GunInfo.Reload = _reload; Tank.GunInfo.AimTime = _aim;
            Tank.GunInfo.AutoReloadTimes.Clear();
            foreach (var time in _autoTimes) Tank.GunInfo.AutoReloadTimes.Add(time);
        }
    }
    public struct BigBossShot
    {
        public float Damage, Speed, Heal, HealRandom, Shield, Poison, Delay;
        public int Life, Ticks;
        public bool Railgun;
        public bool Friendly(BattleTank owner, BattleTank target, ShellInfo shell)
        {
            if (owner == null || target == null || owner.Team != target.Team || owner == target) return false;
            var giver = owner.BigBoss; var receiver = target.BigBoss;
            if (giver == null) return false;
            if (target.Dead || receiver == null) return true;
            // A healing shell always mends: it needs no charge and has no cooldown of its own.
            if (shell != null && Heal > 0)
                receiver.Heal(giver, Mathf.RoundToInt(shell.Damage * Heal * (1 + UnityEngine.Random.Range(-HealRandom, HealRandom))), true);
            if (Shield > 0) giver.GrantShield(receiver, Shield, Life);
            return true;
        }
    }
}
