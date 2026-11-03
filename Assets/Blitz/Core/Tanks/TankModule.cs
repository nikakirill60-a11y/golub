using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// Ids of the damageable modules the Blitz 4.8.0 damage panel shows, in
    /// the order <c>Gfx2/UI/DamagedModules/DamagedModulesDamageTankIcon</c>
    /// packs their icons. The client also knows <c>EXT_RADIO</c>, but ships no
    /// icon for it and never lists it, so neither does the port.
    /// </summary>
    public static class TankModuleIds
    {
        public const string AmmoBay = "ammo_bay";
        public const string FuelTank = "fuel_tank";
        public const string Gun = "gun";
        public const string Tracks = "tracks";
        public const string TrackLeft = "track_left";
        public const string TrackRight = "track_right";
        public const string TurretRotator = "turret_rotator";
        public const string Engine = "engine";
        public const string SurveyingDevice = "surveying_device";

        /// <summary>Panel order, i.e. the atlas frame order of the client.</summary>
        public static readonly string[] All =
        {
            AmmoBay,
            FuelTank,
            Gun,
            Tracks,
            TurretRotator,
            Engine,
            SurveyingDevice,
        };

        /// <summary>`EXT_*` string key of one module.</summary>
        public static string LabelKey(string id)
        {
            switch (id)
            {
                case AmmoBay: return "EXT_AMMO_BAY";
                case FuelTank: return "EXT_FUEL_TANK";
                case Gun: return "EXT_GUN";
                case Tracks:
                case TrackLeft: return "EXT_LEFT_TRACK";
                case TrackRight: return "EXT_RIGHT_TRACK";
                case TurretRotator: return "EXT_TURRET_ROTATOR";
                case Engine: return "EXT_ENGINE";
                case SurveyingDevice: return "EXT_SURVEYING_DEVICE";
                default: return id;
            }
        }
    }

    /// <summary>
    /// Crew roles of <c>&lt;crew&gt;</c>, in the frame order of
    /// <c>DamagedModulesDamageCrew</c>.
    /// </summary>
    public static class TankCrewRoles
    {
        public const string Loader = "loader";
        public const string Radioman = "radioman";
        public const string Driver = "driver";
        public const string Gunner = "gunner";
        public const string Commander = "commander";

        /// <summary>Panel order, i.e. the atlas frame order of the client.</summary>
        public static readonly string[] All =
        {
            Loader,
            Radioman,
            Driver,
            Gunner,
            Commander,
        };

        /// <summary>`EXT_*` string key of one crew role.</summary>
        public static string LabelKey(string role)
        {
            switch (role)
            {
                case Loader: return "EXT_LOADER";
                case Radioman: return "EXT_RADIOMAN";
                case Driver: return "EXT_DRIVER";
                case Gunner: return "EXT_GUNNER";
                case Commander: return "EXT_COMMANDER";
                default: return role;
            }
        }
    }

    /// <summary>
    /// <c>&lt;maxHealth&gt;/&lt;maxRegenHealth&gt;/&lt;repairCost&gt;</c> of one
    /// damageable module, as the client authors it.
    /// </summary>
    public sealed class ModuleHealthInfo
    {
        public float Health;

        /// <summary>
        /// <c>maxRegenHealth</c>: how much health the automatic repair gives
        /// back. Godot's <c>Module.repair(true)</c> hardcodes half the pool;
        /// the client authors the exact value per module.
        /// </summary>
        public float MaxRegenHealth;

        /// <summary><c>repairCost</c>, unused until the post-battle bill lands.</summary>
        public float RepairCost = 1f;

        public static ModuleHealthInfo FromJson(JsonNode node)
        {
            if (node == null || node.Kind != JsonKind.Object)
            {
                return null;
            }

            return new ModuleHealthInfo
            {
                Health = node["health"].AsFloat(),
                MaxRegenHealth = node["max_regen_health"].AsFloat(),
                RepairCost = node["repair_cost"].AsFloat(1f),
            };
        }
    }

    /// <summary>
    /// Port of Godot <c>BaseTank.Module</c>: one health pool with the three
    /// states the damage panel draws, plus the delayed half repair the source
    /// schedules the moment a module is knocked out.
    /// </summary>
    public class TankModule
    {
        public enum Status
        {
            Normal = 0,
            Damaged = 1,
            Destroyed = 2,
        }

        /// <summary><c>on_damaged</c>: the pool fell below half.</summary>
        public event Action<string> OnDamaged;

        /// <summary><c>on_crit</c>: the pool hit zero.</summary>
        public event Action<string> OnCrit;

        /// <summary><c>on_repaired</c>.</summary>
        public event Action<string> OnRepaired;

        public readonly HealthComponent HealthComponent = new HealthComponent();

        /// <summary><c>id</c>, one of <see cref="TankModuleIds"/>.</summary>
        public readonly string Id;

        /// <summary>
        /// <c>maxRegenHealth</c> of the card. Zero keeps Godot's own half-pool
        /// rule, which is what the Godot cards shipped.
        /// </summary>
        public virtual float RegenHealth { get; set; }

        /// <summary><c>health_prev</c>.</summary>
        private int _healthPrev;

        /// <summary><c>next_repair</c>, negative while nothing is scheduled.</summary>
        private float _nextRepair = -1f;

        /// <summary>How long <c>schedule_repair</c> waits, i.e. the tank's
        /// <c>get_repair_time()</c>.</summary>
        private readonly Func<float> _repairTime;

        /// <summary><c>tank.dead()</c>.</summary>
        private readonly Func<bool> _ownerDead;

        public TankModule(
            string id,
            int health,
            Func<float> repairTime,
            Func<bool> ownerDead) : this(id, health, repairTime, ownerDead, true) { }

        protected TankModule(string id, int health, Func<float> repairTime, Func<bool> ownerDead, bool observeHealth)
        {
            Id = id;
            _repairTime = repairTime;
            _ownerDead = ownerDead;
            HealthComponent.MaxHealth = health;
            _healthPrev = health;
            if (observeHealth) HealthComponent.HealthChanged += OnHealthChanged;
        }

        /// <summary>
        /// <c>get_status</c>. <paramref name="oneTimer"/> is the source's own
        /// edge guard: the damaged state only fires on the transition, so the
        /// panel does not re-announce it on every further hit.
        /// </summary>
        public virtual Status GetStatus(bool oneTimer = false)
        {
            if (HealthComponent.Health <= 0)
            {
                return Status.Destroyed;
            }

            var max = HealthComponent.MaxHealth;
            var ratio = max <= 0 ? 1f : HealthComponent.Health / (float)max;
            if ((_healthPrev >= max * 0.5f || !oneTimer) && ratio < 0.5f)
            {
                return Status.Damaged;
            }

            return Status.Normal;
        }

        /// <summary><c>Module._physics_process</c>: the delayed half repair.</summary>
        public virtual void Tick(float now)
        {
            if (_nextRepair >= 0f && now > _nextRepair)
            {
                Repair(true);
                _nextRepair = -1f;
            }
        }

        /// <summary><c>schedule_repair</c>.</summary>
        public virtual void ScheduleRepair(float now)
        {
            _nextRepair = now + (_repairTime == null ? 0f : _repairTime());
        }

        /// <summary>Whether an automatic repair is pending.</summary>
        public virtual bool RepairPending
        {
            get { return _nextRepair >= 0f; }
        }

        /// <summary><c>receive_damage</c>.</summary>
        public virtual void ReceiveDamage(int dmg, float now)
        {
            _healthPrev = HealthComponent.Health;

            // Faithful to the source: a module that is already out takes the
            // hit as one more repair delay instead of more damage.
            if (GetStatus() == Status.Destroyed)
            {
                ScheduleRepair(now);
            }

            if (HealthComponent.Health <= 0) { return; }

            _now = now;
            HealthComponent.ReceiveDamageBasic(dmg);
        }

        /// <summary>
        /// <c>repair</c>. A half repair is the automatic one the source runs
        /// after <c>get_repair_time()</c>; the client gives back
        /// <c>maxRegenHealth</c> where Godot gave back half the pool.
        /// </summary>
        public virtual bool Repair(bool half = false)
        {
            if (_ownerDead != null && _ownerDead()) { return false; }
            if (HealthComponent.Health == HealthComponent.MaxHealth) { return false; }

            _nextRepair = -1f;
            var max = HealthComponent.MaxHealth;
            var regen = RegenHealth > 0f ? (int)RegenHealth : (int)(max * 0.5f);
            HealthComponent.Health = half ? Math.Min(regen, max) : max;
            RaiseRepaired();
            return true;
        }

        protected void RaiseDamaged() { OnDamaged?.Invoke(Id); }
        protected void RaiseCrit() { OnCrit?.Invoke(Id); }
        protected void RaiseRepaired() { OnRepaired?.Invoke(Id); }

        private float _now;

        /// <summary>
        /// <c>health_component.health_changed</c> of <c>Module._init</c>: the
        /// pool itself announces the two edges and books the repair.
        /// </summary>
        private void OnHealthChanged(int health)
        {
            var dead = _ownerDead != null && _ownerDead();
            switch (GetStatus(true))
            {
                case Status.Damaged:
                    if (!dead) { RaiseDamaged(); }

                    break;
                case Status.Destroyed:
                    if (!dead) { RaiseCrit(); }

                    ScheduleRepair(_now);
                    break;
            }
        }
    }

    /// <summary>
    /// <c>BaseTank.modules</c>: every damageable module of one tank, in panel
    /// order, plus the crew roster the panel draws next to it.
    /// </summary>
    public sealed class TankModuleSet
    {
        private readonly Dictionary<string, TankModule> _modules =
            new Dictionary<string, TankModule>(StringComparer.Ordinal);

        private readonly List<TankModule> _order = new List<TankModule>();

        /// <summary>The modules, in registration order.</summary>
        public IReadOnlyList<TankModule> Modules
        {
            get { return _order; }
        }

        /// <summary><c>register_module</c>.</summary>
        public TankModule Register(TankModule module)
        {
            if (module == null) { return null; }

            _modules[module.Id] = module;
            _order.Add(module);
            if (module is TankTracksModule tracks)
            {
                // Side aliases are not extra panel rows and are ticked/repaired by their facade once.
                _modules[TankModuleIds.TrackLeft] = tracks.Left;
                _modules[TankModuleIds.TrackRight] = tracks.Right;
            }
            return module;
        }

        public TankModule Get(string id)
        {
            TankModule module;
            return id != null && _modules.TryGetValue(id, out module) ? module : null;
        }

        /// <summary><c>get_module_status</c>: an unregistered module reads as intact.</summary>
        public TankModule.Status StatusOf(string id)
        {
            var module = Get(id);
            return module == null ? TankModule.Status.Normal : module.GetStatus();
        }

        /// <summary><c>damage_module</c>.</summary>
        public void Damage(string id, int dmg, float now)
        {
            var module = Get(id);
            if (module == null) { return; }

            module.ReceiveDamage(dmg, now);
        }

        /// <summary><c>process_modules</c>.</summary>
        public void Tick(float now)
        {
            for (var i = 0; i < _order.Count; i++)
            {
                _order[i].Tick(now);
            }
        }

        /// <summary><c>repair_everything</c>.</summary>
        public bool RepairEverything()
        {
            var any = false;
            for (var i = 0; i < _order.Count; i++)
            {
                if (_order[i].Repair()) { any = true; }
            }

            return any;
        }

        public void Clear()
        {
            _modules.Clear();
            _order.Clear();
        }
    }
}
