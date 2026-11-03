using System;
using System.Globalization;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Sandbox
{
    /// <summary>
    /// The Sandbox tank modifiers: the nine of the Godot UI, in its order, then
    /// the hull traverse rate, which the Godot build does not have.
    /// </summary>
    public enum SandboxStat
    {
        Hp = 0,
        Damage = 1,
        Penetration = 2,
        Reload = 3,
        MagazineReload = 4,
        Intraclip = 5,
        Speed = 6,
        Engine = 7,
        AimTime = 8,
        TurnRate = 9,
    }

    /// <summary>
    /// One row of tank modifiers (global, player or a bot slot). Values are
    /// always sanitized positive multipliers in [x0.1, x10].
    /// </summary>
    public sealed class SandboxMultipliers
    {
        public const int Count = 10;

        /// <summary>Godot property stems, e.g. <c>hp</c> for <c>hp_mul</c>.</summary>
        public static readonly string[] Keys =
        {
            "hp", "damage", "penetration", "reload", "magazine_reload",
            "intraclip", "speed", "engine", "aim_time", "turn_rate",
        };

        private readonly float[] _values = { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };

        public float this[SandboxStat stat]
        {
            get { return _values[(int)stat]; }
            set { _values[(int)stat] = value; }
        }

        public float Hp { get { return this[SandboxStat.Hp]; } }
        public float Damage { get { return this[SandboxStat.Damage]; } }
        public float Penetration { get { return this[SandboxStat.Penetration]; } }
        public float Reload { get { return this[SandboxStat.Reload]; } }
        public float MagazineReload { get { return this[SandboxStat.MagazineReload]; } }
        public float Intraclip { get { return this[SandboxStat.Intraclip]; } }
        public float Speed { get { return this[SandboxStat.Speed]; } }
        public float Engine { get { return this[SandboxStat.Engine]; } }
        public float AimTime { get { return this[SandboxStat.AimTime]; } }
        public float TurnRate { get { return this[SandboxStat.TurnRate]; } }

        public bool IsNeutral
        {
            get
            {
                for (var i = 0; i < Count; i++)
                {
                    if (_values[i] != 1f)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        public void Reset()
        {
            for (var i = 0; i < Count; i++)
            {
                _values[i] = 1f;
            }
        }

        public void Sanitize()
        {
            for (var i = 0; i < Count; i++)
            {
                _values[i] = SandboxMultiplierMath.SanitizeMultiplier(_values[i]);
            }
        }

        public void CopyFrom(SandboxMultipliers other)
        {
            if (other == null)
            {
                throw new ArgumentNullException("other");
            }

            Array.Copy(other._values, _values, Count);
        }

        /// <summary>Godot key, e.g. <c>player_hp_mul</c> for prefix <c>player_</c>.</summary>
        public static string Key(string prefix, SandboxStat stat)
        {
            return (prefix ?? string.Empty) + Keys[(int)stat] + "_mul";
        }

        public void Write(JsonNode node, string prefix)
        {
            for (var i = 0; i < Count; i++)
            {
                node.Set(Key(prefix, (SandboxStat)i), (double)_values[i]);
            }
        }

        public void Read(JsonNode node, string prefix)
        {
            for (var i = 0; i < Count; i++)
            {
                _values[i] = SandboxJson.Float(node, Key(prefix, (SandboxStat)i), _values[i]);
            }
        }
    }

    /// <summary>
    /// Godot <c>Dictionary.get(key, default)</c> followed by <c>int()</c>,
    /// <c>float()</c>, <c>bool()</c> or <c>str()</c>, over the engine-free JSON tree.
    /// </summary>
    public static class SandboxJson
    {
        public static float Float(JsonNode node, string key, float fallback)
        {
            var value = node[key];
            switch (value.Kind)
            {
                case JsonKind.Number: return (float)value.AsDouble();
                case JsonKind.Bool: return value.AsBool() ? 1f : 0f;
                case JsonKind.String:
                    double parsed;
                    return double.TryParse(value.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                        ? (float)parsed
                        : 0f;
                default: return fallback;
            }
        }

        public static int Int(JsonNode node, string key, int fallback)
        {
            var value = node[key];
            if (value.Kind == JsonKind.Null)
            {
                return fallback;
            }

            // GDScript int(float) truncates toward zero.
            var number = Float(node, key, fallback);
            if (!SandboxMultiplierMath.IsFinite(number))
            {
                return fallback;
            }

            if (number >= int.MaxValue)
            {
                return int.MaxValue;
            }

            if (number <= int.MinValue)
            {
                return int.MinValue;
            }

            return (int)number;
        }

        public static bool Bool(JsonNode node, string key, bool fallback)
        {
            var value = node[key];
            switch (value.Kind)
            {
                case JsonKind.Bool:
                case JsonKind.Number:
                    return value.AsBool();
                case JsonKind.String:
                    return !string.IsNullOrEmpty(value.AsString());
                default:
                    return fallback;
            }
        }

        public static string Text(JsonNode node, string key, string fallback)
        {
            var value = node[key];
            switch (value.Kind)
            {
                case JsonKind.String: return value.AsString();
                case JsonKind.Null: return fallback;
                default: return value.ToJson(false);
            }
        }
    }
}
