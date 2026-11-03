using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// The arty sight: the line the shot is lobbed along and the aim circle at
    /// the end of it, the way WoT draws them.
    ///
    /// The sight integrates the same speed-limited, turn-limited guidance as
    /// the projectile. Collision checks stop it at actual cover, not at an
    /// unrelated decorative parabola that the rocket could never follow.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleArtyReticle : MonoBehaviour
    {
        /// <summary>Segments in the drawn arc.</summary>
        public const int ArcPoints = 24;

        /// <summary>Segments in the aim circle.</summary>
        public const int CirclePoints = 40;

        /// <summary>How far off the ground the circle is drawn.</summary>
        public const float GroundLift = 0.2f;

        public const float LineWidth = 0.12f;
        public const float CircleWidth = 0.1f;

        /// <summary>The aim circle never collapses or swallows the screen.</summary>
        public const float RadiusMin = 0.5f;
        public const float RadiusMax = 20f;

        private static readonly Color Clear = new Color(0.35f, 0.85f, 0.4f, 0.85f);
        private static readonly Color Blocked = new Color(0.9f, 0.35f, 0.3f, 0.85f);

        private readonly RaycastHit[] _hits = new RaycastHit[32];
        private LineRenderer _line;
        private LineRenderer _circle;

        /// <summary>Whether the last sight was cut short by something.</summary>
        public bool Obstructed { get; private set; }

        /// <summary>Where the last sight ended.</summary>
        public Vector3 Endpoint { get; private set; }

        public static BattleArtyReticle Create()
        {
            var go = new GameObject("ArtyReticle");
            var reticle = go.AddComponent<BattleArtyReticle>();
            reticle.Build();
            go.SetActive(false);
            return reticle;
        }

        private void Build()
        {
            _line = NewLine("sight", ArcPoints + 1, LineWidth);
            _circle = NewLine("circle", CirclePoints + 1, CircleWidth);
            _circle.loop = true;
        }

        private LineRenderer NewLine(string name, int points, float width)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = points;
            line.widthMultiplier = width;
            line.textureMode = LineTextureMode.Stretch;
            line.shadowCastingMode =
                UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.material = SightMaterial();
            return line;
        }

        /// <summary>
        /// The URP unlit shader the port already keeps in every build, so the
        /// sight does not turn into a stripped magenta line on the phone.
        /// </summary>
        private static Material SightMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                ?? Shader.Find("Sprites/Default");
            return new Material(shader);
        }

        public void Show(bool on)
        {
            if (gameObject.activeSelf != on) { gameObject.SetActive(on); }
        }

        /// <summary>
        /// Where the sight ends: the first thing in the way, or the aim point
        /// itself when the way is clear.
        /// </summary>
        public static Vector3 Sight(Vector3 to, bool blocked, Vector3 hit)
        {
            return blocked ? hit : to;
        }

        /// <summary>The aim circle radius a dispersion spreads over a range.</summary>
        public static float Radius(float dispersion, float distance)
        {
            // GunInfo.Dispersion is the client's metres per hundred metres.
            var spread = dispersion * distance * 0.01f;
            if (spread < RadiusMin) { return RadiusMin; }
            return spread > RadiusMax ? RadiusMax : spread;
        }

        /// <summary>
        /// Integrate the actual guidance at the fixed timestep, with only one
        /// collision query per displayed segment. No decorative parabola: the
        /// sight also reveals an unaligned barrel or an unreachable target.
        /// </summary>
        public void Draw(Vector3 from, Vector3 velocity, Vector3 to, float radius,
            ShellInfo shell, Transform owner)
        {
            if (_line == null || _circle == null || shell == null || velocity.sqrMagnitude <= 0f) return;
            var step = Mathf.Max(.005f, Time.fixedDeltaTime);
            var lifetime = GunMath.ShellLifetime(shell.MaxDistance, Mathf.Abs(shell.Speed));
            var duration = Mathf.Min(lifetime, Vector3.Distance(from, to) / velocity.magnitude * 1.4f + .5f);
            var ticks = Mathf.Clamp(Mathf.CeilToInt(duration / step), ArcPoints, 2048);
            var position = from;
            var launchVelocity = velocity;
            var previous = from;
            var end = from;
            var finished = false;
            Obstructed = false;
            _line.SetPosition(0, from);
            var tick = 0;
            for (var sample = 1; sample <= ArcPoints; sample++)
            {
                var stop = Mathf.CeilToInt(ticks * sample / (float)ArcPoints);
                while (!finished && tick < stop)
                {
                    velocity = BattleProjectile.GuidedVelocity(velocity, position, to, shell.TurnSpeed, step, true, from, launchVelocity);
                    var next = position + velocity * step;
                    var segment = next - position;
                    var t = Mathf.Clamp01(Vector3.Dot(to - position, segment) / segment.sqrMagnitude);
                    var closest = position + segment * t;
                    if (Vector3.Distance(closest, to) < BattleProjectile.TraceEpsilon)
                    { next = closest; finished = true; }
                    position = next;
                    tick++;
                }
                if (!Obstructed)
                {
                    var segment = position - previous;
                    var count = Physics.RaycastNonAlloc(previous, segment.normalized, _hits, segment.magnitude,
                        BattleLayers.ProjectileMask, QueryTriggerInteraction.Ignore);
                    var distance = float.PositiveInfinity;
                    for (var index = 0; index < count; index++)
                    {
                        var hit = _hits[index];
                        if (owner != null && hit.collider.transform.IsChildOf(owner)) continue;
                        if (hit.distance >= distance) continue;
                        distance = hit.distance; end = hit.point;
                    }
                    if (!float.IsPositiveInfinity(distance))
                    {
                        position = end; finished = true;
                        // Hitting the surface under the crosshair is success,
                        // not an obstruction in front of the intended tank.
                        Obstructed = Vector3.Distance(end, to) > .5f;
                    }
                }
                end = position;
                _line.SetPosition(sample, end);
                previous = position;
                if (finished)
                {
                    for (var rest = sample + 1; rest <= ArcPoints; rest++) _line.SetPosition(rest, end);
                    break;
                }
            }
            Endpoint = end;
            var colour = Obstructed ? Blocked : Clear;
            _line.startColor = _line.endColor = colour;
            _circle.startColor = _circle.endColor = colour;
            var centre = end + Vector3.up * GroundLift;
            for (var i = 0; i <= CirclePoints; i++)
            {
                var angle = i / (float)CirclePoints * Mathf.PI * 2f;
                _circle.SetPosition(i, centre + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius));
            }
        }
    }
}
