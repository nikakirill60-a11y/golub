using System.Collections.Generic;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Audio;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle.WorldOfDucks
{
    /// <summary>
    /// One WarDuck seat (Godot <c>world_of_ducks_fx.gd</c>): it sails instead
    /// of driving (no drowning, no track marks, the source's water drag), its
    /// engine and track players are replaced by the 4.8 swimming bed, it
    /// quacks now and then, and its shots, hits, bumps and death use the
    /// event's own samples and particle scenes.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WorldOfDucksTank : MonoBehaviour
    {
        public const float LateralWaterDrag = 2.2f;
        public const float ForwardWaterDrag = 0.10f;
        public const float InWaterOffset = 0.6f;
        public const float InWaterBand = 2.6f;

        public const float SwimMinSpeed = 0.9f;
        public const float SwimLocalVolume = 0.16f;
        public const float SwimWorldVolume = 0.055f;
        public const float SwimWorldMinDistance = 8f;
        public const float SwimWorldMaxDistance = 100f;
        public const string SwimSample = "duck_surfaces_water_moving";

        public const float BowSprayMinSpeed = 5f;

        public static readonly Vector2 VoiceInitialLocal = new Vector2(2f, 4f);
        public static readonly Vector2 VoiceInitialRemote = new Vector2(3f, 12f);
        public static readonly Vector2 VoiceRepeatLocal = new Vector2(8f, 17f);
        public static readonly Vector2 VoiceRepeatRemote = new Vector2(14f, 30f);
        public const float VoiceSqueakChance = 0.22f;
        public const float VoiceLocalVolume = 0.58f;
        public const float VoiceRemoteVolume = 0.48f;
        public const float VoiceMinDistance = 6f;
        public const float VoiceMaxDistance = 30f;
        public static readonly string[] Quacks = { "duck_gui_01", "duck_gui_02", "duck_gui_03", "duck_gui_04" };
        public static readonly string[] Squeaks = { "duck_hits_squek_01", "duck_hits_squek_02", "duck_hits_squek_03" };

        public const float ShotMinDistance = 18f;
        public const float ShotMaxDistance = 150f;
        public const float HitVolume = 0.5f;
        public const float HitWaterVolume = 0.24f;
        public const float HitSqueakVolume = 0.44f;
        public const float HitSqueakChance = 0.40f;
        public const float BlockVolume = 0.45f;
        public const float DeathVolume = 0.9f;
        public const float GroupMinDistance = 6f;
        public const float GroupMaxDistance = 100f;

        public const float CollisionImpulseMin = 3f;
        public const float CollisionImpulseFull = 10f;
        public const float CollisionVolumeMin = 0.14f;
        public const float CollisionVolumeMax = 0.50f;

        public BattleTank Tank { get; private set; }
        public WorldOfDucksBattle Battle { get; private set; }

        /// <summary>Gun slot 0..2: duck gun, water gun, torpedo.</summary>
        public int GunIndex { get; private set; }

        public int VoicesPlayed { get; private set; }
        public int ShotsVoiced { get; private set; }
        public bool Swimming { get { return _swim != null; } }

        private Rigidbody _body;
        private AudioSource _swim;
        private bool _swimLocal;
        private DavaFx _wake;
        private DavaFx _bow;
        private readonly HashSet<int> _contacts = new HashSet<int>();
        private System.Random _random;
        private bool _voiceArmed;
        private float _voiceDelay;
        private float _halfLength = 1.4f;
        private bool _wreckConfigured;
        private int _lastBlockFrame = -1;

        public static WorldOfDucksTank Attach(BattleTank tank, WorldOfDucksBattle battle)
        {
            if (tank == null || battle == null) { return null; }
            var duck = tank.GetComponent<WorldOfDucksTank>();
            if (duck == null) { duck = tank.gameObject.AddComponent<WorldOfDucksTank>(); }
            duck.Bind(tank, battle);
            return duck;
        }

        private void Bind(BattleTank tank, WorldOfDucksBattle battle)
        {
            if (Tank == tank) { return; }
            Tank = tank;
            Battle = battle;
            _body = tank.GetComponent<Rigidbody>();
            _random = new System.Random(unchecked(tank.GetHashCode() * 7919 + 17));
            GunIndex = WorldOfDucksBattle.GunIndexOf(tank.Card, tank.GunInfo != null ? tank.GunInfo.Name : null);

            // The ducks sail on the arena's invisible floor: the ordinary
            // drowning model and the tread prints have no place here.
            if (tank.Water != null)
            {
                tank.Water.ResetState();
                tank.Water.enabled = false;
            }
            if (tank.TrackMarks != null) { tank.TrackMarks.enabled = false; }

            if (tank.Hull.Length > 0f) { _halfLength = Mathf.Max(tank.Hull.Length * 0.38f, 1.4f); }

            tank.ProjectileFired += OnProjectileFired;
            if (tank.Health != null)
            {
                tank.Health.OnDamageInfoTaken += OnDamageInfoTaken;
                tank.Health.OnDamageBlock += OnDamageBlock;
                tank.Health.OnDeath += OnDeath;
            }
            ConfigureWreck();
        }

        private void OnDestroy()
        {
            StopSwim();
            StopWake();
            if (Tank == null) { return; }
            Tank.ProjectileFired -= OnProjectileFired;
            if (Tank.Health != null)
            {
                Tank.Health.OnDamageInfoTaken -= OnDamageInfoTaken;
                Tank.Health.OnDamageBlock -= OnDamageBlock;
                Tank.Health.OnDeath -= OnDeath;
            }
        }

        private void ConfigureWreck()
        {
            if (_wreckConfigured || Tank == null || Tank.Wreck == null || Tank.Wreck.Original == null) { return; }
            var wreck = Tank.Wreck.Original;
            wreck.UseWorldOfDucksFx = true;
            wreck.ExplosionSource = Battle.EffectScene("explosion");
            wreck.FlamingSource = Battle.EffectScene("flamingDead");
            wreck.WaterSurface = WorldOfDucksRules.WaterLevel + WorldOfDucksBattle.SurfaceLift;
            _wreckConfigured = true;
        }

        private BattleSession Session { get { return Battle != null ? Battle.Session : null; } }

        private bool IsLocal { get { return Session != null && Tank == Session.Player && !Session.Dedicated; } }

        private bool IsEnemy
        {
            get { return Session != null && Session.Roster != null && Tank.Team != Session.Roster.PlayerTeam; }
        }

        /// <summary>A positional call must not reveal an unspotted enemy.</summary>
        public bool Audible { get { return !IsEnemy || Tank.Spotted; } }

        private bool BattleRunning { get { return Session != null && Session.Started; } }

        public static bool InWater(float height)
        {
            return Mathf.Abs(height - (WorldOfDucksRules.WaterLevel + InWaterOffset)) < InWaterBand;
        }

        /// <summary>The source drag: the sideways slide goes, the top speed stays.</summary>
        public static Vector3 WaterDrag(Vector3 velocity, Vector3 forward, Vector3 right)
        {
            velocity.y = 0f;
            forward.y = 0f;
            right.y = 0f;
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
            right = right.sqrMagnitude > 1e-6f ? right.normalized : Vector3.right;
            var lateral = right * Vector3.Dot(velocity, right);
            var longitudinal = forward * Vector3.Dot(velocity, forward);
            return -lateral * LateralWaterDrag - longitudinal * ForwardWaterDrag;
        }

        private void FixedUpdate()
        {
            if (Tank == null || Tank.Dead || _body == null || _body.isKinematic) { return; }
            if (!InWater(Tank.transform.position.y)) { return; }
            _body.AddForce(WaterDrag(_body.linearVelocity, Tank.transform.forward, Tank.transform.right),
                ForceMode.Acceleration);
        }

        private void Update()
        {
            if (Tank == null) { return; }
            if (!_wreckConfigured) { ConfigureWreck(); }
            if (Tank.Dead || Session == null || Session.Dedicated)
            {
                StopSwim();
                StopWake();
                return;
            }

            UpdateVoice(Time.deltaTime);

            var velocity = _body != null ? _body.linearVelocity : Vector3.zero;
            velocity.y = 0f;
            var speed = velocity.magnitude;
            var inWater = InWater(Tank.transform.position.y);
            var audible = BattleRunning && Audible;
            if (speed > SwimMinSpeed && inWater && audible) { StartSwim(); } else { StopSwim(); }

            var forward = Tank.transform.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
            var moving = inWater && speed > SwimMinSpeed;
            var stern = Tank.transform.position - forward * _halfLength;
            stern.y = WorldOfDucksRules.WaterLevel + 0.035f;
            var bow = Tank.transform.position + forward * _halfLength;
            bow.y = WorldOfDucksRules.WaterLevel + 0.10f;
            _wake = Trail(_wake, moving, "chassisMud", stern, forward);
            _bow = Trail(_bow, moving && speed > BowSprayMinSpeed, "chassisDust", bow, forward);
        }

        private DavaFx Trail(DavaFx fx, bool on, string key, Vector3 position, Vector3 forward)
        {
            if (!on)
            {
                if (fx != null) { fx.Stop(); }
                return null;
            }
            if (fx == null)
            {
                fx = Battle.SpawnScene(Battle.EffectScene(key), position,
                    Quaternion.LookRotation(forward, Vector3.up), 1000f, true);
                if (fx == null) { return null; }
            }
            fx.transform.position = position;
            fx.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            return fx;
        }

        private void StopWake()
        {
            if (_wake != null) { _wake.Stop(); _wake = null; }
            if (_bow != null) { _bow.Stop(); _bow = null; }
        }

        private void StartSwim()
        {
            if (_swim != null) { return; }
            var audio = WorldOfDucksBattle.Audio;
            if (audio == null) { return; }
            _swimLocal = IsLocal;
            if (_swimLocal)
            {
                _swim = audio.PlaySound2D(WorldOfDucksBattle.ClipFolder + SwimSample, AudioBus.Sfx, true, SwimLocalVolume);
            }
            else
            {
                _swim = audio.PlayClipLoop3D(WorldOfDucksBattle.ClipFolder + SwimSample, Tank.transform,
                    SwimWorldVolume, new SpatialRange(SwimWorldMinDistance, SwimWorldMaxDistance));
            }
        }

        private void StopSwim()
        {
            if (_swim == null) { return; }
            var audio = WorldOfDucksBattle.Audio;
            if (audio != null) { audio.StopSound2D(_swim); }
            if (_swim != null) { Destroy(_swim.gameObject); }
            _swim = null;
        }

        private float Range(Vector2 range)
        {
            return range.x + (float)_random.NextDouble() * (range.y - range.x);
        }

        private float Range(float from, float to)
        {
            return from + (float)_random.NextDouble() * (to - from);
        }

        private void UpdateVoice(float delta)
        {
            if (!BattleRunning)
            {
                _voiceArmed = false;
                _voiceDelay = 0f;
                return;
            }
            if (!_voiceArmed)
            {
                _voiceArmed = true;
                _voiceDelay = Range(IsLocal ? VoiceInitialLocal : VoiceInitialRemote);
                return;
            }
            _voiceDelay -= delta;
            if (_voiceDelay > 0f) { return; }
            PlayVoice();
            _voiceDelay = Range(IsLocal ? VoiceRepeatLocal : VoiceRepeatRemote);
        }

        /// <summary>One quack (or, 22 % of the time, a squeak).</summary>
        public AudioSource PlayVoice()
        {
            if (Tank == null || (!IsLocal && !Audible)) { return null; }
            var samples = _random.NextDouble() < VoiceSqueakChance ? Squeaks : Quacks;
            var clip = samples[_random.Next(samples.Length)];
            var pitch = Range(0.96f, 1.04f);
            var source = IsLocal
                ? WorldOfDucksBattle.PlayLocal(clip, VoiceLocalVolume * Range(0.92f, 1.05f), pitch)
                : WorldOfDucksBattle.PlayAt(clip, Tank.transform.position + Vector3.up * 0.9f,
                    VoiceRemoteVolume * Range(0.90f, 1.04f), VoiceMinDistance, VoiceMaxDistance, pitch);
            if (source != null) { VoicesPlayed++; }
            return source;
        }

        /// <summary>The 4.8 shot family of one gun slot.</summary>
        public static string ShotGroup(int gun, out int count)
        {
            switch (gun)
            {
                case 1: count = 5; return "duck_water_gun";
                case 2: count = 3; return "duck_torpedo";
                default: count = 4; return "duck_gun";
            }
        }

        private void OnProjectileFired(bool first, int shell)
        {
            if (Tank == null || Session == null || Session.Dedicated) { return; }
            if (IsEnemy && !Tank.Spotted) { return; }
            var muzzle = Tank.Turret != null && Tank.Turret.Muzzle != null ? Tank.Turret.Muzzle : Tank.transform;
            int count;
            var group = ShotGroup(GunIndex, out count);
            var clip = WorldOfDucksBattle.PickGroup(group, count);
            var source = IsLocal
                ? WorldOfDucksBattle.PlayLocal(clip, 1f)
                : WorldOfDucksBattle.PlayAt(clip, muzzle.position, 1f, ShotMinDistance, ShotMaxDistance);
            if (source != null) { ShotsVoiced++; }

            var scene = Battle.ShotScene(Tank.GunInfo != null ? Tank.GunInfo.Name : null);
            var fx = Battle.SpawnScene(scene, muzzle.position, muzzle.rotation, 3f, false, muzzle);
            if (fx != null) { fx.transform.localPosition = Vector3.zero; fx.transform.localRotation = Quaternion.identity; }
        }

        private void PlayGroup(string prefix, int count, float volume)
        {
            WorldOfDucksBattle.PlayGroupAt(prefix, count, Tank.transform.position, volume, GroupMinDistance, GroupMaxDistance);
        }

        private void OnDamageInfoTaken(DamageInfo info)
        {
            if (Tank == null || Tank.Health == null || Tank.Health.Health <= 0 || Session == null || Session.Dedicated) { return; }
            if (info == null || info.Dmg <= 0)
            {
                PlayBlock();
                return;
            }
            PlayGroup("duck_hits", 5, HitVolume);
            PlayGroup("duck_hits_water", 5, HitWaterVolume);
            if (_random.NextDouble() < HitSqueakChance) { PlayGroup("duck_hits_squek", 3, HitSqueakVolume); }
        }

        private void OnDamageBlock(int amount)
        {
            if (Tank == null || Tank.Dead || Session == null || Session.Dedicated) { return; }
            PlayBlock();
        }

        private void PlayBlock()
        {
            if (_lastBlockFrame == Time.frameCount) { return; }
            _lastBlockFrame = Time.frameCount;
            PlayGroup("duck_not_pierce", 3, BlockVolume);
        }

        public static float CollisionHardness(float impulse)
        {
            return Mathf.Clamp01((impulse - CollisionImpulseMin) / (CollisionImpulseFull - CollisionImpulseMin));
        }

        public static float CollisionVolume(float impulse)
        {
            return Mathf.Lerp(CollisionVolumeMin, CollisionVolumeMax, CollisionHardness(impulse));
        }

        /// <summary>One squeak per touching body until it lets go.</summary>
        public bool ClaimContact(int body, float impulse)
        {
            if (impulse < CollisionImpulseMin) { return false; }
            return _contacts.Add(body);
        }

        public void ReleaseContact(int body) { _contacts.Remove(body); }

        private static int BodyId(Collision collision)
        {
            return collision.rigidbody != null ? collision.rigidbody.GetHashCode() : collision.collider.GetHashCode();
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (Tank == null || Tank.Dead || collision == null || collision.collider == null) { return; }
            if (WorldOfDucksBattle.IsWaterColliderName(collision.collider.name)) { return; }
            if (!ClaimContact(BodyId(collision), collision.relativeVelocity.magnitude)) { return; }
            if (Session == null || Session.Dedicated) { return; }
            PlayGroup("duck_collision", 3, CollisionVolume(collision.relativeVelocity.magnitude));
            var point = collision.contactCount > 0 ? collision.GetContact(0).point : Tank.transform.position;
            var other = collision.rigidbody != null ? collision.rigidbody.GetComponent<BattleTank>() : null;
            var key = other != null ? "ramming" : "collision";
            Battle.SpawnScene(Battle.EffectScene(key), point, Quaternion.identity, 3f);
        }

        private void OnCollisionExit(Collision collision)
        {
            if (collision == null || collision.collider == null) { return; }
            ReleaseContact(BodyId(collision));
        }

        private void OnDeath()
        {
            StopSwim();
            StopWake();
            _contacts.Clear();
            ConfigureWreck();
            if (Tank == null || Session == null || Session.Dedicated) { return; }
            PlayGroup("duck_death", 4, DeathVolume);
        }
    }
}
