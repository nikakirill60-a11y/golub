using System;
using System.Collections.Generic;

namespace BlitzLegacy.Core.Battle
{
    /// <summary>
    /// Engine-free rules of tanks in water: how deep a hull sits, how much the
    /// water carries and brakes it, and the Blitz 4.8.0 drowning countdown.
    ///
    /// The 4.8.0 client ships the presentation only: the
    /// <c>VehicleDestroyTimer</c> indicator, the <c>vehicle_drown</c> and
    /// <c>cant_shoot_gun_under_water</c> messages and the
    /// <c>waterContact 5.5 7.0</c> splash speeds of <c>vehicle.xml</c>. The
    /// server-side drowning delay is not in the client, so the port uses the
    /// ten seconds every WG title of that era counted down.
    ///
    /// Forces are written as accelerations so they stay correct whatever unit
    /// a tank card stores its mass in.
    /// </summary>
    public static class WaterRules
    {
        /// <summary>Seconds a flooded tank survives before it is drowned.</summary>
        public const float DrownSeconds = 10f;

        /// <summary><c>miscParams/collisionEffectVelocities/waterContact</c>: light splash.</summary>
        public const float SplashLightSpeed = 5.5f;

        /// <summary><c>miscParams/collisionEffectVelocities/waterContact</c>: heavy splash.</summary>
        public const float SplashHeavySpeed = 7f;

        /// <summary>Water deeper than this under the tracks counts as being in water.</summary>
        public const float ContactDepth = 0.05f;

        /// <summary>
        /// Share of the tank's weight the water carries once the whole hull is
        /// under. A steel hull is about three times denser than water, so it
        /// gets noticeably lighter but always sinks to the bottom.
        /// </summary>
        public const float FullBuoyancyShare = 0.32f;

        /// <summary>
        /// Quadratic drag gains (1/m) along the hull axes at full submersion:
        /// 0.5 * rho * Cd * A / m for a medium tank, frontal, broadside and
        /// belly areas respectively.
        /// </summary>
        public const float ForwardDragGain = 0.08f;
        public const float SideDragGain = 0.24f;
        public const float VerticalDragGain = 0.35f;

        /// <summary>Linear drag (1/s) that stops slow drift the quadratic term cannot.</summary>
        public const float LinearDragGain = 0.35f;

        /// <summary>Angular damping (1/s) of a fully submerged hull.</summary>
        public const float AngularDragGain = 1.6f;

        /// <summary>Drive speed lost with the hull completely under water.</summary>
        public const float MaxSpeedLoss = 0.65f;

        /// <summary>Speed above which a moving tank throws a bow wave.</summary>
        public const float WaveSpeed = 1.2f;

        /// <summary>
        /// Water level, as a share of the flood line (the hull roof), from
        /// which the HUD shows the tank as being in water. Below it the water
        /// only wets the tracks; above it the ring of the indicator closes
        /// with the water until the roof goes under and the countdown starts.
        /// </summary>
        public const float IndicatorLevel = 0.3f;

        /// <summary>
        /// Fraction (0..1) of a column between <paramref name="bottom"/> and
        /// <paramref name="top"/> that lies below <paramref name="surface"/>.
        /// </summary>
        public static float Submersion(float bottom, float top, float surface)
        {
            var height = top - bottom;
            if (height <= 1e-4f) { return surface > bottom ? 1f : 0f; }
            return Clamp01((surface - bottom) / height);
        }

        /// <summary>Upward acceleration the water lends one fully weighted column.</summary>
        public static float BuoyancyAcceleration(float submersion, float gravity)
        {
            return Math.Abs(gravity) * FullBuoyancyShare * Clamp01(submersion);
        }

        /// <summary>
        /// Drag acceleration along one axis. It opposes
        /// <paramref name="speed"/> and is clamped so that a single step can
        /// stop the motion but never reverse it.
        /// </summary>
        public static float DragAcceleration(float speed, float gain, float submersion, float deltaTime)
        {
            var sub = Clamp01(submersion);
            if (sub <= 0f || speed == 0f) { return 0f; }
            var magnitude = (gain * Math.Abs(speed) + LinearDragGain) * Math.Abs(speed) * sub;
            if (deltaTime > 0f) { magnitude = Math.Min(magnitude, Math.Abs(speed) / deltaTime); }
            return speed > 0f ? -magnitude : magnitude;
        }

