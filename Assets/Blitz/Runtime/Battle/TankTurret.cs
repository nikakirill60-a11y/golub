using System.Collections.Generic;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Tanks;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Turret traverse and gun elevation, ported from <c>base_tank.gd</c>
    /// (<c>rotate_turret</c>, <c>rotate_gun</c>, <c>gun_deviation_reached</c> and
    /// <c>get_gun_pos</c>).
    ///
    /// The exported mesh has every authored transform baked into its vertices,
    /// so the rig rebuilds the two pivots from <see cref="TankNodeTable"/> and
    /// re-parents the <c>turret01</c> and <c>gun01</c> objects under them while
    /// keeping their world placement, which is what makes them spin around the
    /// authored points instead of the model origin.
    ///
    /// Angles are kept in battle space: the port mirrors across Z, so a positive
    /// yaw traverses right and a positive pitch depresses the barrel, both the
    /// opposite sign of the source. Limits stay in the card's degrees.
    /// </summary>
    [DefaultExecutionOrder(-1)]
    public sealed class TankTurret : MonoBehaviour
    {
        /// <summary><c>get_gun_pos</c> pads the measured barrel by this much.</summary>
        public const float GunOriginMargin = 0.1f;

        /// <summary><c>gun_mesh_len</c> starts here until the gun mesh is measured.</summary>
        public const float GunMeshLengthFallback = 0.1f;

        private const float SourceEpsilon = 1e-05f;

        /// <summary>Pivot the turret traverses around.</summary>
        public Transform TurretPivot { get; private set; }

        /// <summary>Pivot the gun elevates around.</summary>
        public Transform GunPivot { get; private set; }

        /// <summary><c>gun_shoot_point</c>: where shells leave the barrel.</summary>
        public Transform Muzzle { get; private set; }

        /// <summary><c>gun_mesh_len</c>, the gun mesh depth.</summary>
        public float GunMeshLength { get; private set; }

        /// <summary>
        /// Spinning sub-assembly of the gun (<c>&lt;gun&gt;_rotor</c>): the
        /// Annihilator's Gatling cylinder, which the client's
        /// <c>gatling_rotate</c> clip turns by one barrel per shell.
        /// </summary>
        public Transform GunRotor { get; private set; }

        /// <summary>Barrels around the rotor; one shell turns it by 360/n.</summary>
        public int RotorBarrels { get; private set; }

        /// <summary>Current and commanded rotor roll, degrees.</summary>
        public float RotorAngle { get; private set; }
        public float RotorTarget { get; private set; }
        private float _rotorSpeed;

        /// <summary>
        /// Baked client clips of the installed gun's moving parts (Gatling
        /// spin, per-barrel recoil, recoil levers, ammunition belt, reload
        /// drum). <see cref="GunClip.BurstTrigger"/> clips play on the first
        /// shell of a burst, <see cref="GunClip.ReloadedTrigger"/> ones when the
        /// gun has reloaded.
        /// </summary>
        public IList<GunClip> GunClips
        {
            get { return _clips; }
        }

        private readonly List<GunClip> _clips = new List<GunClip>();
        private readonly List<float> _clipTimes = new List<float>();
        private readonly Dictionary<string, Transform> _clipPivots = new Dictionary<string, Transform>();
        private readonly Dictionary<string, Vector3> _clipRest = new Dictionary<string, Vector3>();
        private readonly float[] _samplePosition = new float[3];
        private readonly float[] _sampleRotation = new float[4];

        /// <summary>The pivot one clip track moves, or null.</summary>
        public Transform GunClipPivot(string track)
        {
            Transform pivot;
            return _clipPivots.TryGetValue(track ?? string.Empty, out pivot) ? pivot : null;
        }

        /// <summary>Seconds into the playing clip with this trigger, or -1 when idle.</summary>
        public float GunClipTime(string trigger)
        {
            for (var i = 0; i < _clips.Count; i++)
            {
                if (_clips[i].Trigger == trigger && _clipTimes[i] >= 0f) { return _clipTimes[i]; }
            }

            return -1f;
        }

        /// <summary>Moving node names resolved from this card's exported rig.</summary>
        public string TurretNodeName { get; private set; } = TankNodeTable.Turret;
        public string GunNodeName { get; private set; } = TankNodeTable.Gun;
        public string MuzzleNodeName { get; private set; } = TankNodeTable.Muzzle;

        /// <summary>Turret yaw relative to the hull, in radians.</summary>
        public float Yaw { get; private set; }

        /// <summary>Gun pitch relative to the turret, in radians.</summary>
        public float Pitch { get; private set; }

        /// <summary><c>turret_ang_vel</c>: traverse speed in degrees per second.</summary>
        public float AngularVelocity { get; private set; }

        /// <summary><c>auto_rot_lock</c>: suppresses the hull steer assist.</summary>
        [System.NonSerialized]
        public bool TraverseLock;

        /// <summary>Arty view lifts the gun by the range to the aim point.</summary>
        [System.NonSerialized]
        public bool LobAim;

        /// <summary><c>aim_target</c>: where the camera ray landed this frame.</summary>
        [System.NonSerialized]
        public Vector3 AimTarget;

        /// <summary><c>aim_target_lerp</c>: the smoothed point the rig chases.</summary>
        [System.NonSerialized]
        public Vector3 AimTargetLerp;

        /// <summary>
        /// <c>aim_ballistic_lead</c>: whether adjust_aim lifts the target by
        /// the shell drop. The AI aims at a point it already led itself, so
        /// it turns this off for those calls.
        /// </summary>
        [System.NonSerialized]
        public bool AimBallisticLead = true;

        /// <summary>
        /// Set on a network puppet: the angles come from the host through
        /// <see cref="ApplyNetworked"/> and the traverse never runs locally.
        /// </summary>
        [System.NonSerialized]
        public bool NetDriven;

        /// <summary>
        /// Set on the local player's own puppet: the traverse runs here from
        /// the player's own crosshair exactly as on the host, and the host's
        /// angles only reconcile it (<see cref="NetPredictTick"/>).
        /// </summary>
        [System.NonSerialized]
        public bool NetPredicted;

        /// <summary>How far the predicted traverse may lead the host's (input latency).</summary>
        public const float NetLeadSeconds = 0.4f;

        /// <summary>Upper bound of <see cref="NetLead"/>: a lead past it is a stalled link, not latency.</summary>
        public const float NetLeadMaxSeconds = 1.2f;

        /// <summary>
        /// The lead the reconcile allows right now. The network layer raises
        /// it to the measured round trip plus its playback delay: on a 200 ms
        /// link the host's angles reach the screen ~0.45 s after the input, and
        /// a fixed 0.4 s bound held the own turret back behind the camera.
        /// </summary>
        public static float NetLead = NetLeadSeconds;

        /// <summary>The host's last yaw on the own puppet (radians), for diagnostics.</summary>
        public float NetHostYaw { get { return _netYaw; } }

        /// <summary>Slack on top of the lead, so a resting turret never jitters.</summary>
        public const float NetSlackRadians = 1.5f * Mathf.Deg2Rad;

        private float _netYaw;
        private float _netPitch;
        private bool _netAngles;
        private bool _netAimKnown;

        /// <summary>Places the pivots at the host's yaw and pitch (radians).</summary>
        public void ApplyNetworked(float yaw, float pitch)
        {
            if (NetPredicted)
            {
                // Own puppet: keep the host's angles for the reconcile only.
                _netYaw = yaw;
                _netPitch = pitch;
                if (!_netAngles)
                {
                    _netAngles = true;
                    Yaw = yaw;
                    Pitch = pitch;
                    ApplyPivots();
                }
                return;
            }
            Yaw = yaw;
            Pitch = pitch;
            _renderReady = false;
            ApplyPivots();
        }

        /// <summary>
        /// Somebody else's puppet: the host's own smoothed aim point and arty
        /// flag, so a replayed rocket steers onto exactly the point the host
        /// steers the real one onto and lofts the same way.
        /// </summary>
        public void ApplyNetAim(Vector3 aimLerp, bool lob)
        {
            if (NetPredicted || !IsFinite(aimLerp)) { return; }
            AimTarget = aimLerp;
            AimTargetLerp = aimLerp;
            LobAim = lob;
            _netAimKnown = true;
        }

        /// <summary>Back to a simulated rig (host migration).</summary>
        public void ResetNet()
        {
            NetPredicted = false;
            _netAngles = false;
            _netAimKnown = false;
        }

        /// <summary>
        /// Per physics step on a network puppet, where <see cref="Rotate"/>
        /// never runs. The local player's own tank predicts its traverse from
        /// its own camera (<see cref="NetPredictTick"/>); somebody else's takes
        /// the host's aim point through <see cref="ApplyNetAim"/> and only
        /// falls back to the barrel ray before the first host sample.
        /// </summary>
        public void NetAimTick(float delta, bool ownCamera)
        {
            if (!NetDriven || _tank == null) { return; }
            if (ownCamera)
            {
                NetPredictTick(delta);
                return;
            }
            if (_netAimKnown || GunPivot == null) { return; }
            var origin = Muzzle != null ? Muzzle.position : GunPivot.position;
            var forward = GunPivot.forward;
            RaycastHit hit;
            var point = Physics.Raycast(origin + forward * 0.5f, forward, out hit,
                    AimMath.TraceDistance, ~0, QueryTriggerInteraction.Ignore)
                ? hit.point
                : origin + forward * AimMath.TraceDistance;
            AimTarget = point;
            AimTargetLerp = AdjustAim(AimTargetLerp, point, delta, AimLerpFactor);
        }

        /// <summary>
        /// Own puppet: the same adjust_aim / rotate_turret / rotate_gun step
        /// the host runs on this tank from the same crosshair input, so the
        /// barrel, the gun reticle and the arty lob react at once instead of a
        /// round trip later. The hull steer assist stays with the host (it
        /// moves the hull). The host's angles bound the lead to what the
        /// input latency explains, which also absorbs a damaged turret ring
        /// the puppet does not know about.
        /// </summary>
        public void NetPredictTick(float delta)
        {
            if (!NetDriven || _tank == null || TurretPivot == null || GunPivot == null) { return; }
            if (!_netAngles) { _netYaw = Yaw; _netPitch = Pitch; }

            AdjustAim(delta);
            _renderFromYaw = Yaw;
            _renderFromPitch = Pitch;
            var previous = Yaw;
            if (_tank.TurretBlock || _tank.Dead)
            {
                AngularVelocity = 0f;
            }
            else
            {
                RotateTurret(delta, false);
                RotateGun(delta);
            }

            Yaw = Reconcile(Yaw, _netYaw, _tank.TurretRotationSpeed);
            Pitch = Reconcile(Pitch, _netPitch, _gun.RotationSpeed * Mathf.Deg2Rad);
            if (!_tank.TurretBlock) { AngularVelocity = AimMath.TurretAngularVelocity(previous, Yaw, delta); }
            ApplyPivots();

            _renderToYaw = Yaw;
            _renderToPitch = Pitch;
            _renderStep = delta;
            _renderStepTime = Time.fixedTime;
            _renderReady = true;
        }

        /// <summary>Clamps a predicted angle to the host's plus the lead its speed allows.</summary>
        public static float Reconcile(float predicted, float host, float speedRadians)
        {
            var lead = Mathf.Clamp(NetLead, NetLeadSeconds, NetLeadMaxSeconds);
            var tolerance = Mathf.Abs(speedRadians) * lead + NetSlackRadians;
            var error = AimMath.AngleDifference(host, predicted);
            if (Mathf.Abs(error) <= tolerance) { return predicted; }
            return host + Mathf.Sign(error) * tolerance;
        }

        private void ApplyPivots()
        {
            if (TurretPivot != null) { TurretPivot.localRotation = Quaternion.Euler(0f, Yaw * Mathf.Rad2Deg, 0f); }
            if (GunPivot != null) { GunPivot.localRotation = Quaternion.Euler(Pitch * Mathf.Rad2Deg, 0f, 0f); }
        }

        private static bool IsFinite(Vector3 v)
        {
            return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
                || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
        }

        /// <summary><c>aim_target_lerp_factor</c>: how hard the rig chases the
        /// aim point. The bots lower it by skill; the player keeps the source's
        /// default of ten.</summary>
        [System.NonSerialized]
        public float AimLerpFactor = AimMath.AimLerpSpeed;

        private BattleTank _tank;
        private GunInfo _gun = new GunInfo();

        // Render-only interpolation state for the two pivots.
        private float _renderFromYaw;
        private float _renderToYaw;
        private float _renderFromPitch;
        private float _renderToPitch;
        private float _renderStep;
        private float _renderStepTime;
        private bool _renderReady;

        /// <summary>
        /// <c>get_gun_pos</c>: the muzzle pushed back along the barrel. The source
        /// adds <c>gun.global_basis.z</c>, which points backwards, so the aim rays
        /// start behind the barrel rather than at its tip.
        /// </summary>
        public Vector3 GunPosition
        {
            get
            {
                if (Muzzle == null || GunPivot == null)
                {
                    return transform.position;
                }

                return GunOrigin(Muzzle.position, GunPivot.forward, GunMeshLength);
            }
        }

        /// <summary>Builds the pivots and hangs the turret and gun objects off them.</summary>
        public void Rig(BattleTank tank)
        {
            _tank = tank;
            GunMeshLength = GunMeshLengthFallback;
            if (tank == null || tank.Model == null)
            {
                return;
            }

            if (tank.GunInfo != null)
            {
                _gun = tank.GunInfo;
            }

            var paths = TankModelAssets.For(tank.Card);
            var asset = TankAssetSource.NodesAsset(paths);
            if (asset == null)
            {
                Debug.LogError("[Blitz] tank node table is missing: " + paths.NodeDataResource);
                return;
            }

            var nodes = TankNodeTable.Parse(asset.text);
            // The export carries every researchable turret and gun of the
            // client, so the rig picks the pair the loadout installed and the
            // view hides the others.
            TurretNodeName = nodes.ResolveTurret(
                tank.TurretModule != null ? tank.TurretModule.Mesh : null);
            GunNodeName = nodes.ResolveGun(TurretNodeName, tank.GunMesh);
            MuzzleNodeName = nodes.MuzzleOf(TurretNodeName, GunNodeName);
            TankModuleView.ShowInstalled(tank.Model, nodes, TurretNodeName, GunNodeName);
            var model = tank.Model.transform;

            // Every pivot is placed by its model space origin and then keeps the
            // geometry's world placement, so the importer's own nesting does not
            // matter.
            TurretPivot = NewPivot("turret_pivot", model, model, nodes.World(TurretNodeName));
            GunPivot = NewPivot("gun_pivot", TurretPivot, model, nodes.World(GunNodeName));
            Muzzle = NewPivot("gun_shoot_point", GunPivot, model, nodes.World(MuzzleNodeName));

            Reparent(model, TurretNodeName, TurretPivot);
            ReparentSheets(model, TurretNodeName, TurretPivot);
            var gun = Reparent(model, GunNodeName, GunPivot);
            if (gun != null && gun.sharedMesh != null)
            {
                // gun_mesh_len = gun_mdl.mesh.get_aabb().size.z
                var bounds = gun.sharedMesh.bounds;
                // The Silencer draws its barrel tube on a sheet of its own.
                foreach (var sheet in ReparentSheets(model, GunNodeName, GunPivot))
                {
                    if (sheet.sharedMesh != null) { bounds.Encapsulate(sheet.sharedMesh.bounds); }
                }

                GunMeshLength = Mathf.Abs(bounds.size.z);
            }
            else
            {
                ReparentSheets(model, GunNodeName, GunPivot);
            }

            RigRotor(tank, nodes, model);

            // A camera or an AI writes aim_target before the first physics step
            // in the source. Until BattleCamera lands, the rig looks straight
            // ahead so a fresh tank holds its turret centred instead of slewing
            // towards the world origin.
            var hull = tank.transform;
            AimTarget = hull.position + hull.forward * AimMath.TraceDistance;
            AimTargetLerp = AimTarget;
        }

        /// <summary>
        /// Runs the source's traverse step. <c>_physics_process</c> skips both
        /// rotations while the turret is blocked and idles the steer assist.
        /// </summary>
        public void Rotate(float delta)
        {
            if (_tank == null || NetDriven)
            {
                return;
            }

            if (_tank.TurretBlock)
            {
                AngularVelocity = 0f;
                _tank.Drive(0f, 0f, true);
                return;
            }

            if (TurretPivot == null || GunPivot == null)
            {
                return;
            }

            _renderFromYaw = Yaw;
            _renderFromPitch = Pitch;

            RotateTurret(delta);
            RotateGun(delta);

            // The rig steps once per physics tick while the camera renders
            // far more often, so the raw pivot angles arrive in 50 Hz stairs.
            // The hull hides that behind Rigidbody interpolation; the turret
            // gets the same treatment in LateUpdate.
            _renderToYaw = Yaw;
            _renderToPitch = Pitch;
            _renderStep = delta;
            _renderStepTime = Time.fixedTime;
            _renderReady = true;
        }

        /// <summary>
        /// Draws the pivots at where the traverse actually is between two
        /// physics steps. Nothing about the simulated angles changes: only the
        /// transforms the camera sees this frame are interpolated.
        /// </summary>
        private void LateUpdate()
        {
            if (!_renderReady || TurretPivot == null || GunPivot == null)
            {
                return;
            }

            if (_renderStep <= 0f)
            {
                return;
            }

            var alpha = Mathf.Clamp01((Time.time - _renderStepTime) / _renderStep);
            var yaw = _renderFromYaw +
                AimMath.AngleDifference(_renderFromYaw, _renderToYaw) * alpha;
            var pitch = _renderFromPitch +
                AimMath.AngleDifference(_renderFromPitch, _renderToPitch) * alpha;

            TurretPivot.localRotation = Quaternion.Euler(0f, yaw * Mathf.Rad2Deg, 0f);
            GunPivot.localRotation = Quaternion.Euler(pitch * Mathf.Rad2Deg, 0f, 0f);
        }

        /// <summary><c>gun_deviation_reached</c>: true while resting on a limit.</summary>
        public bool DeviationReached()
        {
            return IsDeviationReached(
                Yaw,
                Pitch,
                _gun.DeviationLeft,
                _gun.DeviationRight,
                _gun.DeviationUp,
                _gun.DeviationDown);
        }

        /// <summary><c>set_aim_target</c>: the point the rig is told to look at.</summary>
        public void SetAim(Vector3 target, bool clampY = false)
        {
            if (clampY && _tank != null)
            {
                target.y = Mathf.Min(_tank.transform.position.y, target.y);
            }

            AimTarget = target;
        }

        /// <summary>
        /// <c>adjust_aim</c>: the smoothing half plus the shell drop lead. The
        /// lead needs the loaded shell, so it is read from the tank and folded
        /// into the target before the snap and the ease run.
        /// </summary>
        public void AdjustAim(float delta)
        {
            var target = AimTarget;
            var shell = _tank != null ? _tank.CurrentShell() : null;

            // adjust_aim returns right after the lerp for a homing shell: a
            // rocket is steered onto the crosshair itself, so lifting the aim
            // point by a shell drop would only tilt the barrel off the target.
            var homing = shell != null && shell.Atgm;
            if (AimBallisticLead && !homing && shell != null && GunPivot != null)
            {
                var distance = Vector3.Distance(GunPivot.position, target);
                target.y += AimMath.LeadGravityY(
                    distance,
                    Mathf.Abs(shell.Speed),
                    AimMath.ShellGravity(shell.GravityMultiplier, false)
                        * BattleProjectile.ShellGravityScaleNow(),
                    shell.MaxDistance);
            }

            AimTargetLerp = AdjustAim(AimTargetLerp, target, delta, AimLerpFactor);
        }

        /// <summary><c>adjust_aim</c> without the scene graph.</summary>
        public static Vector3 AdjustAim(Vector3 current, Vector3 target, float delta)
        {
            return AdjustAim(current, target, delta, AimMath.AimLerpSpeed);
        }

        /// <summary><c>adjust_aim</c> with the hull's own lerp factor.</summary>
        public static Vector3 AdjustAim(
            Vector3 current, Vector3 target, float delta, float lerpFactor)
        {
            // The source compares against 50 * 2 and eases with delta * factor.
            if ((target - current).sqrMagnitude > AimMath.AimSnapDistanceSquared)
            {
                return target;
            }

            return current + (target - current) * delta * lerpFactor;
        }

        private void RotateTurret(float delta, bool steerAssist = true)
        {
            var hull = _tank.transform;

            // Source quirk: the magnitude follows aim_target_lerp but the side is
            // read off the unsmoothed aim_target.
            var localAimX = hull.InverseTransformPoint(AimTarget).x;
            var target = TurretTargetYaw(GunPosition, AimTargetLerp, hull.forward, hull.up, localAimX);

            if (steerAssist && !TraverseLock)
            {
                _tank.Drive(0f, SteerAssist(target, _gun.DeviationLeft, _gun.DeviationRight), true);
            }

            target = ClampYaw(target, _gun.DeviationLeft, _gun.DeviationRight);

            var previous = Yaw;
            // turret_rot_speed is already radians per second on the card.
            var speed = _tank.TurretRotationSpeed * delta;
            Yaw = AimMath.RotateTowards(previous, target, speed);
            TurretPivot.localRotation = Quaternion.Euler(0f, Yaw * Mathf.Rad2Deg, 0f);
            AngularVelocity = AimMath.TurretAngularVelocity(previous, Yaw, delta);
        }

        private void RotateGun(float delta)
        {
            var target = GunTargetPitch(GunPosition, AimTargetLerp, TurretPivot.up);
            target = ClampPitch(target, _gun.DeviationUp, _gun.DeviationDown);

            // The overhead view lobs the rocket in, so the barrel is lifted by
            // the range rather than laid on the aim line: a target next to the
            // hull needs almost nothing, a far one needs everything the gun
            // has. Pitch is negative going up.
            if (LobAim)
            {
                var flat = AimTargetLerp - GunPosition;
                var height = flat.y;
                flat.y = 0f;
                var shell = _tank.CurrentShell();
                target = -ArtyMath.GuidedLaunchPitch(flat.magnitude, _gun.DeviationUp,
                    shell != null ? Mathf.Abs(shell.Speed) : 0f,
                    shell != null ? shell.TurnSpeed : 0f, height) * Mathf.Deg2Rad;
                target = ClampPitch(target, _gun.DeviationUp, _gun.DeviationDown);
            }

            // gun_info.rot_speed stays in degrees per second.
            var speed = _gun.RotationSpeed * Mathf.Deg2Rad * delta;
            Pitch = AimMath.RotateTowards(Pitch, target, speed);
            GunPivot.localRotation = Quaternion.Euler(Pitch * Mathf.Rad2Deg, 0f, 0f);
        }

        /// <summary><c>get_gun_pos</c> without the scene graph.</summary>
        public static Vector3 GunOrigin(Vector3 muzzle, Vector3 gunForward, float gunMeshLength)
        {
            return muzzle - gunForward * (gunMeshLength + GunOriginMargin);
        }

        /// <summary>
        /// <c>rotate_turret</c>'s target yaw. The source takes the unsigned angle
        /// between the hull forward and the aim direction flattened onto the hull
        /// plane, then signs it from the aim point's own side.
        /// </summary>
        public static float TurretTargetYaw(
            Vector3 gunPosition,
            Vector3 aimTarget,
            Vector3 hullForward,
            Vector3 hullUp,
            float localAimX)
        {
            var toTarget = (aimTarget - gunPosition).normalized;
            var projected = Vector3.ProjectOnPlane(toTarget, hullUp);
            if (projected.sqrMagnitude <= SourceEpsilon)
            {
                return 0f;
            }

            var angle = Vector3.Angle(hullForward, projected.normalized) * Mathf.Deg2Rad;

            // sign(0) is 0 in the source, which recentres the turret.
            return angle * SuspensionTuning.Sign(localAimX);
        }

        /// <summary>
        /// <c>rotate_gun</c>'s target pitch: the angle between the aim direction
        /// and its own projection onto the turret plane, signed by which side of
        /// that plane the target sits on.
        /// </summary>
        public static float GunTargetPitch(Vector3 gunPosition, Vector3 aimTarget, Vector3 turretUp)
        {
            var toTarget = (aimTarget - gunPosition).normalized;
            var projected = Vector3.ProjectOnPlane(toTarget, turretUp.normalized);
            if (projected.sqrMagnitude <= SourceEpsilon)
            {
                return 0f;
            }

            projected = projected.normalized;
            var angle = Vector3.Angle(projected, toTarget) * Mathf.Deg2Rad;

            // Elevation is a negative pitch once the port mirrors the axis.
            return -angle * SuspensionTuning.Sign(toTarget.y - projected.y);
        }

        /// <summary>Clamps a yaw into the card's traverse arc.</summary>
        public static float ClampYaw(float yaw, float deviationLeft, float deviationRight)
        {
            return SuspensionTuning.Clamp(
                yaw,
                -Mathf.Abs(deviationLeft) * Mathf.Deg2Rad,
                Mathf.Abs(deviationRight) * Mathf.Deg2Rad);
        }

        /// <summary>Clamps a pitch into the card's elevation arc.</summary>
        public static float ClampPitch(float pitch, float deviationUp, float deviationDown)
        {
            return SuspensionTuning.Clamp(
                pitch,
                -Mathf.Abs(deviationUp) * Mathf.Deg2Rad,
                Mathf.Abs(deviationDown) * Mathf.Deg2Rad);
        }

        /// <summary>
        /// The hull nudge <c>rotate_turret</c> queues when the target sits outside
        /// the traverse arc. The returned value is the source's right axis, so a
        /// target past the left limit steers the hull left.
        /// </summary>
        public static float SteerAssist(float targetYaw, float deviationLeft, float deviationRight)
        {
            if (targetYaw < -Mathf.Abs(deviationLeft) * Mathf.Deg2Rad)
            {
                return -1f;
            }

            if (targetYaw > Mathf.Abs(deviationRight) * Mathf.Deg2Rad)
            {
                return 1f;
            }

            return 0f;
        }

        /// <summary><c>gun_deviation_reached</c> without the scene graph.</summary>
        public static bool IsDeviationReached(
            float yaw,
            float pitch,
            float deviationLeft,
            float deviationRight,
            float deviationUp,
            float deviationDown)
        {
            return CameraTuning.IsEqualApprox(pitch, -Mathf.Abs(deviationUp) * Mathf.Deg2Rad)
                || CameraTuning.IsEqualApprox(pitch, Mathf.Abs(deviationDown) * Mathf.Deg2Rad)
                || CameraTuning.IsEqualApprox(yaw, -Mathf.Abs(deviationLeft) * Mathf.Deg2Rad)
                || CameraTuning.IsEqualApprox(yaw, Mathf.Abs(deviationRight) * Mathf.Deg2Rad);
        }

        /// <summary>Hangs the gun's rotor (if the export has one) off the gun.</summary>
        private void RigRotor(BattleTank tank, TankNodeTable nodes, Transform model)
        {
            var rotorName = GunNodeName + TankNodeTable.RotorSuffix;
            if (!nodes.Has(rotorName) || GunPivot == null) { return; }

            GunRotor = NewPivot("gun_rotor", GunPivot, model, nodes.World(rotorName));
            var rotor = Reparent(model, rotorName, GunRotor);
            if (rotor != null && rotor.sharedMesh != null)
            {
                GunMeshLength = Mathf.Max(GunMeshLength, Mathf.Abs(rotor.sharedMesh.bounds.size.z));
            }

            RigGunClips(nodes, model, rotorName);

            // One barrel per shell of the burst; the clip spends the burst
            // interval on each 360/n turn.
            var barrels = 1;
            var interval = 0.333f;
            if (tank.GunInfo != null && tank.GunInfo.Burst != null)
            {
                for (var i = 0; i < tank.GunInfo.Burst.Bursts.Count; i++)
                { barrels = Mathf.Max(barrels, tank.GunInfo.Burst.Bursts[i]); }
                if (tank.GunInfo.Burst.IntraBurst > 0.01f) { interval = tank.GunInfo.Burst.IntraBurst; }
            }

            RotorBarrels = barrels;
            _rotorSpeed = 360f / barrels / interval;
            tank.ProjectileFired += (first, slot) =>
            {
                if (_clips.Count == 0 || !GunClipDrives(rotorName)) { SpinRotor(); }
                if (first) { PlayGunClip(GunClip.BurstTrigger); }
            };
            if (tank.Gun != null)
            {
                tank.Gun.Reloaded += () => PlayGunClip(GunClip.ReloadedTrigger);
            }
        }

        /// <summary>
        /// Hangs every part a baked gun clip moves off the gun: the rotor keeps
        /// its own pivot, the others get one at their exported origin.
        /// </summary>
        private void RigGunClips(TankNodeTable nodes, Transform model, string rotorName)
        {
            var clips = nodes.ClipsOf(GunNodeName);
            for (var c = 0; c < clips.Count; c++)
            {
                foreach (var track in clips[c].Tracks)
                {
                    if (_clipPivots.ContainsKey(track)) { continue; }
                    Transform pivot = null;
                    if (track == rotorName)
                    {
                        pivot = GunRotor;
                    }
                    else if (nodes.Has(track))
                    {
                        pivot = NewPivot(track + "_pivot", GunPivot, model, nodes.World(track));
                        Reparent(model, track, pivot);
                    }

                    if (pivot == null) { continue; }
                    _clipPivots[track] = pivot;
                    _clipRest[track] = pivot.localPosition;
                }

                _clips.Add(clips[c]);
                _clipTimes.Add(-1f);
            }
        }

        private bool GunClipDrives(string track)
        {
            for (var i = 0; i < _clips.Count; i++)
            {
                if (_clips[i].HasTrack(track)) { return true; }
            }

            return false;
        }

        /// <summary>Starts (or restarts) every gun clip with this trigger.</summary>
        public void PlayGunClip(string trigger)
        {
            for (var i = 0; i < _clips.Count; i++)
            {
                if (_clips[i].Trigger == trigger) { _clipTimes[i] = 0f; }
            }
        }

        /// <summary>Advances the playing gun clips by <paramref name="delta"/> seconds.</summary>
        public void StepGunClips(float delta)
        {
            for (var i = 0; i < _clips.Count; i++)
            {
                if (_clipTimes[i] < 0f) { continue; }
                var clip = _clips[i];
                var time = Mathf.Min(clip.Duration, _clipTimes[i] + delta);
                foreach (var track in clip.Tracks)
                {
                    Transform pivot;
                    if (!_clipPivots.TryGetValue(track, out pivot)
                        || !clip.Sample(track, time, _samplePosition, _sampleRotation))
                    {
                        continue;
                    }

                    pivot.localPosition = _clipRest[track]
                        + new Vector3(_samplePosition[0], _samplePosition[1], _samplePosition[2]);
                    pivot.localRotation = new Quaternion(
                        _sampleRotation[0], _sampleRotation[1], _sampleRotation[2], _sampleRotation[3]);
                }

                _clipTimes[i] = time >= clip.Duration ? -1f : time;
            }
        }

        /// <summary>Turns the rotor on by one barrel.</summary>
        public void SpinRotor()
        {
            if (GunRotor == null || RotorBarrels <= 0) { return; }
            RotorTarget += 360f / RotorBarrels;
        }

        private void Update()
        {
            StepGunClips(Time.deltaTime);
            if (GunRotor == null || RotorAngle >= RotorTarget) { return; }
            RotorAngle = Mathf.Min(RotorTarget, RotorAngle + _rotorSpeed * Time.deltaTime);
            if (RotorAngle >= 360f && RotorTarget >= 360f)
            {
                RotorAngle -= 360f;
                RotorTarget -= 360f;
            }

            GunRotor.localRotation = Quaternion.AngleAxis(RotorAngle, Vector3.forward);
        }

        private static Transform NewPivot(string name, Transform parent, Transform model, Vec3 origin)
        {
            var pivot = new GameObject(name).transform;
            pivot.SetParent(parent, false);
            pivot.localRotation = Quaternion.identity;
            pivot.localScale = Vector3.one;
            pivot.position = model.TransformPoint(new Vector3(origin.X, origin.Y, origin.Z));
            return pivot;
        }

        private static MeshFilter Reparent(Transform model, string objectName, Transform pivot)
        {
            var filters = model.GetComponentsInChildren<MeshFilter>(true);
            for (var i = 0; i < filters.Length; i++)
            {
                if (TrimCloneSuffix(filters[i].gameObject.name) != objectName)
                {
                    continue;
                }

                filters[i].transform.SetParent(pivot, true);
                return filters[i];
            }

            Debug.LogWarning(
                "[Blitz] the merged MS-1 model has no separate " + objectName
                + " renderer; the authored pivot still drives aiming and armour");
            return null;
        }

        /// <summary>
        /// Hangs the extra-sheet objects of a part (<c>gun_04_sheet1</c>) off
        /// the same pivot as the part itself.
        /// </summary>
        private static List<MeshFilter> ReparentSheets(Transform model, string objectName, Transform pivot)
        {
            var moved = new List<MeshFilter>();
            var filters = model.GetComponentsInChildren<MeshFilter>(true);
            for (var i = 0; i < filters.Length; i++)
            {
                if (TankNodeTable.SheetOwner(TrimCloneSuffix(filters[i].gameObject.name)) != objectName)
                {
                    continue;
                }

                filters[i].transform.SetParent(pivot, true);
                moved.Add(filters[i]);
            }

            return moved;
        }

        private static string TrimCloneSuffix(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var index = value.IndexOf("(Clone)", System.StringComparison.Ordinal);
            return index >= 0 ? value.Substring(0, index).Trim() : value;
        }
    }
}
