using System;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// Battle camera tuning ported from Godot <c>input_camera.gd</c>, from the
    /// camera nodes in <c>base_tank.tscn</c>, and from the camera defaults in
    /// <c>save_settings.gd</c>. <c>_ready</c> overwrites the script's own export
    /// defaults with the saved settings, so the saved values are the ones the
    /// player actually gets.
    /// </summary>
    public static class CameraTuning
    {
        /// <summary>
        /// input_camera.gd: <c>height = 2.8</c>, scaled by the setting below.
        /// The client has no pivot-height parameter at all - there the camera
        /// only orbits on pitch - but at a 19 m arm out of 40 the coefficient
        /// below lands on ~0.95, i.e. a ~2.66 m rise and a ~8 degree look-down,
        /// which sits inside the client's idle sector (optionsGlobal.yaml
        /// "Camera / Vert custom idle LOW..HIGH", -5..+10). So the ported rig
        /// is kept as is.
        /// </summary>
        public const float Height = 2.8f;

        /// <summary>
        /// save_settings.gd:120 <c>camera_height_mul</c>, clamped to 0.2..2.0.
        /// Only the fallback for a rig built without saved settings; the battle
        /// camera reads <c>SettingsData.CameraHeightMul</c>.
        /// </summary>
        public const float HeightMultiplierMobile = 0.6f;
        public const float HeightMultiplierDesktop = 1f;

        /// <summary>
        /// Arm range. The lower bound and the step still come from
        /// save_settings.gd:353-355 <c>camera_len_min/step</c>; the upper bound
        /// is the client's own slider maximum rather than Godot's 30
        /// (4.8.0 optionsPhone.yaml, "Camera fixed positions / 1) Arcade",
        /// min 0 max 40).
        /// </summary>
        public const float LengthMin = 1f;
        public const float LengthMax = 40f;
        public const float LengthStep = 3f;

        /// <summary>
        /// 4.8.0 options{Phone,Tablet,MiniTablet,Desktop}.yaml, "Camera fixed
        /// positions / 1) Arcade": the arcade camera sits a fixed 19 m behind
        /// the tank on every device, and 11.20.0 still ships the same number.
        /// save_settings.gd's 5.5 m (mobile) / 16 m (desktop) is what made the
        /// legacy view hug the hull.
        /// </summary>
        public const float LengthStart = 19f;

        /// <summary>base_tank.tscn: the SpringArm3D keeps a 0.5 m collision margin.</summary>
        public const float SpringMargin = 0.5f;

        /// <summary>
        /// 4.8.0 options*.yaml, "Camera / 10) Default fov": 54, on a 40..60
        /// slider. base_tank.tscn shipped 50. Far stays at the 10000 the maps
        /// were authored for.
        /// </summary>
        public const float Fov = 54f;
        public const float Far = 10000f;

        /// <summary>input_camera.gd: sniper zoom bounds and the wheel's fov step.</summary>
        public const float SniperFovMin = 2f;
        public const float SniperFovMax = 40f;
        public const float ZoomStep = 15f;

        /// <summary>save_settings.gd:91,95 <c>mouse_sens</c> and <c>mouse_sens_sniper</c>.</summary>
        public const float MouseSensitivity = 0.6f;
        public const float MouseSensitivitySniper = 0.3f;

        /// <summary>
        /// Pitch limits, in degrees, positive being the raised look-down end.
        /// 4.8.0 optionsGlobal.yaml, "Camera / Precise camera max pitch limit"
        /// and "... min pitch limit": 50 and -30. input_camera.gd allowed a
        /// symmetric 65, which let the arcade view swing under the tracks and
        /// straight over the hull. Touch builds cap the top at 25 instead
        /// ("Camera / 9) Max pitch angle limit"); the port has no separate
        /// touch clamp yet.
        /// </summary>
        public const float AngleMax = 50f;
        public const float AngleMin = -30f;

        /// <summary>input_camera.gd: every drag is scaled by 0.001 before the sens.</summary>
        public const float LookScale = 0.001f;

        /// <summary>input_camera.gd: PINCH_DEADZONE, in pixels.</summary>
        public const float PinchDeadzone = 8f;

        /// <summary>input_camera.gd: <c>lerpf(..., delta * 20)</c> when smoothing is on.</summary>
        public const float HeightLerpSpeed = 20f;

        /// <summary>
        /// save_settings.gd:351 <c>camera_smoothing</c> ships disabled. Only
        /// the fallback for a rig built without saved settings; the battle
        /// camera reads <c>SettingsData.CameraSmoothing</c>.
        /// </summary>
        public const bool HeightSmoothing = false;

        /// <summary>save_settings.gd:158 <c>auto_aim_sens_mul</c>, applied while soft locked.</summary>
        public const float AutoAimSensitivityMultiplier = 0.55f;

        private const float HeightCoefficientMin = 0.75f;
        private const float HeightCoefficientMax = 1f;
        private const float HeightCoefficientFull = 2.5f;
        private const float Epsilon = 1e-05f;

        /// <summary>input_camera.gd: the sniper wheel walks this fixed fov ladder.</summary>
        public static readonly float[] ZoomSteps = { 40f, 20f, 10f, 5f, 2f };

        /// <summary>Godot's <c>is_equal_approx</c>, whose tolerance scales with magnitude.</summary>
        public static bool IsEqualApprox(float a, float b)
        {
            if (a == b)
            {
                return true;
            }

            var tolerance = Epsilon * Math.Abs(a);
            if (tolerance < Epsilon)
            {
                tolerance = Epsilon;
            }

            return Math.Abs(a - b) < tolerance;
        }

        /// <summary>
        /// input_camera.gd <c>_process</c>: the camera rides higher as it pulls
        /// back, and jumps to a deliberately larger factor once the arm is fully
        /// extended so the top-down end of the range clears the hull.
        /// </summary>
        public static float HeightCoefficient(float springLength, float lengthMax)
        {
            if (IsEqualApprox(springLength, lengthMax))
            {
                return HeightCoefficientFull;
            }

            if (lengthMax == 0f)
            {
                return HeightCoefficientMin;
            }

            var coefficient = springLength / lengthMax * 2f;
            if (coefficient < HeightCoefficientMin)
            {
                return HeightCoefficientMin;
            }

            return coefficient > HeightCoefficientMax ? HeightCoefficientMax : coefficient;
        }

        /// <summary>input_camera.gd: <c>tank.hull_height + height * coefficient</c>.</summary>
        public static float HeightTarget(float hullHeight, float height, float coefficient)
        {
            return hullHeight + height * coefficient;
        }

        /// <summary>Clamp an arm length into the saved zoom range.</summary>
        public static float ClampLength(float length, float lengthMin, float lengthMax)
        {
            if (length < lengthMin)
            {
                return lengthMin;
            }

            return length > lengthMax ? lengthMax : length;
        }

        /// <summary>
        /// input_camera.gd <c>receive_scroll_input</c>: zooming in walks the ladder
        /// forward, so the wheel delta is subtracted from the current index.
        /// </summary>
        public static int ZoomStepIndex(int current, float zoom)
        {
            var index = current - (int)zoom;
            if (index < 0)
            {
                return 0;
            }

            var last = ZoomSteps.Length - 1;
            return index > last ? last : index;
        }

        /// <summary>The sniper fov at a ladder index.</summary>
        public static float SniperFov(int index)
        {
            if (index < 0)
            {
                index = 0;
            }
            else if (index >= ZoomSteps.Length)
            {
                index = ZoomSteps.Length - 1;
            }

            return ZoomSteps[index];
        }

        /// <summary>
        /// input_camera.gd: sniper look slows down as the view narrows, which is
        /// what keeps a 2 degree fov usable with the same finger travel.
        /// </summary>
        public static float SniperSensitivity(float fov)
        {
            return MouseSensitivitySniper * (fov / SniperFovMax);
        }

        /// <summary>
        /// The same ladder against the sensitivity the player picked;
        /// <see cref="MouseSensitivitySniper"/> is only the default that row
        /// starts at.
        /// </summary>
        public static float SniperSensitivity(float fov, float sensitivity)
        {
            var picked = sensitivity > 0f ? sensitivity : MouseSensitivitySniper;
            return picked * (fov / SniperFovMax);
        }

        /// <summary>Zooming out past the widest sniper step drops back to arcade.</summary>
        public static bool LeavesSniper(float fov, float zoom)
        {
            return fov + ZoomStep * zoom > SniperFovMax;
        }

        /// <summary>Zooming in past the shortest arm enters sniper mode.</summary>
        public static bool EntersSniper(float springLength, float step, float zoom, float lengthMin)
        {
            return springLength + step * zoom < lengthMin;
        }

        // ------------------------------------------------------------------
        // Arty view. The client hangs a top down camera over ATGM carriers so
        // the missile can be walked in over cover; the source has no such rig,
        // so these are the numbers the port settled on.
        // ------------------------------------------------------------------

        /// <summary>How far above the aim point the overhead rig sits, in metres.</summary>
        public const float ArtyHeight = 90f;

        /// <summary>How far ahead of the hull the view opens, in metres.</summary>
        public const float ArtyStartRange = 80f;

        /// <summary>Metres the aim point slides per unit of mouse input.</summary>
        public const float ArtyPanScale = 0.35f;

        /// <summary>Responsiveness of the overhead camera's exponential pan.</summary>
        public const float ArtyPanSmoothingSpeed = 10f;

        /// <summary>How far up the ground under the aim point is looked for.</summary>
        public const float ArtyGroundProbe = 400f;

        /// <summary>The overhead rig keeps one fixed fov.</summary>
        public const float ArtyFov = 45f;

        /// <summary>One wheel step out past the longest arm opens the arty view.</summary>
        public static bool EntersArty(float springLength, float step, float zoom, float lengthMax)
        {
            return zoom > 0f && springLength + step * zoom > lengthMax;
        }

        /// <summary>Any wheel step back in closes it again.</summary>
        public static bool LeavesArty(float zoom)
        {
            return zoom < 0f;
        }

        // ------------------------------------------------------------------
        // Sniper auto zoom. input_camera.gd frames a target by its measured
        // height instead of mapping distance straight onto a fov: even six
        // degrees leaves a 3 m tank at 500 m at about 6% of the viewport.
        // ------------------------------------------------------------------

        /// <summary>Below this distance the scope stays at its widest step.</summary>
        public const float SniperAutoCloseDisableDistance = 50f;

        /// <summary>Past this distance the framing owns the fov outright.</summary>
        public const float SniperAutoFullFramingDistance = 75f;

        /// <summary>How much of the viewport height a framed tank covers.</summary>
        public const float SniperAutoTargetScreenFraction = 0.48f;

        /// <summary>The height used when a target cannot be measured.</summary>
        public const float SniperAutoFallbackTargetHeight = 3f;

        public const float SniperAutoZoomInSpeed = 6f;
        public const float SniperAutoZoomOutSpeed = 8f;

        /// <summary>Seconds a manual zoom step keeps the automatic zoom away.</summary>
        public const float SniperManualOverrideTime = 1.25f;

        /// <summary>
        /// input_camera.gd STICK_SUPPRESS_TIME: seconds the capture pull stands
        /// down after the player touches the look control. Without it the pull
        /// fights the swipe and the camera feels welded to the target.
        /// </summary>
        public const float StickSuppressTime = 0.35f;

        /// <summary>
        /// input_camera.gd ESCAPE_DECAY: how much of the escape budget bleeds
        /// back per second, in radians, so unrelated flicks never add up to a
        /// release nobody asked for.
        /// </summary>
        public const float EscapeDecay = 0.6f;

        /// <summary>
        /// input_camera.gd <c>sniper_fov_for_target</c>: the fov that frames a
        /// target of this height at this distance, eased out of the unzoomed
        /// clinch view.
        /// </summary>
        public static float SniperFovForTarget(float distance, float targetHeight)
        {
            if (distance <= SniperAutoCloseDisableDistance) { return SniperFovMax; }

            var height = targetHeight;
            if (float.IsNaN(height) || float.IsInfinity(height) || height <= 0f)
            {
                height = SniperAutoFallbackTargetHeight;
            }

            var safeDistance = distance < 0.01f ? 0.01f : distance;
            var framed = (float)(2.0 * Math.Atan(
                    height / (2.0 * safeDistance * SniperAutoTargetScreenFraction))
                * 180.0 / Math.PI);
            if (framed < SniperFovMin) { framed = SniperFovMin; }
            else if (framed > SniperFovMax) { framed = SniperFovMax; }

            // The temporal smoothing below keeps the handoff fluid even if a
            // target crosses the boundary quickly.
            var weight = SmoothStep(
                SniperAutoCloseDisableDistance, SniperAutoFullFramingDistance, distance);
            return SniperFovMax + (framed - SniperFovMax) * weight;
        }

        /// <summary>Godot <c>smoothstep</c>.</summary>
        public static float SmoothStep(float from, float to, float value)
        {
            if (from == to) { return value < from ? 0f : 1f; }

            var s = (value - from) / (to - from);
            if (s < 0f) { s = 0f; }
            else if (s > 1f) { s = 1f; }
            return s * s * (3f - 2f * s);
        }

        /// <summary>
        /// Exponential smoothing weight, i.e. 1 - exp(-speed * delta), so no
        /// approach tracks harder on a device that happens to run faster.
        /// </summary>
        public static float SmoothingWeight(float speed, float delta)
        {
            var s = speed < 0f ? 0f : speed;
            var d = delta < 0f ? 0f : delta;
            return (float)(1.0 - Math.Exp(-s * d));
        }

        /// <summary>input_camera.gd <c>smooth_sniper_fov</c>.</summary>
        public static float SmoothSniperFov(
            float current, float target, float speed, float delta)
        {
            return current + (target - current) * SmoothingWeight(speed, delta);
        }

        /// <summary>input_camera.gd <c>_nearest_zoom_step</c>.</summary>
        public static int NearestZoomStep(float fov)
        {
            var nearest = 0;
            var best = float.PositiveInfinity;
            for (var i = 0; i < ZoomSteps.Length; i++)
            {
                var delta = Math.Abs(ZoomSteps[i] - fov);
                if (delta < best)
                {
                    nearest = i;
                    best = delta;
                }
            }

            return nearest;
        }

        /// <summary>True for an ordinary number: neither NaN nor an infinity.</summary>
        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>Godot <c>angle_difference</c>, in radians.</summary>
        public static float AngleDifference(float from, float to)
        {
            const double Tau = Math.PI * 2.0;
            var diff = to - from;
            return (float)(diff - Tau * Math.Floor((diff + Math.PI) / Tau));
        }

        /// <summary>Godot <c>lerp_angle</c>, in radians.</summary>
        public static float LerpAngle(float from, float to, float weight)
        {
            return from + AngleDifference(from, to) * weight;
        }

        /// <summary>
        /// input_camera.gd: a swipe counts as leaving the capture only when it
        /// turns against the yaw the pull still has to cover. Settling onto the
        /// target is damped and forgiven, leaving it is neither.
        /// </summary>
        public static bool WantsAway(float swipe, float yawError)
        {
            if (swipe == 0f || Math.Abs(yawError) <= 0.0001f) { return false; }
            return (swipe > 0f) != (yawError > 0f);
        }

        /// <summary>
        /// Port fix over input_camera.gd: a look event leaves the capture when it
        /// turns against the pull on either axis. The source judged the yaw
        /// alone, so a vertical swipe could never escape and the pitch pull
        /// snapped the view back - the camera seemed to stop tilting whenever
        /// a tank happened to be captured.
        /// </summary>
        public static bool LookWantsAway(
            float swipeX, float swipeY, float yawError, float pitchError)
        {
            return WantsAway(swipeX, yawError) || WantsAway(swipeY, pitchError);
        }

        /// <summary>The angle one look event turned the rig through, in radians.</summary>
        public static float LookTravel(float yaw, float pitch)
        {
            return (float)Math.Sqrt((yaw * yaw) + (pitch * pitch));
        }

        /// <summary>The escape budget after one look event, in radians.</summary>
        public static float EscapeBudget(float current, float moved, bool wantsAway)
        {
            var travel = Math.Abs(moved);
            if (wantsAway) { return current + travel; }

            var left = current - travel;
            return left < 0f ? 0f : left;
        }

        /// <summary>The budget bleeding back between swipes.</summary>
        public static float DecayEscape(float current, float delta)
        {
            var left = current - delta * EscapeDecay;
            return left < 0f ? 0f : left;
        }

        /// <summary>
        /// Whether the accumulated player input ends the capture. The release is
        /// never judged from the instantaneous angle to the target, which a fast
        /// target opens up all by itself.
        /// </summary>
        public static bool ReleasesCapture(float escapeYaw, float releaseAngleDegrees)
        {
            return escapeYaw > releaseAngleDegrees * (float)(Math.PI / 180.0);
        }
    }
}
