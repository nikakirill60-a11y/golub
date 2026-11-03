using System;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Save
{
    /// <summary>The views of the hangar music panel.</summary>
    public enum HangarMusicView
    {
        Player,
        Playlist,
        Equalizer,
    }

    /// <summary>
    /// What the hangar music player keeps between sessions, saved with the
    /// settings as "hangar_music": the track it was on, its own volume, the
    /// shuffle and repeat-one switches, the view of its panel, whether the
    /// panel is folded and where it was dragged, whether the player paused
    /// the music, and the equalizer. Whether a hangar shows the player at all
    /// is <see cref="SettingsData.HangarMusicPlayer"/>.
    /// </summary>
    public sealed class HangarMusicSettings
    {
        /// <summary>The volume of the Panzer War Miku player out of the box.</summary>
        public const float DefaultVolume = 0.8f;

        /// <summary>The farthest a saved edge may lie from the canvas's, in canvas units.</summary>
        private const double FarthestPlace = 100000d;

        private static readonly string[] ViewNames = { "player", "playlist", "equalizer" };

        /// <summary>The id of the last track; empty starts on the playlist's default track.</summary>
        public string Track = string.Empty;

        private float _volume = DefaultVolume;
        private readonly int[] _eqGains = new int[HangarMusicEqualizer.BandCount];

        public bool Shuffle;
        public bool RepeatOne;
        public HangarMusicView View;
        public bool Collapsed;
        public bool Paused;

        /// <summary>
        /// Whether the panel was dragged. Until it is, it stands where the
        /// hangar puts it, and <see cref="Right"/> and <see cref="Top"/> are
        /// not saved.
        /// </summary>
        public bool Placed;

        /// <summary>How far the dragged panel's right edge is from the canvas's, in canvas units.</summary>
        public float Right;

        /// <summary>How far the dragged panel's top edge is from the canvas's, in canvas units.</summary>
        public float Top;

        /// <summary>Whether the equalizer shapes the music; off, its bands wait at their gains.</summary>
        public bool EqOn;

        /// <summary>The player's own volume, 0..1, under the music slider of the settings.</summary>
        public float Volume
        {
            get { return _volume; }
            set { _volume = float.IsNaN(value) ? DefaultVolume : Math.Max(0f, Math.Min(1f, value)); }
        }

        /// <summary>The gain of equalizer band <paramref name="band"/>, in whole decibels.</summary>
        public int EqGain(int band)
        {
            return _eqGains[band];
        }

        /// <summary>Sets the gain of <paramref name="band"/>, held to the equalizer's -12..+12 dB.</summary>
        public void SetEqGain(int band, int decibels)
        {
            _eqGains[band] = HangarMusicEqualizer.ClampGain(decibels);
        }

        public JsonNode ToJson()
        {
            var gains = JsonNode.NewArray();
            foreach (var gain in _eqGains)
            {
                gains.Add(JsonNode.Num(gain));
            }

            var node = JsonNode.NewObject()
                .Set("track", Track ?? string.Empty)
                .Set("volume", Volume)
                .Set("shuffle", Shuffle)
                .Set("repeat_one", RepeatOne)
                .Set("view", ViewNames[(int)View])
                .Set("collapsed", Collapsed)
                .Set("paused", Paused)
                .Set("eq", EqOn)
                .Set("eq_gains", gains);
            if (Placed)
            {
                node.Set("right", Math.Round(Right, 2)).Set("top", Math.Round(Top, 2));
            }

            return node;
        }

        /// <summary>
        /// Reads the saved state. An unknown view opens the player, an
        /// equalizer gain out of range takes the nearer end, and a place
        /// without both edges, or with one that is not a number of a sane
        /// size, is dropped.
        /// </summary>
        public static HangarMusicSettings FromJson(JsonNode node)
        {
            var settings = new HangarMusicSettings();
            if (node == null || node.Kind != JsonKind.Object)
            {
                return settings;
            }

            settings.Track = node["track"].AsString();
            settings.Volume = node["volume"].AsFloat(DefaultVolume);
            settings.Shuffle = node["shuffle"].AsBool();
            settings.RepeatOne = node["repeat_one"].AsBool();
            var view = Array.IndexOf(ViewNames, node["view"].AsString());
            settings.View = view < 0 ? HangarMusicView.Player : (HangarMusicView)view;
            settings.Collapsed = node["collapsed"].AsBool();
            settings.Paused = node["paused"].AsBool();
            settings.EqOn = node["eq"].AsBool();
            var gains = node["eq_gains"];
            if (gains.Kind == JsonKind.Array)
            {
                for (var band = 0; band < HangarMusicEqualizer.BandCount && band < gains.Count; band++)
                {
                    var gain = gains[band].AsDouble();
                    settings.SetEqGain(band, double.IsNaN(gain) ? 0 : (int)Math.Round(Math.Max(-1000d, Math.Min(1000d, gain))));
                }
            }

            var right = node["right"];
            var top = node["top"];
            if (right.Kind == JsonKind.Number && top.Kind == JsonKind.Number &&
                Sane(right.AsDouble()) && Sane(top.AsDouble()))
            {
                settings.Placed = true;
                settings.Right = (float)right.AsDouble();
                settings.Top = (float)top.AsDouble();
            }

            return settings;
        }

        private static bool Sane(double value)
        {
            return !double.IsNaN(value) && Math.Abs(value) <= FarthestPlace;
        }
    }
}
