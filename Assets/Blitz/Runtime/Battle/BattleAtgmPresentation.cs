using System;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Core.Tanks;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Native modern-ATGM presentation: the unmodified aim_rocket_marker sprite
    /// and layout/impact animation of 11.20, plus its original AGTM FX scenes.
    /// No guidance, damage, range or flight-audio policy lives in this class.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(90)]
    public sealed class BattleAtgmPresentation : MonoBehaviour
    {
        public const string ManifestResource = "Data/atgm_1120_source_manifest";
        private BattleProjectile _projectile;
        private BattleHud _hud;
        private Canvas _canvas;
        private RectTransform _parent, _root, _content;
        private Image _image;
        private DavaFx _flight, _trail;
        private Transform _worldParent;
        private bool _ownsCanvas, _exploding, _released;
        private Vector3 _position;
        private Vector2 _size;
        private float _explosionStart, _explosionSeconds, _explosionScale;
        private static JsonNode _manifest;
        private static Sprite _sprite;

        public RectTransform Root { get { return _root; } }
        public RectTransform Content { get { return _content; } }
        public Image Marker { get { return _image; } }
        public DavaFx FlightFx { get { return _flight; } }
        public DavaFx TrailFx { get { return _trail; } }
        public Canvas Canvas { get { return _canvas; } }
        public Vector2 LogicalSize { get { return _size; } }
        public float ExplosionDuration { get { return _explosionSeconds; } }
        public bool Exploding { get { return _exploding; } }
        public Camera ViewCamera { get; set; }

        /// <summary>
        /// The aim_rocket_marker triangle belongs to the shooter's own HUD.
        /// Everybody else - enemies and team-mates - sees only the rocket and
        /// its tracer, so they can tell what is flying at them.
        /// </summary>
        public bool MarkerAllowed { get; private set; }

        /// <summary>The marker is drawn for previews (no shooter) and for the local player's rockets.</summary>
        public static bool ShowsMarker(BattleTank owner)
        {
            return owner == null || owner.IsPlayerDriven;
        }

        private static JsonNode Source
        {
            get
            {
                if (_manifest == null)
                {
                    var asset = Resources.Load<TextAsset>(ManifestResource);
                    if (asset == null) throw new InvalidOperationException("Modern ATGM 11.20 source manifest missing");
                    _manifest = JsonNode.Parse(asset.text);
                }
                return _manifest;
            }
        }

        private static Sprite SourceSprite
        {
            get
            {
                if (_sprite == null)
                {
                    _sprite = Resources.Load<Sprite>(Source["marker"]["resource"].AsString());
                    if (_sprite == null) throw new InvalidOperationException("Original aim_rocket_marker sprite missing");
                }
                return _sprite;
            }
        }

        public static BattleAtgmPresentation Create(BattleProjectile projectile)
        {
            if (projectile == null || projectile.Shell == null || !projectile.Shell.UsesModernAtgmVisual)
            { return null; }
            var hud = FindFirstObjectByType<BattleHud>();
            RectTransform parent; Canvas canvas; var ownsCanvas = false;
            if (hud != null && hud.Root != null)
            {
                parent = hud.Root;
                canvas = parent.GetComponentInParent<Canvas>();
            }
            else
            {
                // Standalone previews/tests have no battle HUD. Real battles use
                // its existing canvas and live battle-UI scale, never a world quad.
                var go = new GameObject("ATGM1120Canvas", typeof(RectTransform));
                go.transform.SetParent(projectile.transform.parent, false);
                canvas = go.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = BattleHud.SortingOrder;
                parent = (RectTransform)go.transform;
                ownsCanvas = true;
            }
            var host = new GameObject("AtgmMarker1120", typeof(RectTransform));
            host.transform.SetParent(parent, false);
            var visual = host.AddComponent<BattleAtgmPresentation>();
            visual._projectile = projectile; visual._hud = hud;
            visual._parent = parent; visual._canvas = canvas; visual._ownsCanvas = ownsCanvas;
            visual._worldParent = projectile.transform.parent;
            visual.MarkerAllowed = ShowsMarker(projectile.Owner);
            visual.Build();
            return visual;
        }

        private void Build()
        {
            var marker = Source["marker"];
            _size = new Vector2(marker["logical_size"][0].AsFloat(), marker["logical_size"][1].AsFloat());
            var pivot = new Vector2(marker["native_pivot"][0].AsFloat(), 1f-marker["native_pivot"][1].AsFloat());
            _explosionSeconds = marker["explosion"]["seconds"].AsFloat();
            _explosionScale = marker["explosion"]["scale_to"].AsFloat();
            _root = (RectTransform)transform;
            _root.anchorMin = _root.anchorMax = new Vector2(.5f,.5f);
            _root.pivot = pivot; _root.sizeDelta = _size;
            var content = new GameObject("MarkerContent", typeof(RectTransform));
            content.transform.SetParent(transform, false);
            _content = (RectTransform)content.transform;
            _content.anchorMin = _content.anchorMax = new Vector2(.5f,.5f);
            _content.pivot = pivot; _content.sizeDelta = _size;
            // DAVA anchors the child's *pivot* at the parent centre. RectTransform
            // does the same; native Y-down pivot .4 becomes Unity Y-up pivot .6.
            _image = content.AddComponent<Image>(); _image.sprite = SourceSprite;
            _image.type = Image.Type.Simple; _image.color = Color.white;
            _image.raycastTarget = false; _image.maskable = false;
            _position = _projectile.transform.position;
            var seconds = GunMath.ShellLifetime(_projectile.Shell.MaxDistance, _projectile.Shell.Speed);
            _flight = DavaFx.CreateAtgm1120(_projectile.transform, "AGTM_raketa", seconds, true);
            _trail = DavaFx.CreateAtgm1120(_projectile.transform, "AGTM_Tracer_trail", seconds, true);
            UpdatePose();
        }

        private Camera ActiveCamera
        {
            get
            {
                if (ViewCamera != null) return ViewCamera;
                if (_hud != null) return _hud.ActiveCamera;
                if (_projectile != null && _projectile.Owner != null && _projectile.Owner.View != null)
                { return _projectile.Owner.View.Active; }
                return Camera.main;
            }
        }

        private void UpdatePose()
        {
            var camera = ActiveCamera;
            if (_flight != null)
            {
                _flight.ViewCamera = camera;
                var velocity = _projectile != null ? _projectile.Velocity : Vector3.forward;
                if (velocity.sqrMagnitude > .000001f)
                {
                    var direction = velocity.normalized;
                    var up = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > .999f ? Vector3.forward : Vector3.up;
                    _flight.transform.rotation = Quaternion.LookRotation(direction, up);
                }
            }
            if (_trail != null) _trail.ViewCamera = camera;
        }

        private void LateUpdate()
        {
            if (!_exploding && (_projectile == null || !_projectile.Alive)) { Cancel(); return; }
            RenderAt(ActiveCamera, Time.time);
        }

        /// <summary>Explicit clock/camera makes source animation and projection testable.</summary>
        public void RenderAt(Camera camera, float now)
        {
            if (_released) return;
            if (!_exploding && _projectile != null) _position = _projectile.transform.position;
            UpdatePose();
            var progress = _exploding ? Mathf.Clamp01((now-_explosionStart)/_explosionSeconds) : 0f;
            _content.sizeDelta = _size * Mathf.Lerp(1f, _explosionScale, progress);
            _image.color = new Color(1f,1f,1f,1f-progress);
            if (_exploding && progress >= 1f) { Cancel(); return; }
            if (camera == null) { _image.enabled = false; return; }
            var screen = camera.WorldToScreenPoint(_position);
            var visible = MarkerAllowed && screen.z > 0f && camera.pixelRect.Contains(new Vector2(screen.x,screen.y))
                && (_hud == null || _hud.Visible);
            _image.enabled = visible;
            if (!visible) return;
            Vector2 local;
            var uiCamera = _canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _canvas.worldCamera;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_parent, screen, uiCamera, out local))
            {
                var anchor = Vector2.Scale(_root.anchorMin - _parent.pivot, _parent.rect.size);
                _root.anchoredPosition = local - anchor;
            }
        }

        public void BeginExplosion(Vector3 position)
        {
            if (_exploding || _released) return;
            _position = position; _exploding = true; _explosionStart = Time.time;
            StopFlightEffects();
            var impact = DavaFx.CreateAtgm1120(_worldParent, "AGTM_Explosion", .25f);
            impact.transform.position = position; impact.ViewCamera = ActiveCamera;
        }

        private void StopFlightEffects(bool detach = true)
        {
            foreach (var fx in new[] { _flight, _trail })
            {
                if (fx == null) continue;
                // Never onto a dead parent: Unity refuses to parent under an
                // object that is being destroyed (logged as an error).
                if (detach && _worldParent != null) fx.transform.SetParent(_worldParent, true);
                fx.Stop();
            }
            _flight = null; _trail = null;
        }

        /// <summary>Scene/external destruction is not an impact: remove the marker without a fake burst.</summary>
        public void Cancel()
        {
            if (_released) return;
            _released = true;
            StopFlightEffects();
            DestroySafe(_ownsCanvas && _canvas != null ? _canvas.gameObject : gameObject);
        }

        private static void DestroySafe(GameObject target)
        {
            if (target == null) return;
            if (Application.isPlaying) Destroy(target); else DestroyImmediate(target);
        }

        private void OnDestroy()
        {
            // World FX keep their authored fade after a real impact (handled
            // before this point). Reaching here with live flight FX means the
            // marker went with its HUD or the whole battle (BattleSession
            // being destroyed); moving the FX onto that parent is refused by
            // Unity, and they go with their projectile anyway, so stop them
            // in place.
            StopFlightEffects(false);
        }
    }
}
