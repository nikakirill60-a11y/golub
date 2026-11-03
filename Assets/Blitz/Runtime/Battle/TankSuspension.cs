using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Telemetry;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>Raycast tracked suspension using imported hull dimensions and
    /// vehicle-card power/speed. Mass-scaled spring/damper and load-limited
    /// drivetrain forces replace the donor's frame-dependent velocity edits.
    /// The original axis mapping and game-mode interfaces remain compatible.</summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class TankSuspension : MonoBehaviour
    {
        /// <summary><c>project.godot</c> uses <c>3d/default_gravity = 10.0</c>, not 9.81.</summary>
        public const float SourceGravity = 10f;

        /// <summary>Godot runs physics at 60 Hz; Unity defaults to 50.</summary>
        public const float SourcePhysicsDelta = 1f / 60f;

        /// <summary><c>Global.GRAVITY_MODE_HIT_STAGGER_MAX</c>.</summary>
        public const float HitStaggerMax = 0.8f;

        // Exported values of the Godot component. Setup overwrites the ones the
        // tank cards drive.
        [System.NonSerialized]
        public float EnginePower = SuspensionTuning.DefaultEnginePower;
        [System.NonSerialized]
        public float MaxSpeedForward = SuspensionTuning.DefaultMaxSpeedForward;
        [System.NonSerialized]
        public float MaxSpeedBack = SuspensionTuning.DefaultMaxSpeedBack;
        [System.NonSerialized]
        public float RotationSpeed = SuspensionTuning.DefaultRotationSpeed;
        [System.NonSerialized]
        public float MaxAngle = SuspensionTuning.MaxAngle;
        [System.NonSerialized]
        public float MaxAngleFriction = SuspensionTuning.MaxAngleFriction;
        [System.NonSerialized]
        public float FrictionForward = SuspensionTuning.FrictionForward;
        [System.NonSerialized]
        public float FrictionForwardThreshold = SuspensionTuning.FrictionForwardThreshold;
        [System.NonSerialized]
        public float FrictionRight = SuspensionTuning.FrictionRight;
        [System.NonSerialized]
        public float FrictionRightThreshold = SuspensionTuning.FrictionRightThreshold;
        [System.NonSerialized]
        public float WheelOffsetY = SuspensionTuning.WheelOffsetY;
        [System.NonSerialized]
        public float WheelLength = SuspensionTuning.WheelLength;

        /// <summary>Ray-origin to visible-track-bottom distance at rest.</summary>
        [System.NonSerialized]
        public float RideDrop = SuspensionTuning.WheelLength - WheelLengthMargin;

        /// <summary>Static spring compression under a full, even load.</summary>
        [System.NonSerialized]
        public float SpringSag = SuspensionTuning.WheelLength * .22f;

        /// <summary>
        /// Exported <c>force_offset.y</c>. <c>setup_hull_phys</c> overwrites it
        /// with <c>-hull_height * 0.15</c>, so the drive force pushes below the
        /// hull centre and the tank squats as it pulls away.
        /// </summary>
        [System.NonSerialized]
        public float DriveForceOffsetY = SuspensionTuning.DriveForceOffsetY;
        [System.NonSerialized]
        public float HullLength = SuspensionTuning.HullLength;
        [System.NonSerialized]
        public float DistanceSides = SuspensionTuning.DistanceSides;
        [System.NonSerialized]
        public float AngularDampCoeff = SuspensionTuning.AngularDampCoeff;
        [System.NonSerialized]
        public float AngularDampThreshold = SuspensionTuning.AngularDampThreshold;
        [System.NonSerialized]
        public int WheelCount = SuspensionTuning.WheelCount;

        /// <summary><c>base.hull_width</c>, one until the hull mesh is measured.</summary>
        [System.NonSerialized]
        public float HullWidth = 1f;

        /// <summary><c>base.blocked</c>: no drive force, no steering.</summary>
        [System.NonSerialized]
        public bool Blocked;

        /// <summary>
        /// <c>air_control_enabled</c>. Switched on only by the gravity game mode,
        /// so a standard battle never leaves the ground branch.
        /// </summary>
        [System.NonSerialized]
        public bool AirControlEnabled;

        /// <summary>
        /// Godot casts the wheel rays with <c>collision_mask = 1 + 2</c>, which is
        /// the ground plus other tanks. The battle builder narrows this mask to
        /// the matching Unity layers.
        /// </summary>
        [System.NonSerialized]
        public LayerMask RaycastMask = ~0;

        /// <summary><c>accel</c>: the hull starts rolling from a standstill.</summary>
        public event Action Accel;

        /// <summary><c>deccel</c>: the driver let go and the hull is coasting down.</summary>
        public event Action Deccel;

        private static int _wheelSimulationRequests;
        private static float _wheelQueueTick = float.NegativeInfinity;

        private readonly DriveCommandQueue _queue = new DriveCommandQueue();
        private readonly List<Vector3> _wheelHitNormals = new List<Vector3>();
        private readonly RaycastHit[] _rayHits = new RaycastHit[8];
        // Main thread only: the hulls that take part in the shared wheel batch.
        private static readonly List<TankSuspension> Active = new List<TankSuspension>();
        private static readonly RaycastBatch SharedWheelRays = new RaycastBatch(8);
        private static float _sharedCastTick = float.NegativeInfinity;
        private readonly RaycastBatch _wheelBatch = new RaycastBatch(8);
        private RaycastBatch _castRays;
        private Vector3[] _wheelOrigin = new Vector3[0];
        private float _castTick = float.NegativeInfinity;
        private Vector3 _castPosition;
        private Quaternion _castRotation;
        private int _castFirstRay;

        private Rigidbody _rb;
        private Vector3[] _wheelLocal = new Vector3[0];
        private float[] _loadShare = new float[0];
        private Vector3 _loadShareCentre = new Vector3(float.NaN, 0, 0);
        private bool _setupCompleted;

        private float _distanceWheels = 1f;

        private float _speed;
        private float _speedMs;
        private int _wheelHits;
        private bool _onTank;
        private bool _brake;
        private float _lastAxis;
        private float _lastRAxis;
        private float _rotTicks;
        private float _staggerUntil;
        private float _pitchDegrees;
        private float _yawDegrees;
        private float _rollDegrees;

        private bool _airDampingActive;
        private float _groundLinearDamping;

        /// <summary>Applied forward axis, <c>fw_axis</c>.</summary>
        public float ForwardAxis { get { return _netMirror ? _netForward : _queue.ForwardAxis; } }

        /// <summary>Applied steering axis, <c>r_axis</c>.</summary>
        public float RightAxis { get { return _netMirror ? _netRight : _queue.RightAxis; } }

        private bool _netMirror;
        private float _netForward;
        private float _netRight;

        /// <summary>
        /// Network puppet: this component is disabled and only mirrors the
        /// motion the host simulated, so the tracks, the wheels, the ground
        /// marks and the speedometer read the same values as on the host.
        /// </summary>
        public void ApplyNetMotion(float forwardAxis, float rightAxis, float speedMs, int wheelHits, bool onTank)
        {
            _netMirror = true;
            _netForward = Mathf.Clamp(forwardAxis, -1f, 1f);
            _netRight = Mathf.Clamp(rightAxis, -1f, 1f);
            _speedMs = speedMs;
            _speed = speedMs * 3.6f;
            _wheelHits = Mathf.Max(0, wheelHits);
            _onTank = onTank;
        }

        /// <summary>Back to real simulation, e.g. after a host migration.</summary>
        public void ClearNetMotion()
        {
            _netMirror = false;
        }

        /// <summary><c>speed</c>, in km/h, signed along the hull forward axis.</summary>
        public float Speed { get { return _speed; } }

        /// <summary>Gravity mode shows horizontal travel speed while airborne.</summary>
        public float DisplaySpeed
        {
            get
            {
                return AirControlEnabled && _wheelHits <= 0 && !RockRecoveryActive
                    ? HorizontalDisplaySpeed(_rb != null ? _rb.linearVelocity : Vector3.zero, _speed)
                    : _speed;
            }
        }

        /// <summary><c>speed_ms</c>, in metres per second.</summary>
        public float SpeedMs { get { return _speedMs; } }

        public float DriveForwardLimitMultiplier { get; private set; } = 1f;
        public float DriveBackwardLimitMultiplier { get; private set; } = 1f;

        /// <summary>
        /// Speed and turn-rate multiplier of the terrain medium, written by
        /// <see cref="TankWaterPhysics"/> while the hull fords water. It is kept
        /// apart from the track-crash caps so neither owner overwrites the other.
        /// </summary>
        public float EnvironmentSpeedFactor
        {
            get { return _environmentSpeedFactor; }
            set { _environmentSpeedFactor = Mathf.Clamp01(value); }
        }

        private float _environmentSpeedFactor = 1f;
        public float LeftTrackForwardLimit { get; private set; } = 1f;
        public float LeftTrackBackwardLimit { get; private set; } = 1f;
        public float RightTrackForwardLimit { get; private set; } = 1f;
        public float RightTrackBackwardLimit { get; private set; } = 1f;
        private bool _sideDriveCaps;
        private bool _sourceLeftOnNegativeX = true;
        private float _trackHalfWidth = 1f;

        /// <summary><c>wheel_hits</c> of the last tick, out of twice the wheel count.</summary>
        public int WheelHits { get { return _wheelHits; } }

        /// <summary><c>any_hit()</c>: at least one wheel is on the ground.</summary>
        /// <summary>Limited drive while the belly rests on a rock between the tracks.</summary>
        public bool RockRecoveryActive { get; private set; }
        private Collider _bellyRock;
        private Vector3 _bellyNormal = Vector3.up;
        private float _bellyContactUntil, _bellyDriveTime;
        public const float RockRecoverySupport = .25f;

        public bool AnyHit { get { return _wheelHits > 0; } }

        /// <summary>Deepest visible-track penetration below the supporting surface
        /// in the latest wheel simulation, in metres (negative = clearance).</summary>
        public float TrackPenetration { get; private set; }

        /// <summary>Mean visible-track penetration over the supporting wheel rays.</summary>
        public float MeanTrackPenetration { get { return _wheelHits > 0 ? _penetrationSum / _wheelHits : 0f; } }
        private float _penetrationSum;

        /// <summary>Mean terrain support normal of the latest wheel simulation.</summary>
        public Vector3 GroundNormal { get; private set; } = Vector3.up;

        /// <summary><c>brake</c> of the last tick.</summary>
        public bool Brake { get { return _brake; } }

        /// <summary><c>on_tank</c>: the wheels are resting on another tank.</summary>
        public bool OnTank { get { return _onTank; } }

        /// <summary>Hull pitch in degrees, in the Godot sense (<c>rotation_degrees.x</c>).</summary>
        public float PitchDegrees { get { return _pitchDegrees; } }

        /// <summary>Hull yaw in degrees, in the Godot sense (<c>rotation_degrees.y</c>).</summary>
        public float YawDegrees { get { return _yawDegrees; } }

        /// <summary>Hull roll in degrees, in the Godot sense (<c>rotation_degrees.z</c>).</summary>
        public float RollDegrees { get { return _rollDegrees; } }

        /// <summary>Godot <c>basis.z</c>, which points backwards along the hull.</summary>
        public Vector3 SourceForward { get { return -transform.forward; } }

        /// <summary>
        /// Aligns the Unity physics world with the Godot project settings. The
        /// battle bootstrap calls this before the first tank exists.
        /// </summary>
        public static void ApplySourcePhysicsDefaults()
        {
            Physics.gravity = new Vector3(0f, -SourceGravity, 0f);
            Time.fixedDeltaTime = SourcePhysicsDelta;
        }

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            // center_of_mass_mode = CUSTOM with the default (0, 0, 0), so every
            // apply_force offset is measured from the hull origin.
            _rb.centerOfMass = Vector3.zero;
            // Bound extreme collision-induced rotation without freezing pitch/roll.
            _rb.maxAngularVelocity = 5f;
        }

        /// <summary>
        /// Hull measurements that <c>base_tank.setup</c> takes off the assembled
        /// model instead of reading them from the scene. They overwrite the
        /// exported defaults, so a port that keeps the exports alone would drive
        /// every tank like the editor placeholder.
        /// </summary>
        public struct HullGeometry
        {
            /// <summary><c>hull_width</c>: X size of the physics hull bounds.</summary>
            public float Width;

            /// <summary><c>hull_len</c>: Z size of the physics hull bounds.</summary>
            public float Length;

            /// <summary><c>hull_height</c>: Y size plus the lowest hull vertex.</summary>
            public float Height;

            /// <summary><c>tracks_height</c>: Y size of one track's bounds.</summary>
            public float TracksHeight;

            /// <summary>Lowest visible track point in hull space (0 = hull origin).</summary>
            public float TracksBottom;
        }

        /// <summary>
        /// <c>base_tank.setup</c>: <c>force_offset.y = -hull_height * 0.15</c>.
        /// </summary>
        private const float DriveForceHeightFactor = 0.15f;

        /// <summary>
        /// <c>base_tank.setup</c>: <c>wheel_len = tracks_height + 0.05</c>.
        /// <c>setup_hull_phys</c> writes <c>+ 0.1</c>, but it runs earlier in the
        /// same function, before the hull is measured, and every suspension
        /// value it writes is overwritten further down, so the shorter ray is
        /// the one the game actually drives on.
        /// </summary>
        private const float WheelLengthMargin = 0.05f;

        /// <summary>
        /// <c>setup</c> plus the geometry half of <c>setup_hull_phys</c>:
        /// derives the spring coefficients from the hull weight, takes the
        /// engine and track numbers from the fitted cards, replaces the
        /// exported hull measurements with the ones measured off the model, and
        /// lays the wheel rays out along the hull.
        /// </summary>
        public void Setup(float hullWeight, float enginePower, float rotationSpeed, HullGeometry hull)
        {
            // Seating a tank rearms the shared wheel budget, so a deadline left
            // behind by an earlier battle cannot starve this one.
            ResetWheelQueue();

            EnginePower = enginePower;
            RotationSpeed = rotationSpeed;

            HullWidth = hull.Width;
            HullLength = hull.Length;
            // base_tank.gd: suspension.dist_sides = hull_width * 0.85
            DistanceSides = hull.Width * SuspensionTuning.HullSideFactor;
            DriveForceOffsetY = -hull.Height * DriveForceHeightFactor;
            WheelOffsetY = hull.TracksHeight;
            // At rest the ray must end exactly at the visible track bottom. The
            // spring keeps the tuned travel (22% of the source ray), so the ray
            // reaches that much further: unloaded tracks keep contact over the
            // same droop, while the rest pose no longer buries the track.
            RideDrop = Mathf.Max(.05f, WheelOffsetY - hull.TracksBottom);
            SpringSag = Mathf.Max(.04f, (RideDrop + WheelLengthMargin) * .22f);
            WheelLength = RideDrop + SpringSag;
            _distanceWheels = SuspensionTuning.DistanceWheelsFor(HullLength, WheelCount);

            _wheelLocal = new Vector3[WheelCount * 2];
            for (int s = 0; s < 2; s++)
            {
                int side = s == 0 ? -1 : 1;
                for (int i = 0; i < WheelCount; i++)
                {
                    int id = SuspensionTuning.WheelIndex(i, side, WheelCount);
                    Vec3 source = SuspensionTuning.WheelPosition(i, side, WheelCount, _distanceWheels, DistanceSides, WheelOffsetY);
                    Vec3 mirrored = GodotSpace.Point(source.X, source.Y, source.Z);
                    _wheelLocal[id] = new Vector3(mirrored.X, mirrored.Y, mirrored.Z);
                }
            }

            _setupCompleted = true;
        }

        /// <summary>Local wheel ray origins in Unity space, for tests and gizmos.</summary>
        public Vector3 WheelLocalPosition(int flatIndex)
        {
            if (flatIndex < 0 || flatIndex >= _wheelLocal.Length) { return Vector3.zero; }
            return _wheelLocal[flatIndex];
        }

        /// <summary><c>suspension.drive</c>: queues a command with the input delay.</summary>
        public void Enqueue(float forward, float right)
        {
            _queue.Request(forward, right, Time.time);
            // A sleeping high-centred body has no wheel force to wake it.
            // Wake on motor demand so contact callbacks can confirm support.
            if (_rb != null && (forward != 0f || right != 0f)) _rb.WakeUp();
        }

        /// <summary>
        /// <c>base_tank.drive</c> writes <c>suspension.fw_axis = 0</c> and
        /// <c>suspension.r_axis = 0</c> directly when the hull is blocked or the
        /// tracks are destroyed. It never touches <c>drive_commands</c>, so a
        /// command queued a moment earlier still lands afterwards; the pending
        /// entries are kept here for the same reason.
        /// </summary>
        public void StopDrive()
        {
            _queue.ClearAxes();
        }

        public void SetDriveSpeedCaps(float forwardMultiplier, float backwardMultiplier)
        {
            DriveForwardLimitMultiplier = Mathf.Clamp01(forwardMultiplier);
            DriveBackwardLimitMultiplier = Mathf.Clamp01(backwardMultiplier);
            _sideDriveCaps = false;
            LeftTrackForwardLimit = RightTrackForwardLimit = DriveForwardLimitMultiplier;
            LeftTrackBackwardLimit = RightTrackBackwardLimit = DriveBackwardLimitMultiplier;
        }

        public void SetDriveTrackCaps(float leftForward, float leftBackward, float rightForward, float rightBackward, float halfWidth, bool sourceLeftOnNegativeX = true)
        {
            SetDriveSpeedCaps(Mathf.Min(leftForward, rightForward), Mathf.Min(leftBackward, rightBackward));
            LeftTrackForwardLimit = Mathf.Clamp01(leftForward); LeftTrackBackwardLimit = Mathf.Clamp01(leftBackward);
            RightTrackForwardLimit = Mathf.Clamp01(rightForward); RightTrackBackwardLimit = Mathf.Clamp01(rightBackward);
            _trackHalfWidth = Mathf.Max(.1f, halfWidth); _sourceLeftOnNegativeX = sourceLeftOnNegativeX; _sideDriveCaps = true;
        }

        /// <summary>Differential yaw can never demand belt speeds outside their source limits.
        /// External impulse/downhill overspeed is not clamped; incompatible limits provide no motor yaw.</summary>
        public static float LimitTrackYaw(float desired, float forwardMs, float maxForwardMs, float maxBackwardMs,
            float halfWidth, float leftForward, float leftBackward, float rightForward, float rightBackward, bool sourceLeftOnNegativeX = true)
        {
            if (!sourceLeftOnNegativeX)
            { var forward = leftForward; var backward = leftBackward; leftForward = rightForward; leftBackward = rightBackward; rightForward = forward; rightBackward = backward; }
            halfWidth = Mathf.Max(.1f, halfWidth);
            var minimum = Mathf.Max((-maxBackwardMs * leftBackward - forwardMs) / halfWidth,
                (forwardMs - maxForwardMs * rightForward) / halfWidth);
            var maximum = Mathf.Min((maxForwardMs * leftForward - forwardMs) / halfWidth,
                (forwardMs + maxBackwardMs * rightBackward) / halfWidth);
            return minimum <= maximum ? Mathf.Clamp(desired, minimum, maximum) : 0f;
        }

        /// <summary><c>staggered()</c>: a shell just shoved the hull.</summary>
        public bool Staggered()
        {
            return Time.time < _staggerUntil;
        }

        /// <summary><c>stagger(seconds)</c>, capped at <see cref="HitStaggerMax"/>.</summary>
        public void Stagger(float seconds)
        {
            if (seconds <= 0f) { return; }
            float until = Time.time + Mathf.Min(seconds, HitStaggerMax);
            if (until > _staggerUntil) { _staggerUntil = until; }
        }

        /// <summary><c>max_angle_not_reached()</c>.</summary>
        public bool MaxAngleNotReached()
        {
            float baseAngle = SuspensionTuning.GetBaseAngle(_pitchDegrees, _rollDegrees, false, false, false);
            return Mathf.Abs(baseAngle) < MaxAngle;
        }

        private void OnCollisionStay(Collision collision)
        {
            var other = collision.collider;
            if (other == null || other.gameObject.layer != BattleLayers.Static ||
                other.attachedRigidbody != null || other.GetComponent<BattleRockSupport>() == null ||
                _rb == null || Vector3.Dot(transform.up, Vector3.up) < .6f) return;
            var normal = Vector3.zero;
            for (var i = 0; i < collision.contactCount; i++)
            {
                var contact = collision.GetContact(i);
                if (contact.normal.y < .6f || contact.point.y > _rb.worldCenterOfMass.y) continue;
                normal += contact.normal;
            }
            if (normal.sqrMagnitude < .01f) return;
            _bellyRock = other;
            _bellyNormal = normal.normalized;
            _bellyContactUntil = Time.fixedTime + Time.fixedDeltaTime * 2f;
        }

        private void OnCollisionExit(Collision collision)
        {
            if (collision.collider == _bellyRock) _bellyRock = null;
        }

        private void UpdateRockRecovery(float forward, float right, float delta)
        {
            var supported = _bellyRock != null && Time.fixedTime <= _bellyContactUntil;
            if (!supported || Blocked || (forward == 0f && right == 0f) ||
                _wheelHits > WheelCount * 2 * RockRecoverySupport)
            {
                _bellyDriveTime = 0f; RockRecoveryActive = false; return;
            }
            if (!RockRecoveryActive &&
                Vector3.ProjectOnPlane(_rb.linearVelocity, _bellyNormal).magnitude > BattleHullUnstick.StuckSpeed)
            {
                _bellyDriveTime = 0f; return;
            }
            _bellyDriveTime += delta;
            RockRecoveryActive = _bellyDriveTime >= BattleHullUnstick.StuckTime;
        }

        private void FixedUpdate()
        {
            using var slowScope = BlitzPerfMonitor.Slow("suspension.fixedUpdate");
            float delta = Time.fixedDeltaTime;
            float now = Time.time;

            _queue.Process(now);

            if (!_setupCompleted) { return; }
            if (!RequestWheel(Time.fixedTime)) { return; }

            GroundNormal = Vector3.up;
            TrackPenetration = float.NegativeInfinity; _penetrationSum = 0;
            _wheelHits = 0;
            _onTank = false;
            _wheelHitNormals.Clear();

            ReadSourceAngles();

            float fwAxis = _queue.ForwardAxis;
            float rAxis = _queue.RightAxis;

            // Invariant for all twelve wheels of one tick, exactly as in the source.
            Vector3 wheelForceUp = transform.up;
            if (fwAxis == 0f && _brake &&
                SuspensionTuning.GetBaseAngle(_pitchDegrees, _rollDegrees, false, false, true) < MaxAngleFriction)
            {
                wheelForceUp = Vector3.up;
            }

            if (_rb.centerOfMass != _loadShareCentre)
            {
                _loadShareCentre = _rb.centerOfMass;
                _loadShare = StaticLoadShares(_wheelLocal, _loadShareCentre);
            }

            CastAllWheels();
            using (BattlePerfMarkers.WheelForces.Auto())
            {
                SimulateSide(-1, wheelForceUp, delta);
                SimulateSide(1, wheelForceUp, delta);
            }

            using var driveScope = BattlePerfMarkers.SuspensionDrive.Auto();

            Vector3 bForward = SourceForward;
            _speedMs = Vector3.Dot(_rb.linearVelocity, bForward) * -1f;
            _speed = _speedMs * 3.6f;

            float axisCombined = SuspensionTuning.Clamp(Mathf.Abs(fwAxis) + Mathf.Abs(rAxis), 0f, 1f);

            if (_wheelHits > 0 && fwAxis > _lastAxis && _brake && Mathf.Abs(_speedMs) <= 0.01f)
            {
                if (Accel != null) { Accel(); }
            }

            float reached = SuspensionTuning.SpeedReachedMul(_speed, MaxSpeedForward, 1f, 1f);
            bool coasting = _lastAxis != axisCombined && axisCombined == 0f && reached > 0.33f;
            bool steeringDown = reached < 0.1f && rAxis != _lastRAxis && _rotTicks > 80f;
            if (coasting || steeringDown)
            {
                if (Deccel != null) { Deccel(); }
            }

            if (_wheelHits > 0)
            {
                _lastAxis = axisCombined;
                _lastRAxis = rAxis;
            }

            if (rAxis != 0f) { _rotTicks += 1f; } else { _rotTicks = 0f; }

            CheckOnBrake(fwAxis);
            UpdateRockRecovery(fwAxis, rAxis, delta);
            UpdateAirborneDamping();

            if (_wheelHits <= 0 && !RockRecoveryActive)
            {
                // air_control runs only in the gravity game mode; a standard
                // battle gives an airborne hull no control at all. The source
                // returns before it clears the normals, so neither does this.
                if (AirControlEnabled && _rb != null)
                {
                    _rb.angularVelocity = ApplyGravityModeAirControl(
                        _rb.angularVelocity, transform.up, transform.right,
                        fwAxis, rAxis, delta);
                }
                return;
            }

            // Continuous support fraction; only terrain supplies traction.
            float support = Mathf.Clamp01((float)_wheelHits / (WheelCount * 2));
            // Only a confirmed upward belly contact with a marked rock, after
            // sustained motor demand, can supply this small recovery load.
            // No lift, transform correction, wall climbing or airborne drive.
            if (RockRecoveryActive) support = Mathf.Max(support, RockRecoverySupport);
            var normal = Vector3.zero;
            foreach (var n in _wheelHitNormals) normal += n;
            normal = normal.sqrMagnitude > .001f ? normal.normalized :
                (RockRecoveryActive ? _bellyNormal : Vector3.up);
            GroundNormal = normal;
            var forward = Vector3.ProjectOnPlane(transform.forward, normal).normalized;
            var right = Vector3.Cross(normal, forward).normalized;
            var velocity = _rb.linearVelocity;
            float forwardSpeed = Vector3.Dot(velocity, forward);
            float sideSpeed = Vector3.Dot(velocity, right);
            float grip = .75f * SourceGravity * support;
            if (!Staggered())
            {
                // Load-limited tyre forces. PhysX integrates dt exactly once.
                float sideways = Mathf.Clamp(-sideSpeed / Mathf.Max(delta, .001f), -grip, grip);
                _rb.AddForce(right * sideways * _rb.mass, ForceMode.Force);
            }
            var longitudinal = DriveAcceleration(forwardSpeed, fwAxis, _rb.mass, EnginePower,
                MaxSpeedForward * DriveForwardLimitMultiplier * _environmentSpeedFactor,
                MaxSpeedBack * DriveBackwardLimitMultiplier * _environmentSpeedFactor,
                support, Blocked, delta,
                Vector3.Dot(Physics.gravity, forward), Staggered());
            ApplyForce(forward * longitudinal * _rb.mass, transform.up * DriveForceOffsetY);
            if (!Staggered())
            {
                float targetYaw = !Blocked ? RotationSpeed * Mathf.Deg2Rad * rAxis * SuspensionTuning.YawStickFactor * _environmentSpeedFactor : 0;
                if (_sideDriveCaps && !Blocked) targetYaw = LimitTrackYaw(targetYaw, forwardSpeed,
                    Mathf.Abs(MaxSpeedForward) / 3.6f, Mathf.Abs(MaxSpeedBack) / 3.6f, _trackHalfWidth,
                    LeftTrackForwardLimit, LeftTrackBackwardLimit, RightTrackForwardLimit, RightTrackBackwardLimit, _sourceLeftOnNegativeX);
                var angular = _rb.angularVelocity;
                float yaw = Vector3.Dot(angular, normal);
                float next = Mathf.MoveTowards(yaw, targetYaw, 2f * support * delta);
                _rb.angularVelocity = angular + normal * (next - yaw);
            }

            _wheelHitNormals.Clear();
        }

        /// <summary>Game-responsive drive without changing vehicle speed caps or
        /// mass. Counter-steering the drivetrain brakes first, even when rollback
        /// has exceeded the opposite direction's speed limit. Returns m/s².</summary>
        public static float DriveAcceleration(float speed, float axis, float mass, float horsepower,
            float maxForwardKmh, float maxBackKmh, float support, bool blocked, float delta,
            float gravityAlong = 0f, bool staggered = false)
        {
            if (mass <= 0 || delta <= 0) return 0;
            support = Mathf.Clamp01(support); axis = Mathf.Clamp(axis, -1f, 1f);
            float grip = .85f * SourceGravity * support;
            float rolling = (.18f + speed * speed * .002f) * support;
            float resistance = staggered ? 0 : -Mathf.Sign(speed) * Mathf.Min(
                blocked || axis == 0 ? grip * .65f : rolling, Mathf.Abs(speed) / delta);
            if (blocked || axis == 0) return resistance;
            float direction = Mathf.Sign(axis);
            // Only the requested direction's limit can inhibit powered thrust.
            float limit = (direction > 0 ? maxForwardKmh : Mathf.Abs(maxBackKmh)) / 3.6f;
            float headroom = Mathf.Max(0f, limit - direction * speed);
            // Mono may round the same nominal cap to either side of the last
            // float bit. Such a deficit must not keep full engine torque on.
            float drive = headroom > .0001f
                ? Mathf.Max(0, horsepower) * 745.7f * .82f * 1.25f / (mass * Mathf.Max(2f, Mathf.Abs(speed)))
                : 0f;
            if (!staggered && direction * speed < -.05f)
            {
                // Aim to stop opposing motion in 0.8 s, allowing for downhill
                // gravity. Load-limited forces still prevent instant reversals.
                float reversalBrake = Mathf.Abs(speed) / .8f + Mathf.Max(0, -gravityAlong * direction);
                drive = Mathf.Max(drive, reversalBrake);
            }
            float acceleration = Mathf.Clamp(
                resistance + direction * Mathf.Abs(axis) * Mathf.Min(drive, grip), -grip, grip);
            // Limit only powered acceleration over this integration step. Do
            // not clamp velocity: downhill gravity and external impacts remain
            // physical, and counter-steering still brakes opposing motion.
            return direction * acceleration > 0f
                ? direction * Mathf.Min(direction * acceleration, headroom / delta)
                : acceleration;
        }

        /// <summary><c>check_on_brake</c>.</summary>
        private void CheckOnBrake(float fwAxis)
        {
            float baseAngle = SuspensionTuning.GetBaseAngle(_pitchDegrees, _rollDegrees, false, false, false);
            _brake = SuspensionTuning.OnBrake(fwAxis, _speed, MaxSpeedForward, MaxSpeedBack, baseAngle, MaxAngle, Blocked, _wheelHits, WheelCount);
        }

        /// <summary>
        /// <c>base.rotation_degrees</c>. Unity's own Euler angles cannot stand in:
        /// they decompose in a different order, are unsigned, and describe the
        /// mirrored hull.
        /// </summary>
        private void ReadSourceAngles()
        {
            Vec3 right = new Vec3(transform.right.x, transform.right.y, transform.right.z);
            Vec3 up = new Vec3(transform.up.x, transform.up.y, transform.up.z);
            Vec3 forward = new Vec3(transform.forward.x, transform.forward.y, transform.forward.z);

            Vec3 sourceX;
            Vec3 sourceY;
            Vec3 sourceZ;
            GodotEuler.SourceBasis(right, up, forward, out sourceX, out sourceY, out sourceZ);
            GodotEuler.Yxz(sourceX, sourceY, sourceZ, out _pitchDegrees, out _yawDegrees, out _rollDegrees);
        }

        /// <summary><c>simulate_side</c>.</summary>
        private void SimulateSide(int side, Vector3 up, float delta)
        {
            for (int i = 0; i < WheelCount; i++)
            {
                int id = SuspensionTuning.WheelIndex(i, side, WheelCount);
                if (id < 0 || id >= _wheelLocal.Length) { continue; }

                Vector3 origin = _wheelOrigin[id];
                RaycastHit hit;
                Collider collider;
                Rigidbody body;
                if (!CastWheel(id, out hit, out collider, out body)) { continue; }

                SimulateWheel(origin, hit, collider, body, up, delta, id);
            }
        }

        /// <summary>
        /// Makes sure this tick's wheel rays are answered. The first hull to
        /// simulate in a physics step casts the rays of every set-up hull in
        /// one batch on the job workers. Forces only apply on the next
        /// simulation step and the fixed-step scripts do not move a hull's
        /// transform, so every ray is the one the hull would cast itself; a
        /// hull whose pose did change since, or that joined after the batch,
        /// casts its own rays.
        /// </summary>
        private void CastAllWheels()
        {
            float tick = Time.fixedTime;
            if (_sharedCastTick != tick)
            {
                _sharedCastTick = tick;
                SharedWheelRays.Clear();
                for (int i = 0; i < Active.Count; i++)
                {
                    Active[i].QueueWheels(SharedWheelRays, tick);
                }

                using (BattlePerfMarkers.WheelCast.Auto())
                {
                    SharedWheelRays.Run();
                }
            }

            if (_castTick == tick && transform.position == _castPosition && transform.rotation == _castRotation)
            {
                _castRays = SharedWheelRays;
                return;
            }

            using (BattlePerfMarkers.WheelCastOwn.Auto())
            {
                _wheelBatch.Clear();
                QueueWheels(_wheelBatch, tick);
                _wheelBatch.Run();
                _castRays = _wheelBatch;
            }
        }

        private void QueueWheels(RaycastBatch batch, float tick)
        {
            if (!_setupCompleted || _rb == null) { return; }
            if (_wheelOrigin.Length != _wheelLocal.Length)
            {
                _wheelOrigin = new Vector3[_wheelLocal.Length];
            }

            _castTick = tick;
            _castPosition = transform.position;
            _castRotation = transform.rotation;
            Vector3 down = -transform.up;
            for (int id = 0; id < _wheelLocal.Length; id++)
            {
                _wheelOrigin[id] = transform.TransformPoint(_wheelLocal[id]);
                int ray = batch.Add(_wheelOrigin[id], down, WheelLength, RaycastMask, QueryTriggerInteraction.Ignore);
                if (id == 0) { _castFirstRay = ray; }
            }
        }

        private void OnEnable()
        {
            if (!Active.Contains(this)) { Active.Add(this); }
        }

        private void OnDisable()
        {
            Active.Remove(this);
        }

        /// <summary>
        /// The Godot ray is a child of the hull with <c>exclude_parent</c> on, so
        /// it never reports the hull it belongs to. Unity has no such flag, hence
        /// the manual nearest-hit scan.
        /// </summary>
        private bool CastWheel(int id, out RaycastHit nearest, out Collider nearestCollider, out Rigidbody nearestBody)
        {
            nearest = default(RaycastHit);
            nearestCollider = null;
            nearestBody = null;
            int count = _castRays.Hits(_castFirstRay + id, _rayHits);
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                // Resolve the collider and its body once per hit; the wheel
                // reuses them instead of looking them up again.
                Collider collider = _rayHits[i].collider;
                Rigidbody body = collider != null ? collider.attachedRigidbody : null;
                if (body == _rb) { continue; }
                if (!found || _rayHits[i].distance < nearest.distance)
                {
                    nearest = _rayHits[i];
                    nearestCollider = collider;
                    nearestBody = body;
                    found = true;
                }
            }
            return found;
        }

        /// <summary><c>simulate_wheel</c>.</summary>
        private void SimulateWheel(Vector3 origin, RaycastHit hit, Collider collider, Rigidbody body, Vector3 up, float delta, int id)
        {
            Vector3 normal = hit.normal;
            if (Mathf.Abs(Vector3.Dot(normal, Vector3.up)) < SuspensionTuning.GroundNormalDotMin) { return; }
            if (collider == null) { return; }

            if (body != null && body.GetComponent<TankSuspension>() != null)
            {
                _onTank = true;
                return;
            }

            _wheelHits += 1;

            Vector3 offset = origin - _rb.worldCenterOfMass;
            // Spring/damper uses the complete installed mass, not hull-only
            // legacy coefficients. No dt belongs inside a ForceMode.Force load.
            TrackPenetration = Mathf.Max(TrackPenetration, RideDrop - hit.distance);
            _penetrationSum += RideDrop - hit.distance;
            float compression = Mathf.Max(0, WheelLength - hit.distance);
            float rayVelocity = Vector3.Dot(_rb.GetPointVelocity(origin), up);
            // Each wheel is rated for its static share of the weight, so a hull
            // whose centre of mass is off the wheel centre still rests level
            // with both track ends on the surface.
            float share = id >= 0 && id < _loadShare.Length ? _loadShare[id] : 1f;
            float spring = WheelSpringForce(_rb.mass * share, WheelCount * 2, SpringSag, compression, rayVelocity);
            ApplyForce(up * spring, offset);

            _wheelHitNormals.Add(normal);
        }

        /// <summary>
        /// Relative static load of each wheel (mean 1) for a hull resting level:
        /// the weight is spread linearly along the length and across the width
        /// so its moment about the wheel centre matches the centre of mass.
        /// Shares are kept within 0.35..1.65 of the mean so no wheel goes limp.
        /// </summary>
        public static float[] StaticLoadShares(Vector3[] wheels, Vector3 centreOfMass)
        {
            var shares = new float[wheels != null ? wheels.Length : 0];
            if (shares.Length == 0) return shares;
            var mean = Vector3.zero;
            foreach (var w in wheels) mean += w;
            mean /= wheels.Length;
            float zz = 0, xx = 0;
            foreach (var w in wheels) { zz += (w.z - mean.z) * (w.z - mean.z); xx += (w.x - mean.x) * (w.x - mean.x); }
            float n = wheels.Length, total = 0;
            for (int i = 0; i < shares.Length; i++)
            {
                float share = 1f;
                if (zz > 1e-6f) share += n * (centreOfMass.z - mean.z) * (wheels[i].z - mean.z) / zz;
                if (xx > 1e-6f) share += n * (centreOfMass.x - mean.x) * (wheels[i].x - mean.x) / xx;
                if (float.IsNaN(share) || float.IsInfinity(share)) share = 1f;
                shares[i] = Mathf.Clamp(share, .35f, 1.65f);
                total += shares[i];
            }
            for (int i = 0; i < shares.Length; i++) shares[i] *= n / total;
            return shares;
        }

        /// <summary>Visible-track depth below the surface at which the bump stop engages.</summary>
        public const float BumpStopDepth = .02f;

        /// <summary>Extra spring rate of the bump stop, as a multiple of the main rate.</summary>
        public const float BumpStopStiffness = 4f;

        /// <summary>
        /// Spring/damper load of one wheel ray, in newtons. Compression is
        /// measured from the ray end; <paramref name="sag"/> is the compression
        /// that carries an even share of the weight, which is where the ray
        /// ends at the visible track bottom. Once the track would sink more than
        /// <see cref="BumpStopDepth"/> into the surface a linear bump stop
        /// adds <see cref="BumpStopStiffness"/> times the main rate (partial
        /// support, landings, uneven centres of mass).
        /// </summary>
        public static float WheelSpringForce(float mass, int wheels, float sag, float compression, float rayVelocity)
        {
            if (mass <= 0 || wheels <= 0 || sag <= 0 || compression <= 0) return 0;
            float wheelMass = mass / wheels;
            float stiffness = wheelMass * SourceGravity / sag;
            float damping = 1.6f * Mathf.Sqrt(stiffness * wheelMass);
            float load = stiffness * compression;
            float bump = compression - sag - BumpStopDepth;
            // Linear and moderate on purpose: end wheels act on a small
            // rotational mass, and a steep or progressive stop becomes
            // numerically unstable at 60 Hz (tanks bounced and tumbled).
            if (bump > 0) load += stiffness * BumpStopStiffness * bump;
            return Mathf.Clamp(load - damping * rayVelocity, 0, wheelMass * SourceGravity * 8f);
        }

        /// <summary>
        /// World-oriented offset measured from the configured centre of mass.
        /// PhysX integrates the applied force over the fixed step once.
        /// </summary>
        private void ApplyForce(Vector3 force, Vector3 offset)
        {
            _rb.AddForceAtPosition(force, _rb.worldCenterOfMass + offset, ForceMode.Force);
        }

        /// <summary>Horizontal travel speed in km/h, for the airborne HUD readout.</summary>
        public static float HorizontalDisplaySpeed(Vector3 linearVelocity, float fallbackKmh = 0f)
        {
            var flat = new Vector3(linearVelocity.x, 0f, linearVelocity.z);
            return flat.sqrMagnitude > 0f ? flat.magnitude * 3.6f : fallbackKmh;
        }

        /// <summary>Gravity-mode airborne steering, ported from suspension_component.gd.</summary>
        public static Vector3 ApplyGravityModeAirControl(
            Vector3 angularVelocity,
            Vector3 hullUp,
            Vector3 hullRight,
            float forwardAxis,
            float rightAxis,
            float delta)
        {
            if (delta <= 0f || float.IsNaN(delta) || float.IsInfinity(delta))
            {
                return FiniteVector(angularVelocity) ? Vector3.ClampMagnitude(angularVelocity, GravityModeTank.AirAngularMax) : Vector3.zero;
            }

            if (!FiniteVector(hullUp) || hullUp.sqrMagnitude <= 0.0001f) hullUp = Vector3.up;
            else hullUp = hullUp.normalized;
            if (!FiniteVector(hullRight) || hullRight.sqrMagnitude <= 0.0001f) hullRight = Vector3.right;
            else hullRight = hullRight.normalized;

            var angVel = angularVelocity;
            var yaw = Vector3.Dot(angVel, Vector3.up);
            // The source writes -deg_to_rad(speed) * r_axis, but that sign is
            // Godot's: the port mirrors the world across Z, which flips every
            // yaw rate. Grounded steering already writes +RotationSpeed * rAxis
            // here, so the airborne target has to share that sign, or the hull
            // turns left on D the moment the tracks leave the ground.
            var yawTarget = Mathf.Deg2Rad * GravityModeTank.AirYawSpeed * rightAxis;
            angVel += Vector3.up * (yawTarget - yaw) * (1f - Mathf.Exp(-delta * GravityModeTank.AirYawAccel));

            var tilt = Vector3.Angle(hullUp, Vector3.up) * Mathf.Deg2Rad;
            var room = Mathf.Clamp01(1f - tilt / (GravityModeTank.AirPitchMax * Mathf.Deg2Rad));
            if (!Mathf.Approximately(forwardAxis, 0f))
            {
                angVel += hullRight * GravityModeTank.AirPitchAccel * forwardAxis * room * delta;
            }

            angVel += Vector3.Cross(hullUp, Vector3.up) * GravityModeTank.AirLevelAccel * delta;
            var tiltVelocity = angVel - Vector3.up * Vector3.Dot(angVel, Vector3.up);
            angVel -= tiltVelocity * Mathf.Clamp01(delta * GravityModeTank.AirAngularDamp);
            if (!FiniteVector(angVel)) angVel = Vector3.zero;
            return Vector3.ClampMagnitude(angVel, GravityModeTank.AirAngularMax);
        }

        private static bool FiniteVector(Vector3 value)
        {
            return !(float.IsNaN(value.x) || float.IsInfinity(value.x)
                || float.IsNaN(value.y) || float.IsInfinity(value.y)
                || float.IsNaN(value.z) || float.IsInfinity(value.z));
        }

        /// <summary>Clear the per-physics-step budget when a battle restarts.</summary>
        public static void ResetWheelQueue()
        {
            _wheelSimulationRequests = 0;
            _wheelQueueTick = float.NegativeInfinity;
            _sharedCastTick = float.NegativeInfinity;
        }

        /// <summary>A separate budget for each fixed step, not render frame or
        /// elapsed 20 ms window. All tanks must receive load at 50/60/100 Hz.</summary>
        private static bool RequestWheel(float fixedTime)
        {
            if (fixedTime != _wheelQueueTick)
            {
                _wheelSimulationRequests = 0;
                _wheelQueueTick = fixedTime;
            }
            if (_wheelSimulationRequests >= SuspensionTuning.MaxWheelSimulations) return false;
            _wheelSimulationRequests++;
            return true;
        }

        /// <summary>
        /// <c>update_airborne_damping</c>. Godot replaces the world damping while
        /// flying; Unity has no world default, so the value is simply saved and
        /// restored.
        /// </summary>
        private void UpdateAirborneDamping()
        {
            bool airborne = AirControlEnabled && _wheelHits <= 0 && !RockRecoveryActive;
            if (airborne && !_airDampingActive)
            {
                _groundLinearDamping = _rb.linearDamping;
                _rb.linearDamping = 0f;
                _airDampingActive = true;
                return;
            }

            if (!airborne && _airDampingActive)
            {
                _rb.linearDamping = _groundLinearDamping;
                _airDampingActive = false;
            }
        }
    }
}
