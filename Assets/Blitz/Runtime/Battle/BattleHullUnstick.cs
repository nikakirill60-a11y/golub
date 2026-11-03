using BlitzLegacy.Runtime.Telemetry;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Keeps a hull out of the solid geometry of the map.
    ///
    /// A prop is one solid body in the client and it never lets a tank inside.
    /// Here the hull is a convex mesh on a dynamic body, and PhysX separates
    /// the contacts it resolves - but only those. A small rock slips between
    /// the wheel rays and the hull settles onto it, a corner ends up inside a
    /// dented rock shell after a fall or a ram, and the solver is then left
    /// with a hull wedged on interior geometry and nothing to push against.
    /// The engine runs and the tank does not move: the rocks players report
    /// being stuck in for good.
    ///
    /// Right after every physics step each overlap with the map is separated
    /// along its own minimum translation, and a hull that keeps asking to
    /// drive without moving is walked out along the same direction with a
    /// little lift, so it ends up on top of the rock instead of inside it.
    /// A hull that is merely leaning on something is left alone: pushing a
    /// wall is a normal thing to do and has to keep feeling solid.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleHullUnstick : MonoBehaviour
    {
        /// <summary>
        /// The overlap PhysX keeps around every contact. Separating that too
        /// would leave the hull hovering and drop the contact altogether.
        /// </summary>
        public const float Skin = 0.02f;

        /// <summary>How fast an overlap is walked off, in metres a second.</summary>
        public const float PushSpeed = 4f;

        /// <summary>A hull below this speed is not driving anywhere.</summary>
        public const float StuckSpeed = 0.35f;

        /// <summary>How long that has to hold while the hull asks to move.</summary>
        public const float StuckTime = 0.9f;

        /// <summary>How fast a wedged hull is walked out.</summary>
        public const float EscapeSpeed = 2.5f;

        /// <summary>The share of the escape that lifts the hull.</summary>
        public const float EscapeLift = 0.35f;

        private const int MaxOverlaps = 16;

        private static readonly Collider[] Overlaps = new Collider[MaxOverlaps];

        private Rigidbody _body;
        private Collider _hull;
        private TankSuspension _suspension;
        private bool _suspensionResolved;
        private float _stuckSince = -1f;

        /// <summary>The separation applied on the last step, for the tests.</summary>
        public Vector3 LastPush { get; private set; }

        /// <summary>Whether the hull is being walked out of something.</summary>
        public bool Escaping { get; private set; }

        /// <summary>The map and the hull shell of another tank.</summary>
        public static int Mask
        {
            get { return (1 << BattleLayers.Static) | (1 << BattleLayers.Physics); }
        }

        public void Setup(Rigidbody body, Collider hull)
        {
            _body = body;
            _hull = hull;
            _suspensionResolved = false;
            _stuckSince = -1f;
        }

        private void FixedUpdate()
        {
            using var slowScope = BlitzPerfMonitor.Slow("unstick.fixedUpdate");
            if (!_suspensionResolved)
            {
                _suspension = GetComponent<TankSuspension>();
                _suspensionResolved = true;
            }

            var suspension = _suspension;
            Resolve(
                Time.fixedDeltaTime,
                suspension != null && suspension.ForwardAxis != 0f,
                suspension != null ? Mathf.Abs(suspension.SpeedMs) : 0f);
        }

        /// <summary>
        /// One separation pass. Returns the translation it applied, which is
        /// zero while the hull is clear of everything solid.
        /// </summary>
        public Vector3 Resolve(float delta, bool wantsToMove, float speed)
        {
            LastPush = Vector3.zero;
            if (_body == null || _hull == null || _body.isKinematic || delta <= 0f)
            {
                return Vector3.zero;
            }

            var separation = Separation();
            TrackStuck(delta, wantsToMove, speed, separation != Vector3.zero);
            if (separation == Vector3.zero)
            {
                return Vector3.zero;
            }

            var direction = separation.normalized;
            if (Escaping)
            {
                // Wedged: walk the hull out along the same direction and lift
                // it a little, so it clears the ledge it caught on instead of
                // grinding along its underside.
                var escape = direction + (Vector3.up * EscapeLift);
                separation = escape.normalized * (EscapeSpeed * delta);
            }
            else
            {
                separation = Vector3.ClampMagnitude(separation, PushSpeed * delta);
            }

            _body.position += separation;

            // Whatever velocity still points into the thing the hull is being
            // pushed out of would put it straight back inside.
            var into = Vector3.Dot(_body.linearVelocity, direction);
            if (into < 0f)
            {
                _body.linearVelocity -= direction * into;
            }

            LastPush = separation;
            return separation;
        }

        /// <summary>
        /// The minimum translation that clears every overlap at once. Concave
        /// geometry - the landscape, a cliff, a building the tanks drive into -
        /// answers no penetration query, so it is left to the solver.
        /// </summary>
        private Vector3 Separation()
        {
            var bounds = _hull.bounds;
            var count = Physics.OverlapBoxNonAlloc(
                bounds.center, bounds.extents, Overlaps, Quaternion.identity,
                Mask, QueryTriggerInteraction.Ignore);

            var total = Vector3.zero;
            for (var i = 0; i < count; i++)
            {
                var other = Overlaps[i];
                if (other == null || other == _hull)
                {
                    continue;
                }

                var body = other.attachedRigidbody;
                if (body != null && !body.isKinematic)
                {
                    // Another tank is a contact the solver owns.
                    continue;
                }

                if (other.transform.IsChildOf(transform))
                {
                    continue;
                }

                Vector3 direction;
                float depth;
                if (!Physics.ComputePenetration(
                        _hull, _hull.transform.position, _hull.transform.rotation,
                        other, other.transform.position, other.transform.rotation,
                        out direction, out depth))
                {
                    continue;
                }

                if (depth <= Skin)
                {
                    continue;
                }

                total += direction * (depth - Skin);
            }

            return total;
        }

        private void TrackStuck(
            float delta, bool wantsToMove, float speed, bool overlapping)
        {
            if (!wantsToMove || !overlapping || speed > StuckSpeed)
            {
                _stuckSince = -1f;
                Escaping = false;
                return;
            }

            if (_stuckSince < 0f)
            {
                _stuckSince = 0f;
            }

            _stuckSince += delta;
            Escaping = _stuckSince >= StuckTime;
        }
    }
}
