using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using UnityEngine;

namespace BlitzLegacy.Runtime.Audio
{
    /// <summary>
    /// Port of TankSoundsManager (assets/tanks/base/tank_sound_manager.gd):
    /// the per-tank spatial rig of engine, track, gun, hull, collision and
    /// destruction beds, plus the 2D cues the source plays from the same
    /// signals.
    ///
    /// The source keeps one AudioStreamPlayer3D per bed and swaps its stream
    /// instead of spawning voices, so a looping engine or track bed keeps its
    /// phase while the picked sample follows load and speed.
    /// <see cref="RandomStreamSource"/> is that player: it holds the pool it
    /// is currently playing, throttles changes and keeps a stable gain that
    /// spotting and sniper mode can mute without compounding.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TankSoundManager : MonoBehaviour
    {
        /// <summary>Port of the inner class RandomStreamPlayer3D.</summary>
        public sealed class RandomStreamSource
        {
            public AudioSource Source;
            public AudioBus Bus = AudioBus.Sfx;
            public float VolumeMul = 1f;
            public float VolumeMulInit = 1f;
            public float PlayFrom;
            public float StreamChangeDelay = TankSoundTuning.StreamChangeDelay;
            public SoundPool Sound;
            public float LastChange;
            public bool Suppressed;
            public int PlayCount { get; private set; }

            private AudioService _audio;
            private float _requestedVolume = 1f;

            public RandomStreamSource(AudioService audio)
            {
                _audio = audio;
            }

            /// <summary>
            /// mute(): only the stable gain is written, never the product, so
            /// repeated spotting or sniper toggles cannot silence the player.
            /// </summary>
            public void Mute(bool muted)
            {
                if (Sound == null) { return; }

                VolumeMul = muted ? 0f : VolumeMulInit;
                ApplyVolume();
            }

            public void ApplyVolume()
            {
                var pool = Sound != null ? Sound.Volume : 1f;
                _requestedVolume = VolumeMul * pool;
                RefreshBusVolume();
            }

            public void SetVolumeLinear(float volume)
            {
                _requestedVolume = volume;
                RefreshBusVolume();
            }
            public void RefreshBusVolume()
            {
                if (Source == null) return;
                Source.volume = _audio != null ? _audio.SpatialVolume(Bus, _requestedVolume) : _requestedVolume;
            }

            /// <summary>Port of change_to_stream(...).</summary>
            public bool ChangeToStream(
                SoundPool newSound,
                bool fullDelay = false,
                bool ignoreDelay = false,
                bool ignoreRepeat = false,
                bool randomizePitch = false,
                bool preservePosition = true)
            {
                if (Suppressed) { return false; }
                if (!ignoreRepeat && Sound == newSound) { return false; }
                if (!ignoreDelay && Time.time < LastChange) { return false; }
                if (newSound == null || !newSound.HasStreams) { return false; }

                var name = newSound.PickRandom(RandomBelow);
                var clip = BlitzResources.LoadAudio(name);
                if (clip == null || Source == null) { return false; }

                if (randomizePitch)
                {
                    Source.pitch = 1f + Random.Range(-1f, 1f)
                        * TankSoundTuning.PitchRandomness;
                }

                // An empty source has no playback position; asking for one
                // logs an engine warning on every shot.
                var position = Source.clip != null ? Source.time : 0f;

                Sound = newSound;
                ApplyVolume();
                Source.clip = clip;
                LastChange = Time.time + (fullDelay
                    ? clip.length * TankSoundTuning.FullDelayShare
                    : StreamChangeDelay);
                Source.time = Mathf.Min(PlayFrom, Mathf.Max(clip.length - 0.01f, 0f));
                Source.Play();
                PlayCount++;

                // Engine and track loops retain phase; a repeated gunshot has
                // to restart its attack, so the caller opts out.
                if (preservePosition && clip.length > 0f)
                {
                    Source.time = Mathf.Min(position, clip.length - 0.01f);
                }

                return true;
            }
        }

        /// <summary>StreamRandom.pick_random()'s index draw.</summary>
        private static int RandomBelow(int count)
        {
            return Random.Range(0, count);
        }

        private BattleTank _tank;
        private AudioService _audio;
        private Transform _root;
        private bool _isPlayer;
        private bool _isEnemy;
        private bool _heavyHull;
        private bool _spotted;
        private bool _fakeWreck;
        private float _speed;
        private float _nextCollision;

        private ExplosionSoundSet _gunSnd;

        /// <summary>
        /// gun_effects.xml shot_atgm: a guided round leaves with the launcher
        /// report, every other round of the same gun with the gun's own.
        /// </summary>
        private ExplosionSoundSet _atgmSnd;
        private EngineSoundSet _engineSnd;
        private TrackSoundSet _trackSnd;

        private RandomStreamSource _enginePlayer;
        private RandomStreamSource _tracksPlayer;
        private RandomStreamSource _gunPlayer;
        private RandomStreamSource _gunPlayerInside;
        private RandomStreamSource _roughnessPlayer;
        private RandomStreamSource _roughnessInsidePlayer;
        private RandomStreamSource _collisionPlayer;
        private RandomStreamSource _explosionPlayer;
        private RandomStreamSource[] _players;

        public RandomStreamSource EnginePlayer { get { return _enginePlayer; } }

        public RandomStreamSource TracksPlayer { get { return _tracksPlayer; } }

        public RandomStreamSource GunPlayer { get { return _gunPlayer; } }

        public RandomStreamSource GunPlayerInside { get { return _gunPlayerInside; } }

        public RandomStreamSource CollisionPlayer { get { return _collisionPlayer; } }

        public RandomStreamSource ExplosionPlayer { get { return _explosionPlayer; } }

        /// <summary>
        /// The source frees the manager on hangar tanks, so the battle spawn
        /// path is the only caller.
        /// </summary>
        public static TankSoundManager Attach(
            BattleTank tank, AudioService audio, bool isPlayer)
        {
            if (tank == null || audio == null)
            {
                return null;
            }

            var existing = tank.GetComponent<TankSoundManager>();
            if (existing != null) return existing;
            var manager = tank.gameObject.AddComponent<TankSoundManager>();
            manager.Setup(tank, audio, isPlayer);
            return manager;
        }

        /// <summary>Port of setup(), called once the tank is built.</summary>
        private void Setup(BattleTank tank, AudioService audio, bool isPlayer)
        {
            _tank = tank;
            _audio = audio;
            _isPlayer = isPlayer;
            _isEnemy = !isPlayer && tank.Team != 0;
            _spotted = tank.Spotted;

            var host = new GameObject("Sounds");
            host.transform.SetParent(tank.transform, false);
            _root = host.transform;

            var table = audio.Sounds;
            var card = tank.Card;
            _gunSnd = table.Gun((ShootSound)(tank.GunInfo != null
                ? tank.GunInfo.ShotSound : 0));
            _atgmSnd = table.Gun(ShootSound.ShotAtgm);
            _engineSnd = table.Engine((EngineSound)(card != null
                ? card.EngineSound : 0));
            _trackSnd = table.Tracks((TrackSound)(card != null
                ? card.TrackSound : 0));
            // hull_weight is read into a bool in the source: any hull with a
            // weight at all uses the heavy impact set.
            _heavyHull = card != null && card.HullWeight != 0f;

            _enginePlayer = CreatePlayer("engine", SpatialAudio.TankBeds);
            _enginePlayer.Sound = _engineSnd.Idle;
            _enginePlayer.VolumeMul *= _isPlayer
                ? TankSoundTuning.PlayerEngineGain
                : TankSoundTuning.RemoteEngineGain;
            _enginePlayer.VolumeMulInit = _enginePlayer.VolumeMul;

            var idle = BlitzResources.LoadAudio(
                _engineSnd.Idle.PickRandom(RandomBelow));
            if (idle != null)
            {
                _enginePlayer.Source.clip = idle;
                _enginePlayer.Source.loop = true;
                _enginePlayer.ApplyVolume();
                // Starting every tank at frame zero sums correlated attacks
                // into one clipped chirp, so the bed gets a random phase.
                var phaseMax = Mathf.Max(idle.length - 0.05f, 0f);
                _enginePlayer.Source.time = phaseMax > 0f
                    ? Random.Range(0f, phaseMax) : 0f;
                _enginePlayer.Source.Play();
            }

            _tracksPlayer = CreatePlayer("tracks", SpatialAudio.TankBeds);
            _tracksPlayer.Sound = _trackSnd.Stop;
            _tracksPlayer.VolumeMul *= _isPlayer
                ? TankSoundTuning.PlayerTrackGain
                : TankSoundTuning.RemoteTrackGain;
            _tracksPlayer.VolumeMulInit = _tracksPlayer.VolumeMul;
            _tracksPlayer.Source.loop = true;
            _tracksPlayer.ApplyVolume();
            // stop is a transient, not an idle loop: movement switches to a
            // start sample or a speed band instead of chirping on spawn.

            _gunPlayer = CreatePlayer("gun", SpatialAudio.GunNear);
            _gunPlayer.VolumeMul = TankSoundTuning.GunVolumeMul;
            _gunPlayer.VolumeMulInit = TankSoundTuning.GunVolumeMul;

            _roughnessPlayer = CreatePlayer("roughness", SpatialAudio.TankBeds);
            _roughnessPlayer.VolumeMul = 1f;
            _roughnessPlayer.VolumeMulInit = 1f;
            _roughnessPlayer.Bus = AudioBus.Sniper;

            _roughnessInsidePlayer = CreatePlayer("roughness_inside", SpatialAudio.TankBeds);
            _roughnessInsidePlayer.VolumeMul = 1f;
            _roughnessInsidePlayer.VolumeMulInit = 1f;
            _roughnessInsidePlayer.Bus = AudioBus.Sniper;

            _collisionPlayer = CreatePlayer("collision", SpatialAudio.Collision);
            _collisionPlayer.VolumeMul = TankSoundTuning.CollisionVolumeMul;
            _collisionPlayer.VolumeMulInit = 1f;

            _gunPlayerInside = CreatePlayer("gun_inside", SpatialAudio.GunNear);
            _gunPlayerInside.Bus = AudioBus.Sniper;
            _gunPlayerInside.VolumeMul = TankSoundTuning.GunInsideVolumeMul;
            _gunPlayerInside.VolumeMulInit = TankSoundTuning.GunInsideVolumeMul;

            _explosionPlayer = CreatePlayer("explosion", SpatialAudio.ExplosionNear);
            _explosionPlayer.VolumeMul = TankSoundTuning.ExplosionVolumeMul;
            _explosionPlayer.VolumeMulInit = TankSoundTuning.ExplosionVolumeMul;

            // Match Godot: listen after a real projectile exists, including duplet, not a trigger intent.
            tank.ProjectileFired += OnProjectileFired;
            audio.SpatialGainsChanged += RefreshBusVolumes;
            if (tank.Suspension != null)
            {
                tank.Suspension.Accel += OnAccel;
                tank.Suspension.Deccel += OnDeccel;
            }

            if (tank.Health != null)
            {
                tank.Health.OnDeath += OnDeath;
                if (_isPlayer)
                {
                    tank.Health.OnDamageInfoTaken += OnDamageInfoTaken;
                    // Online the host decides hits; its verdict arrives here.
                    tank.NetDamageFeedback += OnDamageInfoTaken;
                }
            }

            SetSpotted(tank.Spotted);
        }

        /// <summary>
        /// Port of create_player(). The local tank's players stay 2D
        /// (panning_strength 0, and the engine's 20 km unit_size never
        /// attenuates); every other tank is heard over <paramref name="range"/>.
        /// </summary>
        private RandomStreamSource CreatePlayer(string name, SpatialRange range)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root, false);

