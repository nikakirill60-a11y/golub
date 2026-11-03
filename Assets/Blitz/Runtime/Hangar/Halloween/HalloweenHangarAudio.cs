// Ambient sound of the Halloween hangar: the scene's Wwise emitters as Unity
// AudioSources, played after the spec Tools/port_halloween_hangar_2026_audio.py
// writes to Resources/HalloweenHangar2026/audio.json.
//
// Wwise evaluates its attenuation and the built-in distance and azimuth game
// parameters against the listener every frame; so does this component, for
// the volume, low-pass, high-pass, spread and reverb send of each emitter.
// The containers become single loops, gapless shuffled playlists (two sources
// handing over through PlayScheduled) and scatterers; time modulators a
// looping timeline; the projector's convolution reverb a pre-rendered wet loop
// started on the same DSP tick as the dry one; the RoomVerb aux bus the
// scatterers send to the one AudioReverbZone the hangar runs. Every other
// source of the game bypasses reverb zones.
using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Runtime.Audio;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace BlitzLegacy.Runtime.Hangar.Halloween
{
    /// <summary>Runs after <see cref="AudioListenerRig"/> has moved onto its pose for the frame.</summary>
    [DefaultExecutionOrder(1010)]
    public sealed class HalloweenHangarAudio : MonoBehaviour
    {
        public static string SpecResource => HH.Root + "/audio";

        /// <summary>Unity's reverb send tops out at +10 dB, written as 1.1.</summary>
        public const float MaxReverbZoneMix = 1.1f;

        /// <summary>How far ahead of its start a voice is handed to the mixer.</summary>
        public const double ScheduleAhead = 0.2;

        // ----------------------------------------------------------- spec
        /// <summary>A piecewise linear automation curve, held flat past its ends.</summary>
        public sealed class Curve
        {
            private readonly float[] _x, _y;

            public Curve(float[] x, float[] y)
            {
                _x = x;
                _y = y;
            }

            public static Curve Parse(HalloweenJson points, string label)
            {
                if (!points.Exists)
                    return null;
                var items = points.Items;
                HH.Require(items.Count > 0, label + ": empty curve");
                var x = new float[items.Count];
                var y = new float[items.Count];
                for (int i = 0; i < items.Count; i++)
                {
                    x[i] = items[i][0].F();
                    y[i] = items[i][1].F();
                    HH.Require(i == 0 || x[i] >= x[i - 1], label + ": curve points out of order");
                }
                return new Curve(x, y);
            }

            public float Evaluate(float t)
            {
                if (t <= _x[0]) return _y[0];
                int last = _x.Length - 1;
                if (t >= _x[last]) return _y[last];
                int i = Array.BinarySearch(_x, t);
                if (i >= 0) return _y[i];
                i = ~i;
                float span = _x[i] - _x[i - 1];
                return span > 0f ? Mathf.Lerp(_y[i - 1], _y[i], (t - _x[i - 1]) / span) : _y[i];
            }
        }

        public sealed class Entry
        {
            public AudioClip Clip;
            public float BakeDb, Volume, Pitch, Delay, VolumeRandom, PitchRandom;
            public bool Looping;
        }

        public enum InstrumentType { Single, Multi, Scatterer }

        public sealed class EventSpec
        {
            public string Name, Wwise;
            public float GainDb, SpatialBlend, MaxDistance;
            public Curve Spread, DistanceVolume, DistanceLowpass, DistanceHighpass, DirectionLowpass;
            public float Lowpass, Highpass;
            public bool Reverb;
            public float ReverbLevel = float.NaN;
            public Curve ReverbCurve;
            public AudioClip WetClip;
            public float WetBakeDb, WetDb = float.NaN, DryDb = float.NaN;
            public Curve WetCurve, DryCurve;
            public float FadeIn;
            public float TimelineLength;
            public Curve TimelineVolume, TimelinePitch;
            public InstrumentType Type;
            public bool Looping;
            public float Pitch, DelayMin, DelayMax, VolumeRandom, PitchRandom, IntervalMin, IntervalMax;
            public int Polyphony = 1, Total;
            public Entry[] Entries = new Entry[0];

            public bool FiltersLow => DistanceLowpass != null || DirectionLowpass != null || Lowpass > 0f;
            public bool FiltersHigh => DistanceHighpass != null || Highpass > 0f;
        }

        private sealed class Voice
        {
            public AudioSource Source;
            public AudioLowPassFilter Low;
            public AudioHighPassFilter High;
            public Entry Entry;
            public float Cut, PitchSt;
            public double Start = double.NegativeInfinity, End = double.NegativeInfinity;
            public bool Wet;
            public bool Busy(double now) => now < End;
        }

        private sealed class Emitter
        {
            public EventSpec Spec;
            public Transform Node;
            public Voice[] Voices;
            public Voice WetVoice;
            public double Begin, First, NextSpawn;
            public int Spawned, Pending = -1, LastEntry = -1;
            public double PendingStart, PendingEnd;
            public readonly List<int> Bag = new List<int>();
            public bool Started;
        }

        private readonly List<Emitter> _emitters = new List<Emitter>();
        private readonly List<AudioClip> _clips = new List<AudioClip>();
        private AudioListenerRig.PoseSource _pose;
        private AudioService _listeningService;
        private bool _released;

        public int EmitterCount => _emitters.Count;
        public int ClipCount => _clips.Count;
        public int VoiceCount { get; private set; }
        public AudioReverbZone ReverbZone { get; private set; }
        public IEnumerable<EventSpec> Events
        {
            get { foreach (var e in _emitters) yield return e.Spec; }
        }

        // ---------------------------------------------------------- build
        public static HalloweenHangarAudio Build(GameObject root, HalloweenJson scene, Transform[] nodes)
        {
            var asset = HH.Load<TextAsset>(SpecResource);
            HalloweenJson spec;
            try
            {
                spec = HalloweenJson.Parse(asset.text);
            }
            finally
            {
                Resources.UnloadAsset(asset);
            }
            HH.Require(spec.Req("map").S() == HH.MapName, $"audio.json describes {spec.Req("map").S()}, not {HH.MapName}");
            var audio = root.AddComponent<HalloweenHangarAudio>();
            var clips = new Dictionary<string, AudioClip>();
            var events = new Dictionary<string, EventSpec>();
            foreach (var e in spec.Req("events").Items)
            {
                var parsed = audio.Parse(e, clips);
                events[parsed.Wwise] = parsed;
            }
            var skipped = new HashSet<string>();
            foreach (var s in spec["skipped"].Items)
                skipped.Add(s.Req("wwise").S());

            foreach (var sound in scene.Req("sounds").Items)
            {
                string wwise = sound.Req("event").S();
                int node = sound.Req("node").I(-1);
                HH.Require(node >= 0 && node < nodes.Length, $"sound {wwise}: node {node}");
                if (!events.TryGetValue(wwise, out var e))
                {
                    HH.Require(skipped.Contains(wwise), $"sound {wwise}: no event in audio.json");
                    HH.Log($"sound {wwise} on {nodes[node].name}: not auto-played in the client, left out");
                    continue;
                }
                HH.Require(sound["autoplay"].B(true), $"sound {wwise}: audio.json ports an event the scene does not auto-play");
                audio.AddEmitter(e, nodes[node]);
            }
            audio.BuildReverbZone(spec.Req("reverbZone").Req("unity"));
            HH.Log($"audio: {audio._emitters.Count} emitters, {audio.VoiceCount} sources, {audio._clips.Count} clips, reverb zone decay {audio.ReverbZone.decayTime:0.##} s");
            return audio;
        }

        private AudioClip Clip(string resource, Dictionary<string, AudioClip> clips)
        {
            if (clips.TryGetValue(resource, out var clip))
                return clip;
            clip = HH.Load<AudioClip>(resource);
            clips[resource] = clip;
            _clips.Add(clip);
            return clip;
        }

        private static float Optional(HalloweenJson value) => value.IsNumber ? value.F() : float.NaN;

        private EventSpec Parse(HalloweenJson e, Dictionary<string, AudioClip> clips)
        {
            string name = e.Req("name").S();
            var spec = new EventSpec
            {
                Name = name,
                Wwise = e.Req("wwise").S(),
                GainDb = e["gainDb"].F(),
                SpatialBlend = Mathf.Clamp01(e["spatialBlend"].F(1f)),
                MaxDistance = e["maxDistance"].F(0f),
                Spread = Curve.Parse(e["spread"], name + " spread"),
                Lowpass = e["lowpass"].F(0f),
                Highpass = e["highpass"].F(0f),
                FadeIn = e["fadeInMs"].F() / 1000f,
            };
            var distance = e["distance"];
            if (distance.Exists)
            {
                spec.DistanceVolume = Curve.Parse(distance["volume"], name + " distance volume");
                spec.DistanceLowpass = Curve.Parse(distance["lowpass"], name + " distance low-pass");
                spec.DistanceHighpass = Curve.Parse(distance["highpass"], name + " distance high-pass");
            }
            var direction = e["direction"];
            if (direction.Exists)
                spec.DirectionLowpass = Curve.Parse(direction["lowpass"], name + " azimuth low-pass");
            var reverb = e["reverb"];
            if (reverb.Exists)
            {
                spec.Reverb = true;
                spec.ReverbLevel = Optional(reverb["level"]);
                spec.ReverbCurve = Curve.Parse(reverb["curve"], name + " reverb send");
                HH.Require(!float.IsNaN(spec.ReverbLevel) || spec.ReverbCurve != null, name + ": a reverb send without a level");
            }
            var convolution = e["convolution"];
            if (convolution.Exists)
            {
                spec.WetClip = Clip(convolution.Req("clip").S(), clips);
                spec.WetBakeDb = convolution["bakeDb"].F();
                spec.WetDb = Optional(convolution["wet"]);
                spec.DryDb = Optional(convolution["dry"]);
                spec.WetCurve = Curve.Parse(convolution["wetCurve"], name + " wet level");
                spec.DryCurve = Curve.Parse(convolution["dryCurve"], name + " dry level");
            }
            var timeline = e["timeline"];
            if (timeline.Exists)
            {
                spec.TimelineLength = timeline.Req("length").F();
                HH.Require(spec.TimelineLength > 0f, name + ": timeline without a length");
                spec.TimelineVolume = Curve.Parse(timeline["volume"], name + " timeline volume");
                spec.TimelinePitch = Curve.Parse(timeline["pitch"], name + " timeline pitch");
            }
            var instrument = e.Req("instrument");
            switch (instrument.Req("type").S())
            {
                case "single": spec.Type = InstrumentType.Single; break;
                case "multi": spec.Type = InstrumentType.Multi; break;
                case "scatterer": spec.Type = InstrumentType.Scatterer; break;
                default: throw new InvalidOperationException(name + ": unknown instrument " + instrument.Req("type").S());
            }
            spec.Looping = instrument["looping"].B();
            spec.Pitch = instrument["pitch"].F();
            var delay = instrument["delay"].Floats();
            if (delay.Length >= 2)
            {
                spec.DelayMin = Mathf.Min(delay[0], delay[1]) / 1000f;
                spec.DelayMax = Mathf.Max(delay[0], delay[1]) / 1000f;
            }
            spec.VolumeRandom = instrument["volumeRandom"].F();
            spec.PitchRandom = instrument["pitchRandom"].F();
            var interval = instrument["interval"].Floats();
            if (interval.Length >= 2)
            {
                spec.IntervalMin = Mathf.Min(interval[0], interval[1]) / 1000f;
                spec.IntervalMax = Mathf.Max(interval[0], interval[1]) / 1000f;
            }
            spec.Polyphony = Mathf.Max(1, instrument["polyphony"].I(1));
            spec.Total = Mathf.Max(0, instrument["total"].I(0));
            var entries = new List<Entry>();
            foreach (var entry in instrument.Req("entries").Items)
            {
                var clip = Clip(entry.Req("clip").S(), clips);
                entries.Add(new Entry
                {
                    Clip = clip,
                    BakeDb = entry["bakeDb"].F(),
                    Volume = entry["volume"].F(),
                    Pitch = entry["pitch"].F(),
                    Delay = entry["delay"].F() / 1000f,
                    VolumeRandom = entry["volumeRandom"].F(),
                    PitchRandom = entry["pitchRandom"].F(),
                    Looping = entry["looping"].B(),
                });
            }
            HH.Require(entries.Count > 0, name + ": an instrument without entries");
            HH.Require(spec.Type != InstrumentType.Scatterer || spec.IntervalMax > 0f, name + ": a scatterer without a spawn interval");
            spec.Entries = entries.ToArray();
            return spec;
        }

        private void AddEmitter(EventSpec spec, Transform node)
        {
            var emitter = new Emitter { Spec = spec, Node = node };
            int count = spec.Type == InstrumentType.Multi ? 2 : spec.Type == InstrumentType.Scatterer ? spec.Polyphony : 1;
            emitter.Voices = new Voice[count];
            for (int i = 0; i < count; i++)
                emitter.Voices[i] = CreateVoice(spec, node, $"{spec.Name}_{i}", false);
            if (spec.WetClip != null)
                emitter.WetVoice = CreateVoice(spec, node, spec.Name + "_wet", true);
            _emitters.Add(emitter);
        }

        private Voice CreateVoice(EventSpec spec, Transform node, string name, bool wet)
        {
            var go = new GameObject("Sound_" + name);
            go.transform.SetParent(node, false);
            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.volume = 0f;
            source.spatialBlend = spec.SpatialBlend;
            source.dopplerLevel = 0f;
            // The distance curves are applied to the volume here, so Unity's
            // own roll-off stays flat out to the event's audible range.
            source.rolloffMode = AudioRolloffMode.Custom;
            source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, AnimationCurve.Constant(0f, 1f, 1f));
            source.minDistance = 1f;
            source.maxDistance = Mathf.Max(1f, spec.MaxDistance > 0f ? spec.MaxDistance : 1000f);
            source.reverbZoneMix = 0f;
            source.bypassReverbZones = !spec.Reverb;
            source.priority = 128;
            var voice = new Voice { Source = source, Wet = wet };
            if (spec.FiltersLow)
            {
                voice.Low = go.AddComponent<AudioLowPassFilter>();
                // Butterworth: -3 dB at the cut-off, as the curves are written.
                voice.Low.lowpassResonanceQ = 0.7071f;
                voice.Low.cutoffFrequency = 22000f;
            }
            if (spec.FiltersHigh)
            {
                voice.High = go.AddComponent<AudioHighPassFilter>();
                voice.High.highpassResonanceQ = 0.7071f;
                voice.High.cutoffFrequency = 10f;
            }
            VoiceCount++;
            return voice;
        }

        private void BuildReverbZone(HalloweenJson unity)
        {
            var go = new GameObject("ReverbZone");
            go.transform.SetParent(transform, false);
            var zone = go.AddComponent<AudioReverbZone>();
            // The aux bus is global in Wwise: the zone covers every listener
            // position the stand's camera can reach.
            zone.minDistance = 5000f;
            zone.maxDistance = 5001f;
            zone.reverbPreset = AudioReverbPreset.User;
            zone.room = unity.Req("room").I();
            zone.roomHF = unity.Req("roomHF").I();
            zone.roomLF = unity.Req("roomLF").I();
            zone.decayTime = unity.Req("decayTime").F();
            zone.decayHFRatio = unity.Req("decayHFRatio").F();
            zone.reflections = unity.Req("reflections").I();
            zone.reflectionsDelay = unity.Req("reflectionsDelay").F();
            zone.reverb = unity.Req("reverb").I();
            zone.reverbDelay = unity.Req("reverbDelay").F();
            zone.HFReference = unity.Req("hfReference").F();
            zone.LFReference = unity.Req("lfReference").F();
            zone.diffusion = unity.Req("diffusion").F();
            zone.density = unity.Req("density").F();
            ReverbZone = zone;
        }

        // -------------------------------------------------------- listener
        /// <summary>Hears the hangar from <paramref name="camera"/>, as the client listens from its camera.</summary>
        public void ListenFrom(Camera camera)
        {
            StopListening();
            var service = BlitzBootstrap.Current != null ? BlitzBootstrap.Current.Audio : null;
            if (service == null || camera == null)
                return;
            var view = camera.transform;
            _pose = (out Vector3 position, out Quaternion rotation) =>
            {
                if (view == null)
                {
                    position = Vector3.zero;
                    rotation = Quaternion.identity;
                    return false;
                }
                position = view.position;
                rotation = view.rotation;
                return true;
            };
            _listeningService = service;
            service.ListenFrom(_pose);
        }

        private void StopListening()
        {
            if (_listeningService != null && _pose != null)
                _listeningService.StopListeningFrom(_pose);
            _listeningService = null;
            _pose = null;
        }

        // -------------------------------------------------------- playback
        private void OnEnable()
        {
            double now = AudioSettings.dspTime;
            foreach (var emitter in _emitters)
                if (!emitter.Started)
                    StartEmitter(emitter, now);
        }

        private void OnDisable()
        {
            foreach (var emitter in _emitters)
            {
                foreach (var voice in emitter.Voices)
                    StopVoice(voice);
                if (emitter.WetVoice != null)
                    StopVoice(emitter.WetVoice);
                emitter.Started = false;
                emitter.Pending = -1;
            }
        }

        private static void StopVoice(Voice voice)
        {
            if (voice.Source != null)
                voice.Source.Stop();
            voice.Start = voice.End = double.NegativeInfinity;
            voice.Entry = null;
        }

        private static float Range(float min, float max) => max > min ? Random.Range(min, max) : min;

        private static double Ratio(float semitones) => Math.Pow(2.0, semitones / 12.0);

        private static double Length(Entry entry, double ratio)
        {
            var clip = entry.Clip;
            return clip.frequency > 0 ? clip.samples / (double)clip.frequency / ratio : 0.0;
        }

        private void StartEmitter(Emitter emitter, double now)
        {
            var spec = emitter.Spec;
            emitter.Started = true;
            emitter.Begin = now;
            emitter.Spawned = 0;
            emitter.LastEntry = -1;
            emitter.Bag.Clear();
            emitter.First = now + ScheduleAhead + Range(spec.DelayMin, spec.DelayMax);
            switch (spec.Type)
            {
                case InstrumentType.Single:
                    Play(emitter, emitter.Voices[0], 0, emitter.First, spec.Looping || spec.Entries[0].Looping);
                    break;
                case InstrumentType.Multi:
                {
                    double end = Play(emitter, emitter.Voices[0], NextInPlaylist(emitter), emitter.First, false);
                    emitter.Pending = 1;
                    emitter.PendingStart = end;
                    emitter.PendingEnd = Play(emitter, emitter.Voices[1], NextInPlaylist(emitter), end, false);
                    break;
                }
                case InstrumentType.Scatterer:
                    emitter.NextSpawn = emitter.First;
                    break;
            }
        }

        /// <summary>Shuffled playlist order: every entry once per round.</summary>
        private static int NextInPlaylist(Emitter emitter)
        {
            int count = emitter.Spec.Entries.Length;
            if (emitter.Bag.Count == 0)
            {
                for (int i = 0; i < count; i++)
                    emitter.Bag.Add(i);
                for (int i = count - 1; i > 0; i--)
                {
                    int j = Random.Range(0, i + 1);
                    (emitter.Bag[i], emitter.Bag[j]) = (emitter.Bag[j], emitter.Bag[i]);
                }
                // A new round never opens with the entry the last one closed on.
                if (count > 1 && emitter.Bag[count - 1] == emitter.LastEntry)
                    (emitter.Bag[0], emitter.Bag[count - 1]) = (emitter.Bag[count - 1], emitter.Bag[0]);
            }
            int last = emitter.Bag.Count - 1;
            int next = emitter.Bag[last];
            emitter.Bag.RemoveAt(last);
            emitter.LastEntry = next;
            return next;
        }

        /// <summary>Schedules entry <paramref name="index"/> on <paramref name="voice"/>; returns the DSP time it ends.</summary>
        private double Play(Emitter emitter, Voice voice, int index, double start, bool loop)
        {
            var spec = emitter.Spec;
            var entry = spec.Entries[index];
            voice.Entry = entry;
            voice.Cut = Range(0f, spec.VolumeRandom) + Range(0f, entry.VolumeRandom);
            voice.PitchSt = Range(-spec.PitchRandom * 0.5f, spec.PitchRandom * 0.5f) +
                            Range(-entry.PitchRandom * 0.5f, entry.PitchRandom * 0.5f);
            start += entry.Delay;
            double ratio = Ratio(spec.Pitch + entry.Pitch + voice.PitchSt + TimelinePitch(spec, start - emitter.Begin));
            voice.Start = start;
            voice.End = loop ? double.PositiveInfinity : start + Length(entry, ratio);
            var source = voice.Source;
            source.Stop();
            source.clip = entry.Clip;
            source.loop = loop;
            source.pitch = (float)ratio;
            source.volume = 0f;
            source.PlayScheduled(start);
            if (voice == emitter.Voices[0] && emitter.WetVoice != null)
            {
                // The convolution's wet loop: rendered from the same dry loop,
                // started on the same DSP tick, at the same pitch.
                var wet = emitter.WetVoice;
                wet.Entry = entry;
                wet.Cut = voice.Cut;
                wet.PitchSt = voice.PitchSt;
                wet.Start = voice.Start;
                wet.End = voice.End;
                wet.Source.Stop();
                wet.Source.clip = spec.WetClip;
                wet.Source.loop = loop;
                wet.Source.pitch = (float)ratio;
                wet.Source.volume = 0f;
                wet.Source.PlayScheduled(start);
            }
            emitter.Spawned++;
            return voice.End;
        }

        private static float TimelinePitch(EventSpec spec, double t)
        {
            if (spec.TimelinePitch == null || spec.TimelineLength <= 0f)
                return 0f;
            return spec.TimelinePitch.Evaluate((float)Repeat(t, spec.TimelineLength));
        }

        private static double Repeat(double t, double length) => t - Math.Floor(t / length) * length;

        private void Schedule(Emitter emitter, double now)
        {
            var spec = emitter.Spec;
            bool more = spec.Total == 0 || emitter.Spawned < spec.Total;
            switch (spec.Type)
            {
                case InstrumentType.Multi:
                    // Two sources hand over gaplessly: once the pending one has
                    // begun, the other has ended and takes the entry after it.
                    if (emitter.Pending >= 0 && now >= emitter.PendingStart)
                    {
                        if (!(spec.Looping || more))
                        {
                            emitter.Pending = -1;
                            break;
                        }
                        int other = 1 - emitter.Pending;
                        double start = emitter.PendingEnd;
                        emitter.PendingEnd = Play(emitter, emitter.Voices[other], NextInPlaylist(emitter), start, false);
                        emitter.PendingStart = start;
                        emitter.Pending = other;
                    }
                    break;
                case InstrumentType.Scatterer:
                    while (more && now + ScheduleAhead >= emitter.NextSpawn)
                    {
                        double start = Math.Max(emitter.NextSpawn, now);
                        Play(emitter, FreeVoice(emitter, start), Random.Range(0, spec.Entries.Length), start, false);
                        emitter.NextSpawn += Range(spec.IntervalMin, spec.IntervalMax);
                        more = spec.Total == 0 || emitter.Spawned < spec.Total;
                    }
                    break;
            }
        }

        /// <summary>A source that is silent by <paramref name="start"/>; past the polyphony the oldest is cut.</summary>
        private static Voice FreeVoice(Emitter emitter, double start)
        {
            Voice oldest = emitter.Voices[0];
            foreach (var voice in emitter.Voices)
            {
                if (!voice.Busy(start))
                    return voice;
                if (voice.Start < oldest.Start)
                    oldest = voice;
            }
            return oldest;
        }

        private void LateUpdate()
        {
            if (_emitters.Count == 0)
                return;
            double now = AudioSettings.dspTime;
            var rig = AudioListenerRig.Active;
            var service = BlitzBootstrap.Current != null ? BlitzBootstrap.Current.Audio : null;
            Vector3 listener = rig != null ? rig.transform.position : Vector3.zero;
            Quaternion facing = rig != null ? rig.transform.rotation : Quaternion.identity;
            foreach (var emitter in _emitters)
            {
                if (!emitter.Started)
                    continue;
                Schedule(emitter, now);
                Mix(emitter, now, listener, facing, service);
            }
        }

        /// <summary>Per-frame automation of one emitter against the listener.</summary>
        private static void Mix(Emitter emitter, double now, Vector3 listener, Quaternion facing, AudioService service)
        {
            var spec = emitter.Spec;
            Vector3 offset = emitter.Node.position - listener;
            float d = offset.magnitude;
            float azimuth = Azimuth(offset, facing);
            double t = now - emitter.Begin;
            float timelineDb = 0f;
            if (spec.TimelineVolume != null)
                timelineDb = spec.TimelineVolume.Evaluate((float)Repeat(t, spec.TimelineLength));
            float distanceDb = spec.DistanceVolume != null ? spec.DistanceVolume.Evaluate(d) : 0f;
            float baseDb = spec.GainDb + timelineDb + distanceDb;
            float fade = spec.FadeIn > 0f ? Mathf.Clamp01((float)((now - emitter.First) / spec.FadeIn)) : 1f;
            float lowpass = LowpassCutoff(spec, d, azimuth);
            float highpass = HighpassCutoff(spec, d);
            float spread = spec.Spread != null ? Mathf.Clamp(spec.Spread.Evaluate(d), 0f, 360f) : 0f;
            float mix = ReverbZoneMix(spec, d, distanceDb);
            float pitchSt = spec.Pitch + TimelinePitch(spec, t);

            foreach (var voice in emitter.Voices)
                MixVoice(voice, spec, baseDb, fade, lowpass, highpass, spread, mix, pitchSt, d, now, service);
            if (emitter.WetVoice != null)
                MixVoice(emitter.WetVoice, spec, baseDb, fade, lowpass, highpass, spread, mix, pitchSt, d, now, service);
        }

        private static void MixVoice(Voice voice, EventSpec spec, float baseDb, float fade, float lowpass, float highpass,
                                     float spread, float mix, float pitchSt, float d, double now, AudioService service)
        {
            var entry = voice.Entry;
            if (entry == null || voice.Source == null)
                return;
            float db = baseDb + entry.Volume - voice.Cut;
            if (voice.Wet)
                db += Level(spec.WetDb, spec.WetCurve, d) - spec.WetBakeDb;
            else
                db += Level(spec.DryDb, spec.DryCurve, d) - entry.BakeDb;
            float linear = Mathf.Pow(10f, db / 20f) * fade;
            float volume = service != null ? service.SpatialVolume(AudioBus.Sfx, linear) : linear;
            voice.Source.volume = Mathf.Clamp01(volume);
            if (spec.TimelinePitch != null && voice.Busy(now))
                voice.Source.pitch = (float)Ratio(pitchSt + entry.Pitch + voice.PitchSt);
            voice.Source.spread = spread;
            voice.Source.reverbZoneMix = mix;
            if (voice.Low != null)
                voice.Low.cutoffFrequency = lowpass;
            if (voice.High != null)
                voice.High.cutoffFrequency = highpass;
        }

        private static float Level(float constant, Curve curve, float d)
        {
            if (curve != null) return curve.Evaluate(d);
            return float.IsNaN(constant) ? 0f : constant;
        }

        /// <summary>
        /// Wwise's built-in azimuth: the angle on the listener's horizontal
        /// plane from its front to the emitter, positive to the right.
        /// </summary>
        public static float Azimuth(Vector3 offset, Quaternion facing)
        {
            Vector3 forward = facing * Vector3.forward, right = facing * Vector3.right;
            float x = Vector3.Dot(offset, right), z = Vector3.Dot(offset, forward);
            if (x * x + z * z < 1e-8f)
                return 0f;
            return Mathf.Atan2(x, z) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// The cut-off of first-order low-pass stages in a row: Wwise adds the
        /// filter amounts of distance, azimuth and the event before it
        /// filters, the curves give each stage's -3 dB point, and the chain
        /// is -3 dB where (1 + f^2 / a^2)(1 + f^2 / b^2) = 2.
        /// </summary>
        public static float CascadeLowpass(float a, float b)
        {
            if (a <= 0f) return b;
            if (b <= 0f) return a;
            double ia = 1.0 / ((double)a * a), ib = 1.0 / ((double)b * b);
            double s = ia + ib;
            double f2 = (-s + Math.Sqrt(s * s + 4.0 * ia * ib)) / (2.0 * ia * ib);
            return (float)Math.Sqrt(f2);
        }

        /// <summary>High-pass stages in a row: -3 dB where (1 + a^2 / f^2)(1 + b^2 / f^2) = 2.</summary>
        public static float CascadeHighpass(float a, float b)
        {
            if (a <= 0f) return b;
            if (b <= 0f) return a;
            double a2 = (double)a * a, b2 = (double)b * b;
            double s = a2 + b2;
            double f2 = (s + Math.Sqrt(s * s + 4.0 * a2 * b2)) / 2.0;
            return (float)Math.Sqrt(f2);
        }

        public static float LowpassCutoff(EventSpec spec, float d, float azimuth)
        {
            float f = 0f;
            if (spec.DistanceLowpass != null) f = CascadeLowpass(f, spec.DistanceLowpass.Evaluate(d));
            if (spec.DirectionLowpass != null) f = CascadeLowpass(f, spec.DirectionLowpass.Evaluate(azimuth));
            if (spec.Lowpass > 0f) f = CascadeLowpass(f, spec.Lowpass);
            return f > 0f ? Mathf.Clamp(f, 10f, 22000f) : 22000f;
        }

        public static float HighpassCutoff(EventSpec spec, float d)
        {
            float f = 0f;
            if (spec.DistanceHighpass != null) f = CascadeHighpass(f, spec.DistanceHighpass.Evaluate(d));
            if (spec.Highpass > 0f) f = CascadeHighpass(f, spec.Highpass);
            return f > 0f ? Mathf.Clamp(f, 10f, 22000f) : 10f;
        }

        /// <summary>
        /// The aux send as Unity's reverb zone mix. Unity sends the source's
        /// output, after the volume that already carries the dry distance
        /// attenuation, so the send level is divided by that attenuation;
        /// above unity gain Unity's scale runs 1 + dB / 100 up to +10 dB.
        /// </summary>
        public static float ReverbZoneMix(EventSpec spec, float d, float distanceDb)
        {
            if (!spec.Reverb)
                return 0f;
            float sendDb = spec.ReverbCurve != null ? spec.ReverbCurve.Evaluate(d) : spec.ReverbLevel;
            float db = sendDb - distanceDb;
            if (db <= -80f)
                return 0f;
            if (db > 0f)
                return Mathf.Min(MaxReverbZoneMix, 1f + db / 100f);
            return Mathf.Pow(10f, db / 20f);
        }

        // -------------------------------------------------------- release
        /// <summary>Stops every voice, stops listening and unloads the clips.</summary>
        public void Release()
        {
            if (_released)
                return;
            _released = true;
            StopListening();
            foreach (var emitter in _emitters)
            {
                foreach (var voice in emitter.Voices)
                    if (voice.Source != null)
                    {
                        voice.Source.Stop();
                        voice.Source.clip = null;
                    }
                if (emitter.WetVoice != null && emitter.WetVoice.Source != null)
                {
                    emitter.WetVoice.Source.Stop();
                    emitter.WetVoice.Source.clip = null;
                }
            }
            _emitters.Clear();
            foreach (var clip in _clips)
                if (clip != null)
                    Resources.UnloadAsset(clip);
            _clips.Clear();
        }

        private void OnDestroy()
        {
            Release();
        }
    }
}
