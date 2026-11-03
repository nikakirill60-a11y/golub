using System.Collections.Generic;
using System.Reflection;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Audio;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    public sealed class SpatialVoiceTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        private readonly List<GameObject> _objects = new List<GameObject>();
        private BattleSession _session;
        private BattleTank _tank;
        private BattleCamera _view;

        [TearDown]
        public void Cleanup()
        {
            if (_view != null)
            {
                Object.DestroyImmediate(_view.Sniper.transform.parent.gameObject);
                Object.DestroyImmediate(_view.Arty.transform.parent.gameObject);
                Object.DestroyImmediate(_view.gameObject);
            }

            _view = null;
            if (_tank != null) { Object.DestroyImmediate(_tank.gameObject); }
            _tank = null;
            if (_session != null) { Object.DestroyImmediate(_session.gameObject); }
            _session = null;
            foreach (var go in _objects) { if (go != null) { Object.DestroyImmediate(go); } }
            _objects.Clear();
        }

        [Test]
        public void RolloffCurveFollowsTheDistanceLawAndIsSharedPerRange()
        {
            var ranges = new[]
            {
                SpatialAudio.TankBeds, SpatialAudio.GunNear, SpatialAudio.GunFar,
                SpatialAudio.ExplosionNear, SpatialAudio.ExplosionFar, SpatialAudio.Collision,
                SpatialAudio.OneShot, SpatialAudio.AtgmFlight, SpatialAudio.UprisingCue,
                SpatialAudio.Cue(400f), SpatialAudio.Cue(2500f),
            };
            foreach (var range in ranges)
            {
                var curve = SpatialVoice.RolloffCurve(range);
                Assert.AreSame(curve, SpatialVoice.RolloffCurve(new SpatialRange(range.Reference, range.Max)));
                for (var i = 0; i <= 300; i++)
                {
                    var distance = range.Max * i / 300f;
                    Assert.AreEqual(SpatialAudio.DistanceGain(distance, range),
                        curve.Evaluate(distance / range.Max), 0.02f, range + " at " + distance);
                }

                Assert.AreEqual(0f, curve.Evaluate(1.5f), 1e-4f, range + " past the cut");
            }
        }

        [Test]
        public void ConfigureSpatialMakesOneFull3DVoicePerSource()
        {
            var go = Make("voice");
            var source = go.AddComponent<AudioSource>();
            source.dopplerLevel = 1f;
            var voice = AudioService.ConfigureSpatial(source, SpatialAudio.Collision);
            Assert.AreEqual(1f, source.spatialBlend);
            Assert.AreEqual(0f, source.dopplerLevel);
            Assert.AreEqual(AudioRolloffMode.Custom, source.rolloffMode);
            Assert.AreEqual(SpatialAudio.Collision.Reference, source.minDistance);
            Assert.AreEqual(SpatialAudio.Collision.Max, source.maxDistance);
            Assert.AreEqual(SpatialAudio.Collision, voice.Range);
            var curve = source.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
            Assert.AreEqual(SpatialAudio.DistanceGain(30f, SpatialAudio.Collision),
                curve.Evaluate(30f / SpatialAudio.Collision.Max), 0.02f);

            // A reference past the current cut and back again: min never ends up above max.
            Assert.AreSame(voice, AudioService.ConfigureSpatial(source, SpatialAudio.ExplosionFar));
            Assert.AreEqual(SpatialAudio.ExplosionFar.Reference, source.minDistance);
            Assert.AreEqual(SpatialAudio.ExplosionFar.Max, source.maxDistance);
            voice.SetRange(SpatialAudio.Collision);
            Assert.AreEqual(SpatialAudio.Collision.Reference, source.minDistance);
            Assert.AreEqual(SpatialAudio.Collision.Max, source.maxDistance);
            Assert.AreEqual(1, go.GetComponents<SpatialVoice>().Length);

            voice.SetRange(default(SpatialRange));
            Assert.AreEqual(SpatialAudio.OneShot, voice.Range, "an unauthored range falls back to the one-shot law");
        }

        [Test]
        public void StaticWorldBetweenListenerAndVoiceMufflesItSmoothly()
        {
            var listener = new Vector3(0f, 2f, -20f);
            var go = Make("voice", new Vector3(0f, 2f - SpatialVoice.SourceLift, 20f));
            var heard = go.transform.position + Vector3.up * SpatialVoice.SourceLift;
            var voice = AudioService.ConfigureSpatial(go.AddComponent<AudioSource>(), SpatialAudio.GunNear);

            voice.Refresh(listener, true, 0f, 0.02f);
            Assert.AreEqual(0f, voice.Occlusion);
            Assert.AreEqual(1f, voice.Gain);
            Assert.AreEqual(SpatialAudio.OpenCutoffHz, voice.Cutoff, "40 m is inside the reference");

            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _objects.Add(wall);
            wall.layer = BattleLayers.Static;
            wall.transform.position = new Vector3(0f, 2f, 0f);
            wall.transform.localScale = new Vector3(10f, 10f, 6f);
            Physics.SyncTransforms();
            var target = SpatialAudio.Occlusion(true, 6f);
            Assert.AreEqual(target, SpatialVoice.Probe(listener, heard), 0.01f, "six metres of house");
            Assert.AreEqual(target, SpatialVoice.Probe(heard, listener), 0.01f, "either way round");

            voice.Refresh(listener, true, 0.05f, 0.02f);
            Assert.AreEqual(0f, voice.Occlusion, "the next probe is not due yet");
            voice.Refresh(listener, true, 0.12f, 0.02f);
            Assert.Greater(voice.Occlusion, 0f);
            Assert.Less(voice.Occlusion, target * 0.5f, "a voice sliding behind cover glides");
            var now = 0.12f;
            for (var i = 0; i < 60; i++)
            {
                now += 0.02f;
                voice.Refresh(listener, true, now, 0.02f);
            }

            Assert.AreEqual(target, voice.Occlusion, 0.01f);
            Assert.AreEqual(SpatialAudio.OcclusionGain(target), voice.Gain, 0.01f);
            Assert.AreEqual(SpatialAudio.OcclusionCutoff(target), voice.Cutoff, 50f);
            Assert.Less(voice.Gain, 0.6f);
            Assert.Less(voice.Cutoff, 3000f);

            voice.Refresh(listener, false, now + 0.02f, 0.02f);
            Assert.AreEqual(0f, voice.Occlusion, "a silent voice costs no probe");
            Assert.AreEqual(1f, voice.Gain);
            Assert.AreEqual(SpatialAudio.OpenCutoffHz, voice.Cutoff);
            voice.Refresh(listener, true, now + 0.04f, 0.02f);
            Assert.AreEqual(target, voice.Occlusion, 0.01f, "a voice that starts behind cover starts muffled");

            var beyond = new Vector3(0f, 2f, heard.z - SpatialAudio.GunNear.Max - 10f);
            voice.Refresh(beyond, true, now + 1f, 0.02f);
            Assert.AreEqual(0f, voice.Occlusion, "out of range");
            voice.Refresh(null, true, now + 2f, 0.02f);
            Assert.AreEqual(1f, voice.Gain, "no listener");

            wall.layer = 0;
            Physics.SyncTransforms();
            Assert.AreEqual(0f, SpatialVoice.Probe(listener, heard), "tanks and props do not occlude");
            var far = new Vector3(0f, 2f, heard.z - 300f);
            voice.Refresh(far, true, now + 3f, 0.02f);
            Assert.AreEqual(0f, voice.Occlusion);
            Assert.AreEqual(1f, voice.Gain, "air only darkens; the rolloff curve does the level");
            Assert.AreEqual(SpatialAudio.AirCutoff(300f, SpatialAudio.GunNear), voice.Cutoff, 1f);
        }

        [Test]
        public void ListenerRestsOnTheAudioRootUntilABattleLendsItsView()
        {
            var audio = Audio();
            var rig = audio.Listener;
            Assert.NotNull(rig);
            Assert.AreSame(rig, AudioListenerRig.Active);
            Assert.AreSame(audio.transform, rig.transform.parent);
            Assert.AreSame(rig.Listener, rig.GetComponent<AudioListener>());
            Assert.IsNull(audio.GetComponent<AudioListener>(), "the root carries the 2D voices and stays put");
            Assert.AreEqual(1, audio.GetComponentsInChildren<AudioListener>(true).Length);

            var eye = new Vector3(30f, 4f, -12f);
            var turn = Quaternion.Euler(10f, 90f, 0f);
            AudioListenerRig.PoseSource view = (out Vector3 p, out Quaternion r) =>
            {
                p = eye;
                r = turn;
                return true;
            };
            audio.ListenFrom(view);
            Near(eye, audio.ListenerPosition);
            Assert.Less(Quaternion.Angle(turn, rig.transform.rotation), 0.01f);
            Near(Vector3.zero, audio.transform.position, "the root does not move with the view");

            eye = new Vector3(-5f, 2f, 40f);
            rig.Follow();
            Near(eye, audio.ListenerPosition, "follows the view every frame");

            AudioListenerRig.PoseSource gone = (out Vector3 p, out Quaternion r) =>
            {
                p = Vector3.one * 99f;
                r = Quaternion.identity;
                return false;
            };
            audio.StopListeningFrom(gone);
            Near(eye, audio.ListenerPosition, "a stale battle cannot take the listener back");
            audio.ListenFrom(gone);
            Near(audio.transform.position, audio.ListenerPosition, "no view: rests on the root");
            audio.ListenFrom(view);
            audio.StopListeningFrom(view);
            Assert.IsNull(rig.Source);
            Near(audio.transform.position, audio.ListenerPosition);

            Object.DestroyImmediate(audio.gameObject);
            Assert.IsNull(AudioListenerRig.Active);
        }

        [Test]
        public void BattleIsHeardFromTheActiveCameraAndArtyFromAboveTheHull()
        {
            var db = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            _tank = BattleTank.Spawn(null, "listener pose", db.Get("g14_kpfpz_70r"), null, new Bounds(), 0);
            _tank.GetComponent<Rigidbody>().isKinematic = true;
            _tank.transform.position = new Vector3(40f, 3f, -25f);
            _view = BattleCamera.Create(_tank, false);
            _tank.View = _view;
            var audio = Audio();
            audio.ListenFrom(_view.ListenerPose);

            var eye = _view.Active.transform;
            Assert.True(_view.ListenerPose(out var position, out var rotation));
            Near(eye.position, position);
            Assert.Less(Quaternion.Angle(eye.rotation, rotation), 0.01f);
            Near(eye.position, audio.ListenerPosition);

            _tank.SelectShell(1);
            _view.SetArtyMode(true);
            Assert.True(_view.ArtyMode);
            Assert.True(_view.ListenerPose(out position, out rotation));
            Near(_tank.transform.position + Vector3.up * BattleCamera.ArtyListenerHeight, position,
                "the overhead view hears from the hull, not from the sky");
            var facing = rotation * Vector3.forward;
            Assert.AreEqual(0f, facing.y, 1e-4f, "level");
            var heading = Vector3.ProjectOnPlane(_view.Active.transform.forward, Vector3.up);
            if (heading.sqrMagnitude > 1e-6f) { Assert.Less(Vector3.Angle(heading, facing), 0.5f); }
            audio.Listener.Follow();
            Near(position, audio.ListenerPosition);

            var torn = _view;
            _view = null;
            Object.DestroyImmediate(torn.Sniper.transform.parent.gameObject);
            Object.DestroyImmediate(torn.Arty.transform.parent.gameObject);
            Object.DestroyImmediate(torn.gameObject);
            Assert.False(torn.ListenerPose(out position, out rotation));
            audio.Listener.Follow();
            Near(audio.transform.position, audio.ListenerPosition, "a torn-down view hands the listener back");
        }

        [Test]
        public void RemoteTanksAreHeardOverTheirRangesAndThePlayersOwnRigStays2D()
        {
            _session = BattleSession.Create(null, false, tankId: "t-34", autoStart: true);
            var audio = Audio();
            var ally = _session.Allies.Find(t => t != _session.Player);
            var remote = TankSoundManager.Attach(ally, audio, false);
            TankSoundManager.Attach(_session.Player, audio, true);

            var expected = new Dictionary<string, SpatialRange>
            {
                { "engine", SpatialAudio.TankBeds },
                { "tracks", SpatialAudio.TankBeds },
                { "roughness", SpatialAudio.TankBeds },
                { "roughness_inside", SpatialAudio.TankBeds },
                { "gun", SpatialAudio.GunNear },
                { "gun_inside", SpatialAudio.GunNear },
                { "collision", SpatialAudio.Collision },
                { "explosion", SpatialAudio.ExplosionNear },
            };
            var sounds = ally.transform.Find("Sounds");
            foreach (var pair in expected)
            {
                var voice = sounds.Find(pair.Key).GetComponent<SpatialVoice>();
                Assert.NotNull(voice, pair.Key);
                Assert.AreEqual(pair.Value, voice.Range, pair.Key);
                Assert.AreEqual(1f, voice.GetComponent<AudioSource>().spatialBlend, pair.Key);
            }

            var own = _session.Player.transform.Find("Sounds").GetComponentsInChildren<AudioSource>(true);
            Assert.IsNotEmpty(own);
            foreach (var source in own)
            {
                Assert.AreEqual(0f, source.spatialBlend, source.name);
                Assert.IsNull(source.GetComponent<SpatialVoice>(), source.name);
            }

            var point = ally.GunShootPoint();
            var gun = sounds.Find("gun").GetComponent<SpatialVoice>();
            var fired = typeof(TankSoundManager).GetMethod("OnProjectileFired", Hidden);
            audio.ListenFrom(At(point + Vector3.forward * 600f));
            fired.Invoke(remote, new object[] { true, 0 });
            Assert.AreEqual(SpatialAudio.GunFar, gun.Range, "the distant report carries further");
            audio.ListenFrom(At(point + Vector3.forward * 20f));
            fired.Invoke(remote, new object[] { true, 0 });
            Assert.AreEqual(SpatialAudio.GunNear, gun.Range, "and the near one is back up close");

            var blast = sounds.Find("explosion").GetComponent<SpatialVoice>();
            var explode = typeof(TankSoundManager).GetMethod("PlayExplosion", Hidden);
            audio.ListenFrom(At(point + Vector3.forward * 600f));
            explode.Invoke(remote, null);
            Assert.AreEqual(SpatialAudio.ExplosionFar, blast.Range);
            audio.ListenFrom(At(point + Vector3.forward * 20f));
            explode.Invoke(remote, null);
            Assert.AreEqual(SpatialAudio.ExplosionNear, blast.Range);
        }

        [Test]
        public void BigBossCuesOfOtherTanksFollowTheirEventReachAndOwnCuesStay2D()
        {
            _session = BattleSession.Create(null, false, tankId: "t-34", autoStart: true, mode: BattleMode.BigBoss);
            var configure = typeof(BigBossAudio).GetMethod("Configure", Hidden);
            var enemy = _session.Enemies[0];
            var cues = enemy.GetComponent<BigBossAudio>();

            var source = Make("cue").AddComponent<AudioSource>();
            configure.Invoke(cues, new object[] { new BigBossAudio.CueTimeline.Voice { MaxDistance = 400f, Source = source } });
            var voice = source.GetComponent<SpatialVoice>();
            Assert.NotNull(voice);
            Assert.AreEqual(SpatialAudio.Cue(400f), voice.Range);
            Assert.AreEqual(AudioRolloffMode.Custom, source.rolloffMode);
            Assert.AreEqual(1f, source.spatialBlend);
            Near(enemy.transform.position, source.transform.position);

            var anchored = Make("anchored").AddComponent<AudioSource>();
            var point = enemy.transform.position + new Vector3(30f, 0f, 5f);
            configure.Invoke(cues, new object[]
            {
                new BigBossAudio.CueTimeline.Voice { MaxDistance = 0f, Anchored = true, Point = point, Source = anchored },
            });
            Assert.AreEqual(SpatialAudio.Cue(0f), anchored.GetComponent<SpatialVoice>().Range, "an event without reach still fades");
            Near(point, anchored.transform.position);

            var mine = Make("own").AddComponent<AudioSource>();
            configure.Invoke(_session.Player.GetComponent<BigBossAudio>(), new object[]
            {
                new BigBossAudio.CueTimeline.Voice { MaxDistance = 400f, Source = mine },
            });
            Assert.AreEqual(0f, mine.spatialBlend);
            Assert.IsNull(mine.GetComponent<SpatialVoice>());
        }

        [Test]
        public void RocketFlightBedRidesTheRocketOverItsShortRange()
        {
            var audio = Audio();
            var rocket = Make("rocket", new Vector3(5f, 2f, 60f));
            var pool = audio.Sounds.Sound(TankSoundTable.ShellFlightSet);
            Assert.NotNull(pool);
            Assert.True(pool.HasStreams, "the client ships the tracer bed");

            var flight = audio.PlayLoop3D(pool, rocket.transform, 1f, SpatialAudio.AtgmFlight);
            Assert.NotNull(flight);
            Assert.AreSame(rocket.transform, flight.transform.parent);
            Assert.True(flight.loop);
            Assert.AreEqual(SpatialAudio.AtgmFlight, flight.GetComponent<SpatialVoice>().Range);
            Assert.AreEqual(SpatialAudio.AtgmFlight.Max, flight.maxDistance);

            var fallback = audio.PlayLoop3D(pool, rocket.transform);
            Assert.AreEqual(SpatialAudio.OneShot, fallback.GetComponent<SpatialVoice>().Range);
        }

        private GameObject Make(string name, Vector3 position = default(Vector3))
        {
            var go = new GameObject(name);
            go.transform.position = position;
            _objects.Add(go);
            return go;
        }

        private AudioService Audio()
        {
            return AudioService.Create(Make("AudioRoot").transform, new SettingsData());
        }

        private static AudioListenerRig.PoseSource At(Vector3 position)
        {
            return (out Vector3 p, out Quaternion r) =>
            {
                p = position;
                r = Quaternion.identity;
                return true;
            };
        }

        private static void Near(Vector3 expected, Vector3 actual, string message = null)
        {
            Assert.Less(Vector3.Distance(expected, actual), 1e-3f, (message ?? string.Empty) + " " + expected + " vs " + actual);
        }
    }
}
