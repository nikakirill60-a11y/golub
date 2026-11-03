// Client particle archives as exported by Tools/prepare_halloween_hangar.py:
// property lines (PropertyLineKeyframes with step, linear and Hermite keys) and
// the per record field lists (ints, floats, strings, lines) of emitters, layers
// and forces. Vectors of these records are in the client's axes (z up).
using System;
using System.Collections.Generic;
using UnityEngine;

namespace BlitzLegacy.Runtime.Hangar.Halloween
{
    public sealed class PropertyLine
    {
        struct Key
        {
            public int interp;
            public float time, value, inSlope, outSlope;
        }

        public readonly string name;
        readonly Key[][] channels;

        PropertyLine(string name, Key[][] channels)
        {
            this.name = name;
            this.channels = channels;
        }

        public int Dims => channels.Length;

        public static PropertyLine Parse(HalloweenJson line, string label)
        {
            string name = line.Req("name").S();
            var list = line.Req("channels").Items;
            HH.Require(list.Count > 0, $"{label}: property line {name} has no channels");
            var channels = new Key[list.Count][];
            for (int c = 0; c < list.Count; c++)
            {
                var keys = list[c].Req("keys").Items;
                HH.Require(keys.Count > 0, $"{label}: property line {name} channel {c} has no keys");
                channels[c] = new Key[keys.Count];
                for (int k = 0; k < keys.Count; k++)
                {
                    var key = keys[k];
                    var parsed = new Key
                    {
                        interp = key.Req("interp").I(),
                        time = key.Req("time").F(),
                        value = key.Req("value").F(),
                        inSlope = key["inSlope"].F(),
                        outSlope = key["outSlope"].F(),
                    };
                    HH.Require(parsed.interp >= 0 && parsed.interp <= 2, $"{label}: property line {name} uses interpolation {parsed.interp}");
                    HH.Require(k == 0 || parsed.time >= channels[c][k - 1].time, $"{label}: property line {name} keys are not sorted");
                    channels[c][k] = parsed;
                }
            }
            return new PropertyLine(name, channels);
        }

        public static PropertyLine Constant(string name, params float[] values)
        {
            var channels = new Key[values.Length][];
            for (int c = 0; c < values.Length; c++)
                channels[c] = new[] { new Key { interp = 1, time = 0f, value = values[c] } };
            return new PropertyLine(name, channels);
        }

        public bool IsConstant
        {
            get
            {
                foreach (var keys in channels)
                    for (int k = 0; k < keys.Length; k++)
                    {
                        if (Mathf.Abs(keys[k].value - keys[0].value) > 1e-6f)
                            return false;
                        if (k + 1 < keys.Length && keys[k].interp == 2
                            && (Mathf.Abs(keys[k].outSlope) > 1e-6f || Mathf.Abs(keys[k + 1].inSlope) > 1e-6f))
                            return false;
                    }
                return true;
            }
        }

        // Key times of one channel, for sampling the line without missing a knot.
        public IEnumerable<float> Times(int channel)
        {
            foreach (var key in channels[Channel(channel)])
                yield return key.time;
        }

        public bool HasStep(int channel)
        {
            var keys = channels[Channel(channel)];
            for (int k = 0; k + 1 < keys.Length; k++)
                if (keys[k].interp == 0 && Mathf.Abs(keys[k + 1].value - keys[k].value) > 1e-6f)
                    return true;
            return false;
        }

        int Channel(int channel)
        {
            HH.Require(channel >= 0 && channel < channels.Length, $"property line {name} has no channel {channel}");
            return channel;
        }

        // PropertyLineKeyframes::GetValue: clamped to the first and last key,
        // each segment interpolated the way its left key says.
        public float Value(int channel, float t)
        {
            var keys = channels[Channel(channel)];
            if (keys.Length == 1 || t <= keys[0].time)
                return keys[0].value;
            var last = keys[keys.Length - 1];
            if (t >= last.time)
                return last.value;
            int i = 0;
            while (i < keys.Length - 2 && t >= keys[i + 1].time)
                i++;
            var a = keys[i];
            var b = keys[i + 1];
            float dt = b.time - a.time;
            if (dt <= 0f)
                return b.value;
            float u = (t - a.time) / dt;
            switch (a.interp)
            {
                case 0:
                    return a.value;
                case 1:
                    return a.value + (b.value - a.value) * u;
                default:
                    float u2 = u * u, u3 = u2 * u;
                    return (2f * u3 - 3f * u2 + 1f) * a.value + (u3 - 2f * u2 + u) * a.outSlope * dt
                         + (-2f * u3 + 3f * u2) * b.value + (u3 - u2) * b.inSlope * dt;
            }
        }

