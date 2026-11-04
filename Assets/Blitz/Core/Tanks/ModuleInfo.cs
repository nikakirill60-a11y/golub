using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>Fields shared by every module (Godot <c>ModuleInfo</c>).</summary>
    public abstract class ModuleInfo
    {
        public string Name = string.Empty;
        public string DisplayName = string.Empty;
        public int Level = 1;
        public int CostXp;
        public float Health;
        public float Weight;

        /// <summary>
        /// <c>maxRegenHealth</c>: what the automatic repair gives back. Zero
        /// means the client authored none for this module kind.
        /// </summary>
        public float MaxRegenHealth;

        /// <summary><c>repairCost</c>.</summary>
        public float RepairCost = 1f;

        /// <summary>
        /// Client research edges of this module: "turret:X", "gun:X",
        /// "engine:X", "chassis:X" or "vehicle:X" -&gt; XP cost.
        /// </summary>
        public readonly Dictionary<string, int> Unlocks =
            new Dictionary<string, int>(StringComparer.Ordinal);

        protected void ReadShared(JsonNode node)
        {
            Name = node["name"].AsString();
            DisplayName = node["display_name"].AsString();
            Level = node["level"].AsInt(1);
            CostXp = node["cost_xp"].AsInt();
            Health = node["health"].AsFloat();
            Weight = node["weight"].AsFloat();
            MaxRegenHealth = node["max_regen_health"].AsFloat();
            RepairCost = node["repair_cost"].AsFloat(1f);
            TurretGunSlot.ReadUnlocks(node["unlocks"], Unlocks);
        }
    }

    /// <summary>Presentation only; never changes guided flight or warhead physics.</summary>
    public enum AtgmVisualProfile { LegacyTrail, Blitz1120Marker }

    /// <summary>Port of Godot <c>ShellInfo</c>.</summary>
    public sealed class ShellInfo
    {
        public string Name = string.Empty;
        public float Damage;
        /// <summary>Original damage/devices, not calibre or hull alpha.</summary>
        public float DeviceDamage;
        public float Penetration;
        public int Caliber;
        public float Speed;
        public float MaxDistance = 720f;
        public ShellType Type = ShellType.Ap;
        public int Cost;
        public bool Premium;

        /// <summary>
        /// Guided rocket of the XM81 launcher: the shot keeps flying toward
        /// the crosshair instead of following a ballistic arc.
        /// </summary>
        public bool Atgm;

        /// <summary>Explicit source profile; old cards keep the legacy XM81 tracer.</summary>
        public AtgmVisualProfile AtgmVisual;

        public bool UsesModernAtgmVisual
        { get { return Atgm && AtgmVisual == AtgmVisualProfile.Blitz1120Marker; } }

        /// <summary>
        /// Shallow copy for a battle-local override (the Sandbox penetration
        /// multiplier). Catalogue shells are shared by every tank, so a battle
        /// never edits them in place.
        /// </summary>
        public ShellInfo Clone()
        {
            return (ShellInfo)MemberwiseClone();
        }

        /// <summary>Radians per second the rocket may turn while steered.</summary>
        public float TurnSpeed;

        /// <summary>
        /// <c>gravity_mul</c>: the client's gravity scale, not an absolute
        /// acceleration. Ordinary shells omit the key and fall at the shared
        /// <see cref="AimMath.DefaultGravity"/>; the XM81 rockets author 0.01,
        /// and <c>projectile.gd</c> zeroes it outright once the shell homes.
        /// </summary>
        public float GravityMultiplier = 1f;

        /// <summary>Client ricochet angle; zero keeps the per-type default.</summary>
        public float RicochetAngle;

        /// <summary>
        /// <c>explosionRadius</c>: how far the blast of an explosive shell
        /// reaches, in metres. Zero means the card shipped none and the
        /// calibre rule of <see cref="ArmorMath.ExplosionRadiusFor"/> decides.
        /// </summary>
        public float ExplosionRadius;

        public static ShellInfo FromJson(JsonNode node)
        {
            var shell = new ShellInfo();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return shell;
            }

            shell.Name = node["name"].AsString();
            shell.Damage = node["dmg"].AsFloat();
            shell.DeviceDamage = node["dmg_devices"].AsFloat();
            shell.Penetration = node["pen"].AsFloat();
            shell.Caliber = node["caliber"].AsInt();
            shell.Speed = node["speed"].AsFloat();
            shell.MaxDistance = node["max_dist"].AsFloat(shell.MaxDistance);
            shell.Type = (ShellType)node["type"].AsInt();
            shell.Cost = node["cost"].AsInt();
            shell.Premium = node["premium"].AsBool();
            shell.Atgm = node["atgm"].AsBool();
            if (node["atgm_visual"].AsString() == "blitz1120_marker")
            { shell.AtgmVisual = AtgmVisualProfile.Blitz1120Marker; }
            shell.TurnSpeed = node["turn_speed"].AsFloat();
            shell.GravityMultiplier =
                node["gravity"].AsFloat(shell.GravityMultiplier);
            shell.RicochetAngle = node["ricochet_angle"].AsFloat();
            shell.ExplosionRadius = node["explosion_radius"].AsFloat();
            return shell;
        }
    }

    /// <summary>Port of Godot <c>BurstInfo</c> (clip and burst timings).</summary>
    public sealed class BurstInfo
    {
        public readonly List<int> Bursts = new List<int>();
        // Obscured (anti memory-editing): both feed the magazine reload clock.
        private Security.ObscuredFloat _intraClip, _intraBurst;
        public float IntraClip { get { return _intraClip.Get("gun.intraclip"); } set { _intraClip = value; } }
        public float IntraBurst { get { return _intraBurst.Get("gun.intraburst"); } set { _intraBurst = value; } }

        public bool HasBursts
        {
            get { return Bursts.Count > 0; }
        }

        public static BurstInfo FromJson(JsonNode node)
        {
            var burst = new BurstInfo();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return burst;
            }

            JsonMap.ReadInts(node["bursts"], burst.Bursts);
            burst.IntraClip = node["intraclip"].AsFloat();
            burst.IntraBurst = node["intraburst"].AsFloat();
            return burst;
        }
    }

    /// <summary>Port of Godot <c>ModuleGun</c>.</summary>
    public sealed class GunInfo : ModuleInfo
    {
        // Obscured (anti memory-editing): the reload time is the first value a
        // GameGuardian / Cheat Engine user looks for.
        private Security.ObscuredFloat _reload;
        public float Reload { get { return _reload.Get("gun.reload"); } set { _reload = value; } }
        public float Dispersion;
        public float AimTime = 2f;
        public float DeviationUp;
        public float DeviationDown;

        // ModuleGun exports deviation_r = Vector2(180, 180) and MS-1 cards
        // omit the key, so the default has to keep the turret traversable.
        public float DeviationLeft = 180f;
        public float DeviationRight = 180f;
        public int Caliber;
        public int ShotSound;
        public int FireMode;
        public float ImpulseMultiplier = 1f;
        public float RotationSpeed;
        public int MaxAmmo;
        // ModuleGun.disp_factors is (turretRotation, afterShot, whileGunDamaged):
        // that is the order the Blitz importer fills it in, the order
        // disp_manager reads the traverse factor out of and the order
        // _launch_single_shell reads the after-shot bloom out of.
        public float DispersionTurret = 0.1f;
        public float DispersionShot = 3f;
        public float DispersionDamaged = 2f;
        public BurstInfo Burst = new BurstInfo();
        /// <summary>11.20 pumpGunReloadTimes, indexed by rounds already loaded.</summary>
        public readonly Security.ObscuredFloatList AutoReloadTimes = new Security.ObscuredFloatList("gun.autoreload");
        public bool AutoReload { get { return AutoReloadTimes.Count > 0; } }
        public readonly List<ShellInfo> Shells = new List<ShellInfo>();

        /// <summary>
        /// The same barrel as the given turret mounts it. The client authors
        /// <c>reloadTime</c> and <c>maxAmmo</c> inside every turret, so this is
        /// the only place where the two values may differ from the card.
        /// </summary>
        public GunInfo WithTurretSlot(TurretGunSlot slot)
        {
            if (slot == null)
            {
                return this;
            }

            var copy = (GunInfo)MemberwiseClone();
            if (slot.Reload > 0f)
            {
                copy.Reload = slot.Reload;
            }

            if (slot.MaxAmmo > 0)
            {
                copy.MaxAmmo = slot.MaxAmmo;
            }

            return copy;
        }

        public static GunInfo FromJson(JsonNode node)
        {
            var gun = new GunInfo();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return gun;
            }

            gun.ReadShared(node);
            gun.Reload = node["reload"].AsFloat();
            gun.Dispersion = node["disp"].AsFloat();
            gun.AimTime = node["aim_time"].AsFloat(gun.AimTime);
            gun.DeviationUp = node["deviation_up"]["x"].AsFloat();
            gun.DeviationDown = node["deviation_up"]["y"].AsFloat();
            // rotate_turret clamps to [-|deviation_r.y|, +|deviation_r.x|] and a
            // positive turret yaw turns left in Godot, so .x is the left half.
            gun.DeviationLeft = node["deviation_r"]["x"].AsFloat(gun.DeviationLeft);
            gun.DeviationRight = node["deviation_r"]["y"].AsFloat(gun.DeviationRight);
            gun.Caliber = node["caliber"].AsInt();
            gun.ShotSound = node["shot_sound"].AsInt();
            gun.FireMode = node["fire_mode"].AsInt();
            gun.ImpulseMultiplier = node["impulse_mul"].AsFloat(gun.ImpulseMultiplier);
            gun.RotationSpeed = node["rot_speed"].AsFloat();
            gun.MaxAmmo = node["max_ammo"].AsInt();
            gun.DispersionTurret =
                node["disp_factors"]["x"].AsFloat(gun.DispersionTurret);
            gun.DispersionShot = node["disp_factors"]["y"].AsFloat(gun.DispersionShot);
            gun.DispersionDamaged =
                node["disp_factors"]["z"].AsFloat(gun.DispersionDamaged);
            gun.Burst = BurstInfo.FromJson(node["burst_info"]);
            var autoTimes = node["autoreload_times"];

            // A card that describes a magazine but forgets fire_mode still
            // means a magazine gun: per-shell timers make it an autoloader,
            // a bare burst table a burst gun. Stock data always names the
            // mode, so this only rescues hand-written mod cards.
            if (gun.FireMode == 0
                && (gun.Burst.Bursts.Count > 0 || autoTimes.Kind == JsonKind.Array && autoTimes.Count > 0))
            {
                gun.FireMode = autoTimes.Kind == JsonKind.Array && autoTimes.Count > 0 ? 2 : 1;
            }
            if (autoTimes.Kind == JsonKind.Array)
            {
                for (var i = 0; i < autoTimes.Count; i++)
                {
                    var seconds = autoTimes[i].AsFloat();
                    if (seconds <= 0f || float.IsNaN(seconds) || float.IsInfinity(seconds))
                    { throw new System.FormatException("Invalid autoreload timer"); }
                    gun.AutoReloadTimes.Add(seconds);
                }
            }

            var shells = node["shells"];
            if (shells.Kind == JsonKind.Array)
            {
                for (var i = 0; i < shells.Count; i++)
                {
                    gun.Shells.Add(ShellInfo.FromJson(shells[i]));
                }
            }

            return gun;
        }
    }

    /// <summary>
    /// One gun slot of a turret, as the Blitz client authors it: the client
    /// keeps the gun list inside <c>turrets0/&lt;turret&gt;/guns</c>, and the same
    /// gun reloads slower and holds fewer rounds in the stock turret.
    /// </summary>
    public sealed class TurretGunSlot
    {
        public string Name = string.Empty;
        private Security.ObscuredFloat _reload;
        public float Reload { get { return _reload.Get("gun.slot_reload"); } set { _reload = value; } }
        public int MaxAmmo;

        /// <summary>Blitz <c>blitz/blitzPartName</c>, e.g. <c>gun_09</c>.</summary>
        public string Mesh = string.Empty;

        /// <summary>Research edges of the client: "gun:X" / "vehicle:Y" -&gt; XP.</summary>
        public readonly Dictionary<string, int> Unlocks =
            new Dictionary<string, int>(StringComparer.Ordinal);

        public static TurretGunSlot FromJson(JsonNode node)
        {
            var slot = new TurretGunSlot();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return slot;
            }

            slot.Name = node["name"].AsString();
            slot.Reload = node["reload"].AsFloat();
            slot.MaxAmmo = node["max_ammo"].AsInt();
            slot.Mesh = node["mesh"].AsString();
            ReadUnlocks(node["unlocks"], slot.Unlocks);
            return slot;
        }

        internal static void ReadUnlocks(JsonNode node, Dictionary<string, int> target)
        {
            if (node == null || node.Kind != JsonKind.Object)
            {
                return;
            }

            foreach (var entry in node.Fields)
            {
                target[entry.Key] = entry.Value.AsInt();
            }
        }
    }

    /// <summary>
    /// A researchable turret of the Blitz client. The Godot cards had no turret
    /// module at all and kept one rotation speed, one health value and one view
    /// range on the hull; the client authors all three per turret.
    /// </summary>
    public sealed class TurretInfo : ModuleInfo
    {
        public float RotationSpeed;
        public float SpotRadius;
        public float YawLeft = 180f;
        public float YawRight = 180f;

        /// <summary>Blitz <c>blitz/blitzPartName</c>, e.g. <c>turret_02</c>.</summary>
        public string Mesh = string.Empty;

        public readonly List<TurretGunSlot> Guns = new List<TurretGunSlot>();

        /// <summary>The gun slot of this turret, or null when it cannot mount it.</summary>
        public TurretGunSlot Slot(string gunName)
        {
            for (var i = 0; i < Guns.Count; i++)
            {
                if (string.Equals(Guns[i].Name, gunName, StringComparison.Ordinal))
                {
                    return Guns[i];
                }
            }

            return null;
        }

        public bool CanMount(string gunName)
        {
            return Slot(gunName) != null;
        }

        public static TurretInfo FromJson(JsonNode node)
        {
            var turret = new TurretInfo();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return turret;
            }

            turret.ReadShared(node);
            // The turret card keeps the same degrees-per-second traverse the
            // hull card does, and base_tank spends it as radians per second.
            turret.RotationSpeed =
                TankInfo.TraverseRadians(node["rot_speed"].AsFloat());
            turret.SpotRadius = node["spot_radius"].AsFloat();
            turret.YawLeft = node["yaw_limits"]["x"].AsFloat(turret.YawLeft);
            turret.YawRight = node["yaw_limits"]["y"].AsFloat(turret.YawRight);
            turret.Mesh = node["mesh"].AsString();

            var guns = node["guns"];
            if (guns.Kind == JsonKind.Array)
            {
                for (var i = 0; i < guns.Count; i++)
                {
                    turret.Guns.Add(TurretGunSlot.FromJson(guns[i]));
                }
            }

            return turret;
        }
    }

    /// <summary>Port of Godot <c>ModuleEngine</c>.</summary>
    public sealed class EngineInfo : ModuleInfo
    {
        public float Power;
        public float FireStartingChance;

        public static EngineInfo FromJson(JsonNode node)
        {
            var engine = new EngineInfo();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return engine;
            }

            engine.ReadShared(node);
            engine.Power = node["power"].AsFloat();
            engine.FireStartingChance = node["fireStartingChance"].AsFloat();
            return engine;
        }
    }

    /// <summary>Radio module authored by the Blitz client.</summary>
    public sealed class RadioInfo : ModuleInfo
    {
        public float Distance;

        public static RadioInfo FromJson(JsonNode node)
        {
            var radio = new RadioInfo();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return radio;
            }

            radio.ReadShared(node);
            radio.Distance = node["distance"].AsFloat();
            return radio;
        }
    }

    public sealed class InternalTrackInfo
    {
        // Aggregate values remain for existing consumers; the runtime uses authored per-side entries.
        public float ForwardSpeedLimit;
        public float BackwardSpeedLimit;
        public float LeftForwardSpeedLimit, LeftBackwardSpeedLimit, RightForwardSpeedLimit, RightBackwardSpeedLimit;
        public bool Active => ActiveFor(TrackSide.Left) || ActiveFor(TrackSide.Right);
        public bool ActiveFor(TrackSide side) => ForwardLimit(side) > 0f || BackwardLimit(side) > 0f;
        public float ForwardLimit(TrackSide side) => side == TrackSide.Left ? LeftForwardSpeedLimit : RightForwardSpeedLimit;
        public float BackwardLimit(TrackSide side) => side == TrackSide.Left ? LeftBackwardSpeedLimit : RightBackwardSpeedLimit;
        public static InternalTrackInfo FromJson(JsonNode node)
        {
            if (node == null || node.Kind != JsonKind.Object) return null;
            var result = new InternalTrackInfo {
                ForwardSpeedLimit = node["forward_speed_limit"].AsFloat(),
                BackwardSpeedLimit = node["backward_speed_limit"].AsFloat(),
            };
            var entries = node["limitations"];
            if (entries.Kind != JsonKind.Array || entries.Count == 0)
            {
                // Legacy aggregate-only cards apply to both tracks; absent sides in an explicit list do not.
                result.LeftForwardSpeedLimit = result.RightForwardSpeedLimit = result.ForwardSpeedLimit;
                result.LeftBackwardSpeedLimit = result.RightBackwardSpeedLimit = result.BackwardSpeedLimit;
            }
            else foreach (var entry in entries.Items)
            {
                var side = entry["track"].AsString();
                if (side == "left") { result.LeftForwardSpeedLimit = entry["forward_speed_limit"].AsFloat(); result.LeftBackwardSpeedLimit = entry["backward_speed_limit"].AsFloat(); }
                else if (side == "right") { result.RightForwardSpeedLimit = entry["forward_speed_limit"].AsFloat(); result.RightBackwardSpeedLimit = entry["backward_speed_limit"].AsFloat(); }
            }
            return result;
        }
    }

    /// <summary>Port of Godot <c>ModuleTracks</c>.</summary>
    public sealed class TracksInfo : ModuleInfo
    {
        /// <summary>Source track plate thickness; zero keeps legacy table value.</summary>
        public int Armor;
        public float BulkHealthFactor = 1f;
        public float RotationSpeed;
        public float DispersionMoveMultiplier = 0.2f;
        public InternalTrackInfo InternalTrack;

        /// <summary>
        /// <c>disp_rotation_mul</c>. ModuleTracks marks a vehicle whose
        /// coefficients nobody audited yet with a negative value, and the MS-1
        /// card omits the key, so the MS-1 keeps the legacy dispersion model.
        /// </summary>
        public float DispersionRotationMultiplier = -1f;

        /// <summary><c>uses_blitz_dispersion_model()</c>.</summary>
        public bool UsesBlitzDispersionModel
        {
            get { return DispersionRotationMultiplier >= 0f; }
        }

        public static TracksInfo FromJson(JsonNode node)
        {
            var tracks = new TracksInfo();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return tracks;
            }

            tracks.ReadShared(node);
            tracks.Armor = node["armor"].AsInt();
            tracks.BulkHealthFactor = node["bulk_health_factor"].AsFloat(1f);
            tracks.RotationSpeed = node["rot_speed"].AsFloat();
            tracks.DispersionMoveMultiplier =
                node["disp_move_mul"].AsFloat(tracks.DispersionMoveMultiplier);
            tracks.DispersionRotationMultiplier =
                node["disp_rotation_mul"].AsFloat(tracks.DispersionRotationMultiplier);
            tracks.InternalTrack = InternalTrackInfo.FromJson(node["internal_track"]);
            return tracks;
        }
    }
}
