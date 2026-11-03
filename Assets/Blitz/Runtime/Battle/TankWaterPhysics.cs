using System;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// A tank in the map's water. The hull box is sampled as a 3x3 grid of
    /// columns against <see cref="BattleWater.Active"/>; each wet column lends
    /// buoyancy at its own submerged centre, so a hull nosing into a river
    /// pitches and rolls with the water line instead of being lifted as a
    /// whole. Quadratic drag along the hull axes, angular damping and a lower
    /// drive limit make fording slow and heavy.
    ///
    /// Once the water closes over the hull roof the Blitz 4.8.0 drowning
    /// countdown runs; the host destroys the tank when it reaches zero, and a
    /// gun whose muzzle is under water refuses to fire
    /// (<c>cant_shoot_gun_under_water</c>). Network puppets only evaluate the
    /// state for the HUD: their pose comes from the host, and so does their
    /// death.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TankWaterPhysics : MonoBehaviour
    {
        private const int Columns = 3;
        private const float ShotNoticeInterval = 1.5f;

        private BattleTank _tank;
        private Rigidbody _rb;
        private BattleWater _waterOverride;
        private bool _hasOverride;
        private float _nextShotNotice;
        private DavaFx _wave;
        private float _lastHorizontalSpeed;

        /// <summary>Raised on the tick the hull first touches water.</summary>
        public event Action<TankWaterPhysics> Entered;

        /// <summary>Raised once when the countdown destroys the tank.</summary>
        public event Action<TankWaterPhysics> Drowned;

        /// <summary>Raised (throttled) when the trigger was pulled with the muzzle under water.</summary>
        public event Action<TankWaterPhysics> ShotBlocked;

        /// <summary>Any tank's drowning or blocked-shot notice, for the HUD.</summary>
        public static event Action<TankWaterPhysics> AnyDrowned;
        public static event Action<TankWaterPhysics> AnyShotBlocked;

        public BattleTank Tank { get { return _tank; } }

        public DrowningTimer Timer { get; private set; } = new DrowningTimer();

        /// <summary>Mean hull submersion, 0 dry to 1 roof under water.</summary>
        public float Submersion { get; private set; }

        /// <summary>Water above the track bottom at the hull centre, in metres.</summary>
        public float Depth { get; private set; }

        public bool InWater { get; private set; }

        /// <summary>Water at the hull centre as a share of the flood line: 0 dry, 1 roof under.</summary>
        public float Level { get; private set; }

        /// <summary>The water stands above the hull roof: the countdown runs.</summary>
        public bool Flooded { get; private set; }

        public bool GunUnderWater { get; private set; }

        /// <summary>Whether the countdown should be on screen.</summary>
        public bool TimerVisible { get { return Timer.Running && !Timer.Expired && _tank != null && !_tank.Dead; } }

        /// <summary>
        /// Whether the HUD shows the tank as being in water: deep enough to
        /// reach <see cref="WaterRules.IndicatorLevel"/> of the flood line,
        /// or already counting down.
        /// </summary>
        public bool IndicatorVisible
        {
            get
            {
                if (_tank == null || _tank.Dead) { return false; }
                return TimerVisible || (InWater && Level >= WaterRules.IndicatorLevel);
            }
        }

        /// <summary>Last buoyancy acceleration applied, for tests and diagnostics.</summary>
        public float LastBuoyancy { get; private set; }

        public int SplashCount { get; private set; }

        public void Setup(BattleTank tank, Rigidbody body)
        {
            _tank = tank;
            _rb = body;
            ResetState();
        }

        /// <summary>Pins the field this tank samples; tests use it to run without a map.</summary>
        public void UseWater(BattleWater water)
        {
            _waterOverride = water;
            _hasOverride = true;
        }

        public void ResetState()
        {
            Timer.Reset();
            Submersion = Depth = Level = LastBuoyancy = 0f;
            InWater = Flooded = GunUnderWater = false;
            StopWave();
            if (_tank != null && _tank.Suspension != null) { _tank.Suspension.EnvironmentSpeedFactor = 1f; }
        }

        /// <summary>
        /// Called by the gun when the trigger is pulled; true blocks the shot.
        /// </summary>
        public bool BlocksShot()
        {
            if (!GunUnderWater || _tank == null || _tank.Dead) { return false; }
            if (Time.time >= _nextShotNotice)
            {
                _nextShotNotice = Time.time + ShotNoticeInterval;
                ShotBlocked?.Invoke(this);
                AnyShotBlocked?.Invoke(this);
            }

            return true;
        }

        private BattleWater Water { get { return _hasOverride ? _waterOverride : BattleWater.Active; } }

        private void FixedUpdate()
        {
            Step(Time.fixedDeltaTime);
        }

        /// <summary>One physics tick; public so tests can drive it deterministically.</summary>
        public void Step(float delta)
        {
            if (_tank == null || _rb == null) { return; }
            var water = Water;
            if (water == null || _tank.Dead)
            {
                if (InWater || Flooded || Timer.Running) { ResetState(); }
                return;
            }

            var hull = _tank.Hull;
            var bottom = hull.TracksBottom;
            var top = Mathf.Max(hull.Height, bottom + 0.5f);
            var halfWidth = Mathf.Max(0.5f, hull.Width * 0.5f);
            var halfLength = Mathf.Max(0.5f, hull.Length * 0.5f);
            var t = transform;
            var physical = !_rb.isKinematic && !_tank.NetPuppet;
            var gravity = Physics.gravity.magnitude;
            var columnShare = 1f / (Columns * Columns);
            var total = 0f;
            var buoyancy = 0f;
            for (var ix = 0; ix < Columns; ix++)
            {
                for (var iz = 0; iz < Columns; iz++)
                {
                    var lx = Mathf.Lerp(-halfWidth, halfWidth, (ix + 0.5f) / Columns);
                    var lz = Mathf.Lerp(-halfLength, halfLength, (iz + 0.5f) / Columns);
                    var low = t.TransformPoint(new Vector3(lx, bottom, lz));
                    var high = t.TransformPoint(new Vector3(lx, top, lz));
                    float surface;
                    if (!water.TrySample(low, out surface)) { continue; }
                    var sub = WaterRules.Submersion(Mathf.Min(low.y, high.y), Mathf.Max(low.y, high.y), surface);
                    if (sub <= 0f) { continue; }
                    total += sub;
                    var lift = WaterRules.BuoyancyAcceleration(sub, gravity) * columnShare;
                    buoyancy += lift;
                    if (physical)
                    {
                        // Centre of the wet part of the column.
                        var centre = Vector3.Lerp(low, high, sub * 0.5f);
                        _rb.AddForceAtPosition(Vector3.up * lift * _rb.mass, centre, ForceMode.Force);
                    }
                }
            }

            Submersion = total * columnShare;
            LastBuoyancy = buoyancy;

            var centreLow = t.TransformPoint(new Vector3(0f, bottom, 0f));
            var centreTop = t.TransformPoint(new Vector3(0f, top, 0f));
            float centreSurface;
            var hasCentre = water.TrySample(centreLow, out centreSurface);
            Depth = hasCentre ? Mathf.Max(0f, centreSurface - centreLow.y) : 0f;
            Level = WaterRules.FloodLevel(Depth, Mathf.Max(0.05f, centreTop.y - centreLow.y));
            var wasInWater = InWater;
            InWater = Depth > WaterRules.ContactDepth || Submersion > 0f;
            Flooded = hasCentre && centreSurface > centreTop.y;
            GunUnderWater = MuzzleUnderWater(water);

            if (physical && Submersion > 0f) { ApplyDrag(delta); }
            if (_tank.Suspension != null) { _tank.Suspension.EnvironmentSpeedFactor = WaterRules.SpeedFactor(Submersion); }

            var velocity = _rb.isKinematic ? Vector3.zero : _rb.linearVelocity;
            if (InWater && !wasInWater)
            {
                Entered?.Invoke(this);
                var speed = Mathf.Max(velocity.magnitude, _lastHorizontalSpeed);
                var level = WaterRules.SplashLevel(speed);
                if (level > 0 && hasCentre) { Splash(level, new Vector3(centreLow.x, centreSurface, centreLow.z)); }
            }

            _lastHorizontalSpeed = new Vector3(velocity.x, 0f, velocity.z).magnitude;
            UpdateWave(water, hull, hasCentre ? centreSurface : 0f);

            if (Timer.Tick(Flooded, delta)) { Drown(); }
        }

        private bool MuzzleUnderWater(BattleWater water)
        {
            var turret = _tank.Turret;
            var muzzle = turret != null ? (turret.Muzzle != null ? turret.Muzzle : turret.GunPivot) : null;
            if (muzzle == null) { return false; }
            float surface;
            return water.TrySample(muzzle.position, out surface) && muzzle.position.y < surface;
        }

        private void ApplyDrag(float delta)
        {
            var t = transform;
            var sub = Submersion;
            var v = _rb.linearVelocity;
            var forward = Vector3.Dot(v, t.forward);
            var side = Vector3.Dot(v, t.right);
            var up = Vector3.Dot(v, t.up);
            var accel = t.forward * WaterRules.DragAcceleration(forward, WaterRules.ForwardDragGain, sub, delta)
                + t.right * WaterRules.DragAcceleration(side, WaterRules.SideDragGain, sub, delta)
                + t.up * WaterRules.DragAcceleration(up, WaterRules.VerticalDragGain, sub, delta);
            _rb.AddForce(accel, ForceMode.Acceleration);

            var w = _rb.angularVelocity;
            var damping = Mathf.Clamp01(WaterRules.AngularDrag(sub) * delta);
            if (w.sqrMagnitude > 1e-8f) { _rb.AddTorque(-w * damping / Mathf.Max(delta, 1e-4f), ForceMode.Acceleration); }
        }

        private void Drown()
        {
            Drowned?.Invoke(this);
            AnyDrowned?.Invoke(this);
            // The host alone decides deaths; a client sees it replicated.
            if (BattleNetHooks.PuppetWorld || _tank.Health == null || _tank.Health.Dead) { return; }
            var hp = Mathf.Max(1, _tank.Health.Health);
            _tank.Health.ReceiveDamage(new DamageInfo
            {
                Dmg = hp, DmgBase = hp, DmgType = DamageType.Drowning, SuppressFeedback = true,
            });
        }

        private void Splash(int level, Vector3 point)
        {
            SplashCount++;
            if (!EffectsAllowed) { return; }
            try
            {
                var fx = DavaFx.Create480(null, level >= 2 ? "water_tank_splash" : "water_tank_splash_low", 3f);
                fx.transform.position = point;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Blitz] water splash unavailable: " + e.Message);
            }
        }

        /// <summary><c>chassis_effects.yaml</c> water row: the bow wave while fording.</summary>
        private void UpdateWave(BattleWater water, TankSuspension.HullGeometry hull, float surface)
        {
            var speed = _lastHorizontalSpeed;
            var wanted = EffectsAllowed && InWater && !Flooded && speed > WaterRules.WaveSpeed;
            if (!wanted) { StopWave(); return; }
            var bow = transform.TransformPoint(new Vector3(0f, 0f, hull.Length * 0.5f));
            float bowSurface;
            if (!water.TrySample(bow, out bowSurface)) { bowSurface = surface; }
            if (_wave == null)
            {
                var name = hull.Length < 5.5f ? "wave_water_s" : hull.Length < 7f ? "wave_water_m" : "wave_water_l";
                try { _wave = DavaFx.Create480(null, name, 1f, true); }
                catch (Exception e)
                {
                    Debug.LogWarning("[Blitz] water wave unavailable: " + e.Message);
                    _waveFailed = true;
                    return;
                }
            }

            _wave.transform.SetPositionAndRotation(
                new Vector3(bow.x, bowSurface, bow.z),
                Quaternion.LookRotation(Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized + Vector3.forward * 1e-4f));
        }

        private bool _waveFailed;

        private bool EffectsAllowed { get { return Application.isPlaying && !_waveFailed; } }

        private void StopWave()
        {
            if (_wave == null) { return; }
            _wave.Stop();
            _wave = null;
        }

        private void OnDisable()
        {
            StopWave();
        }
    }
}