        public Vector3 Vector(float t) =>
            new Vector3(Value(0, t), Dims > 1 ? Value(1, t) : 0f, Dims > 2 ? Value(2, t) : 0f);

        public Color Color(float t)
        {
            if (Dims >= 4) return new Color(Value(0, t), Value(1, t), Value(2, t), Value(3, t));
            if (Dims == 3) return new Color(Value(0, t), Value(1, t), Value(2, t), 1f);
            float v = Value(0, t);
            return new Color(v, v, v, 1f);
        }

        // Unity curve over the particle's normalized age a:
        // scale * line(a * timeScale), piecewise linear through the line's knots
        // and 32 regular samples, steps kept sharp.
        public AnimationCurve Curve(int channel, float timeScale, float scale)
        {
            Channel(channel);
            var times = new List<float>();
            for (int i = 0; i <= 32; i++)
                times.Add(i / 32f);
            var keys = channels[channel];
            for (int k = 0; k < keys.Length; k++)
            {
                float t = keys[k].time / timeScale;
                if (t <= 0f || t >= 1f)
                    continue;
                times.Add(t);
                if (k > 0 && keys[k - 1].interp == 0)
                    times.Add(Math.Max(0f, t - 1e-4f));
            }
            times.Sort();
            var frames = new List<Keyframe>();
            foreach (float t in times)
                if (frames.Count == 0 || t - frames[frames.Count - 1].time > 5e-5f)
                    frames.Add(new Keyframe(t, scale * Value(channel, t * timeScale)));
            var result = frames.ToArray();
            for (int i = 0; i < result.Length; i++)
            {
                float left = i > 0 ? (result[i].value - result[i - 1].value) / (result[i].time - result[i - 1].time) : 0f;
                float right = i + 1 < result.Length ? (result[i + 1].value - result[i].value) / (result[i + 1].time - result[i].time) : 0f;
                result[i].inTangent = i > 0 ? left : right;
                result[i].outTangent = i + 1 < result.Length ? right : left;
            }
            return new AnimationCurve(result);
        }
    }

    // One exported particle archive (emitter, layer or force).
    public sealed class ParticleFields
    {
        public readonly string label;
        readonly Dictionary<string, int> ints = new Dictionary<string, int>();
        readonly Dictionary<string, float> values = new Dictionary<string, float>();
        readonly Dictionary<string, string> strings = new Dictionary<string, string>();
        readonly Dictionary<string, PropertyLine> lines = new Dictionary<string, PropertyLine>();

        public ParticleFields(HalloweenJson record, string label)
        {
            this.label = label;
            foreach (var x in record["ints"].Items) ints[x.Req("name").S()] = x.Req("value").I();
            foreach (var x in record["values"].Items) values[x.Req("name").S()] = x.Req("value").F();
            foreach (var x in record["strings"].Items) strings[x.Req("name").S()] = x.Req("value").S();
            foreach (var x in record["lines"].Items)
            {
                var line = PropertyLine.Parse(x, label);
                lines[line.name] = line;
            }
        }

        public bool Has(string name) =>
            ints.ContainsKey(name) || values.ContainsKey(name) || strings.ContainsKey(name) || lines.ContainsKey(name);

        public int Int(string name, int fallback = 0) => ints.TryGetValue(name, out int v) ? v : fallback;

        public bool Flag(string name) => Int(name) != 0;

        public string Str(string name, string fallback = "") => strings.TryGetValue(name, out string v) ? v : fallback;

        // A property line, or a constant line for a property the archive keeps as one float.
        public PropertyLine Line(string name)
        {
            if (lines.TryGetValue(name, out var line))
                return line;
            if (values.TryGetValue(name, out float v))
                return PropertyLine.Constant(name, v);
            return null;
        }

        // A property the client samples once per particle at the layer's loop
        // time; every such line of the hangar is constant.
        public float Num(string name, float fallback = 0f)
        {
            var line = Line(name);
            if (line == null)
                return fallback;
            if (!line.IsConstant)
                HH.Warn($"{label}: {name} changes over the layer time, its value at time 0 is used");
            return line.Value(0, 0f);
        }

        public Vector3 Vec(string name, Vector3 fallback)
        {
            var line = Line(name);
            if (line == null)
                return fallback;
            if (!line.IsConstant)
                HH.Warn($"{label}: {name} changes over the layer time, its value at time 0 is used");
            return line.Vector(0f);
        }
    }
}
