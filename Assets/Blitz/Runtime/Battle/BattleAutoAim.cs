using System;
using BlitzLegacy.Core.Save;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// The auto aim capture of <c>base_tank.gd</c>: <c>tank_auto_aim</c>, the
    /// runtime anchor that keeps the concrete hit the player selected, the
    /// post-release cooldown, the cover grace, and the bookkeeping
    /// <c>cam_trace</c> runs around the crosshair ray.
    ///
    /// The tracked point is a target-local hit, never a ray recomputed against a
    /// coarse volume and never a universal centre: recomputing made any ray
    /// through empty space inside the volume look captured and produced no
    /// correction at all once the reticle left the model, and a fixed centre
    /// threw away the weak spot the player picked.
    /// </summary>
    public sealed class BattleAutoAim
    {
        private const int LosBufferSize = 16;

        /// <summary>
        /// How far short of the anchor the line of sight ray stops, so the
        /// target's own armour does not count as cover in front of itself.
        /// </summary>
        private const float LosSlack = 0.35f;

        private readonly BattleTank _owner;
        private readonly RaycastHit[] _hits = new RaycastHit[LosBufferSize];

        private BattleTank _anchorTarget;
        private Transform _anchorNode;
        private Vector3 _anchorLocal;
        private Vector3 _anchorTargetLocal;
        private bool _anchorValid;
        private bool _adjustPending;
        private BattleTank _blockedTank;
        private float _blockUntil;
        private float _seenTime;
        private bool _engaged;

        public BattleAutoAim(BattleTank owner)
        {
            _owner = owner;
        }

        /// <summary><c>tank_auto_aim</c>: the captured tank, if any.</summary>
        public BattleTank Captured { get; private set; }

        /// <summary><c>is_instance_valid(tank_auto_aim)</c>.</summary>
        public bool Active { get { return Captured != null; } }

        /// <summary><c>was_in_auto_aim</c>.</summary>
        public bool Engaged { get { return _engaged; } }

        /// <summary><c>auto_aim_anchor_valid</c>.</summary>
        public bool AnchorValid { get { return _anchorValid; } }

        /// <summary><c>auto_aim_anchor_adjust_pending</c>.</summary>
        public bool AdjustPending { get { return _adjustPending; } }

        /// <summary><c>auto_aim_start</c>.</summary>
        public Action<BattleTank> Started;

        /// <summary><c>auto_aim_stop</c>.</summary>
        public Action Stopped;

        /// <summary><c>auto_aim_anchor_world</c>.</summary>
        public static Vector3 AnchorWorld(Transform anchor, Vector3 anchorLocal)
        {
            return anchor == null ? anchorLocal : anchor.TransformPoint(anchorLocal);
        }

        /// <summary><c>auto_aim_capture</c>.</summary>
        public void Capture(BattleTank target, Vector3 worldPoint, Collider collider)
        {
            if (target == null || target == _owner || !UprisingTank.IsTargetable(target)) { return; }

            Captured = target;
            _adjustPending = false;
            UpdateAnchor(worldPoint, collider);
            _seenTime = Time.time;
        }

        /// <summary><c>auto_aim_seen_time = Util.curtime()</c>.</summary>
        public void MarkSeen()
        {
            _seenTime = Time.time;
        }

        /// <summary>
        /// <c>auto_aim_mark_manual_adjust</c>: one explicit look event asks for
        /// exactly one weak-spot update.
        /// </summary>
        public void MarkManualAdjust()
        {
            if (Captured != null) { _adjustPending = true; }
        }

        /// <summary><c>auto_aim_update_anchor</c>.</summary>
        public void UpdateAnchor(Vector3 worldPoint, Collider collider)
        {
            var target = Captured;
            if (target == null) { return; }

            var point = IsFinite(worldPoint) ? worldPoint : target.AutoAimPoint();
            var node = AnchorNodeFor(target, collider);

            _anchorTarget = target;
            _anchorNode = node;
            _anchorLocal = node.InverseTransformPoint(point);
            _anchorTargetLocal = target.transform.InverseTransformPoint(point);
            _anchorValid = true;
        }

        /// <summary><c>auto_aim_tracking_point</c>.</summary>
        public Vector3 TrackingPoint()
        {
            var target = Captured;
            if (target == null) { return Vector3.zero; }

            if (_anchorValid && _anchorTarget == target)
            {
                if (_anchorNode != null && BelongsTo(_anchorNode, target))
                {
                    return AnchorWorld(_anchorNode, _anchorLocal);
                }

                // The collider is gone - a blown off track, say - so the hit
                // keeps following the hull it was taken on.
                return AnchorWorld(target.transform, _anchorTargetLocal);
            }

            return target.AutoAimPoint();
        }

        /// <summary>
        /// <c>auto_aim_tracking_point_led</c>: the pull aims where the target is
        /// going, by as much as the pull itself lags behind.
        /// </summary>
        public Vector3 TrackingPointLed(float strength, float leadMultiplier)
        {
            var point = TrackingPoint();
            if (Captured == null || strength <= 0f || leadMultiplier <= 0f)
            {
                return point;
            }

            var velocity = Captured.LinearVelocity;
            if (float.IsNaN(velocity.x) || float.IsNaN(velocity.y) || float.IsNaN(velocity.z)
                || float.IsInfinity(velocity.x) || float.IsInfinity(velocity.y) || float.IsInfinity(velocity.z))
            {
                return point;
            }

            return point + velocity * (leadMultiplier / strength);
        }

        /// <summary><c>auto_aim_has_los</c>.</summary>
        public bool HasLineOfSight(BattleTank target, Vector3 from)
        {
            if (target == null) { return false; }

            var to = target == Captured ? TrackingPoint() : target.AutoAimPoint();
            var ray = to - from;
            var distance = ray.magnitude;
            if (distance <= LosSlack) { return true; }

            var count = Physics.RaycastNonAlloc(
                from,
                ray / distance,
                _hits,
                distance - LosSlack,
                BattleLayers.AimMask,
                QueryTriggerInteraction.Ignore);

            for (var i = 0; i < count; i++)
            {
                var collider = _hits[i].collider;
                if (collider == null) { continue; }
                if (BelongsTo(collider.transform, _owner)) { continue; }
                if (BelongsTo(collider.transform, target)) { continue; }
                return false;
            }

            return true;
        }

        /// <summary>
        /// <c>auto_aim_release</c>. A deliberate release puts the tank on
        /// cooldown, so automatic capture cannot hand it straight back.
        /// </summary>
        public void Release(bool manual, float recaptureDelay)
        {
            if (manual && Captured != null)
            {
                _blockedTank = Captured;
                _blockUntil = Time.time + recaptureDelay;
            }

            Captured = null;
            _anchorTarget = null;
            _anchorNode = null;
            _anchorValid = false;
            _adjustPending = false;
        }

        /// <summary><c>auto_aim_can_capture</c>.</summary>
        public bool CanCapture(BattleTank target, bool requireSpotted)
        {
            if (target == null || target.Dead || !UprisingTank.IsTargetable(target)) { return false; }
            if (target == _owner || target == Captured) { return false; }
            if (_owner != null && target.Team == _owner.Team) { return false; }
            if (requireSpotted && !target.Spotted) { return false; }

            return target != _blockedTank || Time.time >= _blockUntil;
        }

        /// <summary>
        /// The auto aim half of <c>cam_trace</c>, run between the crosshair ray
        /// and the aim point the turret follows. Returns the point the gun has
        /// to aim at: the ray's own hit while the reticle rests on the captured
        /// model under a soft lock, and the stored anchor otherwise.
        /// </summary>
        public Vector3 TrackTrace(
            Vector3 targetPoint,
            bool traceHit,
            Vector3 hitPoint,
            Collider hitCollider,
            Vector3 from,
            SettingsData settings)
        {
            var coverGrace = settings != null ? settings.AutoAimCoverGrace : 0f;
            var soft = settings != null && settings.AutoAimSoft;

            // The Sandbox aimbot owns its capture: it may hold an unspotted or
            // covered target (cam_trace's cheat_aiming).
            var cheatAiming = SandboxCheatController.Aiming(_owner);

            // A target that dies or goes dark drops the capture. That release
            // is not the player's, so it leaves no cooldown behind.
            if (Captured != null && !UprisingTank.IsTargetable(Captured))
            {
                Release(false, 0f);
            }
            else if (!cheatAiming && Captured != null && (Captured.Dead || !Captured.Spotted))
            {
                Release(false, 0f);
            }

            var hitCaptured = Captured != null && traceHit && hitCollider != null
                && BelongsTo(hitCollider.transform, Captured);

            // Only an explicit player look event may move an established weak
            // spot. The mark is consumed even on a miss, so an automatic
            // reacquisition cannot rewrite the anchor without fresh input.
            var manualAdjust = _adjustPending;
            _adjustPending = false;
            if (hitCaptured
                && (!_anchorValid || _anchorTarget != Captured || manualAdjust))
            {
                UpdateAnchor(hitPoint, hitCollider);
            }

            // A target that drives out of sight has to let go of the lock as
            // well. Hitting any visible part, including the turret above cover,
            // refreshes the capture directly; otherwise the selected anchor
            // itself has to be in line of sight.
            if (Captured != null)
            {
                if (cheatAiming || hitCaptured || HasLineOfSight(Captured, from))
                {
                    _seenTime = Time.time;
                }
                else if (Time.time - _seenTime > coverGrace)
                {
                    Release(false, 0f);
                }
            }

            if (Captured != null)
            {
                if (!_engaged)
                {
                    _engaged = true;
                    _seenTime = Time.time;
                    if (Started != null) { Started(Captured); }
                }

                // A direct soft-lock hit stays fully manual, so the player can
                // move across armour plates. Once the reticle leaves the real
                // model the stored hit drives both gun and hull, which is what
                // turns the tracks of a casemate tank destroyer. A hard lock
                // always uses the selected hit instead of a centre point.
                if (!hitCaptured || !soft)
                {
                    targetPoint = TrackingPoint();
                }
            }
            else if (_engaged)
            {
                _engaged = false;
                if (Stopped != null) { Stopped(); }
            }

            return targetPoint;
        }

        private static Transform AnchorNodeFor(BattleTank target, Collider collider)
        {
            if (collider == null) { return target.transform; }

            var node = collider.transform;
            return node != null && BelongsTo(node, target) ? node : target.transform;
        }

        /// <summary><c>hit.collider.owner == tank</c>.</summary>
        private static bool BelongsTo(Transform node, BattleTank tank)
        {
            if (node == null || tank == null) { return false; }
            return node == tank.transform || node.IsChildOf(tank.transform);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
