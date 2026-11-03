using System;
using System.Collections.Generic;
using System.Threading;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Battle.CombatFx;
using BlitzLegacy.Runtime.Battle.WorldOfDucks;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Port of <c>assets/tanks/base/projectile/projectile.gd</c>: a shell that
    /// flies under gravity, traces the segment it covered on every physics tick
    /// and resolves whatever that segment met.
    ///
    /// The source moves the shell itself and raycasts from <c>lastpos</c> to the
    /// new position instead of handing the round to the physics engine, which
    /// is what stops a 442 m/s shell tunnelling through a 13 mm plate. Trails,
    /// impact decals and hit sounds belong to the presentation slice.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleProjectile : MonoBehaviour
    {
        private static int _nextShotId;

        private static readonly List<BattleProjectile> _liveGuided = new List<BattleProjectile>();

        /// <summary>Guided rockets in flight on this peer (the host replicates them).</summary>
        public static IReadOnlyList<BattleProjectile> LiveGuided { get { return _liveGuided; } }

        /// <summary>Replay: longest wait at the muzzle for the host's first sample.</summary>
        public const float FollowHoldSeconds = 0.5f;

        /// <summary>Replay of a host rocket: its shot id there, 0 for a local shell.</summary>
        public int HostShotId { get; private set; }

        /// <summary>True while this replay rides the host's samples.</summary>
        public bool FollowsHost { get { return _followHost; } }

        private bool _followHost;
        private bool _seenHost;
        private float _followSince;
        private int _uprisingLife, _uprisingSplash, _uprisingChainBase;
        private bool _uprisingImpact;

        /// <summary><c>ricoshets</c>: grazes a shell starts its flight with.</summary>
        public const int RicochetBudget = 2;

        /// <summary>
        /// How far off a plate a ricochet restarts. Godot resolves the graze
        /// analytically; a Unity raycast that starts exactly on the surface it
        /// just left can report that surface again, so the shell steps off it.
        /// </summary>
        public const float TraceEpsilon = 0.01f;

        /// <summary><c>shell_info</c>, the card this round was fired from.</summary>
        public ShellInfo Shell { get; private set; }

        /// <summary><c>owner_tank</c>, credited with whatever the shell does.</summary>
        private static float _nextImpactSound;

        public BattleTank Owner { get; private set; }

        /// <summary>
        /// <c>shot_id</c>: one stable id shared by every damage record that
        /// comes from this shell.
        /// </summary>
        public int ShotId { get; private set; }

        /// <summary><c>shell_info.pen</c> left after screens, props and gaps.</summary>
        public float Penetration { get; private set; }

        /// <summary><c>velocity</c>, in metres per second.</summary>
        [System.NonSerialized]
        public Vector3 Velocity;

        /// <summary><c>gravity</c>.</summary>
        public float Gravity { get; private set; }

        /// <summary>
        /// <c>isATGM</c>: the shot keeps steering toward whatever the shooter's
        /// crosshair rests on instead of finishing a ballistic arc.
        /// </summary>
        public bool Guided { get; private set; }

        /// <summary>
        /// The shot left from the arty view, so it climbs over whatever stands
        /// between the gun and the crosshair instead of flying straight at it.
        /// </summary>
        public bool Loft { get; private set; }

        /// <summary><c>lastpos</c>: where the trace of this tick starts.</summary>
        public Vector3 LastPosition { get; private set; }

        /// <summary>Grazes left before the next one ends the flight.</summary>
        public int RicochetsLeft { get; private set; }

        /// <summary>False once the shell has landed, bounced out or timed out.</summary>
        public bool Alive { get; private set; }

        /// <summary><c>trail</c>: the tracer this shell draws, if it draws one.</summary>
        public BattleShellTrail Trail { get; private set; }

        /// <summary>The GPUTrail3D pair an ordinary shell draws (rockets keep <see cref="Trail"/>).</summary>
        public BattleGpuTrail Tracer { get; private set; }

        /// <summary>The billboarded shell sprite of an ordinary shell.</summary>
        public BattleShellSprite Sprite { get; private set; }

        /// <summary><c>last_hit_dict.normal</c>: impact effects turn their Y axis to it.</summary>
        private Vector3 _lastHitNormal;
        private bool _hasHitNormal;
        private Collider _lastHitCollider;

        /// <summary>The surface normal of the last thing the shell met, if any.</summary>
        public Vector3 LastHitNormal { get { return _hasHitNormal ? _lastHitNormal : Vector3.up; } }

        /// <summary><c>last_hit_dict.collider</c>: the last collider the shell met, or null.</summary>
        public Collider LastHitCollider { get { return _lastHitCollider; } }

        /// <summary>
        /// Raised once every shell is built, so a battle mode can dress its
        /// own rounds (War of Ducks hangs the 4.8 torpedo wake on gun 3).
        /// </summary>
        public static event Action<BattleProjectile> Created;

        /// <summary>The 11.20 HUD marker and native missile FX, not an XM81 ribbon.</summary>
        public BattleAtgmPresentation AtgmVisual { get; private set; }

        /// <summary>Plates this shell already crossed and must not meet again.</summary>
        private readonly HashSet<Collider> _exclude = new HashSet<Collider>();

        private BattleTank _screenTank;
        private Vector3 _screenPoint;
        private float _destroyTime;
        private Vector3 _launchPosition, _launchVelocity;

        /// <summary><c>exploded</c>: one warhead only ever bursts once.</summary>
        private bool _exploded;

        /// <summary>Tanks the last burst of this shell damaged, for the tests.</summary>
        public IReadOnlyList<BattleTank> ExplosionTanks { get { return _explosionTanks; } }

        private readonly List<BattleTank> _explosionTanks = new List<BattleTank>();

        /// <summary>Damage the last burst handed each tank, in the same order.</summary>
        public IReadOnlyList<int> ExplosionDamage { get { return _explosionDamage; } }

        private readonly List<int> _explosionDamage = new List<int>();

        /// <summary>The looped flight bed a guided rocket carries.</summary>
        private AudioSource _flight;

        /// <summary>
        /// <c>Projectile.create</c>. <paramref name="origin"/> is the gun's own
        /// position, which is where the source starts the first trace from, and
        /// <paramref name="muzzle"/> is <c>gun_shoot_point</c>, where the shell
        /// becomes visible.
        /// </summary>
        public static BattleProjectile Create(
            ShellInfo shell,
            BattleTank owner,
            Vector3 origin,
            Vector3 muzzle,
            Vector3 direction,
            bool trail = true)
        {
            var host = new GameObject("Projectile");
            if (owner != null)
            {
                // Shells live beside the tanks, not under the tank that fired
                // them, so a destroyed tank cannot take its rounds with it.
                host.transform.SetParent(owner.transform.parent, true);
            }

            var projectile = host.AddComponent<BattleProjectile>();
            projectile.Launch(shell, owner, origin, muzzle, direction);
            if (trail)
            {
                // Projectile.create builds the tracer itself and anchors it at
                // the gun, which is how a spent shell still leaves a trail.
                if (shell != null && shell.UsesModernAtgmVisual)
                {
                    projectile.AtgmVisual = BattleAtgmPresentation.Create(projectile);
                    // Someone else's rocket: no marker, but an unbroken smoke
                    // tracer so enemies and team-mates see where it flies.
                    if (!BattleAtgmPresentation.ShowsMarker(owner))
                    { projectile.Trail = BattleShellTrail.Create(projectile, muzzle); }
                }
                else if (shell != null && shell.Atgm)
                { projectile.Trail = BattleShellTrail.Create(projectile, muzzle); }
                else
                { projectile.Tracer = CreateTracer(shell, owner, origin, muzzle, direction); }
            }

            if (shell == null || !shell.Atgm)
            {
                var owned = owner;
                projectile.Sprite = BattleShellSprite.Create(
                    host.transform,
                    shell != null ? shell.Caliber : 0,
                    shell != null && shell.Type == ShellType.Heat);
                if (projectile.Sprite != null)
                {
                    projectile.Sprite.Concealed = () => owned != null && owned.ConcealedFromPlayer;
                }
            }

            Created?.Invoke(projectile);
            return projectile;
        }

        /// <summary>
        /// The trail node of <c>Projectile.create</c>: placed at
        /// <c>gsp_pos - gun.global_basis.z * randf_range(0.75, 1) * speed * 0.005</c>
        /// (a little ahead of the muzzle) and looking at
        /// <c>pos + dir * 10</c>, where <c>pos</c> is the gun's own position.
        /// </summary>
        private static BattleGpuTrail CreateTracer(
            ShellInfo shell, BattleTank owner, Vector3 origin, Vector3 muzzle, Vector3 direction)
        {
            var manifest = CombatFxManifest.Shared;
            if (manifest == null)
            {
                return null;
            }

            var gunForward = owner != null && owner.Turret != null && owner.Turret.GunPivot != null
                ? owner.Turret.GunPivot.forward
                : direction.normalized;
            var speed = shell != null ? shell.Speed : 0f;
            var lead = UnityEngine.Random.Range(manifest.TrailLeadMin, manifest.TrailLeadMax)
                * speed * manifest.TrailLeadSpeedFactor;
            var tracer = BattleGpuTrail.Create(
                shell != null ? shell.Caliber : 0,
                shell != null && shell.Type == ShellType.Heat,
                muzzle + gunForward * lead,
                origin + direction.normalized * 10f);
            if (tracer != null)
            {
                tracer.Concealed = () => owner != null && owner.ConcealedFromPlayer;
            }

            return tracer;
        }

        /// <summary>
        /// <c>randfn(1, deviation * 0.4)</c>. UnityEngine.Random draws uniformly
        /// only, so this is the Box-Muller transform a normal draw is built on.
        /// </summary>
        public static float NormalRoll(float deviation)
        {
            var u1 = Mathf.Max(UnityEngine.Random.value, 1e-6f);
            var u2 = UnityEngine.Random.value;
            var normal =
                Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
            return 1f + normal * deviation * GunMath.DeviationSigmaFactor;
        }

        private BigBossShot _bigBossShot;

        /// <summary>The Big Boss railgun charge rode this shell out of the barrel.</summary>
        public bool BigBossRailgun { get { return _bigBossShot.Railgun; } }

        internal void Launch(
            ShellInfo shell,
            BattleTank owner,
            Vector3 origin,
            Vector3 muzzle,
            Vector3 direction)
        {
            Shell = shell;
            Owner = owner;
            Alive = true;
            ShotId = Interlocked.Increment(ref _nextShotId);
            var uprising = owner == null ? null : owner.GetComponent<UprisingTank>();
            if (uprising != null)
            {
                _uprisingLife = uprising.State.LifeSerial;
                _uprisingSplash = Mathf.RoundToInt(owner.Health.Health * uprising.State.Number("splash", "health_factor", .3f));
                _uprisingChainBase = Mathf.RoundToInt((shell == null ? 0 : shell.Damage) * uprising.State.Outgoing(DamageType.Shell,Time.time));
            }
            RicochetsLeft = RicochetBudget;
            LastPosition = origin;
            transform.position = muzzle;

            _bigBossShot = owner != null && owner.BigBoss != null ? owner.BigBoss.Launch(shell, ShotId) : new BigBossShot { Damage = 1 };
            var speed = _bigBossShot.Speed > 0 ? _bigBossShot.Speed : shell != null ? Mathf.Abs(shell.Speed) : 0f;
            Velocity = direction.normalized * speed;
            _launchPosition = muzzle;
            _launchVelocity = Velocity;

            // projectile.gd:74 zeroes gravity_mul for a homing shell before it
            // builds its own gravity, so a rocket flies wherever it is pointed
            // and only the steering bends its path. Everything else falls at
            // PROJECTILES_GRAVITY_MUL * gravity_mul * default_gravity.
            Guided = shell != null && shell.Atgm;
            Loft = Guided && owner != null && owner.Turret != null
                && owner.Turret.LobAim;
            Gravity = shell != null
                ? AimMath.ShellGravity(shell.GravityMultiplier, shell.Atgm)
                    * ShellGravityScaleNow()
                : AimMath.ShellGravity(1f, false) * ShellGravityScaleNow();
            Penetration = shell != null
                ? GunMath.PenetrationRoll(
                    shell.Penetration,
                    NormalRoll(PenetrationDeviationNow()),
                    PenetrationDeviationNow())
                : 0f;
            _destroyTime = Time.time + GunMath.ShellLifetime(
                shell != null ? shell.MaxDistance : 0f, speed);

            if (Guided)
            {
                StartFlightSound();
                if (!_liveGuided.Contains(this)) { _liveGuided.Add(this); }
            }
        }

        /// <summary>
        /// Client replay of a host rocket: fly the host's interpolated path
        /// (<see cref="BattleNetHooks.HostRockets"/>) instead of steering
        /// locally, so it turns, lofts and lands exactly where the real one
        /// does. Until the first sample arrives it waits at the muzzle; once
        /// the host's rocket is gone it finishes on its own last velocity.
        /// </summary>
        public void FollowHost(int hostShotId)
        {
            if (!Guided || hostShotId <= 0) { return; }
            HostShotId = hostShotId;
            _followHost = true;
            _seenHost = false;
            _followSince = Time.time;
        }

        /// <summary>
        /// <c>flying</c>: a rocket carries its own looped bed from the shot
        /// until it stops flying, which is what separates it from a shell that
        /// only ever makes a report when it lands.
        /// </summary>
        private void StartFlightSound()
        {
            var audio = BlitzBootstrap.Current != null
                ? BlitzBootstrap.Current.Audio : null;
            if (audio == null)
            {
                return;
            }

            _flight = audio.PlayLoop3D(
                audio.Sounds.Sound(TankSoundTable.ShellFlightSet), transform,
                1f, SpatialAudio.AtgmFlight);
        }

        /// <summary><c>stopFlying</c>.</summary>
        private void StopFlightSound()
        {
            if (_flight == null)
            {
                return;
            }

            var audio = BlitzBootstrap.Current != null
                ? BlitzBootstrap.Current.Audio : null;
            if (audio != null)
            {
                audio.StopSound2D(_flight);
            }

            _flight = null;
        }

        private void OnDestroy()
        {
            StopFlightSound();
            _liveGuided.Remove(this);
            if (AtgmVisual != null && !AtgmVisual.Exploding) { AtgmVisual.Cancel(); }
            if (Tracer != null) { Tracer.Release(); }
        }

        private void FixedUpdate()
        {
            if (!Alive)
            {
                return;
            }

            if (Time.time >= _destroyTime)
            {
                Despawn();
                return;
            }

            if (_followHost && FollowHostStep()) { return; }

            // if !self.visible: self.show()
            if (Sprite != null) { Sprite.SetShown(true); }

            var delta = Time.fixedDeltaTime;
            if (Guided)
            {
                Steer(delta);
            }

            Velocity.y -= Gravity * delta;

            var from = LastPosition;
            var to = transform.position + Velocity * delta;
            transform.position = to;
            Trace(from, to);

            if (Alive)
            {
                LastPosition = transform.position;
            }

            // trail.global_position = self.global_position
            if (Tracer != null) { Tracer.Follow(transform.position); }
        }

        /// <summary>One replay step on the host's path; false hands over to the own flight.</summary>
        private bool FollowHostStep()
        {
            BattleNetHooks.RocketSample sample;
            if (BattleNetHooks.HostRockets.TryGetValue(HostShotId, out sample))
            {
                _seenHost = true;
                Velocity = sample.Velocity;
                var from = LastPosition;
                transform.position = sample.Position;
                Trace(from, sample.Position);
                if (Alive) { LastPosition = transform.position; }
                return true;
            }
            if (!_seenHost && Time.time - _followSince < FollowHoldSeconds) { return true; }
            // The host's rocket hit or expired (or never showed up): the own
            // trace finishes the last metres on the same heading.
            _followHost = false;
            return false;
        }

        /// <summary>
        /// The guidance the client gives an ATGM: every tick the rocket turns
        /// toward whatever the shooter's crosshair rests on, at most
        /// <c>turnSpeed</c> radians per second, and keeps its own speed. A dead
        /// or missing shooter stops steering, so the rocket flies on straight.
        /// </summary>
        private void Steer(float delta)
        {
            if (Owner == null || Owner.Dead || Shell == null || Shell.TurnSpeed <= 0f)
            {
                return;
            }

            // base_tank.gd:998 homes every rocket onto aim_target_lerp, the
            // smoothed crosshair point, not onto the ballistic gun trace. The
            // trace of a 75 m/s round lands on the dirt a few metres ahead of
            // the hull, which is what made the rocket dive into the ground
            // instead of climbing toward the point the player aims at.
            var target = Owner.Turret != null
                ? Owner.Turret.AimTargetLerp
                : Owner.GunTracePoint;

            Velocity = GuidedVelocity(Velocity, transform.position, target,
                Shell.TurnSpeed, delta, Loft, _launchPosition, _launchVelocity);
        }

        /// <summary>The same bounded steering is used by flight and sight prediction.</summary>
        public static Vector3 GuidedVelocity(Vector3 velocity, Vector3 position,
            Vector3 target, float turnSpeed, float delta, bool loft,
            Vector3 launchPosition, Vector3 launchVelocity)
        {
            if (loft) target = LobTarget(launchPosition, launchVelocity, position, target, turnSpeed, delta);
            return HomingVelocity(velocity, position, target, turnSpeed, delta);
        }

        /// <summary>
        /// Follow a reachable bow with the actual launch slope at its start.
        /// The fixed origin avoids a virtual target that recedes overhead as
        /// the rocket closes. A short lookahead produces a real descending
        /// approach instead of flattening out over the intended roof.
        /// </summary>
        public static Vector3 LobTarget(Vector3 origin, Vector3 launchVelocity,
            Vector3 position, Vector3 target, float turnSpeed, float delta)
        {
            var flat = target - origin; flat.y = 0;
            var range = flat.magnitude;
            var speed = launchVelocity.magnitude;
            if (range < TraceEpsilon || turnSpeed <= 0 || speed <= 0) return target;
            var launchFlat = new Vector3(launchVelocity.x, 0, launchVelocity.z).magnitude;
            if (launchFlat < TraceEpsilon) return target;
            var slope = launchVelocity.y / launchFlat - (target.y - origin.y) / range;
            var height = Mathf.Clamp(range * slope * .25f, 0, ArtyMath.LoftCeiling);
            var progress = Vector3.Dot(position - origin, flat / range);
            var lookahead = Mathf.Max(speed * delta * 2f, speed / turnSpeed * .1f);
            var fraction = Mathf.Clamp01((progress + lookahead) / range);
            var aim = Vector3.Lerp(origin, target, fraction);
            aim.y += ArtyMath.ArcHeight(height, fraction);
            return aim;
        }

        /// <summary>
        /// <c>home_to_pos</c> without the scene graph: the rocket keeps its own
        /// speed and swings its heading toward <paramref name="target"/> by at
        /// most <paramref name="turnSpeed"/> radians this tick. The source
        /// spends the same rate through <c>homing_strength</c>.
        /// </summary>
        public static Vector3 HomingVelocity(
            Vector3 velocity,
            Vector3 position,
            Vector3 target,
            float turnSpeed,
            float delta)
        {
            var desired = target - position;
            var speed = velocity.magnitude;
            if (desired.sqrMagnitude <= TraceEpsilon * TraceEpsilon || speed <= 0f)
            {
                return velocity;
            }

            // turnSpeed is authored in radians per second, which is exactly
            // what RotateTowards wants for its angular step.
            var heading = Vector3.RotateTowards(
                velocity / speed,
                desired.normalized,
                turnSpeed * delta,
                0f);
            return heading.normalized * speed;
        }

        /// <summary>
        /// The source's <c>_physics_process</c> trace: everything the segment
        /// crossed, nearest first, until something stops the shell.
        /// </summary>
        private void Trace(Vector3 from, Vector3 to)
        {
            var segment = to - from;
            var distance = segment.magnitude;
            if (distance <= 0f)
            {
                return;
            }

            var direction = segment / distance;
            var hits = Physics.RaycastAll(
                from,
                direction,
                distance,
                BattleLayers.ProjectileMask,
                QueryTriggerInteraction.Collide);
            if (hits.Length > 1)
            {
                Array.Sort(hits, CompareByDistance);
            }

            for (var i = 0; i < hits.Length; i++)
            {
                if (!Alive)
                {
                    return;
                }

                var hit = hits[i];
                if (hit.collider == null || Excluded(hit.collider))
                {
                    continue;
                }

                if (!HandleHit(hit, direction))
                {
                    return;
                }
            }
        }

        private static int CompareByDistance(RaycastHit a, RaycastHit b)
        {
            return a.distance.CompareTo(b.distance);
        }

        /// <summary><c>trace_exclude</c>: the shooter and everything crossed already.</summary>
        private bool Excluded(Collider collider)
        {
            if (Owner != null && Owner.Owns(collider))
            {
                return true;
            }

            return _exclude.Contains(collider);
        }

        /// <summary><c>handle_hit</c>. Returns false once the shell is done.</summary>
        private bool HandleHit(RaycastHit hit, Vector3 direction)
        {
            _lastHitNormal = hit.normal;
            _hasHitNormal = true;
            _lastHitCollider = hit.collider;
            var plate = hit.collider.GetComponent<BattleArmorPlate>();
            if (plate != null)
            {
                return HandleArmor(plate, hit, direction);
            }

            var destructible = hit.collider.GetComponentInParent<BattleDestructible>();
            if (destructible != null && !destructible.Destroyed)
            {
                return HandleDestructible(destructible, hit);
            }

            // Terrain, static props and the hull colliders all stop a shell.
            // An explosive one bursts where it landed first, which is what
            // splashes a tank standing behind a rock or a low wall.
            if (Explosive())
            {
                PlayImpact(ShellSoundKind.Impact, hit.point);
                Explode(hit.point, hit.normal);
            }
            else
            {
                // handle_hit plays HitType.IMPACT on anything that is not a
                // tank; the port keeps that report for explosive rounds and
                // shows the effect for every one.
                SpawnImpactFx(ShellSoundKind.Impact, hit.point);
            }

            Despawn(hit.point);
            return false;
        }

        private bool HandleArmor(BattleArmorPlate plate, RaycastHit hit, Vector3 direction)
        {
            var tank = plate.Tank;
            if (_bigBossShot.Friendly(Owner, tank, Shell)) { Despawn(hit.point); return false; }
            var type = Shell != null ? Shell.Type : ShellType.Ap;
            var caliber = Shell != null ? Shell.Caliber : 0;
            var impactAngle = ArmorMath.ImpactAngle(Vector3.Dot(hit.normal, direction));

            // Sandbox without friendly fire: a live team-mate swallows the shell
            // (projectile.gd get_destroyed_idiot) and takes nothing.
            if (SandboxBattle.BlocksFriendlyHit(Owner, tank)
                || WorldOfDucksBattle.BlocksFriendlyHit(Owner, tank))
            {
                Despawn(hit.point);
                return false;
            }

            if (plate.Screen)
            {
                // Device damage is authored separately from HP damage and calibre.
                if (tank != null && !tank.Dead && (Owner == null || tank.Team != Owner.Team) &&
                    BattleTank.DamageLands(CurrentSettings(), Owner, tank) && !string.IsNullOrEmpty(plate.Module))
                {
                    var moduleDamage = Mathf.RoundToInt(Shell != null ? Shell.DeviceDamage : 0f);

                    tank.ModuleCollide(plate, moduleDamage, hit.point);
                }

                // The source bursts an explosive round on spaced armour before
                // the screen ever takes its cut of the penetration, and it
                // sprays the whole sphere rather than the hemisphere: the
                // warhead sits in the gap, so the hull behind the screen is
                // squarely inside the splash.
                if (Explosive())
                {
                    PlayImpact(ShellSoundKind.Impact, hit.point);
                    Explode(hit.point, hit.normal, 0, null, false);
                    Despawn(hit.point);
                    return false;
                }

                var effective = plate.UseNominalArmor
                    ? plate.Armor
                    : ArmorMath.EffectiveArmor(type, caliber, plate.Armor, impactAngle);
                Penetration = HitResolver.PenetrationAfterPlate(Penetration, effective);
                _screenTank = tank;
                _screenPoint = hit.point;

                // Spaced armour spends the graze: a shell that came through a
                // screen can no longer ricochet off the plate behind it.
                RicochetsLeft = 0;

                if (!HitResolver.Continues(Penetration))
                {
                    GravityModeTank.ApplyHitKnockback(tank, hit.point, hit.normal, direction, caliber, GravityModeTank.HitType.Bounce);
                    Block(tank, hit.point);
                    Despawn(hit.point);
                    return false;
                }

                // A kinetic round punching through the screen shows the
                // penetration effect on it before it flies on.
                if (type != ShellType.Heat)
                {
                    SpawnImpactFx(ShellSoundKind.Pen, hit.point);
                }

                _exclude.Add(hit.collider);
                return true;
            }

            var crossedScreen = _screenTank != null && _screenTank == tank;
            if (crossedScreen)
            {
                Penetration = HitResolver.PenetrationAfterGap(
                    type, Penetration, Vector3.Distance(_screenPoint, hit.point));
            }

            // The card's own ricochetAngle rides along: solid shot keeps its
            // 70 degrees and the shaped charges of the XM81 launcher bounce
            // at the 85 the client authors on them.
            var outcome = HitResolver.ResolveArmor(
                type,
                caliber,
                Penetration,
                plate.Armor,
                impactAngle,
                crossedScreen,
                Shell != null ? Shell.RicochetAngle : 0f);
            switch (outcome)
            {
                case HitOutcome.Overmatch:
                case HitOutcome.Penetration:
                    GravityModeTank.ApplyHitKnockback(tank, hit.point, hit.normal, direction, caliber, GravityModeTank.HitType.Penetration);
                    var damage = Damage(tank, hit.point);
                    if (tank != null && damage != null && damage.Dmg > 0)
                        tank.ApplyPenetratingModuleHit(Shell, hit.point, direction, damage);
                    Despawn(hit.point);
                    return false;
                case HitOutcome.Ricochet:
                    GravityModeTank.ApplyHitKnockback(tank, hit.point, hit.normal, direction, caliber, GravityModeTank.HitType.Ricochet);
                    return Ricochet(hit);
                default:
                    // An explosive round that the plate stopped does not score
                    // a block: it bursts against that plate and the splash
                    // decides what the tank takes, with the thickness it just
                    // met charged against the direct ray.
                    if (Explosive())
                    {
                        PlayImpact(ShellSoundKind.Impact, hit.point);
                        Explode(hit.point, hit.normal, plate.Armor, tank);
                        Despawn(hit.point);
                        return false;
                    }

                    Block(tank, hit.point);
                    Despawn(hit.point);
                    return false;
            }
        }

        /// <summary>
        /// save_settings "DMG_DEVIATION" as the half-width of the damage roll
        /// window. Dev25 is the stock quarter; Dev0 pins every shot to the
        /// card value.
        /// </summary>
        public static float DamageDeviationFor(SettingsData settings)
        {
            return DeviationFor(
                settings != null ? settings.DmgDeviation : SettingsData.Dev25,
                GunMath.DamageDeviation);
        }

        /// <summary>save_settings "PEN_DEVIATION", same window for penetration.</summary>
        public static float PenetrationDeviationFor(SettingsData settings)
        {
            return DeviationFor(
                settings != null ? settings.PenDeviation : SettingsData.Dev5,
                GunMath.PenetrationDeviation);
        }

        private static float DeviationFor(int setting, float fallback)
        {
            switch (setting)
            {
                case SettingsData.Dev25: return 0.25f;
                case SettingsData.Dev15: return 0.15f;
                case SettingsData.Dev5: return 0.05f;
                case SettingsData.Dev0: return 0f;
                default: return fallback;
            }
        }

        private static SettingsData CurrentSettings()
        {
            return BlitzBootstrap.Current != null
                ? BlitzBootstrap.Current.BattleRules : null;
        }

        /// <summary>Damage window of the running battle; the Sandbox preset wins.</summary>
        public static float DamageDeviationNow()
        {
            return SandboxBattle.DamageDeviationOr(DamageDeviationFor(CurrentSettings()));
        }

        /// <summary>Penetration window of the running battle; the Sandbox preset wins.</summary>
        public static float PenetrationDeviationNow()
        {
            return SandboxBattle.PenetrationDeviationOr(PenetrationDeviationFor(CurrentSettings()));
        }

        /// <summary>
        /// save_settings "PROJECTILES_GRAVITY_MUL" as the extra scale on top of
        /// the shell card, so a save can flatten or exaggerate every arc.
        /// </summary>
        public static float ShellGravityScale(SettingsData settings)
        {
            if (settings == null)
            {
                return 1f;
            }

            return Mathf.Clamp(settings.ShellGravityMul, 0.1f, 5f);
        }

        /// <summary>The saved gravity scale of the running game.</summary>
        public static float ShellGravityScaleNow()
        {
            return SandboxBattle.ShellGravityOr(ShellGravityScale(CurrentSettings()));
        }

        private bool HandleDestructible(BattleDestructible destructible, RaycastHit hit)
        {
            PlayImpact(ShellSoundKind.Impact, hit.point);

            var type = Shell != null ? Shell.Type : ShellType.Ap;
            var healthBefore = destructible.Health;
            var rolled = GunMath.DamageRoll(
                Shell != null ? Shell.Damage : 0f,
                NormalRoll(DamageDeviationNow()), DamageDeviationNow());

            destructible.Damage(HitResolver.DestructibleDamage(type, rolled, Penetration));
            Penetration = HitResolver.PenetrationAfterDestructible(Penetration, healthBefore);

            // handle_hit: an explosive round always bursts on the prop it broke
            // and never carries on behind it, however much penetration is left.
            if (Explosive())
            {
                Explode(hit.point, hit.normal);
                Despawn(hit.point);
                return false;
            }

            if (!HitResolver.Continues(Penetration))
            {
                Despawn(hit.point);
                return false;
            }

            _exclude.Add(hit.collider);
            return true;
        }

        /// <summary>
        /// <c>ricoshet</c>. Faithful to the source, which returns before it ever
        /// decrements its counter, so the budget only ever matters once a screen
        /// has zeroed it.
        /// </summary>
        private bool Ricochet(RaycastHit hit)
        {
            if (RicochetsLeft <= 0)
            {
                Despawn(hit.point);
                return false;
            }

            PlayImpact(ShellSoundKind.Ricoshet, hit.point);
            SpeakImpact(CrewMessage.Ricoshet);

            Velocity = Vector3.Reflect(Velocity, hit.normal);
            var away = Velocity.sqrMagnitude > 0f
                ? Velocity.normalized * TraceEpsilon
                : Vector3.zero;
            transform.position = hit.point + away;
            LastPosition = transform.position;

            // self.hide(); the old emitters go top-level here, a new pair follows.
            if (Sprite != null) { Sprite.SetShown(false); }
            if (Tracer != null) { Tracer.Ricochet(transform.position); }
            return false;
        }

        /// <summary><c>explosives.has(shell_info.type)</c>.</summary>
        public bool Explosive()
        {
            return Shell != null && ArmorMath.IsExplosive(Shell.Type);
        }

        /// <summary>
        /// <c>dmg_rand</c>: the damage roll clamped into its own deviation
        /// window, which is the multiplier every splash ray is worth.
        /// </summary>
        private static float DamageRandom()
        {
            var deviation = GunMath.DamageDeviation;
            return Mathf.Clamp(
                NormalRoll(deviation), 1f - deviation, 1f + deviation);
        }

        /// <summary>
        /// <c>Projectile.explode</c>. A high-explosive round that did not go
        /// through stands where it landed and fires a fan of 8 x 8 rays over
        /// the hemisphere around the impact normal, out to its own splash
        /// radius. Each ray that reaches armour is worth
        /// <see cref="ExplosionMath.DmgOverDist"/> at that distance through that
        /// thickness, every tank keeps the best ray that found it, and props on
        /// the way take their own share. That is the whole reason HE rewards a
        /// thin roof over a crowd instead of the thickest plate on one hull.
        ///
        /// <paramref name="halfSphere"/> is false for a burst on spaced armour,
        /// which the source lets spray the full sphere, and
        /// <paramref name="returnThick"/> flips the per-tank pick from the best
        /// ray to the worst one.
        /// </summary>
        public void Explode(
            Vector3 origin,
            Vector3 normal,
            int armorHit = 0,
            BattleTank tankHit = null,
            bool halfSphere = true,
            bool returnThick = false,
            int dmgBlockFlat = 0)
        {
            if (_exploded)
            {
                return;
            }

            _exploded = true;
            _explosionTanks.Clear();
            _explosionDamage.Clear();

            var damage = Shell != null ? Shell.Damage : 0f;
            var radius = Shell != null
                ? ArmorMath.ExplosionRadiusFor(Shell.ExplosionRadius, Shell.Caliber)
                : 0f;
            var heading = Velocity.sqrMagnitude > 0f
                ? Velocity.normalized
                : (normal.sqrMagnitude > 0f ? -normal.normalized : Vector3.forward);

            // The source steps the burst off the surface so its own rays do not
            // start inside the plate that stopped the shell.
            var burst = origin + normal.normalized * ExplosionMath.OriginOffset;
            var end = transform.position;

            if (tankHit != null)
            {
                GravityModeTank.ApplyHitKnockback(
                    tankHit, origin, normal, heading,
                    Shell != null ? Shell.Caliber : 0f, GravityModeTank.HitType.Impact);
            }

            var tanks = new List<BattleTank>();
            var amounts = new List<int>();
            if (tankHit != null)
            {
                tanks.Add(tankHit);
                amounts.Add(ExplosionMath.DmgOverDist(
                    damage, 0f, radius, armorHit, 1f, DamageRandom()));
            }

            var normalVec = new Vec3(normal.x, normal.y, normal.z);
            var headingVec = new Vec3(heading.x, heading.y, heading.z);
            var rings = ExplosionMath.RingCount(halfSphere);
            for (var ring = 0; ring < rings; ring++)
            {
                for (var spoke = 0; spoke < ExplosionMath.Steps; spoke++)
                {
                    var ray = ExplosionMath.RayDirection(
                        normalVec, headingVec, ring, spoke);
                    var direction = new Vector3(ray.X, ray.Y, ray.Z);
                    if (direction.sqrMagnitude <= 0f)
                    {
                        continue;
                    }

                    // The source aims every ray at global_position + dir * radius,
                    // the unshifted impact point, while tracing from the shifted
                    // burst, so the segment is the radius give or take the 2 cm
                    // step off the surface.
                    var target = end + direction * radius;
                    var segment = target - burst;
                    var length = segment.magnitude;
                    if (length <= 0f)
                    {
                        continue;
                    }

                    RaycastHit hit;
                    if (!Physics.Raycast(
                            burst,
                            segment / length,
                            out hit,
                            length,
                            BattleLayers.ExplosionMask,
                            QueryTriggerInteraction.Collide))
                    {
                        continue;
                    }

                    var plate = hit.collider.GetComponent<BattleArmorPlate>();
                    if (plate != null)
                    {
                        SplashPlate(
                            plate, hit, burst, damage, radius, returnThick,
                            tanks, amounts);
                        continue;
                    }

                    var prop = hit.collider.GetComponentInParent<BattleDestructible>();
                    if (prop != null && !prop.Destroyed)
                    {
                        prop.Damage(ExplosionMath.DmgOverDist(
                            damage,
                            Vector3.Distance(burst, hit.point),
                            radius,
                            prop.Health,
                            1f,
                            DamageRandom()));
                    }
                }
            }

            var hitSomebody = false;
            for (var i = 0; i < tanks.Count; i++)
            {
                var tank = tanks[i];
                if (tank == null)
                {
                    continue;
                }

                hitSomebody = hitSomebody || !tank.Dead;

                var dinfo = BuildDamage(burst);
                dinfo.DmgType = DamageType.Explosion;
                dinfo.Dmg = Mathf.Max(amounts[i] - dmgBlockFlat, 0);
                _explosionTanks.Add(tank);
                _explosionDamage.Add(dinfo.Dmg);
                tank.ReceiveDamage(dinfo);
            }

            if (hitSomebody)
            {
                SpeakImpact(CrewMessage.ArmorNotHit);
            }
        }

        /// <summary>
        /// One splash ray that reached armour: friendly plates are skipped, a
        /// zero-thickness collider is ignored and the tank keeps the best - or,
        /// for <paramref name="returnThick"/>, the worst - ray that found it.
        /// </summary>
        private void SplashPlate(
            BattleArmorPlate plate,
            RaycastHit hit,
            Vector3 burst,
            float damage,
            float radius,
            bool returnThick,
            List<BattleTank> tanks,
            List<int> amounts)
        {
            var tank = plate.Tank;
            if (tank == null)
            {
                return;
            }

            // No friendly fire, but the shooter's own hull is fair game: the
            // source only spares live team-mates other than the owner.
            if (!tank.Dead && Owner != null && tank.Team == Owner.Team && tank != Owner
                && !SandboxBattle.FriendlyFireOn)
            {
                return;
            }

            if (plate.Armor == 0)
            {
                return;
            }

            // has_equipment(eq_vent) in the source. This port has no equipment
            // fitted in battle yet, so the liner coefficient stays at one and
            // ExplosionMath carries the value the liner would apply.
            var dmg = ExplosionMath.DmgOverDist(
                damage,
                Vector3.Distance(burst, hit.point),
                radius,
                plate.Armor,
                1f,
                DamageRandom());

            var index = tanks.IndexOf(tank);
            if (index < 0)
            {
                tanks.Add(tank);
                amounts.Add(dmg);
                return;
            }

            var previous = amounts[index];
            var takeIt = returnThick ? dmg < previous : dmg > previous;
            if (takeIt)
            {
                amounts[index] = dmg;
            }
        }

        private DamageInfo Damage(BattleTank tank, Vector3 point)
        {
            if (tank == null)
            {
                Despawn();
                return null;
            }

            var dinfo = BuildDamage(point);
            dinfo.Dmg = GunMath.DamageRoll(
                Shell != null ? Shell.Damage : 0f,
                NormalRoll(DamageDeviationNow()), DamageDeviationNow());
            PlayImpact(ShellSoundKind.Pen, point);
            SpeakImpact(CrewMessage.ArmorPierced);
            tank.ReceiveDamage(dinfo);
            var uprising = Owner == null ? null : Owner.GetComponent<UprisingTank>();
            if (uprising != null && dinfo.Dmg > 0) uprising.Penetration(tank, _uprisingLife, _uprisingChainBase);
            return dinfo;
        }

        private void Block(BattleTank tank, Vector3 point)
        {
            if (tank == null)
            {
                return;
            }

            // A block still reports what the shell was worth, which is what the
            // source puts on the blocked-damage marker.
            var dinfo = BuildDamage(point);
            dinfo.Dmg = 0;
            PlayImpact(ShellSoundKind.Bounce, point);
            SpeakImpact(CrewMessage.ArmorNotPierce);
            tank.ReceiveDamage(dinfo);
        }

        /// <summary>
        /// projectile.gd::_play_sound: the shell report picked by the calibre
        /// of the gun that fired it, thrown at the impact point and throttled
        /// to one report every 0.3 s so a burst does not stack.
        /// </summary>
        private void PlayImpact(ShellSoundKind kind, Vector3 point)
        {
            SpawnImpactFx(kind, point);
            var audio = BlitzBootstrap.Current != null
                ? BlitzBootstrap.Current.Audio : null;
            if (audio == null)
            {
                return;
            }

            var now = Time.time;
            if (now < _nextImpactSound)
            {
                return;
            }

            _nextImpactSound = now + TankSoundTuning.ShellSoundInterval;

            // War of Ducks: the WarDuck's own 4.8 impact family replaces
            // the calibre-sized shell reports.
            if (WorldOfDucksBattle.Active)
            {
                WorldOfDucksBattle.ImpactSound(this, kind, point);
                return;
            }

            // ModuleInfo.ShotSound keeps the raw source int, so the fallback
            // is cast the same way TankSoundManager reads the gun sound.
            var gun = (ShootSound)(Owner != null && Owner.GunInfo != null
                ? Owner.GunInfo.ShotSound
                : (int)ShootSound.ShotAuto);
            var type = Shell != null ? Shell.Type : ShellType.Ap;

            // hits_tow: a rocket that lands has its own report, one per
            // warhead, instead of the calibre-sized shell impact.
            if (Guided && kind == ShellSoundKind.Pen)
            {
                var rocket = audio.Sounds.Sound(
                    type == ShellType.Heat
                        ? TankSoundTable.AtgmPenHeatSet
                        : TankSoundTable.AtgmPenExplosiveSet);
                if (rocket.HasStreams)
                {
                    audio.PlayPool3D(rocket, point);
                    return;
                }
            }

            audio.PlayPool3D(
                audio.Sounds.Shell(type).Pool(kind, gun), point);
        }

        /// <summary>
        /// The particle half of <c>play_sound</c>: <c>shell_type_to_eff_obj</c>
        /// for an impact, <c>shell_type_to_eff_pen</c> for everything that met
        /// armour, scaled to <c>caliber * 0.05</c> at the shell and turned to
        /// the last hit normal. Rockets keep their own explosion.
        /// </summary>
        private void SpawnImpactFx(ShellSoundKind kind, Vector3 point)
        {
            if (WorldOfDucksBattle.Active)
            {
                // The WarDuck shells carry their own 4.8 hit scenes.
                WorldOfDucksBattle.ImpactFx(this, kind, point);
                return;
            }

            if (Guided || !GodotParticleEffect.Enabled)
            {
                return;
            }

            var manifest = CombatFxManifest.Shared;
            if (manifest == null)
            {
                return;
            }

            var type = Shell != null ? Shell.Type : ShellType.Ap;
            var scene = kind == ShellSoundKind.Impact
                ? manifest.ObjectScene(type)
                : manifest.PenetrationScene(type);
            var effect = GodotParticleEffect.Create(scene, "impact_effect");
            if (effect == null)
            {
                return;
            }

            // particle.scale = Vector3.ONE * caliber * 0.05 keeps the root's rotation.
            var scale = (Shell != null ? Shell.Caliber : 0) * manifest.ImpactScaleFactor;
            var root = scene.Root;
            effect.Root = new GodotXform(
                root.X.normalized * scale,
                root.Y.normalized * scale,
                root.Z.normalized * scale,
                GodotFxMath.Mirror(point));
            var owner = Owner;
            effect.Concealed = () => owner != null && owner.ConcealedFromPlayer;
            if (_hasHitNormal)
            {
                effect.Play(true, GodotFxMath.Mirror(_lastHitNormal));
            }
            else
            {
                effect.Play(true);
            }
        }

        /// <summary>
        /// The crew only comments on what the local tank's own shells did.
        /// </summary>
        private void SpeakImpact(CrewMessage message)
        {
            var bootstrap = BlitzBootstrap.Current;
            if (bootstrap == null || bootstrap.Audio == null
                || bootstrap.Battle == null)
            {
                return;
            }

            if (Owner == null || Owner != bootstrap.Battle.Player)
            {
                return;
            }

            bootstrap.Audio.PlayCrewMessage(message);
        }

        private DamageInfo BuildDamage(Vector3 point)
        {
            return new DamageInfo
            {
                Attacker = Owner,
                AttackerTank = Owner,
                DmgBase = Shell != null ? (int)Shell.Damage : 0,
                DmgType = DamageType.Shell,
                Shell = Shell != null ? Shell.Type : ShellType.Ap,
                ShotId = this.ShotId,
                BigBossDamageFactor = _bigBossShot.Damage,
                BigBossPoisonPercent = _bigBossShot.Poison,
                BigBossPoisonTicks = _bigBossShot.Ticks,
                BigBossPoisonDelay = _bigBossShot.Delay,
                BigBossLifeSerial = _bigBossShot.Life,
                Pos = new Vec3(point.x, point.y, point.z),
            };
        }

        /// <summary>
        /// <c>get_destroyed_idiot(hit)</c>, which snaps the shell onto the
        /// surface it met before it disappears. The shell moves a whole tick
        /// before the segment it covered is traced, so at 442 m/s it is already
        /// nine metres past the tank when the hit resolves. Without this the
        /// tracer is drawn all the way out to that point and every shot looks
        /// like it flew straight through the target it just damaged.
        /// </summary>
        private void Despawn(Vector3 point)
        {
            if (!Alive)
            {
                return;
            }

            if (!_uprisingImpact)
            {
                _uprisingImpact = true;
                var uprising = Owner == null ? null : Owner.GetComponent<UprisingTank>();
                if (uprising != null) uprising.Impact(point, _uprisingLife, _uprisingSplash);
            }
            transform.position = point;
            if (AtgmVisual != null) { AtgmVisual.BeginExplosion(point); }
            Despawn();
        }

        private void Despawn()
        {
            if (!Alive)
            {
                return;
            }

            if (Trail != null)
            {
                Trail.CompleteAtCurrentPosition();
            }

            if (Tracer != null)
            {
                Tracer.Follow(transform.position);
                Tracer.Release();
            }

            if (Sprite != null) { Sprite.SetShown(false); }

            // Range expiry is not a collision: do not invent an impact burst.
            if (AtgmVisual != null && !AtgmVisual.Exploding) { AtgmVisual.Cancel(); }
            StopFlightSound();
            _liveGuided.Remove(this);
            Alive = false;
            if (Application.isPlaying) Destroy(gameObject);
            else DestroyImmediate(gameObject);
        }
    }
}
