using System;
using System.Collections.Generic;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>Original 11.20 scenes and source battle_effects.yaml mount contracts.
    /// No repainted effects or other client versions are used.</summary>
    public sealed class BigBossVfx : MonoBehaviour
    {
        /// <summary>Boss_mode/Laser.sc2, the railgun discharge. It is laid out along DAVA -Y: both
        /// bolt carriers fly that way and the flash sits five metres down it, while its beam
        /// ribbons only draw once they are given a span. The scene therefore rides the round:
        /// local -Z (DAVA -Y after the axis swap) along the shot, ribbons from the muzzle to the
        /// shell, bolts stopped where the beam ends.</summary>
        public const string RailgunScene = "Boss_mode_Laser";
        /// <summary>What the shooter's own scope drops: the muzzle flash emitters, which burst on
        /// the barrel the scope camera sits on, and the 11 m glow ribbon, which lies edge-on
        /// across a sight looking straight down the beam.</summary>
        public static readonly string[] RailgunScopeDrops = { "Glow", "Lightning", "Laser_2" };
        /// <summary>Inside the own scope the beam and its bolts start this many metres down the
        /// shot: the scope camera sits on the gun and the first stretch of the beam would
        /// otherwise fill the sight.</summary>
        public const float RailgunScopeClearance = 20;
        private const float RailgunSeconds = 2;

        private sealed class Beam { public BattleProjectile Shot; public DavaFx Fx; public Vector3 From, Axis; public float Start; }

        private BigBossTank _owner;
        private Transform _hull, _gun, _poisonGun;
        private readonly Dictionary<string, DavaFx> _held = new Dictionary<string, DavaFx>();
        private readonly List<Beam> _beams = new List<Beam>();
        private DavaFx _heal;
        public static void Attach(BigBossTank owner)
        {
            var c = owner.gameObject.AddComponent<BigBossVfx>(); c._owner = owner;
            c._hull = c.Mount("hull", false); c._gun = c.Mount("gun", false); c._poisonGun = c.Mount("gun", true);
            owner.Effect += c.Event; owner.Respawned += c.Clear;
            BattleProjectile.Created += c.Fired;
            BigBossAudio.Attach(owner);
        }
        private Transform Mount(string prefix, bool corner)
        {
            MeshFilter selected = null;
            if (_owner.Tank.Model != null) foreach (var mesh in _owner.Tank.Model.GetComponentsInChildren<MeshFilter>(true))
                if (mesh.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && mesh.sharedMesh != null) { selected = mesh; break; }
            var point = new GameObject("BigBossMount_" + prefix + (corner ? "_x1_y1_z1_center" : "_center")).transform;
            point.SetParent(selected != null ? selected.transform : transform, false);
            if (selected != null)
            {
                var b = selected.sharedMesh.bounds;
                // The source names the BB anchor but its native enum evaluation is absent.
                // Centre is exact; the named positive-corner mount is a documented adapter.
                point.localPosition = corner ? new Vector3(b.max.x, b.max.y, b.min.z) : b.center;
            }
            return point;
        }
        private bool Visible { get { return _owner.Tank == _owner.Session.Player || _owner.Tank.Team == _owner.Session.Player.Team || _owner.Tank.Spotted; } }
        /// <summary>The camera is looking through this tank's own scope, i.e. sitting on its gun.</summary>
        private bool ScopeOpen { get { var view = _owner.Session.View; return view != null && view.SniperMode && view.Tank == _owner.Tank; } }
        private DavaFx Spawn(string scene, Transform mount, float seconds, bool sustain = false, bool worldAnchored = false)
        {
            var fx = DavaFx.CreateBigBoss(mount, scene, seconds, sustain);
            // A burst belongs to its activation point, not to a moving wreck or respawned hull.
            // Keep session ownership so leaving the battle still releases every burst.
            if (worldAnchored) fx.transform.SetParent(_owner.Session.transform, true);
            fx.ViewCamera = _owner.Session.View?.Active; return fx;
        }
        private void Hold(string slot, string scene, bool wanted, Transform mount, bool visible)
        {
            _held.TryGetValue(slot, out var fx);
            if (!wanted) { if (fx != null) fx.Stop(); _held.Remove(slot); return; }
            // Spotting flicker only hides a held effect: rebuilding the source scene on
            // every re-spot hitched the frame.
            if (fx == null) { if (!visible) return; _held[slot] = fx = Spawn(scene, mount, 1, true); }
            else fx.ViewCamera = _owner.Session.View?.Active;
            if (fx.Visible != visible) fx.Visible = visible;
        }
        private void Event(string id, Vector3 point)
        {
            if (!Visible) return;
            switch (id)
            {
                case "gravityBurst": Spawn("Boss_mode_Repulsion_Explosion", _hull, 3, worldAnchored: true); break;
                case "kamikazeAbility": Spawn("Boss_mode_Kamikaze_Explosion", _hull, 3, worldAnchored: true); break;
                case "healingAura": Spawn("Boss_mode_MassHealAura", _hull, _owner.Param("healingAura", "healWaveDelay") * _owner.Param("healingAura", "healWavesCount")); break;
                // Every aura wave and healing shot heals; one live burst per tank reads the same.
                case "heal": if (_heal == null) _heal = Spawn("halloween_skills_heal", _hull, 2); break;
                case "levelUp": Spawn("Boss_mode_Minion_LvlUP", _hull, 3); break;
            }
        }
        /// <summary>The railgun charge left the barrel: the discharge is anchored in the world at
        /// the muzzle and its beam follows the shell until it lands.</summary>
        private void Fired(BattleProjectile shot)
        {
            if (shot == null || _owner == null || shot.Owner != _owner.Tank || !shot.BigBossRailgun || !Visible) return;
            var from = shot.transform.position;
            var axis = shot.Velocity.sqrMagnitude > 1e-6f ? shot.Velocity.normalized
                : _owner.Tank.Turret != null && _owner.Tank.Turret.GunPivot != null ? _owner.Tank.Turret.GunPivot.forward : transform.forward;
            var fx = Spawn(RailgunScene, _owner.Session.transform, RailgunSeconds);
            var scope = ScopeOpen;
            AimRailgun(fx, from, axis, from, scope);
            _beams.Add(new Beam { Shot = shot, Fx = fx, From = from, Axis = axis, Start = scope ? RailgunScopeClearance : 0 });
        }
        /// <summary>The scene rotation that lays the discharge's DAVA -Y along <paramref name="direction"/>.</summary>
        public static Quaternion RailgunPose(Vector3 direction)
        {
            direction = direction.sqrMagnitude > 1e-8f ? direction.normalized : Vector3.forward;
            var up = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > .999f ? Vector3.forward : Vector3.up;
            return Quaternion.LookRotation(-direction, up);
        }
        /// <summary>Places a railgun discharge at <paramref name="muzzle"/> along the shot and spans
        /// its beam to <paramref name="end"/>. <paramref name="scope"/> is the shooter's own sniper
        /// view: the gun-side parts are dropped and the beam starts past the barrel; the bolts stay.</summary>
        public static void AimRailgun(DavaFx fx, Vector3 muzzle, Vector3 direction, Vector3 end, bool scope)
        {
            fx.transform.SetPositionAndRotation(muzzle, RailgunPose(direction));
            if (scope) fx.Drop(RailgunScopeDrops);
            StretchRailgun(fx, muzzle, direction, end, scope ? RailgunScopeClearance : 0);
        }
        /// <summary>Spans the beam ribbons from <paramref name="start"/> metres down the shot to the
        /// shell and stops the bolts there; nothing draws until the shell is past the start.</summary>
        public static void StretchRailgun(DavaFx fx, Vector3 muzzle, Vector3 direction, Vector3 end, float start = 0)
        {
            direction = direction.sqrMagnitude > 1e-8f ? direction.normalized : Vector3.forward;
            var along = Mathf.Max(0, Vector3.Dot(end - muzzle, direction));
            var from = muzzle + direction * Mathf.Min(start, along);
            fx.SetStripeEndpoints(from, along > start ? end : from);
            fx.LimitTravel(direction, along, start);
        }
        private void UpdateBeams()
        {
            for (var i = _beams.Count - 1; i >= 0; i--)
            {
                var beam = _beams[i];
                if (beam.Fx == null) { _beams.RemoveAt(i); continue; }
                beam.Fx.ViewCamera = _owner.Session.View?.Active;
                if (beam.Shot == null) continue;
                // A shell that just landed still stands on its impact point this frame.
                StretchRailgun(beam.Fx, beam.From, beam.Axis, beam.Shot.transform.position, beam.Start);
                if (!beam.Shot.Alive) beam.Shot = null;
            }
        }
        private void LateUpdate()
        {
            if (_owner == null || _owner.State == null) return;
            UpdateBeams();
            if (_owner.Tank.Dead || !_owner.Session.Started) { Clear(); return; }
            var visible = Visible;
            // The gun sits inside its own scope camera: gun-mounted scenes would fill the sight.
            var gunVisible = visible && !ScopeOpen;
            Hold("boss", "Boss_mode_BossAura", _owner.State.Boss, _hull, visible);
            Hold("railgun", "Boss_mode_Laser_Ready", _owner.RailgunArmed, _gun, gunVisible);
            Hold("poisonCast", "Boss_mode_Dissolve_gun", _owner.PoisonUntil > Time.time, _poisonGun, gunVisible);
            Hold("poison", "Boss_mode_Dissolve", _owner.Poisoned, _hull, visible);
            Hold("shield", "Boss_mode_HealerShield", _owner.ShieldUntil > Time.time, _hull, visible);
            Hold("graviton", "Boss_mode_GravitonPassive", _owner.State.RoleId == 6, _hull, visible);
        }
        private void Clear() { foreach (var fx in _held.Values) if (fx != null) fx.Stop(); _held.Clear(); }
        private void OnDestroy()
        {
            Clear(); BattleProjectile.Created -= Fired;
            if (_owner != null) { _owner.Effect -= Event; _owner.Respawned -= Clear; }
        }
    }
}
