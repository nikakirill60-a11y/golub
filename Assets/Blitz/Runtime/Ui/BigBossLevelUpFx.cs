using BlitzLegacy.Runtime.Battle;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>Render the source UIParticles scene into the overlay canvas. A world
    /// MeshRenderer parented to an overlay canvas is invisible; use an isolated, additive
    /// render target instead. Source scene scale is 0.45 and source blend is SRC_ALPHA, ONE.
    /// The camera, target and material are built once (behind the loading curtain) and the
    /// camera only runs while a badge plays: creating them per level-up hitched the battle.</summary>
    public sealed class BigBossLevelUpFx : MonoBehaviour
    {
        private const float Seconds = 1.4f;
        private static Camera _camera;
        private static RenderTexture _texture;
        private static Material _material;
        private static DavaFx _fx;
        private static int _playing;
        private float _until;
        /// <summary>Build the shared camera, target and material ahead of the first level-up.</summary>
        public static void Preload() { Ensure(); }
        private static void Ensure()
        {
            if (_texture == null) _texture = new RenderTexture(256, 256, 16, RenderTextureFormat.ARGB32) { name = "BigBossLevelUpRT" };
            if (!_texture.IsCreated()) _texture.Create();
            if (_material == null) _material = new Material(Shader.Find("BlitzLegacy/BigBossUiAdditive"));
            if (_camera != null) return;
            var world = new GameObject("BigBossUiParticlesCamera"); world.transform.position = new Vector3(20000, 20000, 20000);
            _camera = world.AddComponent<Camera>(); _camera.clearFlags = CameraClearFlags.SolidColor; _camera.backgroundColor = Color.clear;
            _camera.orthographic = true; _camera.orthographicSize = 128; _camera.aspect = 1; _camera.nearClipPlane = .1f; _camera.farClipPlane = 2000;
            _camera.cullingMask = 1 << 31; _camera.depth = -100; _camera.targetTexture = _texture; _camera.enabled = false; _playing = 0;
        }
        public static void Play(RectTransform role)
        {
            Ensure();
            var node = UiFactory.Node("Level_up", role); GodotRect.TopLeft(node, 50 - 128, 75 - 128, 50 + 128, 75 + 128);
            var c = node.gameObject.AddComponent<BigBossLevelUpFx>(); c._until = Time.time + Seconds;
            var image = node.gameObject.AddComponent<RawImage>(); image.raycastTarget = false; image.texture = _texture; image.material = _material;
            // One shared target: a newer badge restarts the source burst.
            if (_fx != null) { if (Application.isPlaying) Destroy(_fx.gameObject); else DestroyImmediate(_fx.gameObject); }
            _fx = DavaFx.CreateBigBoss(_camera.transform, "UI_Bossmode_Level_up", Seconds); _fx.transform.localPosition = new Vector3(0, 0, 1000);
            _fx.VisualScale = Vector3.one * .45f; _fx.ViewCamera = _camera;
            foreach (var part in _fx.GetComponentsInChildren<Transform>(true)) part.gameObject.layer = 31;
            _playing++; _camera.enabled = true;
        }
        private void Update() { if (Time.time >= _until) Destroy(gameObject); }
        private void OnDestroy()
        {
            if (--_playing > 0 || _camera == null) return;
            _playing = 0; _camera.enabled = false;
        }
    }
}