        /// <summary>Angular damping rate for the given submersion.</summary>
        public static float AngularDrag(float submersion)
        {
            return AngularDragGain * Clamp01(submersion);
        }

        /// <summary>Multiplier of the drive speed limits and the turn rate.</summary>
        public static float SpeedFactor(float submersion)
        {
            var s = Clamp01(submersion);
            var smooth = s * s * (3f - 2f * s);
            return 1f - MaxSpeedLoss * smooth;
        }

        /// <summary>
        /// Water standing <paramref name="depth"/> metres above the track
        /// bottom as a share (0..1) of a flood line <paramref name="floodHeight"/>
        /// metres up the hull.
        /// </summary>
        public static float FloodLevel(float depth, float floodHeight)
        {
            if (floodHeight <= 1e-4f) { return depth > 0f ? 1f : 0f; }
            return Clamp01(depth / floodHeight);
        }

        /// <summary>Whether a tank entering water this fast throws a splash, and which one.</summary>
        public static int SplashLevel(float speed)
        {
            var abs = Math.Abs(speed);
            if (abs >= SplashHeavySpeed) { return 2; }
            return abs >= SplashLightSpeed ? 1 : 0;
        }

        /// <summary>The <c>mm:ss</c> text of the 4.8.0 timer, rounded up.</summary>
        public static string FormatTimer(float seconds)
        {
            if (float.IsNaN(seconds) || seconds < 0f) { seconds = 0f; }
            var whole = (int)Math.Ceiling(seconds - 1e-4f);
            return (whole / 60).ToString("00") + ":" + (whole % 60).ToString("00");
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : v > 1f ? 1f : v;
        }
    }

    /// <summary>
    /// The drowning countdown. It runs while the tank is flooded, resets as
    /// soon as the hull roof clears the water and fires exactly once.
    /// </summary>
    public sealed class DrowningTimer
    {
        public DrowningTimer(float duration = WaterRules.DrownSeconds)
        {
            Duration = duration > 0f ? duration : WaterRules.DrownSeconds;
            Remaining = Duration;
        }

        public float Duration { get; private set; }

        public float Remaining { get; private set; }

        public bool Running { get; private set; }

        public bool Expired { get; private set; }

        /// <summary>Advances the clock; returns true on the tick it runs out.</summary>
        public bool Tick(bool flooded, float deltaTime)
        {
            if (Expired) { return false; }
            if (!flooded)
            {
                Running = false;
                Remaining = Duration;
                return false;
            }

            Running = true;
            Remaining -= Math.Max(0f, deltaTime);
            if (Remaining > 0f) { return false; }
            Remaining = 0f;
            Expired = true;
            return true;
        }

        public void Reset()
        {
            Running = false;
            Expired = false;
            Remaining = Duration;
        }
    }

    /// <summary>
    /// Height field of a map's water surfaces, built from the up-facing
    /// triangles of its water meshes and sampled on a coarse XZ grid.
    /// </summary>
    public sealed class WaterSurfaceField
    {
        private struct Triangle
        {
            public float Ax, Ay, Az, Bx, By, Bz, Cx, Cy, Cz;
            public float MinX, MinZ, MaxX, MaxZ;
        }

        private readonly List<Triangle> _triangles = new List<Triangle>();
        private Dictionary<long, List<int>> _cells;
        private float _cell = 8f;

        public int TriangleCount { get { return _triangles.Count; } }

        public float MinX { get; private set; } = float.MaxValue;
        public float MinZ { get; private set; } = float.MaxValue;
        public float MaxX { get; private set; } = float.MinValue;
        public float MaxZ { get; private set; } = float.MinValue;

