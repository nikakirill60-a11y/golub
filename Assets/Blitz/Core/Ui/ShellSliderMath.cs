namespace BlitzLegacy.Core.Ui
{
    /// <summary>
    /// The Range / HSlider maths ui_shell_slider.tscn relies on, kept free of
    /// UnityEngine so the drag behaviour can be tested in EditMode.
    ///
    /// The source row is an HSlider with min = 0, max = the rack capacity,
    /// step = the burst of the installed gun and rounded = true, so every
    /// dragged position snaps to a whole burst exactly like Godot's
    /// Range::set_value does.
    ///
    /// Godot never maps the pointer over the full widget: the grabber has to
    /// stay inside the frame, so the travel of its centre is only
    /// <c>width - grabber</c> wide and starts half a grabber from the left
    /// edge. Slider::gui_input reads the value back with the same two terms,
    /// which is why the grabber sits exactly under the finger there.
    /// </summary>
    public static class ShellSliderMath
    {
        /// <summary>Godot Range: snap to the step, then clamp into 0..max.</summary>
        public static int Snap(float value, int max, int step)
        {
            if (max <= 0)
            {
                return 0;
            }

            if (step < 1)
            {
                step = 1;
            }

            var steps = (int)System.Math.Floor((value / step) + 0.5f);
            var snapped = steps * step;
            if (snapped < 0)
            {
                snapped = 0;
            }

            return snapped > max ? max : snapped;
        }

        /// <summary>
        /// <c>size.x - grabber.width</c>: how far the centre of the grabber
        /// can travel. A row narrower than its own grabber keeps the whole
        /// width, so the slider still answers a press.
        /// </summary>
        public static float Travel(float width, float grabber)
        {
            if (width <= 0f)
            {
                return 0f;
            }

            var travel = width - grabber;
            return travel > 0f ? travel : width;
        }

        /// <summary>
        /// The value a pointer at <paramref name="localX"/> pixels from the
        /// left edge picks: <c>set_as_ratio((pos - grab_width / 2) / max)</c>.
        /// </summary>
        public static int ValueAt(
            float localX, float width, float grabber, int max, int step)
        {
            if (width <= 0f)
            {
                return 0;
            }

            var travel = Travel(width, grabber);
            var inset = travel < width ? grabber * 0.5f : 0f;
            return Snap(Clamp01((localX - inset) / travel) * max, max, step);
        }

        /// <summary>
        /// <c>grabber_area</c>: Godot fills the bar up to the centre of the
        /// grabber, so the fill is the travel plus the half grabber the
        /// pointer mapping skips.
        /// </summary>
        public static float FillWidth(float width, float grabber, int value, int max)
        {
            if (max <= 0 || width <= 0f)
            {
                return 0f;
            }

            var travel = Travel(width, grabber);
            var inset = travel < width ? grabber * 0.5f : 0f;
            return (Clamp01((float)value / max) * travel) + inset;
        }

        /// <summary>
        /// The left edge of the grabber itself, which Godot draws at
        /// <c>round(areasize * ratio)</c> and therefore never lets hang over
        /// either end of the frame.
        /// </summary>
        public static float GrabberLeft(
            float width, float grabber, int value, int max)
        {
            return FillWidth(width, grabber, value, max) - (grabber * 0.5f);
        }

        /// <summary>
        /// Godot draws the slider stylebox at its minimum height, centred in
        /// the widget, which is why the fill has to sit on the middle line of
        /// the row instead of its top edge.
        /// </summary>
        public static float CenteredTop(float areaHeight, float barHeight)
        {
            return (areaHeight - barHeight) * 0.5f;
        }

        private static float Clamp01(float value)
        {
            if (value < 0f) { return 0f; }
            return value > 1f ? 1f : value;
        }
    }
}