            var source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            // Only the hangar ambience sends to a reverb zone; see HalloweenHangarAudio.
            source.bypassReverbZones = true;
            if (_isPlayer)
            {
                source.spatialBlend = 0f;
                source.dopplerLevel = 0f;
            }
            else
            {
                AudioService.ConfigureSpatial(source, range);
            }

            var player = new RandomStreamSource(_audio)
            {
                Source = source,
                VolumeMul = _isPlayer
                    ? TankSoundTuning.PlayerVolumeMul
                    : TankSoundTuning.RemoteVolumeMul,
            };
            player.VolumeMulInit = player.VolumeMul;
            return player;
        }

        /// <summary>
        /// Port of the projectile_fired handler: the near or distant muzzle
        /// report, and the hull report for the local tank only.
        /// </summary>
        private void OnProjectileFired(bool firstShell, int firedShell)
        {
            if (_tank == null) { return; }
            if (!_tank.Spotted && _isEnemy) { return; }

            _gunPlayer.PlayFrom = Random.Range(0, 100)
                < TankSoundTuning.GunPlayFromChance
                ? TankSoundTuning.GunPlayFromOffset : 0f;
            _gunPlayerInside.PlayFrom = SniperMode()
                ? 0f : TankSoundTuning.GunInsidePlayFromOffset;

            var report = ReportFor(firedShell);
            if (NearCamera(TankSoundTuning.GunSoundNearDistance))
            {
                SetRange(_gunPlayer, SpatialAudio.GunNear);
                _gunPlayer.ChangeToStream(
                    report.Near, false, true, true, false, false);
            }
            else
            {
                // Per shot: the source left max_distance on the distant value for good.
                SetRange(_gunPlayer, SpatialAudio.GunFar);
                _gunPlayer.ChangeToStream(
                    report.Dist, false, true, true, false, false);
            }

            if (_isPlayer)
            {
                _gunPlayerInside.ChangeToStream(
                    report.Inside, false, true, true, false, false);
            }
        }

        /// <summary>The muzzle report of the round in rack slot <paramref name="shell"/>.</summary>
        private ExplosionSoundSet ReportFor(int shell)
        {
            var gun = _tank != null ? _tank.GunInfo : null;
            var guided = gun != null && shell >= 0 && shell < gun.Shells.Count
                && gun.Shells[shell] != null && gun.Shells[shell].Atgm;
            return guided && _atgmSnd != null ? _atgmSnd : _gunSnd;
        }

        private void OnAccel()
        {
            _enginePlayer.ChangeToStream(_engineSnd.Accel, true, true);
            _tracksPlayer.ChangeToStream(_trackSnd.Start, true);
        }

        private void OnDeccel()
        {
            _enginePlayer.ChangeToStream(_engineSnd.Deccel, true, true);
        }

        /// <summary>
        /// The player half of on_damage_info_taken: a hit that earns a marker
        /// is heard inside the hull as a bounce or a penetration.
        /// </summary>
        private void OnDamageInfoTaken(DamageInfo dinfo)
        {
            if (_tank == null || _tank.Health == null) { return; }
            if (_tank.Health.Health <= 0) { return; }
            if (dinfo == null || !dinfo.ShouldHaveMarker()) { return; }

            var pool = _audio.Sounds.Sound(dinfo.IsBlock()
                ? "inside_bounce" : "inside_pen");
            _audio.PlayPool2D(pool);
        }

        private void OnDeath()
        {
            Explode();
            SetSpotted(false, true);
        }

        /// <summary>Port of explode().</summary>
        public void Explode()
        {
            PlayExplosion();
            if (_isPlayer)
            {
                _audio.PlayCrewMessage(CrewMessage.TankDestroyed);
            }
        }

        /// <summary>The destruction blast alone, without the crew report.</summary>
        private void PlayExplosion()
        {
            if (_explosionPlayer == null || _audio == null) { return; }
            var set = _audio.Sounds.Explosion(TankSoundTable.TankExplosionSet);
            if (NearCamera(TankSoundTuning.ExplosionSoundNearDistance))
            {
                SetRange(_explosionPlayer, SpatialAudio.ExplosionNear);
                _explosionPlayer.ChangeToStream(set.Near, false, true, true);
            }
            else
            {
                SetRange(_explosionPlayer, SpatialAudio.ExplosionFar);
                _explosionPlayer.ChangeToStream(set.Dist, false, true, true);
            }
        }

        /// <summary>Re-authors a remote player's distances; the local tank's 2D players have none.</summary>
        private static void SetRange(RandomStreamSource player, SpatialRange range)
        {
            var voice = player != null && player.Source != null
                ? player.Source.GetComponent<SpatialVoice>() : null;
            if (voice != null) { voice.SetRange(range); }
        }

        /// <summary>
        /// Port of set_spotted(): an unspotted enemy is inaudible, and the
        /// destruction report is exempt so a kill can still be heard.
        /// </summary>
        public void SetSpotted(bool spotted, bool dead = false)
        {
            _spotted = spotted;
            var audible = (spotted || !_isEnemy) && !dead;
            var players = Players();
            for (var i = 0; i < players.Length; i++)
            {
                var player = players[i];
                if (player == null || player == _explosionPlayer) { continue; }

                player.VolumeMul = audible ? player.VolumeMulInit : 0f;
                player.ApplyVolume();
            }

            SyncFakeWreckAudio();
        }

        /// <summary>Port of sniper_mode_switched: the tracks go quiet.</summary>
        public void SetSniperMode(bool on)
        {
            if (_tracksPlayer == null || _tracksPlayer.Sound == null) { return; }

            _tracksPlayer.Mute(on);
            SyncFakeWreckAudio();
            _audio.PlayPool2D(_audio.Sounds.Sound(
                on ? "ui_sniper_in" : "ui_sniper_out"));
        }

        /// <summary>Port of set_fake_wreck(): propulsion only goes silent.</summary>
        public void SetFakeWreck(bool on)
        {
            // Uprising Play Dead sounds like a real kill: the blast, then a
            // silent engine. The owner's crew does not report a destruction.
            if (on && !_fakeWreck) { PlayExplosion(); }
            _fakeWreck = on;
            SyncFakeWreckAudio();
        }

        private void SyncFakeWreckAudio()
        {
            var propulsion = new[] { _enginePlayer, _tracksPlayer };
            for (var i = 0; i < propulsion.Length; i++)
            {
                var player = propulsion[i];
                if (player == null || player.Source == null) { continue; }

                var wasSuppressed = player.Suppressed;
                player.Suppressed = _fakeWreck;
                if (_fakeWreck)
                {
                    player.Source.Stop();
                    player.SetVolumeLinear(0f);
                }
                else if (wasSuppressed && !_tank.Dead
                    && (!_isEnemy || _tank.Spotted))
                {
                    player.VolumeMul = player.VolumeMulInit;
                    player.ApplyVolume();
                    player.Source.Play();
                }
            }
        }

        private RandomStreamSource[] Players()
        {
            return _players ?? (_players = new[]
            {
                _enginePlayer, _tracksPlayer, _gunPlayer, _gunPlayerInside,
                _roughnessPlayer, _roughnessInsidePlayer, _collisionPlayer,
                _explosionPlayer,
            });
        }

        private void RefreshBusVolumes()
        {
            foreach (var player in Players()) player?.RefreshBusVolume();
        }
        private void OnDestroy()
        {
            if (_audio != null) _audio.SpatialGainsChanged -= RefreshBusVolumes;
            if (_tank != null)
            {
                _tank.ProjectileFired -= OnProjectileFired;
                if (_tank.Suspension != null) { _tank.Suspension.Accel -= OnAccel; _tank.Suspension.Deccel -= OnDeccel; }
                if (_tank.Health != null) { _tank.Health.OnDeath -= OnDeath; _tank.Health.OnDamageInfoTaken -= OnDamageInfoTaken; }
                _tank.NetDamageFeedback -= OnDamageInfoTaken;
            }
            foreach (var player in Players()) if (player?.Source != null) player.Source.Stop();
            if (_root != null)
            {
                if (Application.isPlaying) Destroy(_root.gameObject);
                else DestroyImmediate(_root.gameObject);
            }
        }

        /// <summary>Port of on_phys_collision.</summary>
        private void OnCollisionEnter(Collision collision)
        {
            if (_collisionPlayer == null || collision == null) { return; }

            var impulse = collision.impulse.magnitude;
            if (impulse <= TankSoundTuning.CollisionMinImpulse) { return; }
            if (Time.time < _nextCollision) { return; }

            _nextCollision = Time.time + TankSoundTuning.CollisionInterval;
            var pool = _audio.Sounds.Sound(_heavyHull
                ? "tank_collision_heavy" : "tank_collision_light");
            var clip = BlitzResources.LoadAudio(pool.PickRandom(RandomBelow));
            if (clip == null) { return; }

            _collisionPlayer.Source.clip = clip;
            _collisionPlayer.SetVolumeLinear(
                TankSoundTuning.CollisionVolume(impulse));
            _collisionPlayer.Source.time = 0f;
            _collisionPlayer.Source.Play();
        }

        /// <summary>
        /// Port of _physics_process: the engine follows the load term and the
        /// tracks follow ground speed, both through the change throttle.
        /// </summary>
        private void FixedUpdate()
        {
            if (_tank == null || _enginePlayer == null) { return; }
            if (_fakeWreck)
            {
                SyncFakeWreckAudio();
                return;
            }

            if (_tank.Spotted != _spotted)
            {
                SetSpotted(_tank.Spotted, _tank.Dead);
            }

            var susp = _tank.Suspension;
            if (susp == null) { return; }

            var coeff = 0f;
            var tracksOnGround =
                susp.WheelHits > TankSoundTuning.TracksOnGroundHits;
            if (tracksOnGround)
            {
                _speed = Mathf.Ceil(Mathf.Abs((int)susp.Speed));
            }

            if (susp.ForwardAxis != 0f || susp.RightAxis != 0f)
            {
                coeff = TankSoundTuning.EngineLoad(
                    SuspensionTuning.SpeedReachedMul(
                        susp.Speed, susp.MaxSpeedForward, 1f, 1f),
                    susp.RightAxis);
            }

            if (tracksOnGround
                && coeff < TankSoundTuning.TurningLoadMax
                && susp.RightAxis != 0f
                && _engineSnd.Turning != null)
            {
                _enginePlayer.ChangeToStream(
                    _engineSnd.Turning, false, false, false, true);
            }
            else
            {
                _enginePlayer.ChangeToStream(
                    _engineSnd.PickByParam(coeff), false, false, false, true);
            }

            _tracksPlayer.ChangeToStream(_trackSnd.PickBySpeed(
                susp.WheelCount > 1 ? _speed : 0f));
        }

        private bool SniperMode()
        {
            return _tank != null && _tank.View != null && _tank.View.SniperMode;
        }

        /// <summary>
        /// The source measures from the active camera to the gun to decide
        /// between the near and the distant report; the listener stands there.
        /// </summary>
        private bool NearCamera(float distance)
        {
            if (_tank == null || _audio == null) { return true; }

            var target = _tank.GunShootPoint();
            return (_audio.ListenerPosition - target).sqrMagnitude
                < distance * distance;
        }
    }
}
