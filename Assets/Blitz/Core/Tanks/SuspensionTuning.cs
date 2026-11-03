using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Maps;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// Engine free port of the tuning constants and the pure math of the Godot
    /// drivetrain in <c>assets/tanks/base/suspension_component.gd</c>.
    ///
    /// Everything here is a faithful transcription of the source, including the
    /// quirks. Do not "fix" them: the handling of the original game depends on
    /// them.
    /// </summary>
    public static class SuspensionTuning
    {
        /// <summary>Exported <c>engine_pow</c>. Overwritten by the engine card in setup.</summary>
        public const float DefaultEnginePower = 110f;

        /// <summary>Exported <c>max_speed_fw</c>, in km/h.</summary>
        public const float DefaultMaxSpeedForward = 38f;

        /// <summary>Exported <c>max_speed_back</c>, in km/h.</summary>
        public const float DefaultMaxSpeedBack = 20f;

        /// <summary>Exported <c>rot_speed</c>, in degrees per second. Overwritten by the track card.</summary>
        public const float DefaultRotationSpeed = 45f;

        /// <summary>Exported <c>drive_delay</c>: input is applied this many seconds late.</summary>
        public const float DriveDelay = 0.15f;

        /// <summary>Exported <c>damp_mul</c>.</summary>
        public const float DampMul = 1.95f;

        /// <summary>Exported <c>compr_mul</c>.</summary>
        public const float ComprMul = 1.1f;

        /// <summary>Exported <c>max_angle</c>, in degrees.</summary>
        public const float MaxAngle = 30f;

        /// <summary>Exported <c>max_angle_frict</c>, in degrees.</summary>
        public const float MaxAngleFriction = 35f;

        /// <summary>Exported <c>frict_fw</c>.</summary>
        public const float FrictionForward = 1.5f;

        /// <summary>Exported <c>frict_fw_tres</c>.</summary>
        public const float FrictionForwardThreshold = 0.17f;

        /// <summary>Exported <c>frict_r</c>.</summary>
        public const float FrictionRight = 2.1f;

        /// <summary>Exported <c>frict_r_tres</c>.</summary>
        public const float FrictionRightThreshold = 0.17f;

        /// <summary>Exported <c>offset</c> is <c>(0, 0.925, 0)</c>; only Y is non zero.</summary>
        public const float WheelOffsetY = 0.925f;

        /// <summary>Exported <c>force_offset</c> is <c>(0, -0.2, 0)</c>; only Y is non zero.</summary>
        public const float DriveForceOffsetY = -0.2f;

        /// <summary>Exported <c>wheel_count</c>: raycasts per side.</summary>
        public const int WheelCount = 6;

        /// <summary>Exported <c>wheel_len</c>: raycast length, in metres.</summary>
        public const float WheelLength = 0.98f;

        /// <summary>Exported <c>hull_len</c>: wheel row length, in metres.</summary>
        public const float HullLength = 3.75f;

        /// <summary>Exported <c>dist_sides</c>. Rewritten to <c>hull_width * 0.85</c> by the tank.</summary>
        public const float DistanceSides = 1.722691f;

        /// <summary>Exported <c>ang_damp_coeff</c>.</summary>
        public const float AngularDampCoeff = 12f;

        /// <summary>Exported <c>ang_damp_treshold</c>.</summary>
        public const float AngularDampThreshold = 2f;

        /// <summary><c>base_tank.gd</c> sets <c>suspension.dist_sides = hull_width * 0.85</c>.</summary>
        public const float HullSideFactor = 0.85f;

        /// <summary>A wheel hit whose normal is too horizontal is discarded (<c>&lt; 0.2</c>).</summary>
        public const float GroundNormalDotMin = 0.2f;

        /// <summary>Spring compression is clamped to this maximum.</summary>
        public const float CompressionMax = 0.45f;

        /// <summary>Literal <c>65</c> in <c>simulate_wheel</c>.</summary>
        public const float SpringForceScale = 65f;

        /// <summary>Literal <c>450</c> in the drive force of <c>_physics_process</c>.</summary>
        public const float DriveForceScale = 450f;

        /// <summary>Literal <c>0.85</c> applied to the steering angular velocity.</summary>
        public const float YawStickFactor = 0.85f;

        /// <summary>Literal <c>2500</c> multiplied by the hull width for the lean force.</summary>
        public const float LeanForcePerWidth = 2500f;

        /// <summary>Literal <c>400</c>: the lean force cap, multiplied by the mass.</summary>
        public const float LeanForceMax = 400f;

        /// <summary>Static budget <c>max_wheel_simulations</c> shared by all tanks in a frame.</summary>
        public const int MaxWheelSimulations = 32;

        /// <summary>Static <c>queue_reset_delay</c>: the budget above refills this often.</summary>
        public const float WheelQueueResetDelay = 0.02f;

        /// <summary><c>setup</c>: <c>compr_coeff = hull_weight * 10</c>.</summary>
        public static float ComprCoeffFor(float hullWeight)
        {
            return hullWeight * 10f;
        }

        /// <summary><c>setup</c>: <c>damp_coeff = hull_weight * 0.8</c>.</summary>
        public static float DampCoeffFor(float hullWeight)
        {
            return hullWeight * 0.8f;
        }

        /// <summary><c>setup</c>: <c>dist_wheels = hull_len / wheel_count</c>.</summary>
        public static float DistanceWheelsFor(float hullLength, int wheelCount)
        {
            if (wheelCount <= 0) { return 0f; }
            return hullLength / wheelCount;
        }

        /// <summary>
        /// <c>wheel_side_coeff</c>: the flat index of a wheel. The left side (-1)
        /// occupies the first <paramref name="wheelCount"/> slots.
        /// </summary>
        public static int WheelIndex(int index, int side, int wheelCount)
        {
            return side < 0 ? index : index + wheelCount;
        }

        /// <summary>
        /// Local wheel position in Godot space, as built by <c>setup</c>:
        /// <c>off - FORWARD * (wheel_count - 1) * dist_wheels * 0.5 + RIGHT * dist_sides * 0.5 * side + FORWARD * i * dist_wheels</c>.
        /// Godot FORWARD is <c>-Z</c>, so the Z term resolves to
        /// <c>(wheel_count - 1) * dist_wheels * 0.5 - i * dist_wheels</c>.
        /// Mirror the result with <c>GodotSpace.Point</c> for Unity.
        /// </summary>
        public static Vec3 WheelPosition(int index, int side, int wheelCount, float distanceWheels, float distanceSides, float offsetY)
        {
            float x = distanceSides * 0.5f * side;
            float z = (wheelCount - 1) * distanceWheels * 0.5f - index * distanceWheels;
            return new Vec3(x, offsetY, z);
        }

        /// <summary>
        /// <c>speed_reached_mul</c>. Note the source quirk: both the forward and the
        /// backward branch divide by <paramref name="speedThreshold"/>, never by
        /// <c>max_speed_back</c>. A speed of exactly zero takes the backward branch
        /// because Godot's <c>sign(0)</c> is <c>0</c>, which is not <c>&gt; 0</c>.
        /// </summary>
        public static float SpeedReachedMul(float speed, float speedThreshold, float forwardMul, float backMul)
        {
            if (speedThreshold == 0f) { return 0f; }
            float ratio = Math.Abs(speed) / speedThreshold;
            return speed > 0f ? ratio * forwardMul : ratio * backMul;
        }

        /// <summary>
        /// <c>get_base_angle</c>. The ignore flags zero the angle before the maximum,
        /// and only the pitch is optionally taken as an absolute value, so a negative
        /// pitch can lose to the absolute roll.
        /// </summary>
        public static float GetBaseAngle(float pitchDegrees, float rollDegrees, bool ignorePitch, bool ignoreRoll, bool absolutePitch)
        {
            float pitch = ignorePitch ? 0f : pitchDegrees;
            float roll = ignoreRoll ? 0f : rollDegrees;
            float first = absolutePitch ? Math.Abs(pitch) : pitch;
            float second = Math.Abs(roll);
            return first > second ? first : second;
        }

        /// <summary>
        /// <c>base_reached_angle_mul</c>, taking the already resolved base angle.
        /// Returns <c>1</c> below the limit and falls off linearly above it.
        /// </summary>
        public static float BaseReachedAngleMul(float baseAngle, float maxAngle, float baseCoeff)
        {
            if (maxAngle == 0f) { return 1f; }
            if (baseAngle <= maxAngle) { return 1f; }
            float over = Clamp(baseAngle - maxAngle, 0f, maxAngle * 2f);
            return Clamp(baseCoeff - (over / maxAngle), 0f, 1f);
        }

        /// <summary>
        /// <c>get_torque_mul</c>. Returns <c>1</c> while driving on top of another
        /// tank, otherwise samples the torque curve at the normalised speed and
        /// returns <c>sample * 100 + 1</c>, exactly as the source does.
        /// </summary>
        public static float TorqueMul(TorqueCurve curve, float speed, float maxSpeedForward, float pitchDegrees, bool onTank)
        {
            if (onTank) { return 1f; }
            float baseAngle = GetBaseAngle(pitchDegrees, 0f, false, true, false);
            float mult = BaseReachedAngleMul(baseAngle, MaxAngle * 0.8f, 0.5f);
            float offset = SpeedReachedMul(speed, maxSpeedForward, 0.85f, 0.5f);
            float sample = mult * (curve == null ? 0f : curve.Sample(offset));
            return sample * 100f + 1f;
        }

        /// <summary>
        /// <c>max_speed_reached</c>: <c>speed &gt; max_fw * mul or speed &lt; -abs(max_back * mul)</c>.
        /// Speed is in km/h.
        /// </summary>
        public static bool MaxSpeedReached(float speed, float maxSpeedForward, float maxSpeedBack, float mul)
        {
            return speed > maxSpeedForward * mul || speed < -Math.Abs(maxSpeedBack * mul);
        }

        /// <summary>
        /// <c>changing_dir</c>: the hull still rolls against the requested direction.
        /// </summary>
        public static bool ChangingDir(float speed, float forwardAxis, float maxSpeedForward)
        {
            return Sign(speed) != forwardAxis && Math.Abs(speed) > maxSpeedForward * 0.1f;
        }

        /// <summary>
        /// <c>check_on_brake</c>. The angle term also requires
        /// <c>wheel_hits &gt; wheel_count * 2</c>, which the counter can never exceed,
        /// so it is dead in the source as well; it is kept for fidelity.
        /// </summary>
        public static bool OnBrake(float forwardAxis, float speed, float maxSpeedForward, float maxSpeedBack, float baseAngle, float maxAngle, bool blocked, int wheelHits, int wheelCount)
        {
            if (forwardAxis == 0f) { return true; }
            if (MaxSpeedReached(speed, maxSpeedForward, maxSpeedBack, 1.25f)) { return true; }
            if (ChangingDir(speed, forwardAxis, maxSpeedForward)) { return true; }
            if (blocked) { return true; }
            bool angleExceeded = Math.Abs(baseAngle) >= maxAngle;
            return angleExceeded && wheelHits > wheelCount * 2;
        }

        /// <summary>
        /// <c>simulate_wheel</c>: <c>clampf(1 - hit_len_sqr / wheel_len^2, 0, 0.45)</c>.
        /// </summary>
        public static float Compression(float hitLengthSquared, float wheelLength)
        {
            if (wheelLength == 0f) { return 0f; }
            float coeff = 1f / (wheelLength * wheelLength);
            return Clamp(1f - hitLengthSquared * coeff, 0f, CompressionMax);
        }

        /// <summary>
        /// <c>simulate_wheel</c> spring magnitude along the up vector:
        /// <c>clampf(ray_vel * damp * damp_mul - clampf(compr * compr_coeff * compr_mul, 0, compr_coeff), -compr_coeff, 0) * -1</c>.
        /// The result is zero or positive; the caller multiplies it by the up vector.
        /// </summary>
        public static float SpringMagnitude(float rayVelocity, float compression, float dampCoeff, float comprCoeff)
        {
            float spring = Clamp(compression * comprCoeff * ComprMul, 0f, comprCoeff);
            float force = rayVelocity * dampCoeff * DampMul - spring;
            return Clamp(force, -comprCoeff, 0f) * -1f;
        }

        /// <summary>
        /// <c>base_tank.drive</c>: <c>new_r = r * (fw_axis if fw_axis != 0 else 1)</c>.
        /// The multiplier is the forward axis of the <em>previous</em> frame, read
        /// before it is overwritten, so steering inverts while reversing.
        /// </summary>
        public static float SteerAxis(float rightInput, float previousForwardAxis)
        {
            return rightInput * (previousForwardAxis != 0f ? previousForwardAxis : 1f);
        }

        /// <summary>Godot's <c>sign</c>: -1, 0 or 1.</summary>
        public static float Sign(float value)
        {
            if (value > 0f) { return 1f; }
            if (value < 0f) { return -1f; }
            return 0f;
        }

        /// <summary>Godot's <c>clampf</c>.</summary>
        public static float Clamp(float value, float min, float max)
        {
            if (value < min) { return min; }
            if (value > max) { return max; }
            return value;
        }
    }

    /// <summary>
    /// Port of the deferred input queue of <c>suspension_component.gd</c>
    /// (<c>drive</c> and <c>process_drive_commands</c>). Every steering change is
    /// delayed by <see cref="SuspensionTuning.DriveDelay"/> seconds, which is what
    /// gives the original game its heavy start and stop feel.
    /// </summary>
    public sealed class DriveCommandQueue
    {
        private struct Command
        {
            public float Time;
            public float Forward;
            public float Right;
        }

        private readonly List<Command> _commands = new List<Command>();
        private readonly float _delay;
        private float _forwardAxis;
        private float _rightAxis;

        public DriveCommandQueue() : this(SuspensionTuning.DriveDelay) { }

        public DriveCommandQueue(float delay)
        {
            _delay = delay;
        }

        /// <summary>Applied forward axis, mirroring <c>fw_axis</c>.</summary>
        public float ForwardAxis { get { return _forwardAxis; } }

        /// <summary>Applied steering axis, mirroring <c>r_axis</c>.</summary>
        public float RightAxis { get { return _rightAxis; } }

        /// <summary>Number of commands still waiting for their delay to elapse.</summary>
        public int PendingCount { get { return _commands.Count; } }

        /// <summary>
        /// <c>drive(fw, r)</c>. Drops the request when nothing would change, when an
        /// identical command is already pending, or when the axes already hold the
        /// requested values.
        /// </summary>
        public void Request(float forward, float right, float now)
        {
            if (forward == 0f && right == 0f && _forwardAxis == 0f && _rightAxis == 0f) { return; }

            for (int i = 0; i < _commands.Count; i++)
            {
                Command pending = _commands[i];
                if (pending.Forward == forward && pending.Right == right && pending.Time > now) { return; }
            }

            if (forward == _forwardAxis && right == _rightAxis) { return; }

            _commands.Add(new Command { Time = now + _delay, Forward = forward, Right = right });
        }

        /// <summary>
        /// <c>process_drive_commands()</c>. Applies at most one due command per call
        /// because the source breaks out of the loop right after removing it.
        /// </summary>
        public bool Process(float now)
        {
            for (int i = 0; i < _commands.Count; i++)
            {
                if (now < _commands[i].Time) { continue; }
                _forwardAxis = _commands[i].Forward;
                _rightAxis = _commands[i].Right;
                _commands.RemoveAt(i);
                return true;
            }
            return false;
        }

        /// <summary>
        /// <c>base_tank.drive</c> zeroes <c>suspension.fw_axis</c> and
        /// <c>suspension.r_axis</c> when the hull is blocked or the tracks are
        /// destroyed, and leaves <c>drive_commands</c> untouched, so a command
        /// queued a moment earlier still lands afterwards.
        /// </summary>
        public void ClearAxes()
        {
            _forwardAxis = 0f;
            _rightAxis = 0f;
        }

        /// <summary>Full reset, for a hull that is rebuilt or respawned.</summary>
        public void Stop()
        {
            _commands.Clear();
            ClearAxes();
        }
    }
}
