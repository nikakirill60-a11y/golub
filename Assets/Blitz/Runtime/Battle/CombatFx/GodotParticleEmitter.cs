using System;
using System.Collections.Generic;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle.CombatFx
{
    /// <summary>
    /// The camera terms one draw of a Godot particle system reads, in Godot
    /// space: the normalised camera basis and its origin.
    /// </summary>
    public struct GodotView
    {
        public Vector3 X, Y, Z, Origin;

        /// <summary>The Godot camera of a Unity camera transform.</summary>
        public static GodotView FromUnity(Transform camera)
        {
            var g = GodotXform.FromUnity(camera.position, camera.rotation);
            return new GodotView
            {
                X = g.X.normalized,
                Y = g.Y.normalized,
                Z = g.Z.normalized,
                Origin = g.Origin,
            };
        }
    }

    /// <summary>Vertex streams a particle effect appends its quads to, in Unity space.</summary>
    public sealed class GodotFxGeometry
    {
        public readonly List<Vector3> Positions = new List<Vector3>();
        public readonly List<Vector2> Uvs = new List<Vector2>();
        public readonly List<Color> Colors = new List<Color>();

        public void Clear()
        {
            Positions.Clear();
            Uvs.Clear();
            Colors.Clear();
        }

        /// <summary>Appends one Godot-space vertex; the UV stays in Godot's top-down convention.</summary>
        public void Add(Vector3 godotPosition, Vector2 uv, Vector4 color)
        {
            Positions.Add(GodotFxMath.Mirror(godotPosition));
            Uvs.Add(uv);
            Colors.Add(new Color(color.x, color.y, color.z, color.w));
        }

        /// <summary>The two triangles of the quad that starts at <paramref name="first"/>.</summary>
        public static void AddQuadIndices(List<int> indices, int first)
        {
            // PlaneMesh: (prev + i - 1, prev + i, this + i - 1), (prev + i, this + i, this + i - 1).
            indices.Add(first);
            indices.Add(first + 1);
            indices.Add(first + 2);
            indices.Add(first + 1);
            indices.Add(first + 3);
            indices.Add(first + 2);
        }
    }

    /// <summary>
    /// A CPU port of one <c>GPUParticles3D</c> driven by a
    /// <c>ParticleProcessMaterial</c>, as Godot 4.7 runs it on the mobile
    /// renderer: the fixed-step clock of <c>ParticlesStorage::update_particles</c>,
    /// the emission phases of <c>particles.glsl</c>, the start and process
    /// functions the material generates, the transform alignment of
    /// <c>particles_copy.glsl</c> and the vertex stage of the
    /// <c>StandardMaterial3D</c> draw pass. It keeps the same seeds and the
    /// same order of random draws, so a system restarted with one seed lays
    /// out the same particles Godot does.
    /// </summary>
    public sealed class GodotParticleEmitter
    {
        private const float DegreeToRad = GodotFxMath.ShaderPi / 180f;

        public readonly GodotEmitterData Data;

        private readonly GodotXform[] _xform;
        private readonly Vector3[] _velocity;
        private readonly Vector4[] _custom;
        private readonly Vector4[] _userdata;
        private readonly Vector4[] _color;
        private readonly uint[] _cycleOf;
        private readonly bool[] _active;

        private double _phase;
        private uint _cycle;
        private bool _clear;
        private bool _restartRequest;
        private bool _emitting;
        private bool _inactive = true;
        private float _inactiveTime;
        private float _remainder;
        private uint _randomSeed;

        public GodotParticleEmitter(GodotEmitterData data)
        {
            Data = data;
            var n = data.Amount;
            _xform = new GodotXform[n];
            _velocity = new Vector3[n];
            _custom = new Vector4[n];
            _userdata = new Vector4[n];
            _color = new Vector4[n];
            _cycleOf = new uint[n];
            _active = new bool[n];
            for (var i = 0; i < n; i++)
            {
                _xform[i] = GodotXform.Identity;
            }
        }

        /// <summary><c>RANDOM_SEED</c> of the current run.</summary>
        public uint RandomSeed { get { return _randomSeed; } }

        /// <summary>The emitter's RenderingServer <c>emitting</c> flag.</summary>
        public bool Emitting { get { return _emitting; } }

        /// <summary><c>frame_remainder</c> after the last render frame.</summary>
        public float FrameRemainder { get { return _remainder; } }

        /// <summary>The system phase, 0..1 over one lifetime.</summary>
        public double Phase { get { return _phase; } }

        /// <summary>
        /// <c>!particles_is_inactive</c>: the scene cull keeps the instance
        /// (and so draws it) until an idle one-shot system goes inactive.
        /// </summary>
        public bool Drawn { get { return Data.Visible && !(_inactive && !_emitting); } }

        /// <summary>
        /// Nothing left to simulate or draw until the next restart: hidden,
        /// or finished emitting with every particle gone. <see cref="Advance"/>
        /// returns straight away for such an emitter.
        /// </summary>
        public bool Dormant { get { return !_restartRequest && (!Data.Visible || (_inactive && !_emitting)); } }

        /// <summary>How many particles are alive right now.</summary>
        public int ActiveCount
        {
            get
            {
                var count = 0;
                for (var i = 0; i < _active.Length; i++)
                {
                    if (_active[i]) { count++; }
                }

                return count;
            }
        }

        public bool IsActive(int index) { return _active[index]; }
        public GodotXform ParticleTransform(int index) { return _xform[index]; }
        public Vector3 ParticleVelocity(int index) { return _velocity[index]; }
        public Vector4 ParticleCustom(int index) { return _custom[index]; }
        public Vector4 ParticleColor(int index) { return _color[index]; }

        /// <summary>
        /// <c>GPUParticles3D.restart()</c>: a new seed unless the emitter pins
        /// one, a restart request for the next update and emitting on.
        /// </summary>
        public void Restart(uint seed)
        {
            _randomSeed = Data.UseFixedSeed ? Data.Seed : seed;
            _restartRequest = true;
            _emitting = true;
        }

        /// <summary>
        /// One render frame of <c>ParticlesStorage::update_particles</c> with
        /// the frame's delta and the emitter's global transform at draw time.
        /// </summary>
        public void Advance(float frameDelta, GodotXform emitterGlobal)
        {
            if (!Data.Visible)
            {
                return;
            }

            if (_restartRequest)
            {
                _phase = 0;
                _clear = true;
                _restartRequest = false;
                _remainder = 0f;
            }

            if (_inactive && !_emitting)
            {
                return;
            }

            if (_emitting)
            {
                if (_inactive)
                {
                    _phase = 0;
                    _clear = true;
                }

                _inactive = false;
                _inactiveTime = 0f;
            }
            else
            {
                _inactiveTime += Data.SpeedScale * frameDelta;
                if (_inactiveTime > Data.Lifetime * 1.2f)
                {
                    _inactive = true;
                    return;
                }
            }

            var frameTime = Data.FixedFps > 0 ? 1f / Data.FixedFps : 1f / 30f;
            if (_clear && Data.Preprocess > 0f)
            {
                var pre = Data.Preprocess;
                while (pre > 0f)
                {
                    Process(frameTime > pre ? pre : frameTime, emitterGlobal);
                    pre -= frameTime;
                }
            }

            var timeScale = Mathf.Max(Data.SpeedScale, 0f);
            if (Data.FixedFps > 0)
            {
                var delta = frameDelta > 0.1f ? 0.1f : frameDelta < 0f ? 0f : frameDelta;
                var todo = _remainder + delta * timeScale;
                while (todo >= frameTime || _clear)
                {
                    Process(frameTime, emitterGlobal);
                    todo -= frameTime;
                }

                _remainder = todo;
            }
            else
            {
                Process(frameDelta * timeScale, emitterGlobal);
            }
        }

        /// <summary><c>_particles_process</c> plus the particles compute pass.</summary>
        private void Process(float delta, GodotXform emitterGlobal)
        {
            var newPhase = (_phase + delta / (double)Data.Lifetime) % 1.0;
            if (_clear)
            {
                _cycle = 0;
            }
            else if (newPhase < _phase)
            {
                if (Data.OneShot)
                {
                    _emitting = false;
                }

                _cycle++;
            }

            var emitting = _emitting;
            var system = (float)newPhase;
            var previous = (float)_phase;
            var emission = Data.LocalCoords ? GodotXform.Identity : emitterGlobal;
            var total = Data.Amount;

            for (var i = 0; i < total; i++)
            {
                var localDelta = delta;
                var restart = false;
                if (_clear)
                {
                    _color[i] = Vector4.one;
                    _custom[i] = Vector4.zero;
                    _velocity[i] = Vector3.zero;
                    _active[i] = false;
                    _cycleOf[i] = 0;
                    _xform[i] = GodotXform.Identity;
                }

                if (emitting)
                {
                    var restartPhase = (float)i / total;
                    if (Data.Randomness > 0f)
                    {
                        unchecked
                        {
                            var seed = _cycle;
                            if (restartPhase >= system)
                            {
                                seed -= 1u;
                            }

                            seed *= (uint)total;
                            seed += (uint)i;
                            var random = (GodotFxMath.Hash(seed) % 65536u) / 65536f;
                            restartPhase += Data.Randomness * random * 1f / total;
                        }
                    }

                    restartPhase *= 1f - Data.Explosiveness;
                    if (system > previous)
                    {
                        if (restartPhase >= previous && restartPhase < system)
                        {
                            restart = true;
                            if (Data.FractDelta)
                            {
                                localDelta = (system - restartPhase) * Data.Lifetime;
                            }
                        }
                    }
                    else if (delta > 0f)
                    {
                        if (restartPhase >= previous)
                        {
                            restart = true;
                            if (Data.FractDelta)
                            {
                                localDelta = (1f - restartPhase + system) * Data.Lifetime;
                            }
                        }
                        else if (restartPhase < system)
                        {
                            restart = true;
                            if (Data.FractDelta)
                            {
                                localDelta = (system - restartPhase) * Data.Lifetime;
                            }
                        }
                    }

                    if (restart)
                    {
                        _active[i] = true;
                        _cycleOf[i] = _cycle;
                    }
                }

                var active = _active[i];
                var number = unchecked(_cycleOf[i] * (uint)total + (uint)i);
                if (restart && active)
                {
                    active = Start(i, number, emission);
                }

                if (active)
                {
                    active = ProcessParticle(i, number, emission, localDelta);
                }

                _active[i] = active;
            }

            _phase = newPhase;
            _clear = false;
        }

        private struct Parameters
        {
            public float Scale, Hue, AnimSpeed, AnimOffset, Lifetime;
            public Vector4 Color;
            public float Angle, AngularVelocity, Velocity, DirectionalVelocity, RadialVelocity, OrbitVelocity;
            public float LinearAccel, RadialAccel, TangentAccel, Damping;
        }

        /// <summary>
        /// The display, dynamics and physical parameter blocks, fifteen draws
        /// from <c>hash(number + 1 + RANDOM_SEED)</c> in the order the
        /// generated shader makes them.
        /// </summary>
        private Parameters Initial(ref uint seed)
        {
            var d = Data;
            var p = new Parameters();
            var scale = GodotFxMath.Mix(d.ScaleMin, d.ScaleMax, GodotFxMath.Rand(ref seed));
            p.Scale = GodotFxMath.Sign(scale) * Mathf.Max(Mathf.Abs(scale), 0.001f);
            p.Hue = GodotFxMath.ShaderPi * 2f * GodotFxMath.Mix(d.HueMin, d.HueMax, GodotFxMath.Rand(ref seed));
            p.AnimSpeed = GodotFxMath.Mix(d.AnimSpeedMin, d.AnimSpeedMax, GodotFxMath.Rand(ref seed));
            p.AnimOffset = GodotFxMath.Mix(d.AnimOffsetMin, d.AnimOffsetMax, GodotFxMath.Rand(ref seed));
            p.Lifetime = 1f - d.LifetimeRandomness * GodotFxMath.Rand(ref seed);
            p.Color = d.Color;

            p.Angle = GodotFxMath.Mix(d.AngleMin, d.AngleMax, GodotFxMath.Rand(ref seed));
            p.AngularVelocity = GodotFxMath.Mix(d.AngularVelocityMin, d.AngularVelocityMax, GodotFxMath.Rand(ref seed));
            p.Velocity = GodotFxMath.Mix(d.VelocityMin, d.VelocityMax, GodotFxMath.Rand(ref seed));
            p.DirectionalVelocity = GodotFxMath.Mix(d.DirectionalVelocityMin, d.DirectionalVelocityMax, GodotFxMath.Rand(ref seed));
            p.RadialVelocity = GodotFxMath.Mix(d.RadialVelocityMin, d.RadialVelocityMax, GodotFxMath.Rand(ref seed));
            p.OrbitVelocity = GodotFxMath.Mix(d.OrbitVelocityMin, d.OrbitVelocityMax, GodotFxMath.Rand(ref seed));

            p.LinearAccel = GodotFxMath.Mix(d.LinearAccelMin, d.LinearAccelMax, GodotFxMath.Rand(ref seed));
            p.RadialAccel = GodotFxMath.Mix(d.RadialAccelMin, d.RadialAccelMax, GodotFxMath.Rand(ref seed));
            p.TangentAccel = GodotFxMath.Mix(d.TangentialAccelMin, d.TangentialAccelMax, GodotFxMath.Rand(ref seed));
            p.Damping = GodotFxMath.Mix(d.DampingMin, d.DampingMax, GodotFxMath.Rand(ref seed));
            return p;
        }

        /// <summary><c>process_display_param</c>: the curves and ramp at one point of life.</summary>
        private void Display(ref Parameters p, float life)
        {
            var d = Data;
            if (d.ScaleCurve != null)
            {
                p.Scale *= d.ScaleCurve.Sample(life);
            }

            if (d.ColorRamp != null)
            {
                p.Color = Vector4.Scale(p.Color, d.ColorRamp.Sample(life));
            }

            if (d.AlphaCurve != null)
            {
                p.Color.w *= d.AlphaCurve.Sample(life);
            }

            p.Color = GodotFxMath.RotateHue(p.Color, p.Hue);
        }

        /// <summary>The generated <c>start()</c>. Returns <c>ACTIVE</c>.</summary>
        private bool Start(int i, uint number, GodotXform emission)
        {
            var d = Data;
            var seed = GodotFxMath.Hash(unchecked(number + 1u + _randomSeed));
            var p = Initial(ref seed);
            Display(ref p, 0f);
            var active = !(GodotFxMath.Rand(ref seed) > d.AmountRatio);

            _custom[i] = new Vector4(p.Angle, 0f, 0f, p.Lifetime);
            _userdata[i].w = 0f;
            _color[i] = p.Color;

            var local = GodotXform.Identity;
            local.Origin = InitialPosition(ref seed);
            _xform[i] = emission * local;

            var velocity = GodotFxMath.SpreadDirection(ref seed, d.Spread, d.Flatness, d.Direction) * p.Velocity;
            velocity = emission.Vector(velocity);
            _userdata[i] = new Vector4(velocity.x, velocity.y, velocity.z, _userdata[i].w);
            return active;
        }

        /// <summary><c>calculate_initial_position</c> for the point and sphere shapes.</summary>
        private Vector3 InitialPosition(ref uint seed)
        {
            var d = Data;
            var position = Vector3.zero;
            if (d.EmissionShape == 1)
            {
                var s = GodotFxMath.Rand(ref seed) * 2f - 1f;
                var t = GodotFxMath.Rand(ref seed) * 2f * GodotFxMath.ShaderPi;
                var amount = GodotFxMath.Rand(ref seed);
                var radius = d.EmissionRadius * Mathf.Sqrt(1f - s * s);
                position = new Vector3(radius * Mathf.Cos(t), radius * Mathf.Sin(t), d.EmissionRadius * s) * amount;
            }

            return Vector3.Scale(position, d.EmissionScale) + d.EmissionOffset;
        }

        /// <summary>The generated <c>process()</c>. Returns <c>ACTIVE</c>.</summary>
        private bool ProcessParticle(int i, uint number, GodotXform emission, float delta)
        {
            var d = Data;
            var seed = GodotFxMath.Hash(unchecked(number + 1u + _randomSeed));
            var p = Initial(ref seed);
            var custom = _custom[i];
            var xform = _xform[i];
            var user = _userdata[i];
            var active = true;

            var firstFrame = custom.y == 0f;
            custom.y += delta / d.Lifetime;
            var life = custom.y / p.Lifetime;
            if (custom.y > custom.w)
            {
                active = false;
            }

            var controlled = RadialDisplacement(p, ref seed, xform.Origin, emission, delta);

            var u = new Vector3(user.x, user.y, user.z);
            var position = xform.Origin;
            var force = d.Gravity;
            if (GodotFxMath.Length(u) > 0f)
            {
                force += GodotFxMath.Normalize(u) * p.LinearAccel;
            }

            var diff = position - emission.Origin;
            if (GodotFxMath.Length(diff) > 0f)
            {
                force += GodotFxMath.Normalize(diff) * p.RadialAccel;
            }

            var crossDiff = Vector3.Cross(GodotFxMath.Normalize(diff), GodotFxMath.Normalize(d.Gravity));
            if (GodotFxMath.Length(crossDiff) > 0f)
            {
                force += GodotFxMath.Normalize(crossDiff) * p.TangentAccel;
            }

            u += force * delta;
            if (p.Damping > 0f)
            {
                var v = GodotFxMath.Length(u);
                if (!d.DampingAsFriction)
                {
                    v -= p.Damping * delta;
                }
                else if (v > 0.001f)
                {
                    v -= v * v * p.Damping * 0.05f * delta;
                }

                u = v < 0f ? Vector3.zero : GodotFxMath.Normalize(u) * v;
            }

            var final = controlled + u;
            _velocity[i] = final;
            xform.Origin += final * delta;

            Display(ref p, life);
            custom.x = (p.Angle + custom.y * d.Lifetime * p.AngularVelocity) * DegreeToRad;
            _color[i] = p.Color;

            if (d.AlignY)
            {
                xform.Y = GodotFxMath.Length(final) > 0f
                    ? GodotFxMath.NormalizeOrElse(final, new Vector3(0f, 1f, 0f))
                    : GodotFxMath.NormalizeOrElse(xform.Y, new Vector3(0f, 1f, 0f));
                var axisX = GodotFxMath.NormalizeOrElse(xform.X, new Vector3(1f, 0f, 0f));
                // GLSL == on vectors is exact, unlike Vector3's tolerant operator.
                if (xform.Y.x == axisX.x && xform.Y.y == axisX.y && xform.Y.z == axisX.z)
                {
                    xform.X = GodotFxMath.NormalizeOrElse(Vector3.Cross(xform.Y, xform.Z), new Vector3(1f, 0f, 0f));
                    xform.Z = GodotFxMath.NormalizeOrElse(Vector3.Cross(xform.X, xform.Y), new Vector3(0f, 0f, 1f));
                }
                else
                {
                    xform.Z = GodotFxMath.NormalizeOrElse(Vector3.Cross(xform.X, xform.Y), new Vector3(0f, 0f, 1f));
                    xform.X = GodotFxMath.NormalizeOrElse(Vector3.Cross(xform.Y, xform.Z), new Vector3(1f, 0f, 0f));
                }
            }
            else
            {
                xform.X = GodotFxMath.NormalizeOrElse(xform.X, new Vector3(1f, 0f, 0f));
                xform.Y = GodotFxMath.NormalizeOrElse(xform.Y, new Vector3(0f, 1f, 0f));
                xform.Z = GodotFxMath.NormalizeOrElse(xform.Z, new Vector3(0f, 0f, 1f));
            }

            if (d.RotateY)
            {
                var c = Mathf.Cos(custom.x);
                var s = Mathf.Sin(custom.x);
                xform.X = new Vector3(c, 0f, -s);
                xform.Y = new Vector3(0f, 1f, 0f);
                xform.Z = new Vector3(s, 0f, c);
            }

            var scale = firstFrame
                ? (p.Scale < 0f ? -1f : 1f) * Mathf.Max(Mathf.Abs(p.Scale), 0.001f)
                : Mathf.Max(Mathf.Abs(p.Scale), 0.001f);
            xform.X *= scale;
            xform.Y *= scale;
            xform.Z *= scale;

            custom.z = p.AnimOffset + life * p.AnimSpeed;
            if (custom.y > custom.w)
            {
                active = false;
            }

            _custom[i] = custom;
            _xform[i] = xform;
            _userdata[i] = new Vector4(u.x, u.y, u.z, user.w);
            return active;
        }

        /// <summary><c>process_radial_displacement</c>.</summary>
        private Vector3 RadialDisplacement(Parameters p, ref uint seed, Vector3 position, GodotXform emission, float delta)
        {
            if (delta < 0.001f)
            {
                return Vector3.zero;
            }

            var pivot = emission.Point(Data.VelocityPivot);
            var offset = position - pivot;
            Vector3 displacement;
            if (GodotFxMath.Length(offset) > 0.01f)
            {
                displacement = GodotFxMath.Normalize(offset) * p.RadialVelocity;
            }
            else
            {
                displacement = GodotFxMath.SpreadDirection(ref seed, 360f, Data.Flatness, Data.Direction) * p.RadialVelocity;
            }

            if (p.RadialVelocity < 0f)
            {
                displacement = GodotFxMath.Normalize(displacement)
                    * Mathf.Min(Mathf.Abs(p.RadialVelocity), GodotFxMath.Length(offset) / delta);
            }

            return displacement;
        }

        /// <summary>
        /// <c>custom_aabb.get_center()</c> after the emitter transform: the
        /// point the mobile renderer sorts this transparent instance by.
        /// </summary>
        public Vector3 SortCenter(GodotXform emitterGlobal, float aabbExtent)
        {
            var half = -aabbExtent * 0.5f;
            return emitterGlobal.Point(new Vector3(half, half, half));
        }

        /// <summary>
        /// The copy pass and the material vertex stage for every live
        /// particle: one quad each, in index order, appended in Unity space.
        /// </summary>
        public void AppendGeometry(GodotView view, GodotXform emitterGlobal, GodotFxGeometry geometry, List<int> indices)
        {
            if (!Drawn)
            {
                return;
            }

            var d = Data;
            var material = d.Material;
            var sortDirection = view.Z;
            if (d.LocalCoords)
            {
                sortDirection = emitterGlobal.VectorTransposed(sortDirection).normalized;
            }

            var remainder = d.Interpolate ? _remainder : 0f;
            for (var i = 0; i < d.Amount; i++)
            {
                if (!_active[i])
                {
                    continue;
                }

                var xform = Align(i, _xform[i], sortDirection, view.Y);
                xform.Origin += _velocity[i] * remainder;
                var model = d.LocalCoords ? emitterGlobal * xform : xform;
                var first = geometry.Positions.Count;
                var custom = _custom[i];
                var color = _color[i];

                if (material.BillboardParticles)
                {
                    var c = Mathf.Cos(custom.x);
                    var s = Mathf.Sin(custom.x);
                    var axisX = view.X * c - view.Y * s;
                    var axisY = view.X * s + view.Y * c;
                    var billboard = new GodotXform(axisX, axisY, view.Z, model.Origin);
                    var frames = material.HFrames * material.VFrames;
                    var frame = Mathf.Floor(custom.z * frames);
                    frame = material.AnimLoop ? frame - frames * Mathf.Floor(frame / frames) : Mathf.Clamp(frame, 0f, frames - 1f);
                    var frameOffset = new Vector2(
                        (frame - material.HFrames * Mathf.Floor(frame / material.HFrames)) / material.HFrames,
                        Mathf.Floor((frame + 0.5f) / material.HFrames) / material.VFrames);
                    for (var corner = 0; corner < 4; corner++)
                    {
                        Vector2 uv;
                        var vertex = QuadVertex(corner, out uv);
                        uv = new Vector2(uv.x / material.HFrames, uv.y / material.VFrames) + frameOffset;
                        geometry.Add(billboard.Point(vertex), uv, color);
                    }
                }
                else
                {
                    for (var corner = 0; corner < 4; corner++)
                    {
                        Vector2 uv;
                        var vertex = QuadVertex(corner, out uv);
                        geometry.Add(model.Point(vertex), uv, color);
                    }
                }

                GodotFxGeometry.AddQuadIndices(indices, first);
            }
        }

        /// <summary>
        /// <c>PlaneMesh</c> without subdivisions: corner <c>i + 2 j</c>, the
        /// UV flipped to match QuadMesh.
        /// </summary>
        private Vector3 QuadVertex(int corner, out Vector2 uv)
        {
            var i = corner & 1;
            var j = corner >> 1;
            var size = Data.Size;
            var x = -size.x * 0.5f + i * size.x;
            var z = -size.y * 0.5f + j * size.y;
            uv = new Vector2(1f - i, 1f - j);
            Vector3 point;
            switch (Data.Orientation)
            {
                case 0: point = new Vector3(0f, z, x); break;
                case 1: point = new Vector3(-x, 0f, -z); break;
                default: point = new Vector3(-x, z, 0f); break;
            }

            return point + Data.CenterOffset;
        }

        /// <summary>The <c>align_mode</c> switch of <c>particles_copy.glsl</c>.</summary>
        private GodotXform Align(int i, GodotXform t, Vector3 sortDirection, Vector3 alignUp)
        {
            var d = Data;
            switch (d.TransformAlign)
            {
                case 1:
                {
                    var custom = _custom[i];
                    float angle;
                    switch (d.AlignChannel)
                    {
                        case 1: angle = custom.x; break;
                        case 2: angle = custom.y; break;
                        case 3: angle = custom.z; break;
                        case 4: angle = custom.w; break;
                        default: angle = 0f; break;
                    }

                    var axis = GodotFxMath.Normalize(sortDirection);
                    var s = Mathf.Sin(angle);
                    var c = Mathf.Cos(angle);
                    var oc = 1f - c;
                    var col0 = new Vector3(oc * axis.x * axis.x + c, oc * axis.x * axis.y - axis.z * s, oc * axis.z * axis.x + axis.y * s);
                    var col1 = new Vector3(oc * axis.x * axis.y + axis.z * s, oc * axis.y * axis.y + c, oc * axis.y * axis.z - axis.x * s);
                    var col2 = new Vector3(oc * axis.z * axis.x - axis.y * s, oc * axis.y * axis.z + axis.x * s, oc * axis.z * axis.z + c);
                    var newUp = col0 * alignUp.x + col1 * alignUp.y + col2 * alignUp.z;
                    var local = new GodotXform(GodotFxMath.Normalize(Vector3.Cross(newUp, sortDirection)), newUp, sortDirection, Vector3.zero);
                    return new GodotXform(local.Vector(t.X), local.Vector(t.Y), local.Vector(t.Z), t.Origin);
                }
                case 2:
                {
                    var v = GodotFxMath.Normalize(_velocity[i]);
                    var len = new Vector3(GodotFxMath.Length(t.X), GodotFxMath.Length(t.Y), GodotFxMath.Length(t.Z));
                    switch (d.AlignAxis)
                    {
                        case 0:
                            t.X = v;
                            t.Y = GodotFxMath.Normalize(Vector3.Cross(t.Z / len.z, t.X));
                            t.Z = Vector3.Cross(t.X, t.Y);
                            break;
                        case 1:
                            t.X = GodotFxMath.Normalize(Vector3.Cross(v, t.Z / len.z));
                            t.Y = v;
                            t.Z = Vector3.Cross(t.X, t.Y);
                            break;
                        default:
                            t.X = GodotFxMath.Normalize(Vector3.Cross(t.Y / len.y, v));
                            t.Z = v;
                            t.Y = GodotFxMath.Normalize(Vector3.Cross(t.Z, t.X));
                            break;
                    }

                    t.X *= len.x;
                    t.Y *= len.y;
                    t.Z *= len.z;
                    return t;
                }
                case 3:
                {
                    var v = _velocity[i];
                    var sv = v - sortDirection * Vector3.Dot(sortDirection, v);
                    if (GodotFxMath.Length(sv) == 0f)
                    {
                        sv = alignUp;
                    }

                    sv = GodotFxMath.Normalize(sv);
                    var lx = GodotFxMath.Length(t.X);
                    var ly = GodotFxMath.Length(t.Y);
                    var lz = GodotFxMath.Length(t.Z);
                    t.X = GodotFxMath.Normalize(Vector3.Cross(sv, sortDirection)) * lx;
                    t.Y = sv * ly;
                    t.Z = sortDirection * lz;
                    return t;
                }
                case 4:
                {
                    var len = new Vector3(GodotFxMath.Length(t.X), GodotFxMath.Length(t.Y), GodotFxMath.Length(t.Z));
                    if (d.AlignAxis == 0)
                    {
                        t.X = GodotFxMath.Normalize(t.X);
                        t.Y = GodotFxMath.Normalize(Vector3.Cross(sortDirection, t.X));
                        t.Z = Vector3.Cross(t.X, t.Y);
                    }
                    else if (d.AlignAxis == 1)
                    {
                        t.Y = GodotFxMath.Normalize(t.Y);
                        t.X = GodotFxMath.Normalize(Vector3.Cross(t.Y, sortDirection));
                        t.Z = Vector3.Cross(t.X, t.Y);
                    }
                    else
                    {
                        return t;
                    }

                    t.X *= len.x;
                    t.Y *= len.y;
                    t.Z *= len.z;
                    return t;
                }
                default:
                    return t;
            }
        }
    }
}
