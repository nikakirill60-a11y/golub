using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Runtime.Battle.CombatFx
{
    /// <summary>
    /// One instanced <c>ParticleStarter</c> scene: its <c>GPUParticles3D</c>
    /// children simulated by <see cref="GodotParticleEmitter"/> and drawn as
    /// one world-space mesh with a submesh per emitter, ordered the way the
    /// mobile renderer sorts the transparent instances.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GodotParticleEffect : MonoBehaviour
    {
        private static readonly Dictionary<GodotParticleMaterial, Material> MaterialCache =
            new Dictionary<GodotParticleMaterial, Material>();

        public const string ShaderName = "BlitzLegacy/Battle/Godot Particle";

        private static Shader _shader;

        /// <summary>When set, every effect draws from this camera instead of <c>Camera.main</c>.</summary>
        public static Camera ViewOverride;

        /// <summary>Forces the effects on without a graphics device (EditMode tests).</summary>
        public static bool ForceEnabled;

        private readonly GodotFxGeometry _geometry = new GodotFxGeometry();
        private readonly List<List<int>> _indices = new List<List<int>>();
        private readonly List<int> _order = new List<int>();
        private readonly List<float> _depth = new List<float>();
        private readonly List<Material> _materials = new List<Material>();
        private Material[] _assigned = new Material[0];

        private GodotEffectData _data;
        private GodotParticleEmitter[] _emitters;
        private Mesh _mesh;
        private MeshFilter _filter;
        private MeshRenderer _renderer;
        private GodotXform _root;
        private bool _started;

        /// <summary>Re-reads the Godot transform of the scene root every frame, if set.</summary>
        public Func<GodotXform> RootProvider;

        /// <summary>Hides and freezes the effect while it returns true, like a hidden Godot parent.</summary>
        public Func<bool> Concealed;

        /// <summary>Whether combat effects can draw at all in this player.</summary>
        public static bool Enabled
        {
            get { return ForceEnabled || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null; }
        }

        public GodotEffectData Data { get { return _data; } }

        public IReadOnlyList<GodotParticleEmitter> Emitters { get { return _emitters; } }

        /// <summary>The Godot global transform of the scene root.</summary>
        public GodotXform Root
        {
            get { return _root; }
            set { _root = value; }
        }

        public Mesh Mesh { get { return _mesh; } }

        /// <summary>Creates the effect object for a scene; null without a scene or graphics.</summary>
        public static GodotParticleEffect Create(GodotEffectData data, string name)
        {
            if (data == null || !Enabled)
            {
                return null;
            }

            var go = new GameObject(name);
            var effect = go.AddComponent<GodotParticleEffect>();
            effect.Initialise(data);
            return effect;
        }

        private void Initialise(GodotEffectData data)
        {
            _data = data;
            _root = data.Root;
            _emitters = new GodotParticleEmitter[data.Emitters.Count];
            for (var i = 0; i < _emitters.Length; i++)
            {
                _emitters[i] = new GodotParticleEmitter(data.Emitters[i]);
                _indices.Add(new List<int>());
            }

            _mesh = new Mesh { name = data.Name };
            _mesh.MarkDynamic();
            _filter = gameObject.AddComponent<MeshFilter>();
            _filter.sharedMesh = _mesh;
            _renderer = gameObject.AddComponent<MeshRenderer>();
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            _renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            _renderer.enabled = false;
        }

        /// <summary>
        /// <c>ParticleStarter.start(self_destroy, normal)</c>: the root's Y
        /// axis turns to the normal, every emitter restarts with a fresh seed
        /// and a self-destroying effect frees itself a second after the
        /// longest lifetime.
        /// </summary>
        public void Play(bool selfDestroy, Vector3? godotNormal = null)
        {
            Play(selfDestroy, godotNormal, null);
        }

        /// <summary>
        /// <see cref="Play(bool, Vector3?)"/> with the seed of every emitter
        /// chosen by <paramref name="seedFor"/> (<c>use_fixed_seed</c> on
        /// the Godot side), for reference renders.
        /// </summary>
        public void Play(bool selfDestroy, Vector3? godotNormal, Func<int, uint> seedFor)
        {
            if (godotNormal.HasValue && godotNormal.Value != Vector3.up)
            {
                _root.Y = godotNormal.Value.normalized;
            }

            for (var i = 0; i < _emitters.Length; i++)
            {
                var seed = seedFor != null
                    ? seedFor(i)
                    : (uint)UnityEngine.Random.Range(int.MinValue, int.MaxValue);
                _emitters[i].Restart(seed);
            }

            _started = true;
            if (selfDestroy && Application.isPlaying)
            {
                Destroy(gameObject, _data.MaxLifetime + CombatFxManifestDefaults.SelfDestroyExtra());
            }
        }

        /// <summary>The Godot global transform of one emitter.</summary>
        public GodotXform EmitterGlobal(int index)
        {
            return _root * _emitters[index].Data.Local;
        }

        private void LateUpdate()
        {
            var camera = ViewOverride != null ? ViewOverride : Camera.main;
            Tick(Time.deltaTime, camera != null ? camera.transform : null);
        }

        /// <summary>One render frame: advance every emitter and rebuild the mesh.</summary>
        public void Tick(float deltaTime, Transform camera)
        {
            if (_data == null)
            {
                return;
            }

            if (Concealed != null && Concealed())
            {
                _renderer.enabled = false;
                return;
            }

            // An effect that has not played yet, or whose particles all died,
            // simulates and draws nothing: skip the per-frame root, emitter
            // and mesh work (a persistent shot effect idles like this between
            // shots). Play() sets the root again before the next start.
            if (!_started || AllDormant())
            {
                _renderer.enabled = false;
                return;
            }

            if (RootProvider != null)
            {
                _root = RootProvider();
            }

            for (var i = 0; i < _emitters.Length; i++)
            {
                _emitters[i].Advance(deltaTime, EmitterGlobal(i));
            }

            if (camera == null)
            {
                _renderer.enabled = false;
                return;
            }

            Rebuild(GodotView.FromUnity(camera), camera);
        }

        /// <summary>Whether every emitter is dormant (see <see cref="GodotParticleEmitter.Dormant"/>).</summary>
        public bool AllDormant()
        {
            for (var i = 0; i < _emitters.Length; i++)
            {
                if (!_emitters[i].Dormant)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Orders draw indices back to front by depth, equal depths keeping
        /// the index order. An insertion sort: the lists hold a handful of
        /// emitters and, unlike a comparison lambda, it allocates nothing.
        /// </summary>
        public static void SortBackToFront(List<int> order, List<float> depth)
        {
            for (var i = 1; i < order.Count; i++)
            {
                var item = order[i];
                var d = depth[item];
                var j = i - 1;
                while (j >= 0 && (depth[order[j]] < d || (depth[order[j]] == d && order[j] > item)))
                {
                    order[j + 1] = order[j];
                    j--;
                }

                order[j + 1] = item;
            }
        }

        private void Rebuild(GodotView view, Transform camera)
        {
            _geometry.Clear();
            _order.Clear();
            _depth.Clear();
            var cameraPosition = camera.position;
            var cameraForward = camera.forward;
            for (var i = 0; i < _emitters.Length; i++)
            {
                var emitter = _emitters[i];
                var indices = _indices[i];
                indices.Clear();
                // A root whose Y axis was turned into its own XZ plane (a
                // horizontal hit normal) is singular: the copy pass divides
                // by the determinant and the instance draws nothing.
                if (!emitter.Drawn || GodotFxMath.Determinant(EmitterGlobal(i)) == 0f)
                {
                    _depth.Add(0f);
                    continue;
                }

                var global = EmitterGlobal(i);
                var first = _geometry.Positions.Count;
                emitter.AppendGeometry(view, global, _geometry, indices);
                var center = GodotFxMath.Mirror(emitter.SortCenter(global, CombatFxManifestDefaults.AabbExtent()));
                _depth.Add(Vector3.Dot(center - cameraPosition, cameraForward) - emitter.Data.SortingOffset);
                if (_geometry.Positions.Count > first)
                {
                    _order.Add(i);
                }
            }

            if (_order.Count == 0)
            {
                _renderer.enabled = false;
                return;
            }

            // Back to front; equal depths keep the scene order.
            SortBackToFront(_order, _depth);

            _mesh.Clear();
            _mesh.SetVertices(_geometry.Positions);
            _mesh.SetUVs(0, _geometry.Uvs);
            _mesh.SetColors(_geometry.Colors);
            _mesh.subMeshCount = _order.Count;
            _materials.Clear();
            for (var s = 0; s < _order.Count; s++)
            {
                var index = _order[s];
                _mesh.SetTriangles(_indices[index], s, false);
                _materials.Add(MaterialFor(_emitters[index].Data.Material));
            }

            _mesh.RecalculateBounds();
            if (!SameMaterials())
            {
                _assigned = _materials.ToArray();
                _renderer.sharedMaterials = _assigned;
            }

            transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            transform.localScale = Vector3.one;
            _renderer.enabled = true;
        }

        private bool SameMaterials()
        {
            if (_assigned.Length != _materials.Count)
            {
                return false;
            }

            for (var i = 0; i < _assigned.Length; i++)
            {
                if (_assigned[i] != _materials[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The shared Unity material of one Godot StandardMaterial3D.</summary>
        public static Material MaterialFor(GodotParticleMaterial source)
        {
            Material material;
            if (MaterialCache.TryGetValue(source, out material) && material != null)
            {
                return material;
            }

            if (_shader == null)
            {
                _shader = Shader.Find(ShaderName);
                if (_shader == null)
                {
                    Debug.LogError("[Blitz] combat fx shader is missing: " + ShaderName);
                    return null;
                }
            }

            material = new Material(_shader) { name = "GodotParticle" };
            Texture2D texture = null;
            var manifest = CombatFxManifest.Shared;
            var path = manifest != null ? manifest.TexturePath(source.Texture) : null;
            if (path != null)
            {
                texture = Resources.Load<Texture2D>(path);
                if (texture == null)
                {
                    Debug.LogError("[Blitz] combat fx texture is missing: " + path);
                }
            }

            material.SetTexture("_MainTex", texture != null ? texture : Texture2D.whiteTexture);
            material.SetVector("_Albedo", source.Albedo);
            material.SetFloat("_UseAlpha", source.Transparent ? 1f : 0f);
            material.SetFloat("_VertexColor", source.VertexColor ? 1f : 0f);
            material.SetFloat("_VertexSrgb", source.VertexColorSrgb ? 1f : 0f);
            material.SetFloat("_Fog", source.Fog ? 1f : 0f);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)(source.BlendAdd ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
            SetKeyword(material, "_GODOT_NO_MIPMAPS", !source.Mipmaps);
            SetKeyword(material, "_GODOT_CLAMP", !source.Repeat);
            material.renderQueue = (int)RenderQueue.Transparent;
            MaterialCache[source] = material;
            return material;
        }

        private static void SetKeyword(Material material, string keyword, bool on)
        {
            if (on)
            {
                material.EnableKeyword(keyword);
            }
            else
            {
                material.DisableKeyword(keyword);
            }
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

    /// <summary>The ParticleStarter constants, read from the manifest when it is loaded.</summary>
    internal static class CombatFxManifestDefaults
    {
        internal static float AabbExtent()
        {
            var manifest = CombatFxManifest.Shared;
            return manifest != null ? manifest.StarterAabbExtent : 10000f;
        }

        internal static float SelfDestroyExtra()
        {
            var manifest = CombatFxManifest.Shared;
            return manifest != null ? manifest.StarterSelfDestroyExtra : 1f;
        }
    }
}
