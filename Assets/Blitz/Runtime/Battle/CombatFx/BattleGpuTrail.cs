using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Runtime.Battle.CombatFx
{
    /// <summary>
    /// One <c>GPUTrail3D</c> of <c>trail_preset.tscn</c>: 45 particles run by
    /// <c>trail.gdshader</c> at a fixed 60 fps, each caching the emitter's
    /// cross-section line for one step, drawn as quads by
    /// <c>trail_draw_pass.gdshader</c>.
    /// </summary>
    public sealed class GpuTrailEmitter
    {
        private readonly int _amount;
        private readonly float _frameTime;
        private readonly Vector3[] _t0, _t1, _t2, _t3;
        private readonly float[] _w;
        private float _remainder;
        private bool _clear = true;

        /// <summary>The trail-node-relative transform (rotate_z, then the uniform scale).</summary>
        public GodotXform Local;

        /// <summary>Set once the emitter went top_level: its global transform no longer follows the trail.</summary>
        public GodotXform? Detached;

        /// <summary>Seconds since it was detached, for the ricochet free timer.</summary>
        public float DetachedAge;

        public GpuTrailEmitter(int amount, int fixedFps, GodotXform local)
        {
            _amount = amount;
            _frameTime = 1f / fixedFps;
            Local = local;
            _t0 = new Vector3[amount];
            _t1 = new Vector3[amount];
            _t2 = new Vector3[amount];
            _t3 = new Vector3[amount];
            _w = new float[amount];
        }

        public int Amount { get { return _amount; } }

        /// <summary><c>CUSTOM.w</c> of one particle.</summary>
        public float Place(int index) { return _w[index]; }

        public Vector3 Column(int index, int column)
        {
            switch (column)
            {
                case 0: return _t0[index];
                case 1: return _t1[index];
                case 2: return _t2[index];
                default: return _t3[index];
            }
        }

        /// <summary>The emitter's Godot global transform for a trail node transform.</summary>
        public GodotXform Global(GodotXform trail)
        {
            return Detached.HasValue ? Detached.Value : trail * Local;
        }

        /// <summary>The fixed-fps clock of update_particles for one render frame.</summary>
        public void Advance(float frameDelta, GodotXform emission)
        {
            var delta = frameDelta > 0.1f ? 0.1f : frameDelta < 0f ? 0f : frameDelta;
            var todo = _remainder + delta;
            while (todo >= _frameTime || _clear)
            {
                Step(emission);
                _clear = false;
                todo -= _frameTime;
            }

            _remainder = todo;
        }

        /// <summary>One <c>process()</c> of trail.gdshader over every particle.</summary>
        public void Step(GodotXform emission)
        {
            var a = emission.Point(new Vector3(0f, 1f, 0f));
            var b = emission.Point(new Vector3(0f, -1f, 0f));
            var amount = (float)_amount;
            for (var i = 0; i < _amount; i++)
            {
                if (_w[i] == 0f)
                {
                    _w[i] = i + 1f;
                    Cache(i, a, b);
                }

                if (_w[i] == amount + 1f)
                {
                    _w[i] = 1f;
                }

                if (_w[i] == 1f)
                {
                    Cache(i, a, b);
                }

                if (_w[i] == 2f)
                {
                    _t1[i] = a;
                    _t2[i] = b;
                }

                _w[i] += 1f;
            }
        }

        private void Cache(int i, Vector3 a, Vector3 b)
        {
            _t0[i] = a;
            _t1[i] = a;
            _t2[i] = b;
            _t3[i] = b;
        }

        /// <summary>The draw pass vertex stage for every particle, in index order.</summary>
        public void AppendGeometry(GodotCurve curve, GodotFxGeometry geometry, List<int> indices)
        {
            var white = new Vector4(1f, 1f, 1f, 1f);
            var span = _amount - 1f;
            for (var p = 0; p < _amount; p++)
            {
                var first = geometry.Positions.Count;
                // QuadMesh vertex order with UV = (1 - i, 1 - j).
                for (var j = 0; j < 2; j++)
                {
                    for (var i = 0; i < 2; i++)
                    {
                        var u = 1f - i;
                        var v = 1f - j;
                        var top = Vector3.LerpUnclamped(_t1[p], _t0[p], u);
                        var bottom = Vector3.LerpUnclamped(_t2[p], _t3[p], u);
                        var x = (u + _w[p] - 1f - 2f) / span;
                        var h = curve.Sample(x);
                        var position = Vector3.LerpUnclamped(top, bottom, (v - 0.5f) * h + 0.5f);
                        geometry.Add(position, new Vector2(x, v), white);
                    }
                }

                GodotFxGeometry.AddQuadIndices(indices, first);
            }
        }
    }

    /// <summary>
    /// <c>shell.trail</c>: the top-level Node3D <c>Projectile.create</c>
    /// hangs two GPUTrail3D emitters on (rotated 0 and 90 degrees about the
    /// flight axis), which follows the shell every physics frame and is freed
    /// two seconds after the shell.
    /// </summary>
    public sealed class BattleGpuTrail : MonoBehaviour
    {
        public const string ShaderName = "BlitzLegacy/Battle/Gpu Trail";

        private static readonly Dictionary<bool, Texture2D> Ramps = new Dictionary<bool, Texture2D>();
        private static readonly Dictionary<bool, Material> Materials = new Dictionary<bool, Material>();

        private readonly List<GpuTrailEmitter> _emitters = new List<GpuTrailEmitter>();
        private readonly GodotFxGeometry _geometry = new GodotFxGeometry();
        private readonly List<int> _indices = new List<int>();
        private readonly List<int> _order = new List<int>();
        private readonly List<float> _depth = new List<float>();
        private readonly List<int> _scratch = new List<int>();

        private CombatFxManifest _manifest;
        private GodotXform _trail;
        private Mesh _mesh;
        private MeshRenderer _renderer;
        private float _freeAt = -1f;

        /// <summary>Hides and freezes the trail while true (the owner tank is hidden).</summary>
        public System.Func<bool> Concealed;

        /// <summary>HEAT rounds draw the heat colour ramp.</summary>
        public bool Heat { get; private set; }

        public int Caliber { get; private set; }

        /// <summary>The trail node's Godot global transform.</summary>
        public GodotXform TrailTransform { get { return _trail; } }

        public IReadOnlyList<GpuTrailEmitter> Emitters { get { return _emitters; } }

        public Mesh Mesh { get { return _mesh; } }

        /// <summary>Whether any segment has opened up (its two cached lines differ).</summary>
        public bool HasVisibleSegment()
        {
            for (var e = 0; e < _emitters.Count; e++)
            {
                var emitter = _emitters[e];
                for (var i = 0; i < emitter.Amount; i++)
                {
                    if ((emitter.Column(i, 0) - emitter.Column(i, 1)).sqrMagnitude > 0f)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Whether the shell is gone and the trail is waiting to be freed.</summary>
        public bool Released { get { return _freeAt >= 0f; } }

        /// <summary>
        /// <c>Projectile.create</c> with a trail: the node at
        /// <paramref name="start"/>, looking at <paramref name="lookTarget"/>
        /// (Unity space), with the calibre-sized emitter pair.
        /// </summary>
        public static BattleGpuTrail Create(int caliber, bool heat, Vector3 start, Vector3 lookTarget)
        {
            var manifest = CombatFxManifest.Shared;
            if (manifest == null || !GodotParticleEffect.Enabled)
            {
                return null;
            }

            var go = new GameObject("shell_trail");
            var trail = go.AddComponent<BattleGpuTrail>();
            trail.Initialise(manifest, caliber, heat, start, lookTarget);
            return trail;
        }

        private void Initialise(CombatFxManifest manifest, int caliber, bool heat, Vector3 start, Vector3 lookTarget)
        {
            _manifest = manifest;
            Caliber = caliber;
            Heat = heat;
            var origin = GodotFxMath.Mirror(start);
            _trail = GodotFxMath.LookingAt(origin, GodotFxMath.Mirror(lookTarget));
            _mesh = new Mesh { name = "shell_trail" };
            _mesh.MarkDynamic();
            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = gameObject.AddComponent<MeshRenderer>();
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            _renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            _renderer.sharedMaterial = MaterialFor(manifest, heat);
            _renderer.enabled = false;
            AddEmitterPair(manifest.TrailScale(caliber));
        }

        /// <summary><c>add_trail_emitter_to_node</c>.</summary>
        private void AddEmitterPair(float scale)
        {
            var angles = new[] { _manifest.TrailAngleFirst, _manifest.TrailAngleSecond };
            for (var i = 0; i < angles.Length; i++)
            {
                var local = GodotFxMath.RotationZ(angles[i]).Scaled(scale);
                _emitters.Add(new GpuTrailEmitter(_manifest.Trail.Amount, _manifest.Trail.FixedFps, local));
            }
        }

        /// <summary><c>trail.global_position = self.global_position</c>.</summary>
        public void Follow(Vector3 position)
        {
            _trail.Origin = GodotFxMath.Mirror(position);
        }

        /// <summary>
        /// <c>ricoshet</c>: the emitters drawn so far go top-level where the
        /// shell bounced (and are freed three seconds later); a fresh pair,
        /// sized from the shell sprite, carries on with the trail.
        /// </summary>
        public void Ricochet(Vector3 position)
        {
            var origin = GodotFxMath.Mirror(position);
            for (var i = 0; i < _emitters.Count; i++)
            {
                var emitter = _emitters[i];
                if (emitter.Detached.HasValue)
                {
                    continue;
                }

                var global = _trail * emitter.Local;
                global.Origin = origin;
                emitter.Detached = global;
                emitter.DetachedAge = 0f;
            }

            AddEmitterPair(_manifest.ShellPixelSize(Caliber) * _manifest.RicochetTrailPixelFactor);
        }

        /// <summary>The shell left the tree: free the trail after <c>Util.timer(2, ...)</c>.</summary>
        public void Release()
        {
            if (_freeAt >= 0f)
            {
                return;
            }

            _freeAt = Time.time + _manifest.TrailFreeDelay;
        }

        private void LateUpdate()
        {
            if (_freeAt >= 0f && Time.time >= _freeAt)
            {
                Destroy(gameObject);
                return;
            }

            var camera = GodotParticleEffect.ViewOverride != null ? GodotParticleEffect.ViewOverride : Camera.main;
            Tick(Time.deltaTime, camera != null ? camera.transform : null);
        }

        /// <summary>One render frame: step every emitter and rebuild the ribbon mesh.</summary>
        public void Tick(float deltaTime, Transform camera)
        {
            if (Concealed != null && Concealed())
            {
                _renderer.enabled = false;
                return;
            }

            for (var i = _emitters.Count - 1; i >= 0; i--)
            {
                var emitter = _emitters[i];
                if (emitter.Detached.HasValue)
                {
                    emitter.DetachedAge += deltaTime;
                    if (emitter.DetachedAge >= _manifest.RicochetTrailFreeDelay)
                    {
                        _emitters.RemoveAt(i);
                        continue;
                    }
                }

                emitter.Advance(deltaTime, emitter.Global(_trail));
            }

            Rebuild(camera);
        }

        private void Rebuild(Transform camera)
        {
            _geometry.Clear();
            _indices.Clear();
            _order.Clear();
            _depth.Clear();
            for (var i = 0; i < _emitters.Count; i++)
            {
                _order.Add(i);
                var center = GodotFxMath.Mirror(_emitters[i].Global(_trail).Origin);
                _depth.Add(camera != null ? Vector3.Dot(center - camera.position, camera.forward) : 0f);
            }

            // Each GPUTrail3D is its own transparent instance, sorted back to front.
            GodotParticleEffect.SortBackToFront(_order, _depth);

            for (var k = 0; k < _order.Count; k++)
            {
                _scratch.Clear();
                _emitters[_order[k]].AppendGeometry(_manifest.Trail.Curve, _geometry, _scratch);
                _indices.AddRange(_scratch);
            }

            _mesh.Clear();
            if (_indices.Count == 0)
            {
                _renderer.enabled = false;
                return;
            }

            _mesh.SetVertices(_geometry.Positions);
            _mesh.SetUVs(0, _geometry.Uvs);
            _mesh.SetColors(_geometry.Colors);
            _mesh.SetTriangles(_indices, 0, true);
            transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            transform.localScale = Vector3.one;
            _renderer.enabled = true;
        }

        /// <summary>
        /// <c>get_color_ramp(heat)</c>: a GradientTexture1D (256 texels, RGBA8)
        /// from the trail colour to the same colour at zero alpha.
        /// </summary>
        public static Color32[] RampTexels(Vector4 color)
        {
            const int width = 256;
            var texels = new Color32[width];
            for (var i = 0; i < width; i++)
            {
                var t = (float)i / (width - 1);
                var r = color.x;
                var g = color.y;
                var b = color.z;
                var a = color.w + (0f - color.w) * t;
                texels[i] = new Color32(Byte8(r), Byte8(g), Byte8(b), Byte8(a));
            }

            return texels;
        }

        /// <summary><c>Color.get_r8()</c>: <c>round(r * 255)</c>, clamped.</summary>
        private static byte Byte8(float value)
        {
            var v = System.Math.Round(value * 255.0f, System.MidpointRounding.AwayFromZero);
            return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        }

        private static Material MaterialFor(CombatFxManifest manifest, bool heat)
        {
            Material material;
            if (Materials.TryGetValue(heat, out material) && material != null)
            {
                return material;
            }

            var shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                Debug.LogError("[Blitz] combat fx shader is missing: " + ShaderName);
                return null;
            }

            material = new Material(shader) { name = heat ? "GpuTrailHeat" : "GpuTrail" };
            var path = manifest.TexturePath(manifest.Trail.Texture);
            var texture = path != null ? Resources.Load<Texture2D>(path) : null;
            if (texture == null)
            {
                Debug.LogError("[Blitz] combat fx trail texture is missing: " + path);
            }

            material.SetTexture("_MainTex", texture != null ? texture : Texture2D.whiteTexture);
            material.SetTexture("_Ramp", RampFor(manifest, heat));
            material.renderQueue = (int)RenderQueue.Transparent;
            Materials[heat] = material;
            return material;
        }

        private static Texture2D RampFor(CombatFxManifest manifest, bool heat)
        {
            Texture2D ramp;
            if (Ramps.TryGetValue(heat, out ramp) && ramp != null)
            {
                return ramp;
            }

            ramp = new Texture2D(256, 1, TextureFormat.RGBA32, false, false)
            {
                name = heat ? "GpuTrailRampHeat" : "GpuTrailRamp",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };
            ramp.SetPixels32(RampTexels(heat ? manifest.TrailColorHeat : manifest.TrailColor));
            ramp.Apply(false, true);
            Ramps[heat] = ramp;
            return ramp;
        }

        private void OnDestroy()
        {
            if (_mesh != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(_mesh);
                }
                else
                {
                    DestroyImmediate(_mesh);
                }
            }
        }
    }
}
