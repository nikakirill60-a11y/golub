using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>Contact-driven physical tumble back onto the tracks, as requested by the player.
    /// Arcade torque policy, not recovered Reforged/server code. Never writes pose/velocity,
    /// applies lift, waits for a timer, or levels an unsupported airborne tank.</summary>
    [DefaultExecutionOrder(110)]
    [DisallowMultipleComponent]
    public sealed class TankRolloverPhysics : MonoBehaviour
    {
        public const float StartUpDot = -.10f;
        public const float FinishUpDot = .966f;
        public const float SupportUpDot = .55f;
        public const float TurnSpeed = 2.4f; // radians/s, below the existing rigidbody 5 rad/s ceiling
        public const float AccelerationLimit = 18f;
        public const float BrakeLimit = 12f;
        public const float Response = 8f;
        public const float SlowAngle = 1.2f;
        private BattleTank _tank;
        private Rigidbody _body;
        private Vector3 _normal = Vector3.up, _axis;
        private float _contactTick = float.NegativeInfinity;
        private Vector3 _contactSum;
        public bool Active { get; private set; }
        public int RolloverCount { get; private set; }
        public Vector3 RollAxis => _axis;
        public Vector3 LastAngularAcceleration { get; private set; }
        public void Setup(BattleTank tank, Rigidbody body) { _tank = tank; _body = body; ResetState(); }
        public void ResetState()
        {
            Active = false; RolloverCount = 0; LastAngularAcceleration = Vector3.zero;
            _axis = Vector3.zero; _normal = Vector3.up; _contactSum = Vector3.zero;
            _contactTick = float.NegativeInfinity;
        }
        /// <summary>Only actual upward map contacts supply support. Age is collision validity, not a flip delay.</summary>
        public void ObserveSupport(Vector3 normal, float fixedTime)
        {
            if (!Finite(normal) || normal.sqrMagnitude < .001f || !float.IsFinite(fixedTime)) return;
            normal.Normalize(); if (normal.y < SupportUpDot) return;
            if (_contactTick != fixedTime) { _contactSum = Vector3.zero; _contactTick = fixedTime; }
            _contactSum += normal; _normal = _contactSum.normalized;
        }
        public static bool ShouldStart(Vector3 up, Vector3 normal, bool supported, bool allowed)
        { return allowed && supported && Finite(up) && Finite(normal) && Vector3.Dot(up.normalized, normal.normalized) < StartUpDot; }
        /// <summary>Prefer the current fall's pitch/roll sign; exact upside-down has a stable geometry fallback.</summary>
        public static Vector3 ChooseAxis(Vector3 up, Vector3 normal, Vector3 forward, Vector3 angularVelocity)
        {
            if (!Finite(up) || !Finite(normal) || !Finite(forward) || !Finite(angularVelocity) ||
                up.sqrMagnitude < .001f || normal.sqrMagnitude < .001f) return Vector3.zero;
            normal.Normalize(); var momentum = Vector3.ProjectOnPlane(angularVelocity, normal);
            var axis = Vector3.Cross(up.normalized, normal);
            if (axis.sqrMagnitude > .0001f)
            {
                axis.Normalize(); if (momentum.sqrMagnitude > .01f && Vector3.Dot(axis, momentum) < 0) axis = -axis;
                return axis;
            }
            if (momentum.sqrMagnitude > .01f) return momentum.normalized;
            axis = Vector3.ProjectOnPlane(forward, normal);
            if (axis.sqrMagnitude < .001f) axis = Vector3.Cross(normal, Vector3.right);
            if (axis.sqrMagnitude < .001f) axis = Vector3.Cross(normal, Vector3.forward);
            return axis.normalized;
        }
        public static float RemainingAngle(Vector3 up, Vector3 normal, Vector3 axis)
        {
            if (!Finite(up) || !Finite(normal) || !Finite(axis) || axis.sqrMagnitude < .001f) return 0;
            up.Normalize(); normal.Normalize(); axis.Normalize();
            var angle = Mathf.Atan2(Vector3.Dot(axis, Vector3.Cross(up, normal)), Vector3.Dot(up, normal));
            return angle < 0 ? angle + 2 * Mathf.PI : angle;
        }
        public static float MotorAcceleration(float remainingAngle, float angularSpeed)
        {
            if (!float.IsFinite(remainingAngle) || !float.IsFinite(angularSpeed) || remainingAngle <= 0) return 0;
            var target = TurnSpeed * Mathf.Clamp01(remainingAngle / SlowAngle);
            return Mathf.Clamp((target - angularSpeed) * Response, -BrakeLimit, AccelerationLimit);
        }
        private void FixedUpdate()
        {
            LastAngularAcceleration = Vector3.zero;
            if (_tank == null || _body == null || _tank.Dead || _tank.Blocked || _body.isKinematic)
            { Active = false; _axis = Vector3.zero; return; }
            // Collision callbacks follow FixedUpdate: accept only the previous completed PhysX step.
            // One extra step of grace would otherwise torque an already separated airborne hull.
            var supportAge = Time.fixedTime - _contactTick;
            var supported = supportAge >= 0 && supportAge <= Time.fixedDeltaTime * 1.5f;
            // No general flight auto-level; expired support leaves existing momentum untouched.
            if (!supported) return;
            var up = _body.rotation * Vector3.up;
            var dot = Vector3.Dot(up, _normal);
            if (Active && (dot >= FinishUpDot || (dot > .82f && _tank.Suspension != null && _tank.Suspension.WheelHits >= 2)))
            { Active = false; _axis = Vector3.zero; return; }
            if (!Active)
            {
                if (!ShouldStart(up, _normal, true, true)) return;
                _axis = ChooseAxis(up, _normal, _body.rotation * Vector3.forward, _body.angularVelocity);
                if (_axis.sqrMagnitude < .001f) return;
                Active = true; RolloverCount++; _body.WakeUp();
            }
            // Follow a changing support normal/yaw without reversing the latched tumble direction.
            var alignment = Vector3.Cross(up, _normal);
            if (alignment.sqrMagnitude > .0001f)
            { alignment.Normalize(); if (Vector3.Dot(alignment, _axis) < 0) alignment = -alignment; _axis = alignment; }
            var remaining = RemainingAngle(up, _normal, _axis);
            var acceleration = MotorAcceleration(remaining, Vector3.Dot(_body.angularVelocity, _axis));
            LastAngularAcceleration = _axis * acceleration;
            // PhysX integrates angular acceleration over dt once; ground reactions lift/pivot the chassis naturally.
            _body.AddTorque(LastAngularAcceleration, ForceMode.Acceleration);
        }
        private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    }
}
