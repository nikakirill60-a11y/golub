using System;
using System.Globalization;

namespace BlitzLegacy.Core.Ui
{
    /// <summary>
    /// Godot Range / HSlider maths for the continuous sliders of
    /// ui_settings.tscn, kept free of UnityEngine so it can be tested in
    /// EditMode.
    ///
    /// <see cref="ShellSliderMath"/> is the same maths for the loadout screen,
    /// but those sliders are whole shells: min is always 0, the value is an
    /// int and <c>rounded</c> is on. add_slider_option explicitly turns
    /// rounding off and gives every row its own min, max and step, so the
    /// settings rows need the general form.
    /// </summary>
    public static class GodotRangeMath
    {
        /// <summary>
        /// Range::_set_value: snap to the step measured from min, then clamp.
        /// </summary>
        public static float Snap(float value, float min, float max, float step)
        {
            if (step > 0f)
            {
                value = ((float)Math.Round((value - min) / step)) * step + min;
            }

            if (value < min) { return min; }
            return value > max ? max : value;
        }

        /// <summary>Range::get_as_ratio.</summary>
        public static float Ratio(float value, float min, float max)
        {
            if (max <= min)
            {
                return 0f;
            }

            var ratio = (value - min) / (max - min);
            if (ratio < 0f) { return 0f; }
            return ratio > 1f ? 1f : ratio;
        }

        /// <summary>
        /// <c>size.x - grabber.width</c>: how far the centre of the grabber
        /// travels. A widget narrower than its grabber keeps the full width so
        /// it still answers a press.
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
        /// left edge picks: <c>set_as_ratio((pos - grab_width / 2) / areasize)</c>.
        /// </summary>
        public static float ValueAt(
            float localX,
            float width,
            float grabber,
            float min,
            float max,
            float step)
        {
            if (width <= 0f)
            {
                return min;
            }

            var travel = Travel(width, grabber);
            var inset = travel < width ? grabber * 0.5f : 0f;
            var ratio = (localX - inset) / travel;
            if (ratio < 0f) { ratio = 0f; }
            if (ratio > 1f) { ratio = 1f; }
            return Snap(min + (ratio * (max - min)), min, max, step);
        }

        /// <summary>
        /// Slider::_draw grabber_area: <c>areasize * ratio + grabber / 2</c>.
        /// </summary>
        public static float FillWidth(
            float width, float grabber, float value, float min, float max)
        {
            if (width <= 0f)
            {
                return 0f;
            }

            var travel = Travel(width, grabber);
            var inset = travel < width ? grabber * 0.5f : 0f;
            return (Ratio(value, min, max) * travel) + inset;
        }

        /// <summary>
        /// Slider::_draw grabber: <c>round(ratio * areasize)</c>, so the
        /// grabber never hangs over either end of the frame.
        /// </summary>
        public static float GrabberLeft(
            float width, float grabber, float value, float min, float max)
        {
            return (float)Math.Round(Ratio(value, min, max) * Travel(width, grabber));
        }

        /// <summary>
        /// Godot draws the slider stylebox at its minimum height, centred in
        /// the widget.
        /// </summary>
        public static float CenteredTop(float areaHeight, float barHeight)
        {
            return (areaHeight - barHeight) * 0.5f;
        }

        /// <summary>
        /// <c>str(value)</c> for the float the row shows: GDScript prints the
        /// shortest form, so a whole number loses its fraction entirely.
        /// </summary>
        public static string Text(float value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }
    }
}
