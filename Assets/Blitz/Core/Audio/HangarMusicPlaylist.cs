using System;
using System.Collections.Generic;
using System.Globalization;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Audio
{
    /// <summary>One track of the hangar music player.</summary>
    public sealed class HangarMusicTrack
    {
        public HangarMusicTrack(
            string id, string artist, string title, string file, double seconds, float loudness, bool isDefault)
        {
            Id = id;
            Artist = artist ?? string.Empty;
            Title = title ?? string.Empty;
            File = file;
            Seconds = seconds;
            Loudness = loudness;
            IsDefault = isDefault;
        }

        public string Id { get; }

        public string Artist { get; }

        public string Title { get; }

        /// <summary>The file name inside the playlist's StreamingAssets folder.</summary>
        public string File { get; }

        /// <summary>Length of the encoded file.</summary>
        public double Seconds { get; }

        /// <summary>Integrated EBU R128 loudness of the encoded file, LUFS.</summary>
        public float Loudness { get; }

        /// <summary>The track a first session starts with.</summary>
        public bool IsDefault { get; }

        /// <summary>The linear gain that plays the track at <see cref="HangarMusicPlaylist.TargetLoudness"/>.</summary>
        public float Gain
        {
            get { return HangarMusicPlaylist.GainFor(Loudness); }
        }
    }

    /// <summary>
    /// The playlist of the hangar music player, Resources/Data/hangar_music.json
    /// as Tools/export_hangar_music.py writes it: the tracks of the Panzer War
    /// Miku hangar in their order, each an Ogg Vorbis file in
    /// StreamingAssets/&lt;folder&gt; with its length and loudness.
    ///
    /// The sources lie 25 LU apart, from quiet game themes to clipped dance
    /// tracks, so every track is played at the loudness of the client's own
    /// music instead of at the level it was mastered to.
    /// </summary>
    public sealed class HangarMusicPlaylist
    {
        /// <summary>Resources/Data/hangar_music.json.</summary>
        public const string Resource = "hangar_music";

        /// <summary>
        /// The loudness every track is evened out to. The client's own music,
        /// the battle result fanfares and the login loops, sits at -18 LUFS,
        /// so the player is as loud as the rest of the music bus.
        /// </summary>
        public const float TargetLoudness = -18f;

        /// <summary>
        /// The most a quiet track is lifted, in dB. The quietest track of the
        /// playlist needs about 7; a nearly silent file is not blown up.
        /// </summary>
        public const float MaxBoostDb = 9f;

        private readonly List<HangarMusicTrack> _tracks;

        private HangarMusicPlaylist(string folder, List<HangarMusicTrack> tracks)
        {
            Folder = folder;
            _tracks = tracks;
            DefaultIndex = 0;
            for (var i = 0; i < tracks.Count; i++)
            {
                if (tracks[i].IsDefault)
                {
                    DefaultIndex = i;
                    break;
                }
            }
        }

        /// <summary>The StreamingAssets folder of the files.</summary>
        public string Folder { get; }

        public IReadOnlyList<HangarMusicTrack> Tracks
        {
            get { return _tracks; }
        }

        public int Count
        {
            get { return _tracks.Count; }
        }

        public HangarMusicTrack this[int index]
        {
            get { return _tracks[index]; }
        }

        /// <summary>The default track, or the first one when none is marked.</summary>
        public int DefaultIndex { get; }

        /// <summary>The index of the track <paramref name="id"/>, or -1.</summary>
        public int IndexOf(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return -1;
            }

            for (var i = 0; i < _tracks.Count; i++)
            {
                if (string.Equals(_tracks[i].Id, id, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// The track the player starts on: the one the save names, or the
        /// default track for a first session or a track since dropped.
        /// </summary>
        public int StartIndex(string savedId)
        {
            var saved = IndexOf(savedId);
            return saved >= 0 ? saved : DefaultIndex;
        }

        /// <summary>
        /// The gain for a track measured at <paramref name="loudness"/> LUFS:
        /// loud tracks are turned down to the target, quiet ones lifted by at
        /// most <see cref="MaxBoostDb"/>.
        /// </summary>
        public static float GainFor(float loudness)
        {
            if (float.IsNaN(loudness) || float.IsInfinity(loudness))
            {
                return 1f;
            }

            var db = Math.Min(TargetLoudness - loudness, MaxBoostDb);
            return (float)Math.Pow(10d, db / 20d);
        }

        /// <summary>
        /// Reads the exported playlist. Rows without an id, a file or a length
        /// are left out; a missing or broken document is an empty playlist,
        /// which keeps the player off rather than failing the hangar.
        /// </summary>
        public static HangarMusicPlaylist Parse(string json)
        {
            JsonNode root;
            if (string.IsNullOrEmpty(json) || !JsonNode.TryParse(json, out root) || root.Kind != JsonKind.Object)
            {
                return Empty();
            }

            var tracks = new List<HangarMusicTrack>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var rows = root["tracks"];
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var id = row["id"].AsString();
                var file = row["file"].AsString();
                var seconds = row["seconds"].AsDouble();
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(file) || !(seconds > 0d) || !ids.Add(id))
                {
                    continue;
                }

                tracks.Add(new HangarMusicTrack(
                    id,
                    row["artist"].AsString(),
                    row["title"].AsString(id),
                    file,
                    seconds,
                    row["loudness"].AsFloat(TargetLoudness),
                    row["default"].AsBool()));
            }

            return new HangarMusicPlaylist(root["folder"].AsString(), tracks);
        }

        public static HangarMusicPlaylist Empty()
        {
            return new HangarMusicPlaylist(string.Empty, new List<HangarMusicTrack>());
        }

        /// <summary>
        /// A play time the way the player shows it: m:ss, and h:mm:ss from an
        /// hour on. Fractions are cut, so the clock ticks on whole seconds.
        /// </summary>
        public static string FormatTime(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0d)
            {
                seconds = 0d;
            }

            var whole = (long)Math.Floor(Math.Min(seconds, 359999d));
            var hours = whole / 3600;
            var minutes = whole / 60 % 60;
            var rest = whole % 60;
            return hours > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", hours, minutes, rest)
                : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", minutes, rest);
        }
    }
}