        /// <summary>
        /// Adds one world-space triangle. Walls and downward faces are ignored:
        /// only a face whose normal points up is a surface a hull can sit in.
        /// </summary>
        public bool AddTriangle(
            float ax, float ay, float az,
            float bx, float by, float bz,
            float cx, float cy, float cz)
        {
            var ux = bx - ax; var uy = by - ay; var uz = bz - az;
            var vx = cx - ax; var vy = cy - ay; var vz = cz - az;
            var nx = uy * vz - uz * vy;
            var ny = uz * vx - ux * vz;
            var nz = ux * vy - uy * vx;
            var length = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length < 1e-6f || Math.Abs(ny) / length < 0.5f) { return false; }

            var t = new Triangle
            {
                Ax = ax, Ay = ay, Az = az, Bx = bx, By = by, Bz = bz, Cx = cx, Cy = cy, Cz = cz,
                MinX = Math.Min(ax, Math.Min(bx, cx)), MaxX = Math.Max(ax, Math.Max(bx, cx)),
                MinZ = Math.Min(az, Math.Min(bz, cz)), MaxZ = Math.Max(az, Math.Max(bz, cz)),
            };
            _triangles.Add(t);
            MinX = Math.Min(MinX, t.MinX); MaxX = Math.Max(MaxX, t.MaxX);
            MinZ = Math.Min(MinZ, t.MinZ); MaxZ = Math.Max(MaxZ, t.MaxZ);
            _cells = null;
            return true;
        }

        /// <summary>Indexes the triangles; sampling builds the index lazily too.</summary>
        public void Build(float cellSize = 8f)
        {
            _cell = cellSize > 0.5f ? cellSize : 8f;
            _cells = new Dictionary<long, List<int>>();
            for (var i = 0; i < _triangles.Count; i++)
            {
                var t = _triangles[i];
                var x0 = Cell(t.MinX); var x1 = Cell(t.MaxX);
                var z0 = Cell(t.MinZ); var z1 = Cell(t.MaxZ);
                for (var x = x0; x <= x1; x++)
                {
                    for (var z = z0; z <= z1; z++)
                    {
                        var key = Key(x, z);
                        List<int> list;
                        if (!_cells.TryGetValue(key, out list))
                        {
                            list = new List<int>();
                            _cells[key] = list;
                        }
                        list.Add(i);
                    }
                }
            }
        }

        /// <summary>
        /// Water surface height at (x, z). Where surfaces overlap the highest
        /// one wins, which is the one a tank would meet first.
        /// </summary>
        public bool TrySample(float x, float z, out float height)
        {
            height = 0f;
            if (_triangles.Count == 0 || x < MinX || x > MaxX || z < MinZ || z > MaxZ) { return false; }
            if (_cells == null) { Build(_cell); }
            List<int> list;
            if (!_cells.TryGetValue(Key(Cell(x), Cell(z)), out list)) { return false; }

            var found = false;
            var best = float.MinValue;
            for (var i = 0; i < list.Count; i++)
            {
                var t = _triangles[list[i]];
                if (x < t.MinX || x > t.MaxX || z < t.MinZ || z > t.MaxZ) { continue; }
                float h;
                if (!Inside(t, x, z, out h)) { continue; }
                if (h > best) { best = h; found = true; }
            }

            height = found ? best : 0f;
            return found;
        }

        private static bool Inside(Triangle t, float x, float z, out float height)
        {
            height = 0f;
            var d = (t.Bz - t.Cz) * (t.Ax - t.Cx) + (t.Cx - t.Bx) * (t.Az - t.Cz);
            if (Math.Abs(d) < 1e-9f) { return false; }
            var a = ((t.Bz - t.Cz) * (x - t.Cx) + (t.Cx - t.Bx) * (z - t.Cz)) / d;
            var b = ((t.Cz - t.Az) * (x - t.Cx) + (t.Ax - t.Cx) * (z - t.Cz)) / d;
            var c = 1f - a - b;
            const float eps = -1e-4f;
            if (a < eps || b < eps || c < eps) { return false; }
            height = a * t.Ay + b * t.By + c * t.Cy;
            return true;
        }

        private int Cell(float v) { return (int)Math.Floor(v / _cell); }

        private static long Key(int x, int z) { return ((long)x << 32) ^ (uint)z; }
    }
}
