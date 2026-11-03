using System;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// Geometry and thresholds of the mobile control layer, read off the Godot
    /// scene and scripts instead of retuned by hand.
    ///
    /// Sources:
    /// - assets/gfx/ui/ui_player.tscn, node "mobile_shit": the joystick and the
    ///   four battle buttons with their anchors, offsets and the joystick's own
    ///   2.625 scale.
    /// - addons/touch_screen_joystick/touch_screen_joystick.gd: the knob math,
    ///   the pixel deadzone and the four move_* actions the stick presses. The
    ///   scene node stores exactly these exports and the class name the scene
    ///   asks for no longer exists in the project, so this addon is the
    ///   behaviour the shipped build ran.
    /// - base_tank.tscn, PlayerController._physics_process: the drive axes come
    ///   from Input.get_axis over those same actions, and each axis below 0.33
    ///   is dropped before it reaches BaseTank.drive.
    ///
    /// All joystick math works in the control's own unscaled space: the origin
    /// is the top left corner and Y points down, which is Godot's screen
    /// convention. The Unity side converts once, through
    /// <see cref="ControlPointX"/> and <see cref="ControlPointY"/>.
    ///
    /// This assembly is built without engine references, like the rest of the
    /// ported tuning, so the API is plain floats rather than vectors.
    /// </summary>
    public static class ControlsTuning
    {
        /// <summary>mobile_shit/VirtualJoystick, anchors_preset 2 (bottom left).</summary>
        public const float JoystickOffsetLeft = 22f;

        public const float JoystickOffsetTop = -369f;

        public const float JoystickOffsetRight = 161f;

        public const float JoystickOffsetBottom = -231f;

        public const float JoystickWidth = JoystickOffsetRight - JoystickOffsetLeft;

        public const float JoystickHeight = JoystickOffsetBottom - JoystickOffsetTop;

        /// <summary>
        /// The node is drawn at 2.625 and move_knob divides the finger position
        /// by that scale, so the deadzone below is 25 unscaled pixels, i.e.
        /// roughly 66 screen pixels on the phone.
        /// </summary>
        public const float JoystickScale = 2.625f;

        /// <summary>touch_screen_joystick.gd: deadzone, in unscaled pixels.</summary>
        public const float JoystickDeadzone = 25f;

        /// <summary>Joypad.tres, the node's normal_joystick stylebox.</summary>
        public const float JoystickBaseTexture = 360f;

        /// <summary>Joypad02.tres, the node's normal_tip stylebox.</summary>
        public const float JoystickKnobTexture = 206f;

        /// <summary>
        /// The scene node is a stylebox driven VirtualJoystick: both textures
        /// are painted into the authored 139x138 rect, so the ring the player
        /// sees is that rect and not the 120 px circle the standalone
        /// touch_screen_joystick addon draws for its debug shape. Taking the
        /// radius from the rect is what keeps the stick the size it has in
        /// Godot instead of the far larger circle the addon constant implies.
        /// </summary>
        public const float JoystickBaseRadius = JoystickWidth * 0.5f;

        /// <summary>
        /// The tip keeps the texture ratio the two styleboxes have, so the
        /// knob covers the same share of the base as in the source.
        /// </summary>
        public const float JoystickKnobRadius =
            JoystickBaseRadius * (JoystickKnobTexture / JoystickBaseTexture);

        /// <summary>
        /// How far the centre of the knob may travel. The tip is drawn inside
        /// the base control in Godot, so its rim never leaves the ring.
        /// </summary>
        public const float JoystickKnobTravel =
            JoystickBaseRadius - JoystickKnobRadius;

        /// <summary>PlayerController._physics_process: fw = 0 if absf(fw) &lt; .33.</summary>
        public const float AxisThreshold = 0.33f;

        // ui_player_battle._ensure_spectator_controls: an HBoxContainer under
        // mobile_shit at PRESET_CENTER_BOTTOM, holding two buttons the dead
        // player uses to walk the observer camera along his team.
        public const float SpectatorOffsetLeft = -66f;

        public const float SpectatorOffsetTop = -78f;

        public const float SpectatorOffsetRight = 66f;

        public const float SpectatorOffsetBottom = -22f;

        /// <summary>The "separation" theme constant override.</summary>
        public const float SpectatorSeparation = 16f;

        /// <summary>custom_minimum_size of each button.</summary>
        public const float SpectatorButtonWidth = 56f;

        public const float SpectatorButtonHeight = 52f;

        /// <summary>The "font_size" override on both buttons.</summary>
        public const int SpectatorFontSize = 24;

        /// <summary>player_controller.team_tank_id starts before the roster.</summary>
        public const int SpectatorStartIndex = -1;

        /// <summary>btn_fire, anchors_preset 3 (bottom right).</summary>
        public const float FireOffsetLeft = -354f;

        public const float FireOffsetTop = -342f;

        public const float FireOffsetRight = -229f;

        public const float FireOffsetBottom = -217f;

        public const float FireWidth = FireOffsetRight - FireOffsetLeft;

        public const float FireHeight = FireOffsetBottom - FireOffsetTop;

        /// <summary>btn_sniper, anchors_preset 3.</summary>
        public const float SniperOffsetLeft = -228f;

        public const float SniperOffsetTop = -231f;

        public const float SniperOffsetRight = -131f;

        public const float SniperOffsetBottom = -134f;

        public const float SniperWidth = SniperOffsetRight - SniperOffsetLeft;

        public const float SniperHeight = SniperOffsetBottom - SniperOffsetTop;

        /// <summary>btn_autttoaim, anchors_preset 3.</summary>
        public const float AutoAimOffsetLeft = -177f;

        public const float AutoAimOffsetTop = -336f;

        public const float AutoAimOffsetRight = -124f;

        public const float AutoAimOffsetBottom = -283f;

        public const float AutoAimWidth = AutoAimOffsetRight - AutoAimOffsetLeft;

        public const float AutoAimHeight = AutoAimOffsetBottom - AutoAimOffsetTop;

        /// <summary>
        /// btn_menu, anchors_preset 5 (centre top). Moved a little left of the
        /// authored 232..310 so it and the chat button next to it stay clear
        /// of the scoreboard (Tab) strip; still right of total_tanks (222).
        /// </summary>
        public const float MenuOffsetLeft = 228f;

        public const float MenuOffsetTop = 8f;

        public const float MenuOffsetRight = 306f;

        public const float MenuOffsetBottom = 70f;

        public const float MenuWidth = MenuOffsetRight - MenuOffsetLeft;

        public const float MenuHeight = MenuOffsetBottom - MenuOffsetTop;

        /// <summary>Battle chat button, right of btn_menu (4.8.0 ChatButton, 90x70 scaled to the menu height).</summary>
        public const float ChatGap = 4f;

        public const float ChatOffsetLeft = MenuOffsetRight + ChatGap;

        public const float ChatOffsetTop = MenuOffsetTop;

        public const float ChatOffsetRight = ChatOffsetLeft + 80f;

        public const float ChatOffsetBottom = MenuOffsetBottom;

        public const float ChatWidth = ChatOffsetRight - ChatOffsetLeft;

        public const float ChatHeight = ChatOffsetBottom - ChatOffsetTop;

        /// <summary>Right edge of the total_tanks header (bg2 starts at 222).</summary>
        public const float HeaderRightEdge = 222f;

        public static float Distance(float ax, float ay, float bx, float by)
        {
            var dx = bx - ax;
            var dy = by - ay;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Unity hands out a point relative to the RectTransform's pivot with Y
        /// pointing up; the joystick math wants the top left corner with Y
        /// pointing down.
        /// </summary>
        public static float ControlPointX(float localX, float width, float pivotX)
        {
            return localX + width * pivotX;
        }

        /// <summary>The vertical half of <see cref="ControlPointX"/>.</summary>
        public static float ControlPointY(float localY, float height, float pivotY)
        {
            return height * (1f - pivotY) - localY;
        }

        /// <summary>
        /// move_knob: the knob follows the finger while it stays inside
        /// base_radius and is otherwise pinned to that circle at the same angle.
        /// </summary>
        public static void KnobPosition(
            float centerX,
            float centerY,
            float touchX,
            float touchY,
            float baseRadius,
            out float knobX,
            out float knobY)
        {
            var distance = Distance(centerX, centerY, touchX, touchY);
            if (distance < baseRadius)
            {
                knobX = touchX;
                knobY = touchY;
                return;
            }

            var angle = Math.Atan2(touchY - centerY, touchX - centerX);
            knobX = centerX + (float)Math.Cos(angle) * baseRadius;
            knobY = centerY + (float)Math.Sin(angle) * baseRadius;
        }

        /// <summary>
        /// move_knob presses the actions only past the deadzone, and the source
        /// comparison is strict.
        /// </summary>
        public static bool Engaged(float distance)
        {
            return distance > JoystickDeadzone;
        }

        /// <summary>PlayerController drops axes that are too small to count.</summary>
        public static float ApplyThreshold(float axis)
        {
            return Math.Abs(axis) < AxisThreshold ? 0f : axis;
        }

        /// <summary>
        /// The whole chain the source runs per finger movement: gate on the
        /// deadzone, clamp the knob, take the normalised direction, then drop
        /// the axes below the threshold.
        ///
        /// get_direction returns center.direction_to(knob_position), which is
        /// normalised, so the axes are a direction and never scale with how far
        /// the finger travelled: the mobile drive is direction based rather
        /// than proportional. Y points down, so pushing the stick up is a
        /// negative Y and drives forward.
        /// </summary>
        public static void DriveAxes(
            float centerX,
            float centerY,
            float touchX,
            float touchY,
            out float forward,
            out float right)
        {
            forward = 0f;
            right = 0f;

            if (!Engaged(Distance(centerX, centerY, touchX, touchY)))
            {
                return;
            }

            float knobX;
            float knobY;
            KnobPosition(
                centerX, centerY, touchX, touchY, JoystickKnobTravel, out knobX, out knobY);

            var dx = knobX - centerX;
            var dy = knobY - centerY;
            var length = (float)Math.Sqrt(dx * dx + dy * dy);
            if (length <= 0f)
            {
                return;
            }

            forward = ApplyThreshold(-dy / length);
            right = ApplyThreshold(dx / length);
        }
    }
}
