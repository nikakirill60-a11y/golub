using System;
using System.Globalization;

namespace BlitzLegacy.Core.Ui
{
    /// <summary>Tint of a single dot in the dynamic crosshair reload ring.</summary>
    public enum CrosshairDot
    {
        Red = 0,
        Yellow = 1,
        Green = 2,
    }

    /// <summary>
    /// Numbers and formatting behind the battle HUD, ported from the Godot
    /// player HUD: geometry from assets/gfx/ui/ui_player.tscn, behaviour from
    /// assets/gfx/ui/battle/ui_player_battle.gd.
    ///
    /// BlitzLegacy.Core is compiled with noEngineReferences, so this type stays
    /// on primitives; callers build their own Vector2/Color values.
    /// </summary>
    public static class HudTuning
    {
        /// <summary>Godot's TAU in float precision.</summary>
        public const float Tau = 6.2831855f;

        // -----------------------------------------------------------------
        // Crosshairs
        // -----------------------------------------------------------------

        /// <summary>crosshair_static_handle, anchors_preset 8 (ui_player.tscn:427).</summary>
        public const float CrosshairHandleLeft = -23.225037f;
        public const float CrosshairHandleTop = -33.225006f;
        public const float CrosshairHandleRight = 52.450745f;
        public const float CrosshairHandleBottom = 78.486725f;
        public const float CrosshairHandleScale = 0.555f;
        public const float CrosshairHandlePivotX = 5f;
        public const float CrosshairHandlePivotY = 5f;

        /// <summary>crosshair_static (ui_player.tscn:444), centred in the handle.</summary>
        public const float CrosshairStaticLeft = -214.5f;
        public const float CrosshairStaticTop = -212f;
        public const float CrosshairStaticRight = 214.5f;
        public const float CrosshairStaticBottom = 212f;

        /// <summary>crosshair_static_lit (:461) hangs 6 px above the base ring.</summary>
        public const float CrosshairStaticLitTop = -218f;
        public const float CrosshairStaticLitBottom = 206f;

        /// <summary>crosshair_dynamic (:344), a Sprite2D pinned to the gun trace.</summary>
        public const float CrosshairDynScale = 0.5f;
        public const float CrosshairDynOffsetY = 18.135f;

        /// <summary>num_dots (ui_player_battle.gd).</summary>
        public const int DotCount = 35;

        /// <summary>crosshair_dot_size (ui_player_battle.gd).</summary>
        public const float DotScale = 1.75f;

        /// <summary>Dispersion ring radius: (len1 + len2) * 0.35 in _process.</summary>
        public const float DispersionCoeff = 0.35f;
        public const float DispersionMin = 32f;
        public const float DispersionMaxFactor = 1.5f;

        // -----------------------------------------------------------------
        // Health bar (ui_player.tscn:934)
        // -----------------------------------------------------------------

        /// <summary>hp root, anchors_preset 7 (bottom centre), scale 0.52.</summary>
        public const float HealthLeft = -8f;
        public const float HealthTop = -27f;
        public const float HealthRight = 32f;
        public const float HealthBottom = 13f;
        public const float HealthScale = 0.52f;

        /// <summary>hp_back frame (:950).</summary>
        public const float HealthBackLeft = -255f;
        public const float HealthBackTop = -40f;
        public const float HealthBackRight = 255f;
        public const float HealthBackBottom = 0f;

        /// <summary>hp_bar fill (:965), scaled 1.005 x 1.3 in the scene.</summary>
        public const float HealthBarLeft = -252.00002f;
        public const float HealthBarTop = -37f;
        public const float HealthBarRight = 250f;
        public const float HealthBarBottom = -11f;
        public const float HealthBarScaleX = 1.005f;
        public const float HealthBarScaleY = 1.3f;

        /// <summary>hp_text (:984), 25 px bold with a 2/1 px shadow.</summary>
        public const float HealthTextLeft = -56.53848f;
        public const float HealthTextTop = -14.23078f;
        public const float HealthTextRight = 54.461517f;
        public const float HealthTextBottom = 12.769222f;
        public const int HealthTextFontSize = 25;
        public const float HealthTextShadowX = 2f;
        public const float HealthTextShadowY = 1f;

        // -----------------------------------------------------------------
        // Speed / distance / reload labels
        // -----------------------------------------------------------------

        /// <summary>ui_speed (:666), anchors_preset 7.</summary>
        public const float SpeedLeft = -30f;
        public const float SpeedTop = -51f;
        public const float SpeedRight = 34f;
        public const float SpeedBottom = -2f;
        public const int SpeedFontSize = 17;

        /// <summary>ui_dist (:687), anchors_preset 8 with an upward white shadow.</summary>
        public const float DistanceLeft = -16f;
        public const float DistanceTop = -81f;
        public const float DistanceRight = 24f;
        public const float DistanceBottom = -40f;
        public const int DistanceFontSize = 18;
        public const float DistanceShadowX = 0f;
        public const float DistanceShadowY = -1f;
        public const float DistanceColourR = 0.041090988f;
        public const float DistanceColourG = 0.06592237f;
        public const float DistanceColourB = 0.12180645f;

