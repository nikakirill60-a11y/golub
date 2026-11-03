using System;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Lighting;
using BlitzLegacy.Runtime.Platform;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Port of <c>assets/tanks/base/input_camera.gd</c> together with
    /// <c>base_tank.cam_trace</c>: the arcade rig on a spring arm, the sniper rig
    /// pinned to the gun, the fov ladder behind the zoom and the trace that
    /// hands the turret its aim point.
    ///
    /// The soft auto aim pull, its escape budget and the automatic sniper zoom
    /// ride on the same yaw and pitch fields: the capture drags the rig towards
    /// the tracked hit while the gun keeps following the crosshair.
    /// </summary>
    public sealed class BattleCamera : MonoBehaviour
    {
        private const int TraceBufferSize = 64;

        private readonly RaycastHit[] _hits = new RaycastHit[TraceBufferSize];

        private BattleTank _tank;
        private Transform _springArm;
        private Camera _camera;
        private Transform _sniperRoot;
        private Camera _sniperCamera;
        private Transform _artyRoot;
        private Camera _artyCamera;
        private BattleArtyReticle _reticle;

        private float _yaw;
        private float _pitch;
        private float _sniperYaw;
        private float _sniperPitch;
        private float _artyYaw;
        private Vector3 _artyAim;
        private Vector3 _artyAimTarget;
        private float _height;
        private bool _heightSmoothing;
        private float _heightOffset;
        private float _springLength;
        private int _zoomIndex;
        private float _pinchLast = -1f;
        private SettingsData _settings;

        /// <summary><c>stick_suppress</c>: the pull stands down while aiming.</summary>
        private float _stickSuppress;

        /// <summary><c>escape_yaw</c>: yaw the player has swiped away, in radians.</summary>
        private float _escapeYaw;

        /// <summary><c>escape_target</c>: the tank that budget belongs to.</summary>
        private BattleTank _escapeTarget;

        /// <summary><c>sniper_manual_override_time</c>.</summary>
        private float _sniperManualOverride;

        /// <summary><c>cam_len_min</c> after <c>_ready</c> reads the settings.</summary>
        public float LengthMin { get; private set; }

        /// <summary><c>cam_len_max</c>.</summary>
        public float LengthMax { get; private set; }

        /// <summary><c>cam_len_step</c>.</summary>
        public float LengthStep { get; private set; }

        /// <summary><c>tank.sniper_mode</c>, as far as the camera is concerned.</summary>
        /// <summary><c>sniper_mode_switched</c> from base_tank.gd.</summary>
        public event Action<bool> SniperModeSwitched;

        /// <summary>Raised on the edges of the arty view.</summary>
        public event Action<bool> ArtyModeSwitched;

        public bool SniperMode { get; private set; }

        /// <summary>Whether the overhead ATGM view is up.</summary>
        public bool ArtyMode { get; private set; }

        /// <summary>Only the currently selected ATGM gets the overhead view.</summary>
        public bool ArtyAvailable
        {
            get
            {
                var shell = _tank != null ? _tank.CurrentShell() : null;
                return shell != null && shell.Atgm;
            }
        }

        /// <summary><c>spring_arm.spring_length</c>.</summary>
        public float SpringLength { get { return _springLength; } }

        /// <summary>Arcade yaw, in radians. The source keeps it on this node.</summary>
        public float Yaw { get { return _yaw; } }

        /// <summary>Arcade pitch, in radians. The source keeps it on the arm.</summary>
        public float Pitch { get { return _pitch; } }

        /// <summary>
        /// <c>camera_controller.tank</c>: the hull the rig follows. Observing a
        /// dead player's team mate is nothing but a swap of this reference.
        /// </summary>
        public BattleTank Tank
        {
            get { return _tank; }
            set
            {
                if (_tank == value) { return; }
                if (ArtyMode) { SetArtyMode(false); }
                if (_tank != null) { _tank.SetScopeModelVisible(true); }
                _tank = value;
                if (_tank != null) { _tank.SetScopeModelVisible(!SniperMode); }
            }
        }

        public Camera Arcade { get { return _camera; } }

        public Camera Sniper { get { return _sniperCamera; } }

        /// <summary><c>%camera_sniper if sniper_mode else %camera</c>.</summary>
        public Camera Active
        {
            get
            {
                if (ArtyMode) { return _artyCamera; }
                return SniperMode ? _sniperCamera : _camera;
            }
        }

        /// <summary>The overhead rig.</summary>
        public Camera Arty { get { return _artyCamera; } }

        /// <summary>The overhead view hears from this far above the observed hull, not from the sky.</summary>
        public const float ArtyListenerHeight = 3f;

        /// <summary>
        /// Where the battle is heard from: the active camera, as Godot listens
        /// from the current one. The overhead artillery view hears from just
        /// above the observed tank instead, facing the way the view faces.
        /// </summary>
        public bool ListenerPose(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (this == null) { return false; }
            var camera = Active;
            if (camera == null) { return false; }

            var eye = camera.transform;
            if (ArtyMode && _tank != null)
            {
                var heading = Vector3.ProjectOnPlane(eye.forward, Vector3.up);
                if (heading.sqrMagnitude < 1e-6f) { heading = Vector3.ProjectOnPlane(eye.up, Vector3.up); }
                position = _tank.transform.position + Vector3.up * ArtyListenerHeight;
                if (heading.sqrMagnitude > 1e-6f) { rotation = Quaternion.LookRotation(heading, Vector3.up); }
                return true;
            }

            position = eye.position;
            rotation = eye.rotation;
            return true;
        }

        /// <summary><c>cam_trace_last_dist</c>.</summary>
        public float TraceDistanceLast { get; private set; }

        /// <summary>Whether the latest <c>cam_trace_result</c> hit geometry.</summary>
        public bool TraceHitLast { get; private set; }

        /// <summary><c>aim_target_dist</c>.</summary>
        public float AimTargetDistance { get; private set; }

        /// <summary><c>aim_target</c>: the point the turret was last sent to.</summary>
        public Vector3 AimTarget { get; private set; }

        /// <summary><c>cam_trace_result.collider</c>.</summary>
        public Collider TraceCollider { get; private set; }

        /// <summary>
        /// Builds the rig around a hull and points it at the hull. The saved
        /// settings own the arm length, the pivot height and the smoothing;
        /// without them the rig falls back to the tuning defaults.
        /// </summary>
        public static BattleCamera Create(
            BattleTank tank, bool mobile, SettingsData settings = null)
        {
            var go = new GameObject("InputCamera");
            var rig = go.AddComponent<BattleCamera>();
            rig.Build(tank, mobile, settings);
            return rig;
        }

        private void Build(BattleTank tank, bool mobile, SettingsData settings)
        {
            _tank = tank;
            _settings = settings;

            // input_camera._ready captures the pointer for every battle tank;
            // the hangar stand never gets this controller in the first place.
            MouseLock.Set(true);

            // _ready overwrites the script's export defaults with the saved
            // settings, so these are the numbers the player actually gets.
            // The port used to skip that read and stay on the constants, which
            // left camera_len_start, camera_height_mul and camera_smoothing
            // saved but ignored.
            LengthMin = CameraTuning.LengthMin;
            LengthMax = CameraTuning.LengthMax;
            LengthStep = CameraTuning.LengthStep;

            var heightMultiplier = settings != null
                ? settings.CameraHeightMul
                : (mobile
                    ? CameraTuning.HeightMultiplierMobile
                    : CameraTuning.HeightMultiplierDesktop);
            _height = CameraTuning.Height * heightMultiplier;
            _heightSmoothing = settings != null
                ? settings.CameraSmoothing
                : CameraTuning.HeightSmoothing;

            _springLength = CameraTuning.ClampLength(
                settings != null ? settings.CameraStartDistance : CameraTuning.LengthStart,
                LengthMin,
                LengthMax);

            _springArm = new GameObject("SpringArm").transform;
            _springArm.SetParent(transform, false);

            _camera = NewCamera("Camera", _springArm);

            // SpringArm3D pushes its children down its own +Z, which the Z
            // mirror turns into -Z here, i.e. behind the pivot.
            _camera.transform.localPosition = new Vector3(0f, 0f, -_springLength);

            // Both camera roots are top_level in the source, so neither hangs
            // off the hull; they copy its position instead.
            _sniperRoot = new GameObject("SniperRoot").transform;
            _sniperCamera = NewCamera("SniperCamera", _sniperRoot);
            _sniperCamera.nearClipPlane = 0.03f;

            // The overhead rig hangs over the hull rather than on it, so it is
            // a third top level root with a fixed fov of its own.
            _artyRoot = new GameObject("ArtyRoot").transform;
            _artyCamera = NewCamera("ArtyCamera", _artyRoot);
            _artyCamera.fieldOfView = CameraTuning.ArtyFov;
            _artyCamera.enabled = false;
            _reticle = BattleArtyReticle.Create();
            _reticle.transform.SetParent(transform, true);

            // Both roots are instantiated inside the tank scene in the source,
            // so they start out facing the same way as the hull that spawned
            // them. This rig is built in world space instead, which left every
            // battle starting with the view turned away from the gun.
            AlignToHull();

            SetSniperMode(false);
            Follow(0f);
        }

        private bool _reportedBrokenAngles;

        private static bool IsFinite(Vector3 value)
        {
            return CameraTuning.IsFinite(value.x) && CameraTuning.IsFinite(value.y)
                && CameraTuning.IsFinite(value.z);
        }

        /// <summary>
        /// Last line of defence before the angles reach the transforms: a
        /// non-finite angle is reset (yaw keeps the other rig's value when it
        /// is sound) and reported once, instead of freezing the view.
        /// </summary>
        private void SanitizeAngles()
        {
            var yaw = CameraTuning.IsFinite(_yaw);
            var pitch = CameraTuning.IsFinite(_pitch);
            var sniperYaw = CameraTuning.IsFinite(_sniperYaw);
            var sniperPitch = CameraTuning.IsFinite(_sniperPitch);
            if (yaw && pitch && sniperYaw && sniperPitch) { return; }

            if (!_reportedBrokenAngles)
            {
                _reportedBrokenAngles = true;
                Debug.LogWarning("[Blitz] battle camera angle was not finite and has been reset: yaw="
                    + _yaw + " pitch=" + _pitch + " sniperYaw=" + _sniperYaw + " sniperPitch=" + _sniperPitch);
            }

            if (!yaw) { _yaw = sniperYaw ? _sniperYaw : 0f; }
            if (!sniperYaw) { _sniperYaw = _yaw; }
            if (!pitch) { _pitch = sniperPitch ? _sniperPitch : 0f; }
            if (!sniperPitch) { _sniperPitch = _pitch; }
        }

        private static Camera NewCamera(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);

            // BattleTank.rotate_tracks measures its LOD distance from
            // Camera.main, and only one of the two rigs is ever enabled.
            go.tag = "MainCamera";

            var camera = go.AddComponent<Camera>();
            camera.fieldOfView = CameraTuning.Fov;
            camera.farClipPlane = CameraTuning.Far;

            // CameraTuning.Far is the distance the maps were authored for, so
            // it is the top of the draw distance slider; anything lower the
            // player picks pulls this plane in.
            GraphicsQuality.Register(camera, CameraTuning.Far);
            return camera;
        }

        /// <summary>
        /// input_camera.gd <c>_process</c>: the rig rides on the hull, higher the
        /// further back the arm is pulled.
        /// </summary>
        public void Follow(float delta)
        {
            if (_tank == null) { return; }

            var coefficient = CameraTuning.HeightCoefficient(_springLength, LengthMax);
            var target = CameraTuning.HeightTarget(_tank.Hull.Height, _height, coefficient);

            if (_heightSmoothing)
            {
                _heightOffset = Mathf.Lerp(
                    _heightOffset, target, delta * CameraTuning.HeightLerpSpeed);
            }
            else
            {
                _heightOffset = target;
            }

            // The source also derives a y_height local from the same terms and
            // then never reads it; only this offset reaches the transform.
            var hull = _tank.transform.position;
            transform.position = new Vector3(hull.x, hull.y + _heightOffset, hull.z);

            // The sniper root has to sit on the gun before anything reads its
            // position, or sniper tracking trails by a frame.
            var turret = _tank.Turret;
            if (turret != null && turret.GunPivot != null)
            {
                _sniperRoot.position = turret.GunPivot.position;
                _sniperCamera.transform.localPosition = Vector3.zero;
            }

            // Smooth the rendered overhead position towards the requested aim
            // point. The target remains unsmoothed so successive drag events
            // accumulate instead of being swallowed by the camera's lag.
            if (ArtyMode)
            {
                _artyAim = SmoothArtyAim(_artyAim, _artyAimTarget, delta);
            }

            // The overhead rig hangs over the aim point, not over the hull:
            // the player walks that point across the map and the rocket is
            // steered onto wherever it has been left.
            _artyRoot.position = new Vector3(
                _artyAim.x, _artyAim.y + CameraTuning.ArtyHeight, _artyAim.z);
            _artyRoot.rotation = Quaternion.Euler(90f, _artyYaw * Mathf.Rad2Deg, 0f);

            // input_camera._process closes with the capture pull and then the
            // automatic zoom, both of which read the positions written above
            // and write the yaw and pitch applied below.
            StickToTarget(delta);
            UpdateSniperAutoZoom(delta);

            SanitizeAngles();
            transform.localRotation = Quaternion.Euler(0f, _yaw * Mathf.Rad2Deg, 0f);
            _springArm.localRotation = Quaternion.Euler(_pitch * Mathf.Rad2Deg, 0f, 0f);
            _sniperRoot.localRotation = Quaternion.Euler(0f, _sniperYaw * Mathf.Rad2Deg, 0f);
            _sniperCamera.transform.localRotation =
                Quaternion.Euler(_sniperPitch * Mathf.Rad2Deg, 0f, 0f);

            ApplySpring();

            // The lob line is drawn from the muzzle to whatever the trace has
            // just handed the turret, and stops at the first thing in the way.
            if (ArtyMode && _reticle != null && turret != null && turret.GunPivot != null)
            {
                _reticle.Draw(
                    turret.Muzzle != null ? turret.Muzzle.position : turret.GunPivot.position,
                    turret.GunPivot.forward * Mathf.Abs(_tank.CurrentShell() != null ? _tank.CurrentShell().Speed : 0f),
                    turret.AimTargetLerp,
                    BattleArtyReticle.Radius(_tank.Dispersion, AimTargetDistance),
                    _tank.CurrentShell(), _tank.transform);
            }
        }

        private void LateUpdate()
        {
            // The source runs this in _process. LateUpdate is the Unity slot
            // that reads the hull after physics interpolation has landed, so
            // the camera never trails the tank by a frame.
            Follow(Time.deltaTime);
        }

        /// <summary>
        /// SpringArm3D casts backwards and pulls its children in to the first
        /// hit, keeping <c>margin</c> of clearance.
        /// </summary>
        private void ApplySpring()
        {
            var distance = _springLength;

            RaycastHit hit;
            if (Trace(_springArm.position, -_springArm.forward, _springLength, out hit))
            {
                distance = SpringDistance(_springLength, hit.distance);
            }

            _camera.transform.localPosition = new Vector3(0f, 0f, -distance);
        }

        /// <summary>SpringArm3D's <c>margin</c> applied to a hit distance.</summary>
        public static float SpringDistance(float length, float hitDistance)
        {
            var pulled = hitDistance - CameraTuning.SpringMargin;
            if (pulled < 0f) { return 0f; }
            return pulled < length ? pulled : length;
        }

        /// <summary>
        /// input_camera.gd's look branch. The source swipe is <c>-relative.x</c>
        /// and a positive Godot yaw turns left, so the Z mirror cancels the sign
        /// twice and the port adds the raw pixel delta. The pitch axis flips
        /// with the mirror as well, which is why the vertical delta is added
        /// rather than subtracted.
        /// </summary>
        public void Look(Vector2 relative)
        {
            // A broken device or pointer event must never reach the angles.
            if (!CameraTuning.IsFinite(relative.x) || !CameraTuning.IsFinite(relative.y)) { return; }
            var settings = ActiveSettings;

            // The overhead view has no rig to turn: the input walks the aim
            // point over the map instead.
            if (ArtyMode)
            {
                PanArty(relative, settings);
                return;
            }
            var auto = _tank != null ? _tank.AutoAim : null;

            // Which way this drag goes relative to the captured tank decides
            // both the damping and the release below.
            var locked = settings != null && settings.AutoAimSoft
                && auto != null && auto.Active;
            var yawError = locked ? YawErrorToTarget() : 0f;
            var pitchError = locked ? PitchErrorToTarget() : 0f;
            var wantsAway = locked && CameraTuning.LookWantsAway(
                relative.x, relative.y, yawError, pitchError);

            // SaveSettings.mouse_sens. The constant is only the value that
            // row starts at, so reading it here left the whole sensitivity
            // slider doing nothing once the battle was loaded.
            var sensitivity = settings != null && settings.MouseSensitivity > 0f
                ? settings.MouseSensitivity
                : CameraTuning.MouseSensitivity;

            // Soft lock: aiming across the captured tank is damped so the
            // player can pick weak spots without having to drop the capture.
            // Turning away is never damped, because cutting the sensitivity
            // exactly while the player fights to escape is what made a lock
            // feel unshakeable.
            if (locked && !wantsAway && _tank.TankInAim == auto.Captured)
            {
                sensitivity *= settings.AutoAimSensMul;
            }

            var deltaYaw = relative.x * CameraTuning.LookScale * sensitivity;
            _yaw += deltaYaw;
            var pitchBefore = _pitch;
            _pitch = ClampPitch(_pitch + relative.y * CameraTuning.LookScale * sensitivity);
            var deltaPitch = _pitch - pitchBefore;

            // Sniper look slows down as the view narrows.
            // SaveSettings.mouse_sens_sniper, for the same reason.
            var sniperPicked =
                settings != null && settings.MouseSensitivitySniper > 0f
                    ? settings.MouseSensitivitySniper
                    : CameraTuning.MouseSensitivitySniper;
            var sniper = CameraTuning.SniperSensitivity(
                _sniperCamera.fieldOfView, sniperPicked);
            var deltaYawSniper = relative.x * CameraTuning.LookScale * sniper;
            _sniperYaw += deltaYawSniper;
            var sniperPitchBefore = _sniperPitch;
            _sniperPitch = ClampPitch(_sniperPitch + relative.y * CameraTuning.LookScale * sniper);
            var deltaPitchSniper = _sniperPitch - sniperPitchBefore;

            if (!locked) { return; }

            // The player is aiming, so the pull stands down for a moment. The
            // capture itself ends only once the player has really swiped away
            // from it: the release is measured from input, never from the angle
            // to the target, because a fast target opens that angle up all by
            // itself.
            _stickSuppress = CameraTuning.StickSuppressTime;
            auto.MarkManualAdjust();

            var moved = SniperMode
                ? CameraTuning.LookTravel(deltaYawSniper, deltaPitchSniper)
                : CameraTuning.LookTravel(deltaYaw, deltaPitch);
            _escapeYaw = CameraTuning.EscapeBudget(_escapeYaw, moved, wantsAway);
            if (CameraTuning.ReleasesCapture(_escapeYaw, settings.AutoAimReleaseAngle))
            {
                auto.Release(true, settings.AutoAimRecaptureDelay);
                _escapeYaw = 0f;
            }
        }

        /// <summary>input_camera.gd clamps both pitches to the same 65 degrees.</summary>
        public static float ClampPitch(float pitch)
        {
            // Every comparison against NaN is false, so a single NaN used to
            // pass the clamp and stay in the pitch for the rest of the battle:
            // the view stopped tilting and Unity logged two errors per frame.
            if (float.IsNaN(pitch)) { return 0f; }
            var min = CameraTuning.AngleMin * Mathf.Deg2Rad;
            var max = CameraTuning.AngleMax * Mathf.Deg2Rad;
            if (pitch < min) { return min; }
            return pitch > max ? max : pitch;
        }

        /// <summary>
        /// <c>receive_scroll_input</c>: in sniper mode the wheel walks the fov
        /// ladder and falls back to arcade past its widest step, otherwise it
        /// moves the arm and enters sniper mode past its shortest length.
        /// </summary>
        public void Zoom(float zoom)
        {
            if (zoom == 0f) { return; }

            if (ArtyMode)
            {
                // There is nothing to zoom up there, so the wheel only walks
                // back down into the third person rig.
                if (CameraTuning.LeavesArty(zoom)) { SetArtyMode(false); }
                return;
            }

            if (SniperMode)
            {
                // The ladder is walked from wherever the automatic zoom has
                // left the scope, and the wheel then owns the fov for a moment.
                _zoomIndex = CameraTuning.ZoomStepIndex(
                    CameraTuning.NearestZoomStep(_sniperCamera.fieldOfView), zoom);
                _sniperCamera.fieldOfView = CameraTuning.SniperFov(_zoomIndex);
                _sniperManualOverride = CameraTuning.SniperManualOverrideTime;

                // The source tests the fov it has just written.
                if (CameraTuning.LeavesSniper(_sniperCamera.fieldOfView, zoom))
                {
                    SetSniperMode(false);
                }

                return;
            }

            if (CameraTuning.EntersArty(_springLength, LengthStep, zoom, LengthMax)
                && ArtyAvailable)
            {
                SetArtyMode(true);
                return;
            }

            if (CameraTuning.EntersSniper(_springLength, LengthStep, zoom, LengthMin))
            {
                SetSniperMode(true);
            }

            // The source clamps the arm and the camera's own z separately; here
            // the arm alone owns that offset, so one clamp covers both lines.
            _springLength = CameraTuning.ClampLength(
                _springLength + LengthStep * zoom, LengthMin, LengthMax);
        }

        /// <summary>
        /// Points both rigs down the hull's own forward, which is where the
        /// source's tank-local camera roots start every battle.
        /// </summary>
        private void AlignToHull()
        {
            if (_tank == null) { return; }

            var forward = _tank.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude <= Mathf.Epsilon) { return; }

            _yaw = Mathf.Atan2(forward.x, forward.z);
            _pitch = 0f;
            _sniperYaw = _yaw;
            _sniperPitch = 0f;
        }

        /// <summary>
        /// Frame-rate-independent overhead camera movement. Exponential
        /// smoothing also keeps large terrain-height changes from popping.
        /// </summary>
        public static Vector3 SmoothArtyAim(
            Vector3 current, Vector3 target, float delta)
        {
            return Vector3.Lerp(current, target, CameraTuning.SmoothingWeight(
                CameraTuning.ArtyPanSmoothingSpeed, delta));
        }

        private void OnEnable()
        {
            if (_tank != null) { _tank.SetScopeModelVisible(!SniperMode); }
        }

        private void OnDisable()
        {
            if (_tank != null) { _tank.SetScopeModelVisible(true); }
        }

        private void OnDestroy()
        {
            if (_tank != null) { _tank.SetScopeModelVisible(true); }
        }

        /// <summary>The camera half of <c>player_controller.sniper_mode_switch</c>.</summary>
        public void SetSniperMode(bool on)
        {
            var changed = SniperMode != on;
            SniperMode = on;
            if (_tank != null) { _tank.SetScopeModelVisible(!on); }
            if (on)
            {
                // sniper_mode_switch reopens the scope at its widest step and
                // restarts the zoom ladder.
                _zoomIndex = 0;
                if (_sniperCamera != null)
                {
                    _sniperCamera.fieldOfView = CameraTuning.SniperFovMax;
                }

                // The two roots sit on one tank in the source; here they
                // accumulate their own mouse deltas at different
                // sensitivities, so they drift apart over a battle and the
                // scope used to open sideways - far enough to look through the
                // tank's own hull. Entering sniper mode hands the scope the
                // aim the player is already holding.
                _sniperYaw = _yaw;
                _sniperPitch = _pitch;
            }
            else if (changed)
            {
                // Same drift on the way out: the arcade rig keeps taking the
                // raw mouse deltas while the scope is up, so by the time the
                // player leaves it the third person view is staring at the
                // sky. Leaving hands the arcade rig the aim back.
                _yaw = _sniperYaw;
                _pitch = _sniperPitch;
            }
            if (!on)
            {
                // Clear even targets still inside the aim-trace hold interval.
                // Outlines have independent ownership and remain available in
                // both views; only the penetration-color overlay is scope-only.
                var roster = BattleRoster.Current;
                if (roster != null)
                {
                    foreach (var target in roster.TanksA)
                        if (target != null && target.ArmorShown) target.PreviewArmor(false, null);
                    foreach (var target in roster.TanksB)
                        if (target != null && target.ArmorShown) target.PreviewArmor(false, null);
                }
                else if (_tank != null && _tank.TankInAim != null)
                    _tank.TankInAim.PreviewArmor(false, null);
            }
            if (_camera != null) { _camera.enabled = !on && !ArtyMode; }
            if (_sniperCamera != null) { _sniperCamera.enabled = on && !ArtyMode; }
            if (changed && SniperModeSwitched != null)
            {
                SniperModeSwitched(on);
            }
        }

        /// <summary>
        /// The overhead ATGM view: one wheel step out past the longest arm, or
        /// a second press of the sniper key. The gun goes to its elevation stop
        /// and the shot is walked in from above instead of aimed down the
        /// barrel, so only tanks carrying an ATGM are offered it.
        /// </summary>
        public void SetArtyMode(bool on)
        {
            if (on && !ArtyAvailable) { return; }

            var changed = ArtyMode != on;
            ArtyMode = on;

            if (on)
            {
                // Same drift as the scope: the view opens on the aim the
                // player is already holding.
                _artyYaw = SniperMode ? _sniperYaw : _yaw;

                // The view opens on the ground ahead of the hull, which is
                // where the player is already looking.
                var hull = _tank != null ? _tank.transform.position : Vector3.zero;
                var ahead = Quaternion.Euler(0f, _artyYaw * Mathf.Rad2Deg, 0f)
                    * Vector3.forward;
                SetArtyAim(hull + (ahead * CameraTuning.ArtyStartRange), true);
                SetSniperMode(false);

                // A soft lock has nothing to pull towards up here, and it
                // would fight the overhead aim point for the turret.
                if (_tank != null && _tank.AutoAim != null)
                {
                    _tank.AutoAim.Release(false, 0f);
                }
            }
            else if (changed)
            {
                _yaw = _artyYaw;
            }

            if (_camera != null) { _camera.enabled = !on && !SniperMode; }
            if (_sniperCamera != null) { _sniperCamera.enabled = SniperMode && !on; }
            if (_artyCamera != null) { _artyCamera.enabled = on; }
            if (_reticle != null) { _reticle.Show(on); }
            if (_tank != null && _tank.Turret != null) { _tank.Turret.LobAim = on; }

            if (changed && ArtyModeSwitched != null)
            {
                ArtyModeSwitched(on);
            }
        }

        /// <summary>
        /// Walking the aim point across the map. The whole map is within
        /// reach: a rocket crosses most of one, so the point is held only
        /// inside the shell's own range, and it rides the ground under it.
        /// </summary>
        private void PanArty(Vector2 relative, SettingsData settings)
        {
            var sensitivity = settings != null && settings.MouseSensitivity > 0f
                ? settings.MouseSensitivity
                : CameraTuning.MouseSensitivity;
            var scale = CameraTuning.ArtyPanScale
                * (sensitivity / CameraTuning.MouseSensitivity);

            var yaw = Quaternion.Euler(0f, _artyYaw * Mathf.Rad2Deg, 0f);
            SetArtyAim(_artyAimTarget + (yaw * new Vector3(
                relative.x * scale, 0f, -relative.y * scale)));
        }

        /// <summary>Holds the aim point in range of the tank and on the ground.</summary>
        private void SetArtyAim(Vector3 point, bool instant = false)
        {
            var hull = _tank != null ? _tank.transform.position : point;
            var shell = _tank != null ? _tank.CurrentShell() : null;
            var range = ArtyMath.Range(shell != null ? shell.MaxDistance : 0f);

            var flat = new Vector3(point.x - hull.x, 0f, point.z - hull.z);
            if (flat.magnitude > range) { flat = flat.normalized * range; }

            var ground = new Vector3(hull.x + flat.x, hull.y, hull.z + flat.z);

            RaycastHit hit;
            if (Physics.Raycast(
                    ground + (Vector3.up * CameraTuning.ArtyGroundProbe),
                    Vector3.down,
                    out hit,
                    CameraTuning.ArtyGroundProbe * 2f,
                    BattleLayers.AimMask,
                    QueryTriggerInteraction.Ignore))
            {
                ground.y = hit.point.y;
            }

            _artyAimTarget = ground;
            if (instant) { _artyAim = ground; }
        }

        /// <summary>Feeds a two finger pinch distance into the zoom.</summary>
        public void Pinch(float distance)
        {
            if (_pinchLast < 0f)
            {
                _pinchLast = distance;
                return;
            }

            var zoom = PinchZoom(distance, _pinchLast);
            if (zoom != 0f)
            {
                Zoom(zoom);
                _pinchLast = distance;
            }
        }

        /// <summary>Any touch going down or up resets the pinch baseline.</summary>
        public void PinchReset()
        {
            _pinchLast = -1f;
        }

        /// <summary>
        /// input_camera.gd: past the deadzone, spreading the fingers pulls the
        /// camera closer, which is the negative direction of the zoom axis.
        /// </summary>
        public static float PinchZoom(float distance, float last)
        {
            var difference = distance - last;
            if (Mathf.Abs(difference) <= CameraTuning.PinchDeadzone) { return 0f; }
            return -Mathf.Sign(difference);
        }

        /// <summary>
        /// <c>base_tank.cam_trace</c>: the crosshair ray the gun follows. The
        /// auto aim bookkeeping around it needs a captured target and lands with
        /// the rest of auto aim.
        /// </summary>
        public void CamTrace()
        {
            if (_tank == null) { return; }

            var camera = Active;
            if (camera == null) { return; }

            var eye = camera.transform;
            var forward = eye.forward;
            var offset = SniperMode ? 0f : AimMath.CameraTraceOffset;
            var from = eye.position + forward * offset;
            var to = eye.position + forward * AimMath.TraceDistance;

            // Util.raycast_3d hands back position = to on a miss, so an empty
            // sky aims at the far end of the ray instead of at the origin.
            var target = to;
            TraceHitLast = false;
            TraceDistanceLast = Vector3.Distance(from, to);

            RaycastHit hit;
            if (Trace(
                    from,
                    forward,
                    AimMath.TraceDistance - offset,
                    out hit,
                    BattleLayers.AimMask))
            {
                target = hit.point;
                TraceHitLast = true;
                TraceDistanceLast = Vector3.Distance(from, target);
            }

            // The auto aim half of cam_trace: the capture is refreshed or
            // dropped here, and it takes the aim point over from the ray
            // whenever the reticle is no longer on the captured model.
            var traced = TraceHitLast ? hit.collider : null;
            TraceCollider = traced;
            target = _tank.AutoAim.TrackTrace(
                target, TraceHitLast, target, traced, from, ActiveSettings);
            // training_cheats.aim_override: the Sandbox aimbot has the last
            // word, after the reticle and the ordinary auto aim.
            var cheats = SandboxCheatController.Of(_tank);
            if (cheats != null)
            {
                target = cheats.AimOverride(from, forward, target, Time.deltaTime);
            }
            AimTarget = target;

            // The source closes cam_trace by handing whatever the crosshair
            // rests on to tank_in_aim, which is what paints the armour and
            // traces the outline.
            _tank.AimTraceHit(traced);

            var turret = _tank.Turret;
            if (turret == null) { return; }

            var gun = turret.GunPivot != null ? turret.GunPivot.position : turret.GunPosition;
            AimTargetDistance = Vector3.Distance(gun, target);
            turret.SetAim(target);
        }

        /// <summary>
        /// The saved settings this rig was built with, falling back to the live
        /// profile so a rig created before the bootstrap still reads the
        /// player's auto aim options.
        /// </summary>
        private SettingsData ActiveSettings
        {
            get
            {
                if (_settings != null) { return _settings; }
                return BlitzBootstrap.Current != null
                    ? BlitzBootstrap.Current.Settings
                    : null;
            }
        }

        /// <summary>
        /// input_camera.gd <c>_stick_camera_to_target</c>: the captured tank
        /// drags the camera towards itself while the gun keeps following the
        /// crosshair, so the player still aims manually across the enemy hull.
        /// It drives the sniper rig as well - bailing out in sniper mode left a
        /// capture doing nothing whatsoever inside the scope.
        /// </summary>
        private void StickToTarget(float delta)
        {
            // Nothing to stick to from overhead.
            if (ArtyMode) { return; }

            _stickSuppress = _stickSuppress > delta ? _stickSuppress - delta : 0f;

            var settings = ActiveSettings;
            if (settings == null || !settings.AutoAimSoft) { return; }

            var auto = _tank != null ? _tank.AutoAim : null;
            if (auto == null || !auto.Active) { return; }

            // A fresh capture starts from a clean escape budget.
            if (auto.Captured != _escapeTarget)
            {
                _escapeTarget = auto.Captured;
                _escapeYaw = 0f;
            }

            // Hands off while the player is actively aiming.
            if (_stickSuppress > 0f) { return; }

            // Between swipes the budget bleeds back.
            _escapeYaw = CameraTuning.DecayEscape(_escapeYaw, delta);

            var sniper = SniperMode;
            var strength = sniper
                ? settings.AutoAimStickStrengthSniper
                : settings.AutoAimStickStrength;
            if (strength <= 0f) { return; }

            // Sniper yaw lives on the sniper root and its pitch on the camera
            // itself, mirroring the arcade split between this node and the arm.
            var origin = sniper ? _sniperRoot.position : transform.position;

            // Lead the concrete turret, hull or track hit chosen on capture (or
            // during a manual adjustment), never a fixed centre.
            var to = auto.TrackingPointLed(strength, settings.AutoAimLeadMul) - origin;
            if (!IsFinite(to)) { return; }
            var flat = new Vector2(to.x, to.z).magnitude;
            if (flat < 1f) { return; }

            var wantYaw = Mathf.Atan2(to.x, to.z);

            // A raised rig looks down, which is the positive pitch here, so the
            // elevation to the target is negated.
            var wantPitch = -Mathf.Atan2(to.y, flat);

            // Exponential smoothing, so the pull no longer tracks harder on a
            // phone that happens to be running at a better framerate.
            var weight = CameraTuning.SmoothingWeight(strength, delta);
            if (!CameraTuning.IsFinite(weight)) { return; }

            if (sniper)
            {
                _sniperYaw = CameraTuning.LerpAngle(_sniperYaw, wantYaw, weight);
                _sniperPitch = ClampPitch(
                    CameraTuning.LerpAngle(_sniperPitch, wantPitch, weight));
            }
            else
            {
                _yaw = CameraTuning.LerpAngle(_yaw, wantYaw, weight);
                _pitch = ClampPitch(CameraTuning.LerpAngle(_pitch, wantPitch, weight));
            }
        }

        /// <summary>
        /// <c>_yaw_error_to_target</c>: the signed yaw the active rig still has
        /// to turn through to face the captured tank.
        /// </summary>
        private float YawErrorToTarget()
        {
            var auto = _tank != null ? _tank.AutoAim : null;
            if (auto == null || !auto.Active) { return 0f; }

            var sniper = SniperMode;
            var yawNow = sniper ? _sniperYaw : _yaw;
            var origin = sniper ? _sniperRoot.position : transform.position;
            var to = auto.TrackingPoint() - origin;
            if (new Vector2(to.x, to.z).sqrMagnitude < 1f) { return 0f; }

            return CameraTuning.AngleDifference(yawNow, Mathf.Atan2(to.x, to.z));
        }

        /// <summary>
        /// The signed pitch the active rig still has to turn through to face
        /// the captured tank, in the same convention as the stick pull.
        /// </summary>
        private float PitchErrorToTarget()
        {
            var auto = _tank != null ? _tank.AutoAim : null;
            if (auto == null || !auto.Active) { return 0f; }

            var sniper = SniperMode;
            var pitchNow = sniper ? _sniperPitch : _pitch;
            var origin = sniper ? _sniperRoot.position : transform.position;
            var to = auto.TrackingPoint() - origin;
            var flat = new Vector2(to.x, to.z).magnitude;
            if (flat < 1f) { return 0f; }

            return CameraTuning.AngleDifference(pitchNow, -Mathf.Atan2(to.y, flat));
        }

        /// <summary>
        /// input_camera.gd <c>_update_sniper_auto_zoom</c>: the scope frames the
        /// tank it is looking at by that tank's measured height, stays wide in a
        /// clinch, and hands the fov back to the wheel for a moment after any
        /// manual step.
        /// </summary>
        private void UpdateSniperAutoZoom(float delta)
        {
            _sniperManualOverride = _sniperManualOverride > delta
                ? _sniperManualOverride - delta
                : 0f;

            var settings = ActiveSettings;
            if (settings == null || !settings.SniperAutoZoom) { return; }
            if (_tank == null || !SniperMode || _sniperCamera == null) { return; }
            if (_sniperManualOverride > 0f) { return; }

            var target = SniperTarget();
            var fov = CameraTuning.SniperFovMax;
            if (target != null)
            {
                fov = CameraTuning.SniperFovForTarget(
                    SniperTargetDistance(target), SniperTargetHeight(target));
            }

            var speed = fov < _sniperCamera.fieldOfView
                ? CameraTuning.SniperAutoZoomInSpeed
                : CameraTuning.SniperAutoZoomOutSpeed;
            _sniperCamera.fieldOfView = CameraTuning.SmoothSniperFov(
                _sniperCamera.fieldOfView, fov, speed, delta);

            // The wheel picks up from wherever the automatic zoom has landed.
            _zoomIndex = CameraTuning.NearestZoomStep(_sniperCamera.fieldOfView);
        }

        /// <summary>
        /// <c>_sniper_target</c>: whatever the crosshair rests on wins over the
        /// capture, so the scope frames the tank the player is looking at.
        /// </summary>
        private BattleTank SniperTarget()
        {
            var direct = _tank.TankInAim;
            if (IsEnemy(direct)) { return direct; }

            var auto = _tank.AutoAim;
            var captured = auto != null ? auto.Captured : null;
            return IsEnemy(captured) ? captured : null;
        }

        private bool IsEnemy(BattleTank target)
        {
            return target != null && target != _tank && !target.Dead
                && target.Team != _tank.Team;
        }

        /// <summary><c>_sniper_target_distance</c>.</summary>
        private float SniperTargetDistance(BattleTank target)
        {
            if (target == _tank.TankInAim && AimTargetDistance > 0f)
            {
                return AimTargetDistance;
            }

            return Vector3.Distance(_sniperRoot.position, target.AutoAimPoint());
        }

        /// <summary><c>_sniper_target_height</c>.</summary>
        private float SniperTargetHeight(BattleTank target)
        {
            var height = target.Hull.Height;
            if (float.IsNaN(height) || float.IsInfinity(height) || height <= 0f)
            {
                return CameraTuning.SniperAutoFallbackTargetHeight;
            }

            return height;
        }

        /// <summary>
        /// A ray that ignores the hull it belongs to, which is what the source's
        /// <c>trace_exclude</c> does with the tank's own collision RIDs.
        /// </summary>
        private bool Trace(
            Vector3 origin,
            Vector3 direction,
            float distance,
            out RaycastHit best,
            int mask = Physics.DefaultRaycastLayers)
        {
            best = default(RaycastHit);

            var count = Physics.RaycastNonAlloc(
                origin,
                direction,
                _hits,
                distance,
                mask,
                QueryTriggerInteraction.Ignore);

            var found = false;
            for (var i = 0; i < count; i++)
            {
                var collider = _hits[i].collider;
                if (collider == null) { continue; }
                if (_tank != null && collider.transform.IsChildOf(_tank.transform)) { continue; }

                if (!found || _hits[i].distance < best.distance)
                {
                    best = _hits[i];
                    found = true;
                }
            }

            return found;
        }
    }
}
