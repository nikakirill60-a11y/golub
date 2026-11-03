using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
namespace BlitzLegacy.Runtime.Ui
{
    /// <summary>One frozen Godot menu backdrop, shared by result and account screens.</summary>
    public sealed class FrozenMenuBackdrop : MonoBehaviour
    {
        private RawImage _backdrop;
        private RenderTexture _backdropTexture;
        private Material _backdropMaterial;
        public bool Ready => _backdropTexture != null;
        public RenderTexture Texture => _backdropTexture;
        public static FrozenMenuBackdrop Attach(RawImage image, Canvas canvas)
        {
            var component = image.gameObject.AddComponent<FrozenMenuBackdrop>();
            component._backdrop = image;
            image.raycastTarget = false;
            image.color = new Color(.08f,.12f,.15f,.75f);
            if (Application.isPlaying && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                component.StartCoroutine(component.CaptureBackdrop(canvas));
            return component;
        }
        private IEnumerator CaptureBackdrop(Canvas canvas)
        {
            // One frozen menu capture, not a camera rendering the hangar every frame.
            canvas.enabled = false;
            // WaitForEndOfFrame is not pumped by the editor's batch-mode loop.
            if (Application.isBatchMode) yield return null;
            else yield return new WaitForEndOfFrame();
            var capture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            try
            {
                if (Application.isBatchMode) CaptureBatchBackdrop(capture);
                else
                {
                    RenderTexture.active = null;
                    capture.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
                    capture.Apply();
                }
            }
            finally { RenderTexture.active = previous; canvas.enabled = true; }
            if (capture == null) yield break;
            SetBackdrop(capture);
            Destroy(capture);
        }

        private static void CaptureBatchBackdrop(Texture2D capture)
        {
            // Batch mode has no drawable system backbuffer outside the render loop.
            // Render one snapshot into a texture instead, including overlay menus.
            var hangar = BlitzBootstrap.Current == null ? null : BlitzBootstrap.Current.Hangar;
            var camera = hangar == null || hangar.World == null ? Camera.main : hangar.World.HangarCamera;
            if (camera == null) throw new InvalidOperationException("No backdrop camera");
            var target = new RenderTexture(capture.width, capture.height, 24);
            var previousTarget = camera.targetTexture;
            var overlays = new List<Canvas>();
            var cameras = new List<Camera>();
            var distances = new List<float>();
            try
            {
                foreach (var item in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
                {
                    if (!item.enabled || item.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                    overlays.Add(item); cameras.Add(item.worldCamera); distances.Add(item.planeDistance);
                    item.renderMode = RenderMode.ScreenSpaceCamera;
                    item.worldCamera = camera;
                    item.planeDistance = camera.nearClipPlane + .1f;
                }
                camera.targetTexture = target;
                Canvas.ForceUpdateCanvases();
                camera.Render(); camera.Render();
                RenderTexture.active = target;
                capture.ReadPixels(new Rect(0, 0, capture.width, capture.height), 0, 0);
                capture.Apply();
            }
            finally
            {
                camera.targetTexture = previousTarget;
                for (var i = 0; i < overlays.Count; i++)
                {
                    overlays[i].renderMode = RenderMode.ScreenSpaceOverlay;
                    overlays[i].worldCamera = cameras[i]; overlays[i].planeDistance = distances[i];
                }
                RenderTexture.active = null;
                target.Release(); DestroyBackdropObject(target);
            }
        }

        public void SetBackdrop(Texture capture)
        {
            if (capture == null) return;
            if (_backdropTexture != null) { _backdropTexture.Release(); DestroyBackdropObject(_backdropTexture); }
            _backdropTexture = new RenderTexture(capture.width, capture.height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            _backdropTexture.useMipMap = true;
            _backdropTexture.autoGenerateMips = true;
            _backdropTexture.filterMode = FilterMode.Trilinear;
            Graphics.Blit(capture, _backdropTexture);
            if (_backdropMaterial == null)
                _backdropMaterial = new Material(Resources.Load<Shader>("ResultBackdrop"));
            _backdrop.material = _backdropMaterial;
            _backdrop.texture = _backdropTexture;
            _backdrop.color = Color.white;
        }

        private static void DestroyBackdropObject(UnityEngine.Object value)
        {
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }

        private void OnDestroy()
        {
            if (_backdropTexture != null) { _backdropTexture.Release(); DestroyBackdropObject(_backdropTexture); }
            if (_backdropMaterial != null) DestroyBackdropObject(_backdropMaterial);
        }
    }
}
