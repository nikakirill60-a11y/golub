using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Tanks
{
    /// <summary>
    /// A gun sub-assembly animation baked by <c>Tools/blitz_1120_joints.py</c>
    /// from the client's skeletal <c>.anim</c> clip (the Annihilator's Gatling
    /// spin, barrel recoil, recoil levers, ammunition belt and reload drum).
    /// Each track drives one exported object (<c>gun_01_rotor</c>,
    /// <c>gun_01_anim_&lt;joint&gt;</c>) with a rigid motion around that
    /// object's pivot, sampled at <see cref="Fps"/>.
    /// </summary>
    public sealed class GunClip
    {
        /// <summary>Played on the first shell of a burst.</summary>
        public const string BurstTrigger = "burst";

        /// <summary>Played when the gun finishes reloading.</summary>
        public const string ReloadedTrigger = "reloaded";

        private readonly Dictionary<string, float[][]> _tracks = new Dictionary<string, float[][]>();

        public string Name { get; private set; }
        public string Trigger { get; private set; }
        public float Duration { get; private set; }
        public float Fps { get; private set; }

        public IEnumerable<string> Tracks
        {
            get { return _tracks.Keys; }
        }

        public bool HasTrack(string name)
        {
            return _tracks.ContainsKey(name ?? string.Empty);
        }

        public static GunClip Parse(string name, JsonNode node)
        {
            var clip = new GunClip
            {
                Name = name,
                Trigger = node["trigger"].AsString(BurstTrigger),
                Duration = node["duration"].AsFloat(),
                Fps = node["fps"].AsFloat(30f),
            };
            var tracks = node["tracks"];
            if (tracks != null && tracks.Kind == JsonKind.Object)
            {
                foreach (var track in tracks.Fields)
                {
                    var frames = new float[track.Value.Count][];
                    for (var i = 0; i < frames.Length; i++)
                    {
                        var frame = new float[7];
                        var source = track.Value[i];
                        for (var k = 0; k < 7 && k < source.Count; k++)
                        {
                            frame[k] = (float)source[k].AsDouble();
                        }

                        frames[i] = frame;
                    }

                    clip._tracks[track.Key] = frames;
                }
            }

            return clip;
        }

        /// <summary>
        /// The motion of one track at <paramref name="time"/>, in battle space:
        /// <paramref name="position"/> is the offset from the pivot's rest
        /// place and <paramref name="rotation"/> an (x, y, z, w) quaternion.
        /// The porter writes raw nodes-table coordinates, not imported mesh
        /// coordinates. Battle keeps source X and Y and reflects Z, as for
        /// every node pivot; conjugating a rotation negates quaternion X/Y.
        /// </summary>
        public bool Sample(string track, float time, float[] position, float[] rotation)
        {
            float[][] frames;
            if (!_tracks.TryGetValue(track ?? string.Empty, out frames) || frames.Length == 0)
            {
                return false;
            }

            var at = Math.Max(0f, time) * Fps;
            var index = Math.Min(frames.Length - 1, (int)Math.Floor(at));
            var next = Math.Min(frames.Length - 1, index + 1);
            var f = Math.Min(1f, Math.Max(0f, at - index));
            var a = frames[index];
            var b = frames[next];
            for (var k = 0; k < 3; k++)
            {
                position[k] = a[k] + (b[k] - a[k]) * f;
            }

            // Normalised lerp along the shorter arc; frames are 1/30 s apart.
            var dot = a[3] * b[3] + a[4] * b[4] + a[5] * b[5] + a[6] * b[6];
            var sign = dot < 0f ? -1f : 1f;
            var length = 0f;
            for (var k = 0; k < 4; k++)
            {
                rotation[k] = a[3 + k] + (sign * b[3 + k] - a[3 + k]) * f;
                length += rotation[k] * rotation[k];
            }

            length = (float)Math.Sqrt(length);
            for (var k = 0; k < 4; k++)
            {
                rotation[k] = length > 1e-6f ? rotation[k] / length : (k == 3 ? 1f : 0f);
            }

            position[2] = -position[2];
            rotation[0] = -rotation[0];
            rotation[1] = -rotation[1];
            return true;
        }
    }
}
