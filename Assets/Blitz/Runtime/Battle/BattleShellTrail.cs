using System.Collections.Generic;
using BlitzLegacy.Core.Tanks;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Port of <c>assets/tanks/base/projectile/trail_component.gd</c>
    /// (<c>Trail3D</c>): the shell tracer. Every 0.2 s a cross of two quads is
    /// appended from where the shell was to where it is now, and every segment
    /// fades out over 0.8 s at a fifth of full opacity.
    ///
    /// The source rebuilds an <c>ImmediateMesh</c> on every repaint, so the port
    /// rebuilds one <see cref="Mesh"/> instead. Deviations, all cosmetic: the
    /// expired segments are dropped instead of emitting the source's degenerate
    /// zero triangle, the ribbon sits on this object rather than on a
    /// <c>top_level</c> child, and the port carries no trail texture, because
    /// the source's own material drives the fade from the vertex colour.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleShellTrail : MonoBehaviour
    {
        public const string ShaderName = "BlitzLegacy/Battle/Shell Trail";

        /// <summary><c>max_segments</c>.</summary>
        public const int MaxSegments = 32;

        /// <summary><c>max_dist</c>: no tracer is drawn past this camera range.</summary>
        public const float MaxDistance = 500f;

        /// <summary><c>width</c>.</summary>
        public const float Width = 0.2f;

        /// <summary><c>segment_life_time</c>.</summary>
        public const float SegmentLifeTime = 0.8f;

        /// <summary>The source repaints and appends on a 0.2 s clock.</summary>
        public const float RepaintInterval = 0.2f;

        /// <summary><c>age = clampf(age, 0, 1) * 0.2</c>.</summary>
        public const float AlphaScale = 0.2f;

        /// <summary><c>Color.GOLDENROD</c>, which the source paints HEAT with.</summary>
        public static readonly Color HeatColour =
            new Color(0.854902f, 0.647059f, 0.12549f, 1f);

        /// <summary>
        /// A guided rocket draws its own trail in the Blitz client
        /// (<c>ATGMHollowCharge</c> -&gt; <c>trace_HC_3</c> and
        /// <c>ATGMHighExplosive</c> -&gt; <c>trace_HE_4</c>): an unbroken smoke
        /// column that stays up behind the rocket instead of the dashed,
        /// fast-fading ribbon a shell leaves. The port keeps one tracer class
        /// and gives it this second set of timings, because the rocket flies at
        /// 75-85 m/s and a 0.2 s clock would cut its steered curve into chords.
        /// </summary>
        public const float AtgmRepaintInterval = 0.04f;

        /// <summary>How long a rocket's smoke stays up, in seconds.</summary>
        public const float AtgmSegmentLifeTime = 4f;

        /// <summary>
        /// Enough spans to hold <see cref="AtgmSegmentLifeTime"/> of smoke at
        /// the rocket clock; the shell budget would cut the column short.
        /// </summary>
        public const int AtgmMaxSegments = 160;

        /// <summary>The rocket column is drawn solid, not at a fifth alpha.</summary>
        public const float AtgmAlphaScale = 0.9f;

        /// <summary><c>trace_HE_4</c>: the pale smoke of the MGM-51C.</summary>
        public static readonly Color AtgmExplosiveColour =
            new Color(0.85f, 0.85f, 0.82f, 1f);

        /// <summary>Six vertices per quad, two quads crossed, as the source emits them.</summary>
        public const int VerticesPerSegment = 12;

        private static readonly Vector2[] SegmentUvs =
        {
            new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(0f, 0f),
            new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f),
            new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(0f, 0f),
            new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f),
        };

        private struct Segment
        {
            public float Birth;
            public Vector3[] Vertices;
        }

        private readonly List<Segment> _segments = new List<Segment>();
        private readonly List<Vector3> _vertexBuffer = new List<Vector3>();
        private readonly List<Vector2> _uvBuffer = new List<Vector2>();
        private readonly List<Color> _colourBuffer = new List<Color>();
        private readonly List<int> _indexBuffer = new List<int>();

        private BattleProjectile _projectile;
        private Mesh _mesh;
        private MeshRenderer _renderer;
        private Color _colour = Color.white;
        private Vector3 _lastPosition;
        private bool _first = true;
        private float _nextPoint;
        private float _repaintInterval = RepaintInterval;
        private float _segmentLifeTime = SegmentLifeTime;
        private float _alphaScale = AlphaScale;
        private int _maxSegments = MaxSegments;

        /// <summary>Whether this tracer belongs to a guided rocket.</summary>
        public bool Guided { get; private set; }

        /// <summary>The clock this tracer appends on, in seconds.</summary>
        public float Interval { get { return _repaintInterval; } }

        /// <summary>How long one of its segments stays up, in seconds.</summary>
        public float SegmentLife { get { return _segmentLifeTime; } }

        /// <summary>How many segments the tracer is drawing right now.</summary>
        public int SegmentCount
        {
            get { return _segments.Count; }
        }

        /// <summary>The colour the shell type picked.</summary>
        public Color Colour
        {
            get { return _colour; }
        }

        /// <summary>
        /// The source hangs the tracer on the tank that fired, not on the round,
        /// so the trail keeps fading after the shell is gone. <paramref
        /// name="origin"/> is <c>gun_shoot_point</c>, which anchors the ribbon.
        /// </summary>
        public static BattleShellTrail Create(BattleProjectile projectile, Vector3 origin)
        {
            if (projectile == null)
            {
                return null;
            }

            var host = new GameObject("ShellTrail");
            host.transform.SetParent(projectile.transform.parent, true);
            host.transform.SetPositionAndRotation(origin, Quaternion.identity);

            var trail = host.AddComponent<BattleShellTrail>();
            trail.Build(projectile);
            return trail;
        }

        private void Build(BattleProjectile projectile)
        {
            _projectile = projectile;
            var shell = projectile.Shell;
            _colour = shell != null && shell.Type == ShellType.Heat
                ? HeatColour
                : Color.white;

            // A guided rocket swaps the shell ribbon for its own smoke column.
            if (shell != null && shell.Atgm)
            {
                Guided = true;
                _repaintInterval = AtgmRepaintInterval;
                _segmentLifeTime = AtgmSegmentLifeTime;
                _alphaScale = AtgmAlphaScale;
                _maxSegments = AtgmMaxSegments;
                if (shell.Type != ShellType.Heat)
                {
                    _colour = AtgmExplosiveColour;
                }
            }

            _mesh = new Mesh { name = "ShellTrail" };
            _mesh.MarkDynamic();
            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;

            _renderer = gameObject.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = TrailMaterial();
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        private static Material _material;

        /// <summary>One material serves every tracer, as the source's is static.</summary>
        private static Material TrailMaterial()
        {
            if (_material != null)
            {
                return _material;
            }

            var shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }

            if (shader == null)
            {
                shader = Shader.Find("Unlit/Transparent");
            }

            _material = new Material(shader) { name = "ShellTrail" };
            return _material;
        }

        /// <summary>
        /// Finalises the ribbon when the projectile lands. The current Godot
        /// build uses a GPU trail which follows the shell through the impact
        /// frame; this is the ImmediateMesh port's equivalent and also keeps a
        /// very short-range shot visible instead of discarding its only span.
        /// </summary>
        public void CompleteAtCurrentPosition()
        {
            if (_projectile == null)
            {
                return;
            }

            if (_first)
            {
                _first = false;
                _lastPosition = Vector3.zero;
            }

            AddPoint();
            Rebuild();
            _projectile = null;
        }

        /// <summary><c>_physics_process</c> to <c>update(delta)</c>.</summary>
        private void FixedUpdate()
        {
            var alive = _projectile != null && _projectile.Alive;
            if (!alive && _segments.Count == 0)
            {
                Destroy(gameObject);
                return;
            }

            if (Time.time <= _nextPoint)
            {
                return;
            }

            _nextPoint = Time.time + _repaintInterval;
            if (alive)
            {
                AddPoint();
            }

            Rebuild();
        }

        /// <summary><c>add_point</c>.</summary>
        private void AddPoint()
        {
            var vertices = CreatePoint();
            if (vertices == null)
            {
                return;
            }

            _segments.Add(new Segment { Birth = Time.time, Vertices = vertices });

            // The source declares a segment budget but never trims to it. The
            // 0.2 s clock and the 0.8 s life keep the ribbon far below it, so
            // the cap here only ever guards against a stalled repaint.
            while (_segments.Count > _maxSegments)
            {
                _segments.RemoveAt(0);
            }
        }

        /// <summary>
        /// <c>create_point</c>. The first call only records where the shell was,
        /// because a segment needs two positions. Godot's cross products are
        /// right-handed, so the port's ribbon is mirrored across its own axis,
        /// which a two-sided unlit quad cannot show.
        /// </summary>
        private Vector3[] CreatePoint()
        {
            var camera = Camera.main;
            if (camera == null || _projectile == null)
            {
                return null;
            }

            var cameraDistance =
                (camera.transform.position - _projectile.transform.position).sqrMagnitude;
            if (cameraDistance > MaxDistance * MaxDistance)
            {
                return null;
            }

            var start = _lastPosition;
            var end = _projectile.transform.position - transform.position;
            _lastPosition = end;

            if (_first)
            {
                _first = false;
                return null;
            }

            var forward = _projectile.Velocity.normalized;
            var right = Vector3.Cross(forward, Vector3.up) * Width;
            var up = Vector3.Cross(forward, right);

            var vertices = new Vector3[VerticesPerSegment];
            for (var i = 0; i < 2; i++)
            {
                var dir = i == 0 ? right : up;
                var at = i * 6;
                vertices[at] = start + dir;
                vertices[at + 1] = start - dir;
                vertices[at + 2] = end - dir;
                vertices[at + 3] = end - dir;
                vertices[at + 4] = end + dir;
                vertices[at + 5] = start + dir;
            }

            return vertices;
        }

        /// <summary>
        /// <c>process_segment</c> for every live segment: the expired ones leave
        /// the front of the list and the rest are re-emitted with their fade.
        /// </summary>
        private void Rebuild()
        {
            var now = Time.time;
            while (_segments.Count > 0 && now > _segments[0].Birth + _segmentLifeTime)
            {
                _segments.RemoveAt(0);
            }

            _vertexBuffer.Clear();
            _uvBuffer.Clear();
            _colourBuffer.Clear();
            _indexBuffer.Clear();

            for (var s = 0; s < _segments.Count; s++)
            {
                var segment = _segments[s];
                var age = 1f - ((now - segment.Birth) / _segmentLifeTime);
                var colour = _colour;
                colour.a = Mathf.Clamp01(age) * _alphaScale;

                for (var i = 0; i < VerticesPerSegment; i++)
                {
                    _indexBuffer.Add(_vertexBuffer.Count);
                    _vertexBuffer.Add(segment.Vertices[i]);
                    _uvBuffer.Add(SegmentUvs[i]);
                    _colourBuffer.Add(colour);
                }
            }

            _mesh.Clear();
            if (_vertexBuffer.Count == 0)
            {
                return;
            }

            _mesh.SetVertices(_vertexBuffer);
            _mesh.SetUVs(0, _uvBuffer);
            _mesh.SetColors(_colourBuffer);
            _mesh.SetTriangles(_indexBuffer, 0, true);
        }

        private void OnDestroy()
        {
            if (_mesh != null)
            {
                Destroy(_mesh);
                _mesh = null;
            }
        }
    }
}
