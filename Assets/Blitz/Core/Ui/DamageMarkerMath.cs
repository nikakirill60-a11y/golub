using System;
using BlitzLegacy.Core.Maps;

namespace BlitzLegacy.Core.Ui
{
    /// <summary>
    /// The hit direction markers around the crosshair: colours from
    /// <c>ui_player_battle.gd</c> (<c>color_ind_pen</c> /
    /// <c>color_ind_bounce</c>), lifetime and fade from
    /// <c>ui_damage_indicator.gd</c>, the rotation from <c>update_markers</c>
    /// and the <c>marker_template_dmg/head</c> geometry of ui_player.tscn.
    /// </summary>
    public static class DamageMarkerMath
    {
        private const float Pi = 3.1415927f;
        private const float DegToRad = Pi / 180f;

        /// <summary><c>color_ind_pen = Color(0.808, 0.0, 0.0, 0.808)</c>.</summary>
        public static readonly float[] PenetrationTint = { 0.808f, 0f, 0f, 0.808f };

        /// <summary><c>color_ind_bounce = Color(0.061, 0.061, 0.061, 0.583)</c>.</summary>
        public static readonly float[] BounceTint = { 0.061f, 0.061f, 0.061f, 0.583f };

        /// <summary>The one-shot Timer of <c>configure</c>.</summary>
        public const float Lifetime = 5f;

        /// <summary><c>tween_property(self, "modulate:a", 0.0, 1.0)</c>.</summary>
        public const float FadeTime = 1f;

        /// <summary>Atlas region of the DamageInd texture.</summary>
        public const string Region = "battle_damage_ind";

        // markers: anchors_preset 8, offsets -20..20.
        public const float HandleHalfSize = 20f;

        // marker_template_dmg/head: offsets -105.5, 0, 572.5, 180,
        // rotation PI/2, scale 0.5, pivot_offset (339, 0).
        public const float HeadLeft = -105.5f;
        public const float HeadTop = 0f;
        public const float HeadRight = 572.5f;
        public const float HeadBottom = 180f;
        public const float HeadRotation = Pi * 0.5f;
        public const float HeadScale = 0.5f;
        public const float HeadPivotX = 339f;
        public const float HeadPivotY = 0f;

        /// <summary>The marker tint, picked once in <c>add_dmg_marker</c>.</summary>
        public static float[] Tint(bool penetrated)
        {
            return penetrated ? PenetrationTint : BounceTint;
        }

        /// <summary>
        /// Factor applied to the tint alpha <paramref name="age"/> seconds after
        /// the hit: full for the lifetime, then a linear tween down to zero.
        /// </summary>
        public static float Fade(float age)
        {
            if (age <= Lifetime)
            {
                return 1f;
            }

            var t = (age - Lifetime) / FadeTime;
            return t >= 1f ? 0f : 1f - t;
        }

        /// <summary>The fade tween has finished and the marker is freed.</summary>
        public static bool Expired(float age)
        {
            return age >= Lifetime + FadeTime;
        }

        /// <summary>
        /// <c>update_markers</c>, in source space:
        /// <c>dirto.signed_angle_to(Vector3.RIGHT, Vector3.UP)
        /// + cam.global_rotation.y - PI</c>, with
        /// <c>dirto = cam.global_position - pos</c>. Radians, clockwise on
        /// screen like a Godot Control rotation.
        /// </summary>
        public static float Rotation(Vec3 camera, Vec3 attacker, float cameraYaw)
        {
            var dx = camera.X - attacker.X;
            var dy = camera.Y - attacker.Y;
            var dz = camera.Z - attacker.Z;

            // Vector3.signed_angle_to: cross(dirto, RIGHT) = (0, dz, -dy), and
            // its dot with UP (dz) picks the sign.
            var unsigned = (float)Math.Atan2(Math.Sqrt(dy * dy + dz * dz), dx);
            var angle = dz < 0f ? -unsigned : unsigned;
            return angle + cameraYaw - Pi;
        }

        /// <summary>
        /// The same rotation for Unity-space inputs: positions and the camera
        /// axes are mirrored back into the source before the formula runs, and
        /// the camera yaw is the YXZ Euler <c>global_rotation.y</c>.
        /// </summary>
        public static float RotationFromMirrored(
            Vec3 camera, Vec3 attacker, Vec3 cameraRight, Vec3 cameraUp, Vec3 cameraForward)
        {
            Vec3 sx;
            Vec3 sy;
            Vec3 sz;
            GodotEuler.SourceBasis(cameraRight, cameraUp, cameraForward, out sx, out sy, out sz);
            float pitch;
            float yaw;
            float roll;
            GodotEuler.Yxz(sx, sy, sz, out pitch, out yaw, out roll);

            return Rotation(
                GodotSpace.Point(camera.X, camera.Y, camera.Z),
                GodotSpace.Point(attacker.X, attacker.Y, attacker.Z),
                yaw * DegToRad);
        }

        /// <summary>
        /// uGUI rotates counter-clockwise for a positive Z angle, the opposite
        /// of a Godot Control.
        /// </summary>
        public static float UnityZDegrees(float godotRotation)
        {
            return -godotRotation / DegToRad;
        }
    }
}
