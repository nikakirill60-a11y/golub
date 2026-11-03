using System;
using System.Collections;
using System.Collections.Generic;
using BlitzLegacy.Core.Audio;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Audio;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle.WorldOfDucks
{
    /// <summary>Where a WarDuck shell ended, for its 4.8 hit scene and sound.</summary>
    public enum DuckImpactSurface { Pierced, Resisted, Water, Ground }

    /// <summary>
    /// The running War of Ducks battle (Godot <c>world_of_ducks_fx.gd</c> plus
    /// <c>world_of_ducks_projectile_fx.gd</c>, dressed with the 4.8.0 scenes
    /// and samples). Lives on the <see cref="BattleSession"/> object: it hangs
    /// a <see cref="WorldOfDucksTank"/> on every seat, plays the arena's
    /// particle emitters and ambient sound emitters, and owns the shell hit
    /// presentation and the torpedo wake. Only ever active offline.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WorldOfDucksBattle : MonoBehaviour
    {
        /// <summary>Audio/ sub-folder of the 59 decoded 4.8 samples.</summary>
        public const string ClipFolder = "world_of_ducks/";

        public const float ImpactVolumePierced = 0.55f;
        public const float ImpactVolumeResisted = 0.52f;
        public const float ImpactVolumeWater = 0.52f;
        public const float ImpactVolumeWaterFall = 0.38f;
        public const float ImpactVolumeSand = 0.5f;
        public const float ImpactVolumeOneShort = 0.25f;
        public const float ImpactMinDistance = 18f;
        public const float ImpactMaxDistance = 150f;

        public const float SeagullVolume = 0.26f;
        public const float SeagullMinDistance = 28f;
        public const float SeagullMaxDistance = 145f;
        public const float WaterBedVolume = 0.3f;
        public const float WaterBedMinDistance = 6f;
        public const float WaterBedMaxDistance = 60f;

        /// <summary>Gun slot of the torpedo launcher (<c>_Duck_Watergun_3</c>).</summary>
        public const int TorpedoGun = 2;
        /// <summary>
        /// Height above <see cref="WorldOfDucksRules.WaterLevel"/> of the scenes laid on
        /// the sea (the torpedo wake, the death whirl): the water writes depth, so
        /// anything authored under the surface would simply vanish.
        /// </summary>
        public const float SurfaceLift = 0.05f;

        public static WorldOfDucksBattle Current { get; private set; }

        public BattleSession Session { get; private set; }
        public WorldOfDucksRules Rules { get; private set; }

        /// <summary>Every WarDuck seat of this battle.</summary>
        public readonly List<WorldOfDucksTank> Ducks = new List<WorldOfDucksTank>();

        /// <summary>Map particle scenes started so far (tests and render checks).</summary>
        public readonly List<DavaFx> MapEffects = new List<DavaFx>();

        /// <summary>Looping water beds of the arena's sound emitters.</summary>
        public readonly List<AudioSource> WaterBeds = new List<AudioSource>();

        public int SeagullCalls { get; private set; }
        public int ImpactScenes { get; private set; }
        public int TorpedoWakes { get; private set; }

        private JsonNode _effects;

        /// <summary>True while an offline War of Ducks battle runs.</summary>
        public static bool Active
        {
            get
            {
                var current = Current;
                return current != null && current.Session != null && current.Session.Net == null;
            }
        }

        private static WorldOfDucksBattle Live { get { return Active ? Current : null; } }

        public static WorldOfDucksBattle Create(BattleSession session)
        {
            if (session == null) { throw new ArgumentNullException("session"); }
            if (session.Net != null) { throw new SandboxOnlineException("world_of_ducks"); }
            if (session.Ducks == null) { throw new InvalidOperationException("The battle carries no War of Ducks rules"); }

            var battle = session.GetComponent<WorldOfDucksBattle>();
            if (battle == null) { battle = session.gameObject.AddComponent<WorldOfDucksBattle>(); }
            battle.Session = session;
            battle.Rules = session.Ducks;
            battle._effects = session.Ducks.Document["effects"];
            Current = battle;

            foreach (var tank in session.Roster != null ? AllTanks(session) : new List<BattleTank>())
            {
                var duck = WorldOfDucksTank.Attach(tank, battle);
                if (duck != null && !battle.Ducks.Contains(duck)) { battle.Ducks.Add(duck); }
            }

            BattleProjectile.Created -= battle.OnProjectileCreated;
            BattleProjectile.Created += battle.OnProjectileCreated;
            if (!session.Dedicated)
            {
                battle.StartMapEffects();
                battle.StartMapSounds();
            }

            Debug.Log("[Blitz] war of ducks active: map=" + battle.Rules.MapId
                + " ducks=" + battle.Ducks.Count + " mapFx=" + battle.Rules.Document["mapEffects"].Count
                + " mapSounds=" + battle.Rules.Document["mapSounds"].Count);
            return battle;
        }

        private static List<BattleTank> AllTanks(BattleSession session)
        {
            var list = new List<BattleTank>();
            for (var i = 0; i < session.Roster.TanksA.Count; i++) { list.Add(session.Roster.TanksA[i]); }
            for (var i = 0; i < session.Roster.TanksB.Count; i++) { list.Add(session.Roster.TanksB[i]); }
            return list;
        }

        private void OnDestroy()
        {
            BattleProjectile.Created -= OnProjectileCreated;
            for (var i = 0; i < WaterBeds.Count; i++)
            {
                var bed = WaterBeds[i];
                if (bed == null) { continue; }
                var audio = Audio;
                if (audio != null) { audio.StopSound2D(bed); }
                Destroy(bed.gameObject);
            }
            WaterBeds.Clear();
            if (Current == this) { Current = null; }
        }

        // ----- rules ------------------------------------------------------------

        /// <summary>No friendly fire: a live team-mate swallows the shell.</summary>
        public static bool BlocksFriendlyHit(BattleTank owner, BattleTank target)
        {
            var s = Live;
            return s != null && !s.Rules.FriendlyFire && owner != null && target != null
                && target != owner && !target.Dead && target.Team == owner.Team;
        }

        /// <summary>The <c>effects</c> table of the ported event document.</summary>
        public JsonNode Effects { get { return _effects; } }

        public string EffectScene(string key)
        {
            return _effects == null ? string.Empty : _effects[key].AsString(string.Empty);
        }

        /// <summary><c>effects.guns[name].shot</c>: the 4.8 muzzle scene of one gun.</summary>
        public string ShotScene(string gunName)
        {
            if (_effects == null || string.IsNullOrEmpty(gunName)) { return string.Empty; }
            return _effects["guns"][gunName]["shot"].AsString(string.Empty);
        }

        // ----- pure helpers (unit tested) -------------------------------------

        /// <summary><c>"%s_%02d" % [prefix, index]</c> inside the event folder.</summary>
        public static string ClipName(string prefix, int index)
        {
            return ClipFolder + prefix + "_" + index.ToString("00");
        }

        /// <summary>Gun slot of a WarDuck gun by its card name, or 0.</summary>
        public static int GunIndexOf(TankInfo card, string gunName)
        {
            if (card != null && !string.IsNullOrEmpty(gunName))
            {
                for (var i = 0; i < card.Guns.Count; i++)
                {
                    if (card.Guns[i] != null && card.Guns[i].Name == gunName) { return i; }
                }
            }
            // _Duck_Watergun_N carries its slot in the name.
            if (!string.IsNullOrEmpty(gunName))
            {
                var last = gunName[gunName.Length - 1];
                if (last >= '1' && last <= '9') { return last - '1'; }
            }
            return 0;
        }

        /// <summary>The arena's water props: the sea tiles and the invisible floor.</summary>
        public static bool IsWaterColliderName(string name)
        {
            if (string.IsNullOrEmpty(name)) { return false; }
            var lower = name.ToLowerInvariant();
            return lower.Contains("sea") || lower.Contains("water") || lower.Contains("surf")
                || lower.Contains("collisionplain");
        }

        public static DuckImpactSurface Classify(ShellSoundKind kind, string colliderName, float height)
        {
            switch (kind)
            {
                case ShellSoundKind.Pen:
                case ShellSoundKind.Crit:
                    return DuckImpactSurface.Pierced;
                case ShellSoundKind.Bounce:
                case ShellSoundKind.Ricoshet:
                    return DuckImpactSurface.Resisted;
            }
            return IsWaterColliderName(colliderName) || WorldOfDucksRules.IsWaterHit(height)
                ? DuckImpactSurface.Water : DuckImpactSurface.Ground;
        }

        /// <summary>The 4.8 hit scene for one burst.</summary>
        public static string ImpactScene(DuckImpactSurface surface, bool torpedo)
        {
            switch (surface)
            {
                case DuckImpactSurface.Pierced: return "hit_tank/armorHit_HE4";
                case DuckImpactSurface.Resisted: return "hit_tank/armorResisted";
                case DuckImpactSurface.Water:
                    return torpedo ? "hit_surface/DeepWaterHitMedium_HE2" : "hit_surface/DeepWaterHit_HE2";
                default: return "hit_surface/woodHit_HE4";
            }
        }

        /// <summary>Seagull call delays: staggered first call, then 7 to 15 s.</summary>
        public static float SeagullFirstDelay(int emitter)
        {
            return 1.5f + (Mathf.Abs(emitter) % 4) * 1.7f;
        }

        // ----- shells -----------------------------------------------------------

        private static bool IsTorpedo(BattleProjectile projectile)
        {
            if (projectile == null || projectile.Owner == null) { return false; }
            var duck = projectile.Owner.GetComponent<WorldOfDucksTank>();
            return duck != null && duck.GunIndex == TorpedoGun;
        }

        private static DuckImpactSurface ClassifyShell(BattleProjectile projectile, ShellSoundKind kind, Vector3 point)
        {
            var collider = projectile != null ? projectile.LastHitCollider : null;
            return Classify(kind, collider != null ? collider.name : null, point.y);
        }

        /// <summary>BattleProjectile.PlayImpact in the War of Ducks.</summary>
        public static void ImpactSound(BattleProjectile projectile, ShellSoundKind kind, Vector3 point)
        {
            var s = Live;
            if (s == null || s.Session.Dedicated) { return; }
            var surface = ClassifyShell(projectile, kind, point);
            switch (surface)
            {
                case DuckImpactSurface.Pierced:
                    PlayGroupAt("duck_hits", 5, point, ImpactVolumePierced, ImpactMinDistance, ImpactMaxDistance);
                    break;
                case DuckImpactSurface.Resisted:
                    PlayGroupAt("duck_not_pierce", 3, point, ImpactVolumeResisted, ImpactMinDistance, ImpactMaxDistance);
                    break;
                case DuckImpactSurface.Water:
                    PlayGroupAt("duck_impacts_water", 3, point, ImpactVolumeWater, ImpactMinDistance, ImpactMaxDistance);
                    if (IsTorpedo(projectile))
                    {
                        PlayGroupAt("duck_impacts_water_fall", 3, point, ImpactVolumeWaterFall, ImpactMinDistance, ImpactMaxDistance);
                    }
                    break;
                default:
                    PlayGroupAt("duck_impacts_sand", 3, point, ImpactVolumeSand, ImpactMinDistance, ImpactMaxDistance);
                    PlayGroupAt("duck_impacts_oneshort", 3, point, ImpactVolumeOneShort, ImpactMinDistance, ImpactMaxDistance);
                    break;
            }
        }

        /// <summary>BattleProjectile.SpawnImpactFx in the War of Ducks.</summary>
        public static void ImpactFx(BattleProjectile projectile, ShellSoundKind kind, Vector3 point)
        {
            var s = Live;
            if (s == null || s.Session.Dedicated) { return; }
            var surface = ClassifyShell(projectile, kind, point);
            var scene = ImpactScene(surface, IsTorpedo(projectile));
            var normal = projectile != null ? projectile.LastHitNormal : Vector3.up;
            if (surface == DuckImpactSurface.Water)
            {
                normal = Vector3.up;
                point.y = WorldOfDucksRules.WaterLevel;
            }
            if (s.SpawnScene(scene, point, Quaternion.FromToRotation(Vector3.up, normal), 4f) != null)
            {
                s.ImpactScenes++;
            }
        }

        private void OnProjectileCreated(BattleProjectile projectile)
        {
            if (!Active || Current != this || Session.Dedicated || !IsTorpedo(projectile)) { return; }
            var scene = _effects["guns"]["_Duck_Watergun_3"]["effects"].AsString("shot_torpedo");
            var fx = SpawnScene("gun_effects/" + scene, projectile.transform.position, Quaternion.identity, 1000f, true);
            if (fx == null) { return; }
            fx.gameObject.AddComponent<WorldOfDucksWake>().Follow(projectile, fx);
            TorpedoWakes++;
        }

        // ----- scenes and sounds ----------------------------------------------

        /// <summary>One 4.8 scene from the event folder, parented to the battle.</summary>
        public DavaFx SpawnScene(string scene, Vector3 position, Quaternion rotation, float seconds, bool sustain = false, Transform parent = null)
        {
            if (string.IsNullOrEmpty(scene)) { return null; }
            try
            {
                var fx = DavaFx.CreateWorldOfDucks(parent != null ? parent : transform, scene, seconds, sustain);
                fx.transform.position = position;
                fx.transform.rotation = rotation;
                return fx;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Blitz] war of ducks scene failed: " + scene + " (" + e.Message + ")");
                return null;
            }
        }

        public static AudioService Audio
        {
            get { return BlitzBootstrap.Current != null ? BlitzBootstrap.Current.Audio : null; }
        }

        /// <summary>A one-shot spatial sample with the event's own distances.</summary>
        public static AudioSource PlayAt(string clip, Vector3 position, float volume,
            float minDistance, float maxDistance, float pitch = 1f)
        {
            var audio = Audio;
            if (audio == null || string.IsNullOrEmpty(clip)) { return null; }
            var source = audio.PlaySound3D(ClipFolder + clip, position, volume, AudioBus.Sfx,
                new SpatialRange(Mathf.Min(minDistance, maxDistance), maxDistance));
            if (source != null) { source.pitch = pitch; }
            return source;
        }

        /// <summary>A non-positional sample for the local duck.</summary>
        public static AudioSource PlayLocal(string clip, float volume, float pitch = 1f)
        {
            var audio = Audio;
            if (audio == null || string.IsNullOrEmpty(clip)) { return null; }
            var source = audio.PlaySound2D(ClipFolder + clip, AudioBus.Sfx, false, volume);
            if (source != null) { source.pitch = pitch; }
            return source;
        }

        public static string PickGroup(string prefix, int count)
        {
            return prefix + "_" + UnityEngine.Random.Range(1, Mathf.Max(1, count) + 1).ToString("00");
        }

        public static AudioSource PlayGroupAt(string prefix, int count, Vector3 position, float volume,
            float minDistance, float maxDistance)
        {
            return PlayAt(PickGroup(prefix, count), position, volume, minDistance, maxDistance);
        }

        private void StartMapEffects()
        {
            var effects = Rules.Document["mapEffects"];
            for (var i = 0; i < effects.Count; i++)
            {
                StartCoroutine(RunMapEffect(effects[i]));
            }
        }

        private static Vector3 Vec(JsonNode node, Vector3 fallback)
        {
            if (node.Kind != JsonKind.Array || node.Count < 3) { return fallback; }
            return new Vector3(node[0].AsFloat(), node[1].AsFloat(), node[2].AsFloat());
        }

        private IEnumerator RunMapEffect(JsonNode entry)
        {
            var delay = entry["delay"].AsFloat() + entry["delayVariation"].AsFloat() * UnityEngine.Random.value;
            if (delay > 0f) { yield return new WaitForSeconds(delay); }
            var forward = Vec(entry["forward"], Vector3.forward);
            var up = Vec(entry["up"], Vector3.up);
            if (forward.sqrMagnitude < 1e-6f) { forward = Vector3.forward; }
            if (up.sqrMagnitude < 1e-6f) { up = Vector3.up; }
            var repeats = entry["repeats"].AsInt(-1);
            var duration = Mathf.Max(0.1f, entry["duration"].AsFloat(10f));
            var sustain = repeats < 0;
            var seconds = sustain ? duration : duration * Mathf.Max(1, repeats);
            var fx = SpawnScene(entry["source"].AsString(), Vec(entry["position"], Vector3.zero),
                Quaternion.LookRotation(forward, up), seconds, sustain);
            if (fx == null) { yield break; }
            var start = entry["startFromTime"].AsFloat();
            while (start > 0f)
            {
                var step = Mathf.Min(start, 1f / 30f);
                fx.Simulate(step);
                start -= step;
            }
            MapEffects.Add(fx);
        }

        private void StartMapSounds()
        {
            var sounds = Rules.Document["mapSounds"];
            var seagull = 0;
            var bed = 0;
            for (var i = 0; i < sounds.Count; i++)
            {
                var entry = sounds[i];
                var name = entry["event"].AsString();
                var position = Vec(entry["position"], Vector3.zero);
                if (name.Contains("seagull"))
                {
                    StartCoroutine(RunSeagull(position, seagull++));
                }
                else if (name.Contains("lake") || name.Contains("river") || name.Contains("sea"))
                {
                    var clip = name.Contains("lake") ? "lake_01" : "river_" + ((bed % 3) + 1).ToString("00");
                    bed++;
                    StartWaterBed(clip, position, name);
                }
            }
        }

        private void StartWaterBed(string clip, Vector3 position, string name)
        {
            var audio = Audio;
            if (audio == null) { return; }
            var anchor = new GameObject("DuckAmbient_" + name);
            anchor.transform.SetParent(transform, false);
            anchor.transform.position = position;
            var source = audio.PlayClipLoop3D(ClipFolder + clip, anchor.transform, WaterBedVolume,
                new SpatialRange(WaterBedMinDistance, WaterBedMaxDistance));
            if (source == null) { Destroy(anchor); return; }
            // Each bed starts somewhere inside its loop, so the arena does not pulse.
            if (source.clip != null && source.clip.samples > 0)
            {
                source.timeSamples = UnityEngine.Random.Range(0, source.clip.samples);
            }
            WaterBeds.Add(source);
        }

        private IEnumerator RunSeagull(Vector3 position, int emitter)
        {
            yield return new WaitForSeconds(SeagullFirstDelay(emitter));
            var call = emitter;
            while (true)
            {
                if (PlayAt("seagull_" + ((call % 3) + 1).ToString("00"), position, SeagullVolume,
                    SeagullMinDistance, SeagullMaxDistance) != null)
                {
                    SeagullCalls++;
                }
                call++;
                yield return new WaitForSeconds(UnityEngine.Random.Range(7f, 15f));
            }
        }
    }

    /// <summary>
    /// The 4.8 <c>shot_torpedo</c> wake riding one torpedo; stopped (and left
    /// to fade) once the shell is gone.
    /// </summary>
    public sealed class WorldOfDucksWake : MonoBehaviour
    {
        private BattleProjectile _projectile;
        private DavaFx _fx;
        private bool _stopped;

        public void Follow(BattleProjectile projectile, DavaFx fx)
        {
            _projectile = projectile;
            _fx = fx;
            Track();
        }

        private void LateUpdate()
        {
            if (_stopped) { return; }
            if (_projectile == null || !_projectile.Alive || !_projectile.gameObject.activeInHierarchy)
            {
                _stopped = true;
                if (_fx != null) { _fx.Stop(); }
                return;
            }
            Track();
        }

        private void Track()
        {
            if (_projectile == null) { return; }
            // The shell runs just under the surface (muzzle 14.59 m, water 14.7 m):
            // the ribbon and the shark fins ride on top of the water instead.
            var position = _projectile.transform.position;
            position.y = Mathf.Max(position.y, WorldOfDucksRules.WaterLevel + WorldOfDucksBattle.SurfaceLift);
            transform.position = position;
            var velocity = _projectile.Velocity;
            if (velocity.sqrMagnitude > 1e-4f)
            {
                transform.rotation = Quaternion.LookRotation(velocity.normalized, Vector3.up);
            }
        }
    }
}
