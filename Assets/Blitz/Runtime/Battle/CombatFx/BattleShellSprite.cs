using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Runtime.Battle.CombatFx
{
    /// <summary>
    /// The Projectile node itself, a billboarded <c>Sprite3D</c>:
    /// <c>shell.png</c> at <c>pixel_size = clamp(caliber, 40, 120) * 0.00012</c>,
    /// modulated BEIGE (BISQUE for HEAT), drawn with the 2D sprite material
    /// (unshaded, double-sided, alpha blended, sRGB vertex colour, fog on).
    /// It lives under the shell, so it goes when the shell does.
    /// </summary>
    public sealed class BattleShellSprite : MonoBehaviour
    {
        private static GodotParticleMaterial _material;

        private MeshRenderer _renderer;
        private Mesh _mesh;
        private bool _shown;

        /// <summary>Hides the sprite while true (the owner tank is hidden).</summary>
        public System.Func<bool> Concealed;

        /// <summary>Side of the square quad in metres: texture pixels times pixel_size.</summary>
        public float Side { get; private set; }

        public Color32 Modulate { get; private set; }

        /// <summary>Whether the Sprite3D is visible (it shows on the first physics frame).</summary>
        public bool Shown { get { return _shown; } }

        public Mesh Mesh { get { return _mesh; } }

        public static BattleShellSprite Create(Transform shell, int caliber, bool heat)
        {
            var manifest = CombatFxManifest.Shared;
            if (manifest == null || !GodotParticleEffect.Enabled)
            {
                return null;
            }

            var go = new GameObject("shell_sprite");
            go.transform.SetParent(shell, false);
            var sprite = go.AddComponent<BattleShellSprite>();
            sprite.Initialise(manifest, caliber, heat);
            return sprite;
        }

        private void Initialise(CombatFxManifest manifest, int caliber, bool heat)
        {
            if (_material == null)
            {
                _material = new GodotParticleMaterial
                {
                    Transparent = true,
                    BlendAdd = false,
                    Fog = true,
                    VertexColor = true,
                    VertexColorSrgb = true,
                    Albedo = new Vector4(1f, 1f, 1f, 1f),
                    Texture = manifest.ShellTexture,
                    Mipmaps = manifest.ShellMipmaps,
                    Repeat = false,
                };
            }

            var modulate = heat ? manifest.ShellModulateHeat : manifest.ShellModulate;
            Modulate = new Color(modulate.x, modulate.y, modulate.z, modulate.w);
            var material = GodotParticleEffect.MaterialFor(_material);
            var texture = material != null ? material.GetTexture("_MainTex") : null;
            var pixels = texture != null ? texture.width : 32;
            Side = pixels * manifest.ShellPixelSize(caliber);

            var h = Side * 0.5f;
            _mesh = new Mesh { name = "shell_sprite" };
            _mesh.SetVertices(new[]
            {
                new Vector3(-h, h, 0f), new Vector3(h, h, 0f),
                new Vector3(-h, -h, 0f), new Vector3(h, -h, 0f),
            });
            // Godot's top-down UV; the shader flips it for Unity textures.
            _mesh.SetUVs(0, new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 1f), new Vector2(1f, 1f),
            });
            var c = Modulate;
            _mesh.SetColors(new[] { c, c, c, c });
            _mesh.SetTriangles(new[] { 0, 1, 2, 1, 3, 2 }, 0);
            _mesh.RecalculateBounds();
            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = gameObject.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = material;
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            _renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            _renderer.enabled = false;
        }

        /// <summary><c>show()</c> / <c>hide()</c> of the shell node.</summary>
        public void SetShown(bool shown)
        {
            _shown = shown;
        }

        private void LateUpdate()
        {
            var camera = GodotParticleEffect.ViewOverride != null ? GodotParticleEffect.ViewOverride : Camera.main;
            Face(camera != null ? camera.transform : null);
        }

        /// <summary>BILLBOARD_ENABLED: the quad takes the camera's basis at the shell's origin.</summary>
        public void Face(Transform camera)
        {
            var visible = _shown && camera != null && (Concealed == null || !Concealed());
            _renderer.enabled = visible;
            if (!visible)
            {
                return;
            }

            transform.rotation = camera.rotation;
            var parent = transform.parent;
            var parentScale = parent != null ? parent.lossyScale : Vector3.one;
            transform.localScale = new Vector3(1f / parentScale.x, 1f / parentScale.y, 1f / parentScale.z);
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
