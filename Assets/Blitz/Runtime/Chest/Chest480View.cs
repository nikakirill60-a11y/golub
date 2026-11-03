using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Chest
{
    /// <summary>
    /// The 4.8.0 ChestUsage UI3DView: renders the chest scene off-screen and
    /// shows it in a RawImage. The background, the six animated box parts and
    /// the particles are drawn into a linear (non-sRGB) RenderTexture, so all
    /// shading and blending happen on raw gamma values exactly like DAVA's
    /// renderer; <c>BlitzLegacy/Chest480/Present</c> decodes the result once
    /// for the linear project. Nothing here touches the main camera or URP's
    /// frame: the scene is drawn with one CommandBuffer when it changed.
    /// </summary>
    [RequireComponent(typeof(RawImage))]
    public sealed class Chest480View : MonoBehaviour
    {
        public const string UnlitShader = "BlitzLegacy/Chest480/Unlit";
        public const string BoxShader = "BlitzLegacy/Chest480/Box";
        public const string ParticleShader = "BlitzLegacy/Chest480/Particle";
        public const string PresentShader = "BlitzLegacy/Chest480/Present";
        public const int MaxTextureSize = 2560;
        private const int AlphaBlending = 1;
        private const int AdditiveBlending = 3;
        private static readonly int MatrixVP = Shader.PropertyToID("_Chest480MatrixVP");
        private static readonly int DarknessId = Shader.PropertyToID("_Darkness");

        private static Mesh[] _sharedMeshes;

        private RawImage _image;
        private Chest480Definition _definition;
        private Chest480Particles _particles;
        private RenderTexture _target;
        private CommandBuffer _commands;
        private Material _present;
        private Material _unlit;
        private Material _box;
        private readonly Dictionary<int, Material> _particleMaterials = new Dictionary<int, Material>();
        private Texture2D[] _sprites;
        private Mesh _particleMesh;
        private readonly float[] _partWorld = new float[16];
        private readonly Chest480ParticleMesh _builder = new Chest480ParticleMesh();
        private float _darkness;
        private bool _dirty = true;
        private bool _failed;
        private Vector2Int _renderedSize;

        public Chest480Particles Particles { get { return _particles; } }
        public Chest480Definition Definition { get { return _definition; } }
        public float SceneTime { get { return _particles == null ? 0f : _particles.SceneTime; } }
        public RawImage Image { get { return _image; } }
        /// <summary>True once a frame was actually rendered (false without a GPU).</summary>
        public bool HasRendered { get; private set; }

        /// <summary>Reward-screen dimming in gamma space, 0..1.</summary>
        public float Darkness
        {
            get { return _darkness; }
            set
            {
                _darkness = Mathf.Clamp01(value);
                if (_present != null) _present.SetFloat(DarknessId, _darkness);
            }
        }

        public static Chest480View Create(string name, RectTransform parent)
        {
            var node = new GameObject(name, typeof(RectTransform));
            node.layer = parent != null ? parent.gameObject.layer : 5;
            var rect = (RectTransform)node.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            var image = node.AddComponent<RawImage>();
            image.raycastTarget = false;
            image.color = Color.white;
            var view = node.AddComponent<Chest480View>();
            view.Initialise(image);
            return view;
        }

        private void Initialise(RawImage image)
        {
            _image = image;
            _definition = Chest480Definition.Load();
            _particles = new Chest480Particles(_definition, 1u, 1f);
            var shader = Shader.Find(PresentShader);
            if (shader != null)
            {
                _present = new Material(shader) { name = "chest480_present", hideFlags = HideFlags.DontSave };
                _present.SetFloat(DarknessId, _darkness);
                _image.material = _present;
            }
            _image.texture = Texture2D.blackTexture;
        }

        /// <summary>Start the scene from t = 0 (a new opening).</summary>
        public void Play(uint seed, float grade)
        {
            _particles.Restart(seed, grade);
            _dirty = true;
        }

        public void Advance(float seconds)
        {
            if (seconds <= 0f) return;
            _particles.Advance(seconds);
            _dirty = true;
        }

        /// <summary>Simulate up to <paramref name="time"/> when the scene is behind it (skip).</summary>
        public void AdvanceTo(float time)
        {
            if (time > _particles.SceneTime) Advance(time - _particles.SceneTime);
        }

        /// <summary>Row-major DAVA world matrix of a box part at the current scene time.</summary>
        public float[] PartWorld(int part)
        {
            return Chest480Animation.World(_definition.parts[part], _particles.SceneTime);
        }

        /// <summary>
        /// Where the HP_gui reward locator projects, in normalized image
        /// coordinates (0,0 bottom-left .. 1,1 top-right) for the given aspect.
        /// </summary>
        public Vector2 ProjectHardpoint(float aspect)
        {
            return Project(DavaVector3.From(_definition.hardpoint), aspect);
        }

        public Vector2 Project(DavaVector3 point, float aspect)
        {
            var clip = Chest480Projection.Projection(_definition.camera, aspect) * Chest480Projection.View(_definition.camera)
                * new Vector4(point.x, point.z, point.y, 1f);
            if (Mathf.Abs(clip.w) < 1e-6f) return new Vector2(0.5f, 0.5f);
            return new Vector2(clip.x / clip.w * 0.5f + 0.5f, clip.y / clip.w * 0.5f + 0.5f);
        }

        private void OnEnable()
        {
            Canvas.willRenderCanvases += OnWillRenderCanvases;
            _dirty = true;
        }

        private void OnDisable()
        {
            Canvas.willRenderCanvases -= OnWillRenderCanvases;
        }

        private void OnRectTransformDimensionsChange()
        {
            _dirty = true;
        }

        private void OnWillRenderCanvases()
        {
            if (!_dirty || _failed || _image == null || !isActiveAndEnabled) return;
            RenderNow();
        }

        /// <summary>Render the current frame immediately (also used by captures).</summary>
        public void RenderNow()
        {
            if (_failed || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            var size = TargetSize();
            if (size.x < 2 || size.y < 2) return;
            try
            {
                if (!EnsureResources(size)) return;
                Draw(size);
                _dirty = false;
                HasRendered = true;
            }
            catch (Exception exception)
            {
                // Never take the menu down with a broken scene: show black.
                _failed = true;
                Debug.LogError("[Blitz] chest scene rendering failed: " + exception);
            }
        }

        private Vector2Int TargetSize()
        {
            var rect = ((RectTransform)transform).rect;
            var canvas = _image.canvas;
            var scale = canvas != null ? canvas.scaleFactor : 1f;
            var w = rect.width * scale;
            var h = rect.height * scale;
            var longest = Mathf.Max(w, h);
            if (longest > MaxTextureSize)
            {
                w *= MaxTextureSize / longest;
                h *= MaxTextureSize / longest;
            }
            return new Vector2Int(Mathf.RoundToInt(w), Mathf.RoundToInt(h));
        }

        private bool EnsureResources(Vector2Int size)
        {
            if (_present == null) { Fail("missing shader " + PresentShader); return false; }
            if (_unlit == null)
            {
                _unlit = CreateMaterial(UnlitShader);
                _box = CreateMaterial(BoxShader);
                if (_unlit == null || _box == null) return false;
                _unlit.mainTexture = LoadTexture(_definition.background.texture.resource);
                var box = _definition.box;
                _box.mainTexture = LoadTexture(box.albedo.resource);
                _box.SetTexture("_NormalTex", LoadTexture(box.normal.resource));
                _box.SetFloat("_Glossiness", box.glossiness);
                _box.SetFloat("_Specularity", box.specularity);
                _box.SetFloat("_NormalScale", box.normalScale);
                _box.SetVector("_Fresnel", new Vector4(box.fresnel[0], box.fresnel[1], box.fresnel[2], 0f));
                var light = _definition.light;
                _box.SetVector("_LightPos", DavaVector3.From(light.position).ToUnity());
                _box.SetVector("_LightColor", new Vector4(light.color[0], light.color[1], light.color[2], 0f));
                _box.SetVector("_Ambient", new Vector4(light.ambient[0], light.ambient[1], light.ambient[2], 0f));
                _box.SetVector("_CameraPos", DavaVector3.From(_definition.camera.position).ToUnity());
                _sprites = new Texture2D[_definition.sprites.Length];
                for (var i = 0; i < _sprites.Length; i++) _sprites[i] = LoadTexture(_definition.sprites[i].frames[0]);
            }
            if (_sharedMeshes == null || Array.Exists(_sharedMeshes, mesh => mesh == null))
            {
                var bytes = Resources.Load<TextAsset>(_definition.meshResource);
                if (bytes == null) { Fail("missing " + _definition.meshResource); return false; }
                _sharedMeshes = Chest480Definition.ReadMeshes(bytes.bytes);
                foreach (var mesh in _sharedMeshes) mesh.hideFlags = HideFlags.DontSave;
            }
            if (_particleMesh == null)
            {
                _particleMesh = new Mesh { name = "chest480_particles", hideFlags = HideFlags.DontSave };
                _particleMesh.MarkDynamic();
            }
            if (_target == null || _renderedSize != size)
            {
                ReleaseTarget();
                var descriptor = new RenderTextureDescriptor(size.x, size.y, RenderTextureFormat.ARGB32, 24)
                {
                    sRGB = false,
                    msaaSamples = 4,
                    useMipMap = false,
                };
                _target = new RenderTexture(descriptor) { name = "chest480_scene", hideFlags = HideFlags.DontSave };
                if (!_target.Create()) { Fail("cannot create the scene render texture"); return false; }
                _renderedSize = size;
                _image.texture = _target;
            }
            if (_commands == null) _commands = new CommandBuffer { name = "Chest480" };
            return true;
        }

        private void Draw(Vector2Int size)
        {
            var aspect = size.x / (float)size.y;
            var camera = _definition.camera;
            var projection = GL.GetGPUProjectionMatrix(Chest480Projection.Projection(camera, aspect), true);
            var cmd = _commands;
            cmd.Clear();
            cmd.SetRenderTarget(_target);
            cmd.ClearRenderTarget(true, true, Color.black);
            cmd.SetGlobalMatrix(MatrixVP, projection * Chest480Projection.View(camera));
            cmd.DrawMesh(_sharedMeshes[_definition.background.mesh], Matrix4x4.identity, _unlit, 0, 0);
            foreach (var part in _definition.parts)
            {
                Chest480Animation.World(part, _particles.SceneTime, _partWorld);
                cmd.DrawMesh(_sharedMeshes[part.mesh], Chest480Animation.ToUnity(_partWorld), _box, 0, 0);
            }
            _builder.Build(_particles, camera);
            _builder.Apply(_particleMesh);
            for (var i = 0; i < _builder.BatchCount; i++)
            {
                var batch = _builder.Batch(i);
                var material = ParticleMaterial(batch.Sprite, batch.Blending);
                if (material != null) cmd.DrawMesh(_particleMesh, Matrix4x4.identity, material, i, 0);
            }
            Graphics.ExecuteCommandBuffer(cmd);
        }

        private Material ParticleMaterial(int sprite, int blending)
        {
            var key = sprite * 8 + blending;
            Material material;
            if (_particleMaterials.TryGetValue(key, out material)) return material;
            material = CreateMaterial(ParticleShader);
            if (material != null)
            {
                material.mainTexture = _sprites[sprite];
                material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float)(blending == AdditiveBlending ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
            }
            _particleMaterials[key] = material;
            return material;
        }

        private Material CreateMaterial(string shaderName)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null) { Fail("missing shader " + shaderName); return null; }
            return new Material(shader) { name = shaderName, hideFlags = HideFlags.DontSave };
        }

        private Texture2D LoadTexture(string resource)
        {
            var texture = Resources.Load<Texture2D>(resource);
            if (texture == null) Debug.LogError("[Blitz] chest scene texture is missing: " + resource);
            return texture;
        }

        private void Fail(string reason)
        {
            _failed = true;
            Debug.LogError("[Blitz] chest scene cannot render: " + reason);
        }

        private void ReleaseTarget()
        {
            if (_target == null) return;
            if (_image != null && _image.texture == _target) _image.texture = Texture2D.blackTexture;
            _target.Release();
            Release(_target);
            _target = null;
        }

        private void OnDestroy()
        {
            ReleaseTarget();
            if (_commands != null) _commands.Release();
            _commands = null;
            Release(_present);
            Release(_unlit);
            Release(_box);
            foreach (var material in _particleMaterials.Values) Release(material);
            _particleMaterials.Clear();
            Release(_particleMesh);
        }

        private static void Release(UnityEngine.Object item)
        {
            if (item == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(item);
            else UnityEngine.Object.DestroyImmediate(item);
        }
    }

    /// <summary>
    /// The chest camera in Unity axes. DAVA's fov is horizontal; the image is
    /// not mirrored because swapping Y and Z also swaps the handedness.
    /// </summary>
    public static class Chest480Projection
    {
        /// <summary>Vertical fov in degrees for DAVA's horizontal fovX at this aspect.</summary>
        public static float VerticalFov(Chest480Camera camera, float aspect)
        {
            var halfX = camera.fovX * 0.5f * Mathf.Deg2Rad;
            return 2f * Mathf.Atan(Mathf.Tan(halfX) / Mathf.Max(aspect, 1e-4f)) * Mathf.Rad2Deg;
        }

        public static Matrix4x4 Projection(Chest480Camera camera, float aspect)
        {
            return Matrix4x4.Perspective(VerticalFov(camera, aspect), aspect, camera.znear, camera.zfar);
        }

        /// <summary>World-to-camera like Camera.worldToCameraMatrix (camera looks down -Z).</summary>
        public static Matrix4x4 View(Chest480Camera camera)
        {
            var position = DavaVector3.From(camera.position).ToUnity();
            var target = DavaVector3.From(camera.target).ToUnity();
            var up = DavaVector3.From(camera.up).ToUnity();
            var pose = Matrix4x4.TRS(position, Quaternion.LookRotation(target - position, up), Vector3.one);
            return Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * pose.inverse;
        }
    }
}
