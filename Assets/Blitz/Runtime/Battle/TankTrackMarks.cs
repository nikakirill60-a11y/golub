using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Lighting;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Blitz 11.20 tank treads: two terrain-conforming strips built from the
    /// client's national tread textures. One reusable mesh belongs to a tank;
    /// there is deliberately no GameObject, material or allocation per mark.
    /// Old samples fade and are discarded by travelled distance as well as age.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BattleTank))]
    public sealed class TankTrackMarks : MonoBehaviour
    {
        public const string ShaderResource = "Shaders/TankTreads1120";
        public const string TextureRoot = "TankTreads1120/";

        // The shipped DAVA shader exposes both distance and time fading. Its
        // native constants are not present in the asset pack, so the Unity port
        // keeps a strict, documented budget with a soft tail instead of ever
        // allowing trails to accumulate for the whole battle.
        public const float SampleDistance = 0.30f;
        public const float DistanceFadeStart = 80f;
        public const float MaximumTrailDistance = 96f;
        public const float TimeFadeStart = 24f;
        public const float TimeFadeEnd = 30f;
        public const int MaximumSamplesPerSide = 384;
        public const float GroundLift = 0.018f;
        public const float ProbeHeight = 2.5f;
        public const float ProbeDistance = 6f;
        public const float MinimumGroundNormalY = 0.35f;
        public const float TextureRepeatMetres = 1.25f;
        public const float TeleportDistance = 8f;

        private sealed class TrailSide
        {
            private readonly Sample[] _samples = new Sample[MaximumSamplesPerSide];
            private int _head;
            public int Count { get; private set; }
            public Vector3 LastProbe;
            public Vector3 LastRight = Vector3.right;
            public float TotalTravel;
            public float DistanceSinceSample;
            public bool HasProbe;
            public bool BreakNext = true;

            public Sample Get(int index)
            {
                return _samples[(_head + index) % _samples.Length];
            }

            public void Set(int index, Sample sample)
            {
                _samples[(_head + index) % _samples.Length] = sample;
            }

            public void Add(Sample sample)
            {
                if (Count == _samples.Length)
                {
                    _head = (_head + 1) % _samples.Length;
                    Count--;
                }
                _samples[(_head + Count) % _samples.Length] = sample;
                Count++;
            }

            public void RemoveFirst(int count)
            {
                count = Mathf.Clamp(count, 0, Count);
                _head = (_head + count) % _samples.Length;
                Count -= count;
                if (Count == 0) { _head = 0; }
            }

            public void Clear()
            {
                _head = 0;
                Count = 0;
            }
        }

        public struct Sample
        {
            public Vector3 Centre;
            public Vector3 Normal;
            public Vector3 Right;
            public float Travel;
            public float Born;
            public bool BreakBefore;
        }

        private readonly TrailSide _left = new TrailSide();
        private readonly TrailSide _right = new TrailSide();
        private readonly RaycastHit[] _hits = new RaycastHit[12];
        private readonly List<Vector3> _vertices = new List<Vector3>(MaximumSamplesPerSide * 4);
        private readonly List<Vector3> _normals = new List<Vector3>(MaximumSamplesPerSide * 4);
        private readonly List<Vector2> _uv = new List<Vector2>(MaximumSamplesPerSide * 4);
        private readonly List<Vector2> _birth = new List<Vector2>(MaximumSamplesPerSide * 4);
        private readonly List<Color32> _colours = new List<Color32>(MaximumSamplesPerSide * 4);
        private readonly List<int> _triangles = new List<int>(MaximumSamplesPerSide * 12);

        private BattleTank _tank;
        private Mesh _mesh;
        private MeshRenderer _renderer;
        private Material _material;
        private GameObject _host;
        private float _trackWidth = 0.35f;
        private float _halfSpacing = 0.6f;
        private string _textureResource = TextureRoot + "Other";
        private bool _configured;
        private bool _settingsEnabled;
        private bool _dirty;

        public int SampleCount { get { return _left.Count + _right.Count; } }
        public int VertexCount { get { return _mesh != null ? _mesh.vertexCount : 0; } }
        public int TriangleCount { get { return _triangles.Count / 3; } }
        public bool EnabledBySettings { get { return _settingsEnabled; } }
        public Mesh TrailMesh { get { return _mesh; } }
        public float TrackWidth { get { return _trackWidth; } }
        public float HalfSpacing { get { return _halfSpacing; } }

        /// <summary>Configures the component after the tank model and suspension
        /// have supplied their real geometry. Calling it again resets the trail.</summary>
        public void Configure(
            BattleTank tank, Nation nation, string tankName,
            float trackWidth, float halfSpacing)
        {
            _tank = tank != null ? tank : GetComponent<BattleTank>();
            _trackWidth = Mathf.Max(0.12f, trackWidth);
            _halfSpacing = Mathf.Max(_trackWidth * 0.55f, halfSpacing);
            _textureResource = TextureResource(nation, tankName);
            if (_material != null) { ApplyMaterialSource(); }

            _configured = true;
            ClearTrail();
            ApplySetting();
        }

        public static string TextureResource(Nation nation, string tankName = null)
        {
            if (!string.IsNullOrEmpty(tankName) &&
                tankName.IndexOf("oth50_stt", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return TextureRoot + "Other_Oth50_STT";
            }

            switch (nation)
            {
                case Nation.Ussr: return TextureRoot + "Ussr";
                case Nation.Germany: return TextureRoot + "Germany";
                case Nation.Usa: return TextureRoot + "Usa";
                case Nation.Gb: return TextureRoot + "Gb";
                case Nation.Japan: return TextureRoot + "Japan";
                case Nation.France: return TextureRoot + "France";
                case Nation.China: return TextureRoot + "China";
                case Nation.European: return TextureRoot + "European";
                default: return TextureRoot + "Other";
            }
        }

        /// <summary>Opacity of the oldest end of the distance-bounded trail.</summary>
        public static float DistanceOpacity(float distanceBehind)
        {
            if (distanceBehind <= DistanceFadeStart) { return 1f; }
            if (distanceBehind >= MaximumTrailDistance) { return 0f; }
            return 1f - Mathf.InverseLerp(
                DistanceFadeStart, MaximumTrailDistance, distanceBehind);
        }

        /// <summary>Hard retention predicate used by both the live ring and tests.</summary>
        public static bool Retains(
            float newestTravel, float sampleTravel, float now, float born)
        {
            return newestTravel - sampleTravel <= MaximumTrailDistance &&
                now - born <= TimeFadeEnd;
        }

        private void FixedUpdate()
        {
            if (!_configured || _tank == null) { return; }
            if (!ApplySetting()) { return; }

            var now = Time.time;
            var suspension = _tank.Suspension;
            if (suspension == null || !suspension.AnyHit || suspension.OnTank || HiddenFromPlayer())
            {
                Break(_left);
                Break(_right);
                Prune(_left, now);
                Prune(_right, now);
                SetCurrentTime(now);
                FlushIfDirty();
                return;
            }

            TickSide(_left, -1f, now);
            TickSide(_right, 1f, now);
            Prune(_left, now);
            Prune(_right, now);
            SetCurrentTime(now);
            FlushIfDirty();
        }

        /// <summary>Used by deterministic physics and GPU smoke tests without
        /// waiting for Unity's player loop.</summary>
        public void SimulateNow(float now)
        {
            if (!_configured || _tank == null || !ApplySetting()) { return; }
            var suspension = _tank.Suspension;
            if (suspension == null || !suspension.AnyHit || suspension.OnTank || HiddenFromPlayer())
            {
                Break(_left);
                Break(_right);
            }
            else
            {
                TickSide(_left, -1f, now);
                TickSide(_right, 1f, now);
            }
            Prune(_left, now);
            Prune(_right, now);
            SetCurrentTime(now);
            FlushIfDirty();
        }

        /// <summary>
        /// Uprising Shadow: an enemy of the local player in Shadow lays no new
        /// marks, so the trail cannot give the invisible tank away. The marks
        /// already on the ground fade out as usual.
        /// </summary>
        private bool HiddenFromPlayer()
        {
            return _tank.HidesTrackMarksFromLocalPlayer;
        }

        private bool ApplySetting()
        {
            var settings = GraphicsQuality.Settings;
            var enabled = settings == null || settings.GraphicsTankTreads;
            if (enabled == _settingsEnabled)
            {
                if (_renderer != null) { _renderer.enabled = enabled && SampleCount > 1; }
                return enabled;
            }

            _settingsEnabled = enabled;
            if (!enabled)
            {
                ClearTrail();
            }
            if (_renderer != null)
            {
                _renderer.enabled = enabled && SampleCount > 1;
            }
            return enabled;
        }

        private void TickSide(TrailSide side, float sign, float now)
        {
            var raw = transform.position + transform.right * (sign * _halfSpacing);
            var right = Vector3.ProjectOnPlane(transform.right, Vector3.up);
            if (right.sqrMagnitude < 0.001f) { right = side.LastRight; }
            right.Normalize();

            if (!side.HasProbe)
            {
                side.HasProbe = true;
                side.LastProbe = raw;
                side.LastRight = right;
                AddProjected(side, raw, right, side.TotalTravel, now, true);
                side.DistanceSinceSample = 0f;
                return;
            }

            var segment = raw - side.LastProbe;
            var distance = segment.magnitude;
            if (distance < 0.0001f)
            {
                side.LastRight = right;
                return;
            }

            var travelBefore = side.TotalTravel;
            side.TotalTravel += distance;
            if (distance > TeleportDistance)
            {
                side.BreakNext = true;
                side.DistanceSinceSample = 0f;
                side.LastProbe = raw;
                side.LastRight = right;
                AddProjected(side, raw, right, side.TotalTravel, now, true);
                return;
            }

            var accumulated = side.DistanceSinceSample + distance;
            var needed = SampleDistance - side.DistanceSinceSample;
            while (needed <= distance + 0.0001f)
            {
                var t = Mathf.Clamp01(needed / distance);
                var point = Vector3.Lerp(side.LastProbe, raw, t);
                var across = Vector3.Slerp(side.LastRight, right, t);
                if (across.sqrMagnitude < 0.001f) { across = right; }
                across.Normalize();
                AddProjected(side, point, across, travelBefore + needed, now, side.BreakNext);
                side.DistanceSinceSample = 0f;
                needed += SampleDistance;
            }
            side.DistanceSinceSample = accumulated % SampleDistance;
            side.LastProbe = raw;
            side.LastRight = right;
        }

        private void AddProjected(
            TrailSide side, Vector3 point, Vector3 right, float travel,
            float now, bool forceBreak)
        {
            Vector3 centre;
            Vector3 normal;
            if (!ProjectToGround(point, out centre, out normal))
            {
                side.BreakNext = true;
                return;
            }

            right = Vector3.ProjectOnPlane(right, normal);
            if (right.sqrMagnitude < 0.001f)
            {
                right = Vector3.Cross(normal, transform.forward);
            }
            if (right.sqrMagnitude < 0.001f)
            {
                side.BreakNext = true;
                return;
            }

            EnsureRenderer();
            var sample = new Sample
            {
                Centre = centre + normal * GroundLift,
                Normal = normal,
                Right = right.normalized,
                Travel = travel,
                Born = now,
                BreakBefore = forceBreak || side.BreakNext || side.Count == 0,
            };
            var wasFull = side.Count == MaximumSamplesPerSide;
            side.Add(sample);
            side.BreakNext = false;
            if (wasFull && side.Count > 0)
            {
                var first = side.Get(0);
                first.BreakBefore = true;
                side.Set(0, first);
            }
            _dirty = true;
        }

        private bool ProjectToGround(
            Vector3 point, out Vector3 centre, out Vector3 normal)
        {
            using var perfScope = BattlePerfMarkers.TrackMarkCast.Auto();
            centre = Vector3.zero;
            normal = Vector3.up;
            var origin = point + Vector3.up * ProbeHeight;
            var count = Physics.RaycastNonAlloc(
                origin, Vector3.down, _hits, ProbeDistance,
                1 << BattleLayers.Static, QueryTriggerInteraction.Ignore);
            var nearest = float.PositiveInfinity;
            var found = false;
            for (var i = 0; i < count; i++)
            {
                var hit = _hits[i];
                if (hit.collider == null || hit.normal.y < MinimumGroundNormalY ||
                    hit.distance >= nearest)
                {
                    continue;
                }
                nearest = hit.distance;
                centre = hit.point;
                normal = hit.normal.normalized;
                found = true;
            }
            return found;
        }

        private static void Break(TrailSide side)
        {
            side.HasProbe = false;
            side.DistanceSinceSample = 0f;
            side.BreakNext = true;
        }

        private void Prune(TrailSide side, float now)
        {
            var remove = 0;
            while (remove < side.Count)
            {
                var sample = side.Get(remove);
                if (Retains(side.TotalTravel, sample.Travel, now, sample.Born)) { break; }
                remove++;
            }
            if (remove <= 0) { return; }
            side.RemoveFirst(remove);
            if (side.Count > 0)
            {
                var first = side.Get(0);
                first.BreakBefore = true;
                side.Set(0, first);
            }
            _dirty = true;
        }

        private void FlushIfDirty()
        {
            if (!_dirty) { return; }
            RebuildMesh();
            _dirty = false;
        }

        private void RebuildMesh()
        {
            if (_mesh == null) { return; }
            _vertices.Clear();
            _normals.Clear();
            _uv.Clear();
            _birth.Clear();
            _colours.Clear();
            _triangles.Clear();

            Append(_left);
            Append(_right);

            _mesh.Clear(false);
            _mesh.SetVertices(_vertices);
            _mesh.SetNormals(_normals);
            _mesh.SetUVs(0, _uv);
            _mesh.SetUVs(1, _birth);
            _mesh.SetColors(_colours);
            _mesh.SetTriangles(_triangles, 0, true);
            if (_renderer != null)
            {
                _renderer.enabled = _settingsEnabled && _triangles.Count > 0;
            }
        }

        private void Append(TrailSide side)
        {
            var firstVertex = _vertices.Count;
            for (var i = 0; i < side.Count; i++)
            {
                var sample = side.Get(i);
                var half = sample.Right * (_trackWidth * 0.5f);
                var alpha = (byte)Mathf.RoundToInt(
                    255f * DistanceOpacity(side.TotalTravel - sample.Travel));
                var colour = new Color32(255, 255, 255, alpha);
                var v = sample.Travel / TextureRepeatMetres;

                _vertices.Add(sample.Centre - half);
                _vertices.Add(sample.Centre + half);
                _normals.Add(sample.Normal);
                _normals.Add(sample.Normal);
                _uv.Add(new Vector2(0f, v));
                _uv.Add(new Vector2(1f, v));
                _birth.Add(new Vector2(sample.Born, 0f));
                _birth.Add(new Vector2(sample.Born, 0f));
                _colours.Add(colour);
                _colours.Add(colour);

                if (i == 0 || sample.BreakBefore) { continue; }
                var previous = firstVertex + (i - 1) * 2;
                var current = firstVertex + i * 2;
                _triangles.Add(previous);
                _triangles.Add(current);
                _triangles.Add(previous + 1);
                _triangles.Add(previous + 1);
                _triangles.Add(current);
                _triangles.Add(current + 1);
            }
        }

        private void EnsureRenderer()
        {
            if (_host != null) { return; }
            _host = new GameObject(name + "_TrackMarks");
            _host.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            _host.transform.localScale = Vector3.one;

            var filter = _host.AddComponent<MeshFilter>();
            _renderer = _host.AddComponent<MeshRenderer>();
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            _renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

            _mesh = new Mesh { name = name + " Track Marks" };
            _mesh.MarkDynamic();
            filter.sharedMesh = _mesh;

            var shader = Resources.Load<Shader>(ShaderResource);
            if (shader == null)
            {
                Debug.LogError("[Blitz] missing tank tread shader resource " + ShaderResource);
                _renderer.enabled = false;
                return;
            }
            _material = new Material(shader) { name = name + " Track Marks" };
            ApplyMaterialSource();
            _renderer.sharedMaterial = _material;
        }

        private void ApplyMaterialSource()
        {
            if (_material == null) { return; }
            var texture = Resources.Load<Texture2D>(_textureResource);
            if (texture == null) { texture = Resources.Load<Texture2D>(TextureRoot + "Other"); }
            _material.SetTexture("_BaseMap", texture);
            _material.SetFloat("_FadeStart", TimeFadeStart);
            _material.SetFloat("_FadeEnd", TimeFadeEnd);
            _material.SetFloat("_CurrentTime", Time.time);
        }

        private void SetCurrentTime(float now)
        {
            if (_material != null) { _material.SetFloat("_CurrentTime", now); }
        }

        public void ClearTrail()
        {
            _left.Clear();
            _right.Clear();
            Break(_left);
            Break(_right);
            _left.TotalTravel = 0f;
            _right.TotalTravel = 0f;
            _dirty = false;
            _vertices.Clear();
            _normals.Clear();
            _uv.Clear();
            _birth.Clear();
            _colours.Clear();
            _triangles.Clear();
            if (_mesh != null) { _mesh.Clear(false); }
            if (_renderer != null) { _renderer.enabled = false; }
        }

        private void OnDestroy()
        {
            DestroySafely(_material);
            DestroySafely(_mesh);
            DestroySafely(_host);
            _material = null;
            _mesh = null;
            _host = null;
        }

        private static void DestroySafely(UnityEngine.Object value)
        {
            if (value == null) { return; }
            if (Application.isPlaying) { Destroy(value); }
            else { DestroyImmediate(value); }
        }
    }
}
