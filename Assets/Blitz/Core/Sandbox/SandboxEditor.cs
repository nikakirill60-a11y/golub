using System;
using System.Collections.Generic;
using System.Globalization;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;

namespace BlitzLegacy.Core.Sandbox
{
    /// <summary>
    /// The data side of Godot's training_mode_screen.gd: roster slot editing,
    /// the tank picker's catalogue and the slider captions. Kept free of
    /// Unity types so the editor rules are covered by EditMode tests and both
    /// menu styles share one implementation.
    /// </summary>
    public static class SandboxEditor
    {
        /// <summary>
        /// _active_slot_count: every enemy seat is a bot, while the first
        /// allied seat belongs to the player.
        /// </summary>
        public static int ActiveSlotCount(SandboxConfig config, bool enemy)
        {
            if (config == null)
            {
                return 0;
            }

            var count = enemy ? config.EnemyTeamSize : Math.Max(0, config.AlliedTeamSize - 1);
            return Math.Max(0, Math.Min(count, config.Slots(enemy).Count));
        }

        /// <summary>
        /// _picker_next_random_slot: the first RANDOM slot at or after
        /// <paramref name="start"/>, wrapping around; -1 when every active
        /// slot already holds an exact vehicle.
        /// </summary>
        public static int NextRandomSlot(SandboxConfig config, bool enemy, int start)
        {
            var count = ActiveSlotCount(config, enemy);
            if (count <= 0)
            {
                return -1;
            }

            var slots = config.Slots(enemy);
            var from = Math.Max(0, Math.Min(start, count));
            for (var i = from; i < count; i++)
            {
                if (slots[i].RandomTank)
                {
                    return i;
                }
            }

            for (var i = 0; i < from; i++)
            {
                if (slots[i].RandomTank)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// _choose_tank: writes <paramref name="tankName"/> into slot
        /// <paramref name="index"/> (or the first RANDOM slot when the index
        /// is out of range), resets that slot's tuning and switches the team
        /// to a custom roster. Returns the slot that was written, or -1 when
        /// no slot could take the vehicle.
        /// </summary>
        public static int ChooseTank(SandboxConfig config, bool enemy, int index, string tankName)
        {
            if (config == null || string.IsNullOrEmpty(tankName))
            {
                return -1;
            }

            if (index < 0 || index >= ActiveSlotCount(config, enemy))
            {
                index = NextRandomSlot(config, enemy, 0);
            }

            if (index < 0)
            {
                return -1;
            }

            var slot = config.Slots(enemy)[index];
            slot.MakeRandom();
            slot.SelectTank(tankName);
            if (enemy)
            {
                config.EnemyRosterMode = SandboxRosterMode.Custom;
            }
            else
            {
                config.AlliedRosterMode = SandboxRosterMode.Custom;
            }

            return index;
        }

        /// <summary>_remove_picker_tank / _set_slot_random.</summary>
        public static bool MakeSlotRandom(SandboxConfig config, bool enemy, int index)
        {
            if (config == null || index < 0 || index >= config.Slots(enemy).Count)
            {
                return false;
            }

            config.Slots(enemy)[index].MakeRandom();
            return true;
        }

        /// <summary>_clear_team_tanks: every slot of the team back to RANDOM.</summary>
        public static void ClearTeam(SandboxConfig config, bool enemy)
        {
            if (config == null)
            {
                return;
            }

            foreach (var slot in config.Slots(enemy))
            {
                slot.MakeRandom();
            }
        }

        /// <summary>
        /// True when the slot shows an exact vehicle: not RANDOM and the
        /// vehicle still exists (TanksDataBase.tank_exists), a hidden event
        /// vehicle counting as missing.
        /// </summary>
        public static bool IsExact(SandboxBotSlot slot, TanksDatabase tanks)
        {
            return slot != null && !slot.RandomTank && tanks != null && tanks.HasVisible(slot.TankName);
        }

        /// <summary>The exact vehicles of the active slots, in slot order.</summary>
        public static List<TankInfo> ExactTanks(SandboxConfig config, bool enemy, TanksDatabase tanks)
        {
            var result = new List<TankInfo>();
            var count = ActiveSlotCount(config, enemy);
            for (var i = 0; i < count; i++)
            {
                var slot = config.Slots(enemy)[i];
                if (IsExact(slot, tanks))
                {
                    result.Add(tanks.Get(slot.TankName));
                }
            }

            return result;
        }

        /// <summary>The names already chosen for the team (the picker highlights them).</summary>
        public static HashSet<string> SelectedNames(SandboxConfig config, bool enemy)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = ActiveSlotCount(config, enemy);
            for (var i = 0; i < count; i++)
            {
                var slot = config.Slots(enemy)[i];
                if (!slot.RandomTank && !string.IsNullOrEmpty(slot.TankName))
                {
                    names.Add(slot.TankName);
                }
            }

            return names;
        }

        /// <summary>
        /// _tank_picker_infos: every visible vehicle except the base tank,
        /// mods included, ordered by level and then by localized short name.
        /// </summary>
        public static List<TankInfo> PickerTanks(TanksDatabase tanks, Func<TankInfo, string> displayName)
        {
            var result = new List<TankInfo>();
            if (tanks == null)
            {
                return result;
            }

            foreach (var info in tanks.Tanks)
            {
                if (info == null || info.Hidden
                    || string.Equals(info.Name, TankSaveData.DefaultName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(info);
            }

            result.Sort((a, b) =>
            {
                if (a.Level != b.Level)
                {
                    return a.Level.CompareTo(b.Level);
                }

                var byName = string.CompareOrdinal(Display(a, displayName), Display(b, displayName));
                return byName != 0 ? byName : string.CompareOrdinal(a.Name, b.Name);
            });
            return result;
        }

        /// <summary>_fill_tank_picker's filter: the localized name or the id contains the query.</summary>
        public static bool MatchesSearch(TankInfo info, string display, string query)
        {
            if (info == null)
            {
                return false;
            }

            var needle = (query ?? string.Empty).Trim().ToLowerInvariant();
            if (needle.Length == 0)
            {
                return true;
            }

            return (display ?? string.Empty).ToLowerInvariant().Contains(needle)
                || (info.Name ?? string.Empty).ToLowerInvariant().Contains(needle);
        }

        /// <summary>
        /// Godot's "%.2f" with trailing zeros and a bare point trimmed
        /// (_slider_text), always with an invariant decimal point.
        /// </summary>
        public static string Number(float value, int decimals = 2)
        {
            var text = value.ToString("F" + Math.Max(0, decimals), CultureInfo.InvariantCulture);
            if (text.IndexOf('.') >= 0)
            {
                text = text.TrimEnd('0').TrimEnd('.');
            }

            return text == "-0" ? "0" : text;
        }

        /// <summary>_slider_text: the number plus an optional unit suffix.</summary>
        public static string SliderText(float value, string suffix)
        {
            var text = Number(value);
            return string.IsNullOrEmpty(suffix) ? text : text + " " + suffix;
        }

        /// <summary>_multiplier_slider_text: only the resulting multiplier is shown.</summary>
        public static string MultiplierText(float offset)
        {
            return "×" + Number(SandboxMultiplierMath.OffsetToMultiplier(offset));
        }

        /// <summary>Godot SpinBox / HSlider snapping: clamp then round to the step grid.</summary>
        public static float Snap(float value, float min, float max, float step)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                value = min;
            }

            if (step > 0f)
            {
                value = min + ((float)Math.Round((value - min) / step) * step);
            }

            value = Math.Max(min, Math.Min(max, value));
            return (float)Math.Round(value, 4);
        }

        /// <summary>
        /// GDScript's <c>"..." % [..]</c> for the locale patterns the Sandbox
        /// keys carry: <c>%s</c> and <c>%d</c> take the next argument in
        /// order, <c>%%</c> is a literal percent sign. Missing arguments leave
        /// the placeholder in place instead of throwing.
        /// </summary>
        public static string Percent(string pattern, params object[] args)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder(pattern.Length + 16);
            var next = 0;
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                if (c != '%' || i + 1 >= pattern.Length)
                {
                    builder.Append(c);
                    continue;
                }

                var code = pattern[i + 1];
                if (code == '%')
                {
                    builder.Append('%');
                    i++;
                }
                else if ((code == 's' || code == 'd') && args != null && next < args.Length)
                {
                    var value = args[next++];
                    builder.Append(value is IFormattable formattable
                        ? formattable.ToString(null, CultureInfo.InvariantCulture)
                        : Convert.ToString(value, CultureInfo.InvariantCulture));
                    i++;
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }

        private static string Display(TankInfo info, Func<TankInfo, string> displayName)
        {
            var text = displayName != null ? displayName(info) : null;
            return string.IsNullOrEmpty(text) ? info.Name : text;
        }
    }
}