        /// <summary>reload (:712), anchors_preset 8, right aligned.</summary>
        public const float ReloadLeft = -139f;
        public const float ReloadTop = -22f;
        public const float ReloadRight = -99f;
        public const float ReloadBottom = 23f;
        public const int ReloadFontSize = 17;

        /// <summary>color_reload, hex f6f629.</summary>
        public const float ReloadColourR = 0.9647059f;
        public const float ReloadColourG = 0.9647059f;
        public const float ReloadColourB = 0.16078432f;

        /// <summary>color_reload_complete, hex 0a111f.</summary>
        public const float ReloadCompleteColourR = 0.039215688f;
        public const float ReloadCompleteColourG = 0.06666667f;
        public const float ReloadCompleteColourB = 0.12156863f;

        /// <summary>fps_meter (:1541), anchors_preset 3.</summary>
        public const float FpsLeft = -121f;
        public const float FpsTop = -24f;
        public const float FpsRight = -8f;
        public const float FpsBottom = -8f;
        public const int FpsFontSize = 18;

        /// <summary>Online ping meter: the FPS meter mirrored to the bottom left.</summary>
        public const float PingLeft = -FpsRight;
        public const float PingRight = -FpsLeft;
        public const string PingKey = "battle:hud/ping";

        // -----------------------------------------------------------------
        // Update cadence (ui_player_battle.gd _physics_process)
        // -----------------------------------------------------------------

        /// <summary>Speed, distance and reload refresh every 0.1 s.</summary>
        public const float HudUpdateInterval = 0.1f;

        /// <summary>Battle clock refresh.</summary>
        public const float ClockUpdateInterval = 0.1f;

        /// <summary>FPS label refresh.</summary>
        public const float FpsUpdateInterval = 0.25f;

        /// <summary>Ping label refresh.</summary>
        public const float PingUpdateInterval = 0.5f;

        /// <summary>
        /// Engine.get_frames_per_second() counts the frames of the last whole
        /// second, so the source readout is an average and not the reciprocal
        /// of one frame time. Measuring over a shorter window makes the label
        /// jitter by tens of frames even when the frame rate is steady.
        /// </summary>
        public const float FpsSampleWindow = 1f;

        /// <summary>Minimap refresh.</summary>
        public const float MinimapUpdateInterval = 0.5f;

        /// <summary>Team box rebuild.</summary>
        public const float TeamBoxUpdateInterval = 5f;

        /// <summary>Speed under this reads as a standstill.</summary>
        public const float SpeedIdleThreshold = 1f;

        /// <summary>get_display_speed converts m/s to km/h while airborne.</summary>
        public const float MsToKmh = 3.6f;

        // -----------------------------------------------------------------
        // Dynamic crosshair maths
        // -----------------------------------------------------------------

        /// <summary>Dispersion ring radius before clamping.</summary>
        public static float DispersionRadius(float lengthNear, float lengthFar)
        {
            return (lengthNear + lengthFar) * DispersionCoeff;
        }

        /// <summary>
        /// clampf(disp, 32, absf(disp * 1.5)) from _process. Godot's clamp
        /// applies the minimum first, so a tiny dispersion ends up at the
        /// scaled maximum rather than at 32.
        /// </summary>
        public static float ClampDispersion(float dispersion)
        {
            var maximum = Math.Abs(dispersion * DispersionMaxFactor);
            var value = dispersion < DispersionMin ? DispersionMin : dispersion;
            return value > maximum ? maximum : value;
        }

        /// <summary>
        /// Angle of dot <paramref name="index"/>. The Godot loop starts at
        /// (0, -radius) and rotates by -TAU / num_dots per dot.
        /// </summary>
        public static float DotAngle(int index)
        {
            return -Tau * index / DotCount;
        }

        /// <summary>Dot offset from the crosshair centre, in Godot UI pixels.</summary>
        public static void DotPosition(
            int index, float radius, out float x, out float y)
        {
            var angle = DotAngle(index);
            x = radius * (float)Math.Sin(angle);
            y = -radius * (float)Math.Cos(angle);
        }

        /// <summary>Swept angle used by the reload colouring of a dot.</summary>
        public static float DotFillAngle(int index)
        {
            return index * (Tau / DotCount);
        }

        /// <summary>
        /// Dot tint: green while the gun can fire, otherwise yellow past the
        /// reload coefficient and red before it.
        /// </summary>
        public static CrosshairDot DotState(
            int index, float reloadCoeff, bool canShoot)
        {
            if (canShoot)
            {
                return CrosshairDot.Green;
            }

            return DotFillAngle(index) >= Tau * reloadCoeff
                ? CrosshairDot.Yellow
                : CrosshairDot.Red;
        }

        // -----------------------------------------------------------------
        // Health
        // -----------------------------------------------------------------

        /// <summary>
        /// Color.GREEN.lerp(Color.RED, minf(1 - hp / hp_max, 1)) from
        /// update_health.
        /// </summary>
        public static void HealthColour(
            float health, float maxHealth, out float r, out float g, out float b)
        {
            var weight = maxHealth <= 0f
                ? 1f
                : Math.Min(1f - health / maxHealth, 1f);
            r = weight;
            g = 1f - weight;
            b = 0f;
        }

        /// <summary>hp_text, formatted through the localized battle:hud/health key.</summary>
        public static string HealthText(int health, int maxHealth, string format)
        {
            return Format(
                format,
                health.ToString(CultureInfo.InvariantCulture),
                maxHealth.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// The bar only ever catches up upwards inside _physics_process; a
        /// separate damage animation walks it down.
        /// </summary>
        public static float HealthBarValue(float current, float health)
        {
            return current < health ? health : current;
        }

        // -----------------------------------------------------------------
        // Labels
        // -----------------------------------------------------------------

        /// <summary>absf(speed) &lt; 1 reads as a standstill.</summary>
        public static float DisplaySpeed(float speed)
        {
            return Math.Abs(speed) < SpeedIdleThreshold ? 0f : speed;
        }

        /// <summary>
        /// str(round(speed)) plus the localized unit from battle:hud/speed.
        /// Godot prints the rounded float with one decimal for whole values.
        /// </summary>
        public static string SpeedText(float speed, string format)
        {
            var rounded = (float)Math.Round(
                DisplaySpeed(speed), MidpointRounding.AwayFromZero);
            return Format(format, GodotFloatText(rounded));
        }

        /// <summary>
        /// ceili(dist) through battle:hud/distance, or the localized
        /// battle:hud/distance_unknown text when cam_trace missed.
        /// </summary>
        public static string DistanceText(
            float distance, bool hit, string format, string unknown)
        {
            if (!hit)
            {
                return unknown ?? string.Empty;
            }

            var meters = (int)Math.Ceiling(distance);
            return Format(format, meters.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Util.round_to_dec.</summary>
        public static float RoundToDec(float value, int digits)
        {
            var factor = (float)Math.Pow(10.0, digits);
            return (float)Math.Round(value * factor, MidpointRounding.AwayFromZero)
                / factor;
        }

        /// <summary>reload label text: the remaining seconds rounded to 2 decimals.</summary>
        public static string ReloadText(float remaining)
        {
            return GodotFloatText(RoundToDec(remaining, 2));
        }

        /// <summary>
        /// get_reload_remaining() equals get_reload_total() exactly while the
        /// gun is loaded, which is what flips the label to the dark tint.
        /// </summary>
        public static bool ReloadComplete(float remaining, float total)
        {
            return remaining == total;
        }

        // -----------------------------------------------------------------
        // Battle clock
        // -----------------------------------------------------------------

        /// <summary>Util.split_time: whole minutes and the leftover seconds.</summary>
        public static void SplitTime(
            float seconds, out int minutes, out int remainder)
        {
            minutes = (int)Math.Floor(seconds / 60f);
            remainder = (int)Math.Floor(seconds - minutes * 60f);
        }

        /// <summary>time_remaining_min: plain minute count.</summary>
        /// <summary>
        /// <c>$fps_meter.text = "FPS: " + str(int(Engine.get_frames_per_second()))</c>.
        /// </summary>
        public static string FpsText(int fps)
        {
            return "FPS: " + fps;
        }

        public static string ClockMinutesText(float seconds)
        {
            int minutes;
            int remainder;
            SplitTime(seconds, out minutes, out remainder);
            return minutes.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>time_remaining_sec: zero padded to two digits.</summary>
        public static string ClockSecondsText(float seconds)
        {
            int minutes;
            int remainder;
            SplitTime(seconds, out minutes, out remainder);
            var text = remainder.ToString(CultureInfo.InvariantCulture);
            return text.Length < 2 ? "0" + text : text;
        }

        /// <summary>
        /// Locale text owns every user-visible word and unit. Values are
        /// already formatted with invariant digits before they reach this
        /// helper, so changing the UI language never changes save-independent
        /// simulation output.
        /// </summary>
        public static string Format(string format, params string[] values)
        {
            if (string.IsNullOrEmpty(format))
            {
                return string.Empty;
            }

            return string.Format(CultureInfo.InvariantCulture, format, values);
        }

        /// <summary>
        /// Mimics Godot's str(float): whole values keep a single decimal, so
        /// round(19.4) prints as "19.0" and round_to_dec(0.85, 2) as "0.85".
        /// </summary>
        public static string GodotFloatText(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return value.ToString(CultureInfo.InvariantCulture);
            }

            return value == (float)Math.Floor(value)
                ? value.ToString("0.0", CultureInfo.InvariantCulture)
                : value.ToString("0.0###############", CultureInfo.InvariantCulture);
        }
    }
}
