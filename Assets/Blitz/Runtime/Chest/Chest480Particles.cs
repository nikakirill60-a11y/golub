using System;
using System.Collections.Generic;

namespace BlitzLegacy.Runtime.Chest
{
    /// <summary>
    /// Deterministic port of DAVA's ParticleEffectSystem (Scene3D/Systems/
    /// ParticleEffectSystem.cpp) for the 4.8.0 chest scene, simulated in DAVA
    /// space. It follows the engine step by step: effect states and repeats,
    /// group loops, particle generation from property lines, Van der Corput
    /// emission, simplified forces and over-life lines. DAVA's global random
    /// generator and the group address used as a sequence offset are replaced
    /// by one seeded xorshift generator, so a run is reproducible per seed.
    /// </summary>
    public sealed class Chest480Particles
    {
        /// <summary>DAVA's EPSILON (Math/MathConstants.h).</summary>
        public const float Epsilon = 0.000001f;
        /// <summary>Largest simulation step; longer frames are split.</summary>
        public const float MaxStep = 1f / 60f;
        private const float Pi = 3.14159265358979323846f;
        private const float Pi2 = Pi * 2f;
        private const float DegToRad = Pi / 180f;

        public enum EffectState { Waiting, Starting, Playing, Stopping, Stopped }

        public sealed class Layer
        {
            public Chest480Layer Source;
            public bool Single;
            public bool Looped;
            public bool InheritPosition;
            public bool RandomSpinDirection;
            public float PivotX, PivotY;
            public DavaPropertyLine Life, LifeVariation, Number, NumberVariation;
            public DavaPropertyLine Size, SizeVariation, SizeOverLife;
            public DavaPropertyLine Velocity, VelocityVariation, VelocityOverLife;
            public DavaPropertyLine Spin, SpinVariation, Angle, AngleVariation;
            public DavaPropertyLine ColorRandom, ColorOverLife, AlphaOverLife;
            public DavaPropertyLine[] Forces, ForcesOverLife;
        }

        public sealed class Emitter
        {
            public Chest480Emitter Source;
            public DavaPropertyLine EmissionVector, EmissionRange, EmissionAngle, Radius, Size, ColorOverLife;
            public Layer[] Layers;
        }

        public sealed class Particle
        {
            public float Life, LifeTime;
            public DavaVector3 Position, Speed;
            public float Angle, Spin;
            public DavaVector2 BaseSize, CurrSize;
            public DavaColor Color;
        }

        public sealed class Group
        {
            public Emitter Emitter;
            public Layer Layer;
            public DavaVector3 SpawnPosition;
            public bool VisibleLod;
            public bool Finishing;
            public float Time, LoopStartTime, LoopLayerStartTime, LoopDuration;
            public float ParticlesToGenerate;
            public uint ParticlesGenerated;
            public uint Offset;
            /// <summary>Oldest first. DAVA prepends, so its draw order is this list reversed.</summary>
            public readonly List<Particle> Particles = new List<Particle>();
        }

        public sealed class Effect
        {
            public Chest480Effect Source;
            public int Index;
            public float[] World;
            public float[] Rotation;
            public DavaVector3 Position;
            public EffectState State;
            public float Time;
            public uint Repeats;
            public readonly List<Group> Groups = new List<Group>();
        }

        private readonly Chest480Definition _definition;
        private readonly Emitter[] _emitters;
        private readonly List<DavaPropertyLine> _modifiable = new List<DavaPropertyLine>();
        private readonly List<Effect> _effects = new List<Effect>();
        private readonly Stack<Particle> _pool = new Stack<Particle>();
        private readonly DavaVector3[] _forceValues = new DavaVector3[8];
        private uint _random;

        public Chest480Particles(Chest480Definition definition, uint seed, float grade)
        {
            if (definition == null) throw new ArgumentNullException("definition");
            _definition = definition;
            _emitters = new Emitter[definition.emitters.Length];
            for (var i = 0; i < _emitters.Length; i++) _emitters[i] = Compile(definition.emitters[i]);
            for (var i = 0; i < definition.effects.Length; i++)
            {
                var source = definition.effects[i];
                var rotation = new float[16];
                Array.Copy(source.world, rotation, 16);
                _effects.Add(new Effect
                {
                    Source = source,
                    Index = i,
                    World = source.world,
                    Rotation = rotation,
                    Position = DavaVector3.From(source.world, 12),
                });
            }
            Restart(seed, grade);
        }

        public float SceneTime { get; private set; }
        public float Grade { get; private set; }
        public IList<Effect> Effects { get { return _effects.AsReadOnly(); } }
        public Chest480Definition Definition { get { return _definition; } }

        /// <summary>Restart the whole scene (open-more) with a new seed and grade.</summary>
        public void Restart(uint seed, float grade)
        {
            if (float.IsNaN(grade) || float.IsInfinity(grade)) throw new ArgumentOutOfRangeException("grade");
            _random = seed == 0u ? 0x9E3779B9u : seed;
            Grade = grade;
            foreach (var line in _modifiable) line.SetExternalValue(grade);
            SceneTime = 0f;
            foreach (var effect in _effects)
            {
                ClearGroups(effect);
                effect.State = EffectState.Waiting;
                effect.Time = 0f;
                effect.Repeats = 0u;
            }
        }

        /// <summary>Advance by <paramref name="seconds"/>, in steps of at most <see cref="MaxStep"/>.</summary>
        public void Advance(float seconds)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0f)
                throw new ArgumentOutOfRangeException("seconds");
            while (seconds > 0f)
            {
                var step = seconds > MaxStep ? MaxStep : seconds;
                seconds -= step;
                if (seconds < 1e-7f) { step += seconds; seconds = 0f; }
                Process(step);
            }
        }

        public int ParticleCount(string effectName)
        {
            var count = 0;
            foreach (var effect in _effects)
            {
                if (effectName != null && effect.Source.name != effectName) continue;
                foreach (var group in effect.Groups) count += group.Particles.Count;
            }
            return count;
        }

        public int TotalParticles { get { return ParticleCount(null); } }

        // ------------------------------------------------------------------
        // ParticleEffectSystem::Process
        // ------------------------------------------------------------------

        private void Process(float dt)
        {
            SceneTime += dt;
            foreach (var effect in _effects)
            {
                // ActionComponent "added to scene" starts fire once their delay
                // has passed; effects without an action autostart with the scene.
                if (effect.State == EffectState.Waiting && SceneTime >= effect.Source.delay - 1e-6f)
                    effect.State = EffectState.Starting;
                if (effect.State == EffectState.Starting) RunEffect(effect);
                if (effect.State != EffectState.Playing && effect.State != EffectState.Stopping) continue;

                UpdateEffect(effect, dt);
                var ended = effect.Source.stopWhenEmpty != 0 ? effect.Groups.Count == 0 : effect.Time > effect.Source.duration;
                if (ended)
                {
                    effect.Repeats++;
                    var limit = effect.Source.repeats;
                    var more = limit <= 0d || limit >= uint.MaxValue || effect.Repeats < limit;
                    if (more && effect.State != EffectState.Stopping)
                    {
                        if (effect.Source.clearOnRestart != 0) ClearGroups(effect);
                        RunEffect(effect);
                    }
                    else
                    {
                        effect.State = EffectState.Stopping;
                        foreach (var group in effect.Groups) group.Finishing = true;
                    }
                }
                if (effect.State == EffectState.Stopping && effect.Groups.Count == 0)
                    effect.State = EffectState.Stopped;
            }
        }

        private void RunEffect(Effect effect)
        {
            foreach (var instance in effect.Source.instances)
            {
                var emitter = _emitters[instance.emitter];
                foreach (var layer in emitter.Layers)
                {
                    effect.Groups.Add(new Group
                    {
                        Emitter = emitter,
                        Layer = layer,
                        SpawnPosition = DavaVector3.From(instance.position),
                        VisibleLod = layer.Source.activeLods[effect.Source.lod] != 0,
                        LoopLayerStartTime = layer.Source.startTime,
                        LoopDuration = layer.Source.endTime,
                        // Stands in for the group address DAVA adds to the index.
                        Offset = NextUInt(),
                    });
                }
            }
            effect.State = EffectState.Playing;
            effect.Time = 0f;
        }

        private void ClearGroups(Effect effect)
        {
            foreach (var group in effect.Groups)
            {
                foreach (var particle in group.Particles) _pool.Push(particle);
                group.Particles.Clear();
            }
            effect.Groups.Clear();
        }

        // ------------------------------------------------------------------
        // ParticleEffectSystem::UpdateEffect
        // ------------------------------------------------------------------

        private void UpdateEffect(Effect effect, float dt)
        {
            effect.Time += dt;
            for (var g = 0; g < effect.Groups.Count; g++)
            {
                var group = effect.Groups[g];
                var layer = group.Layer;
                var source = layer.Source;
                group.Time += dt;
                var groupEnd = layer.Looped ? source.loopEndTime : source.endTime;
                var loopTime = group.Time - group.LoopStartTime;
                if (group.Time > groupEnd) group.Finishing = true;
                if (!group.Finishing && layer.Looped && loopTime > group.LoopDuration)
                {
                    group.LoopStartTime = group.Time;
                    group.LoopLayerStartTime = source.deltaTime + source.deltaVariation * RandFloat();
                    group.LoopDuration = group.LoopLayerStartTime + (source.endTime - source.startTime)
                        + source.loopVariation * RandFloat();
                    loopTime = 0f;
                }

                var forces = layer.Forces.Length;
                if (group.Particles.Count > 0)
                    for (var f = 0; f < forces; f++)
                        _forceValues[f] = layer.Forces[f].IsNull ? new DavaVector3() : layer.Forces[f].Vector3(loopTime);

                var particles = group.Particles;
                var write = 0;
                for (var read = 0; read < particles.Count; read++)
                {
                    var particle = particles[read];
                    particle.Life += dt;
                    if (particle.Life >= particle.LifeTime)
                    {
                        _pool.Push(particle);
                        continue;
                    }
                    UpdateParticle(particle, layer, particle.Life / particle.LifeTime, forces, dt);
                    particles[write++] = particle;
                }
                if (write < particles.Count) particles.RemoveRange(write, particles.Count - write);

                if (!group.Finishing && loopTime > group.LoopLayerStartTime && group.VisibleLod)
                {
                    if (layer.Single)
                    {
                        if (particles.Count == 0) GenerateParticle(effect, group, loopTime);
                    }
                    else
                    {
                        var count = 0f;
                        if (!layer.Number.IsNull) count = layer.Number.Scalar(loopTime);
                        if (!layer.NumberVariation.IsNull) count += layer.NumberVariation.Scalar(loopTime) * RandFloat();
                        group.ParticlesToGenerate += count * dt;
                        while (group.ParticlesToGenerate >= 1f)
                        {
                            group.ParticlesToGenerate -= 1f;
                            GenerateParticle(effect, group, loopTime);
                        }
                    }
                }

                if (group.Finishing && particles.Count == 0)
                {
                    effect.Groups.RemoveAt(g);
                    g--;
                }
            }
        }

        /// <summary>UpdateRegularParticleData for the subset the chest uses.</summary>
        private void UpdateParticle(Particle particle, Layer layer, float overLife, int forces, float dt)
        {
            var velocityOverLife = layer.VelocityOverLife.IsNull ? 1f : layer.VelocityOverLife.Scalar(overLife);
            particle.Position = particle.Position + particle.Speed * (velocityOverLife * dt);
            particle.Angle += particle.Spin * dt;
            var acceleration = new DavaVector3();
            for (var f = 0; f < forces; f++)
            {
                var value = _forceValues[f];
                acceleration = acceleration + (layer.ForcesOverLife[f].IsNull ? value : value * layer.ForcesOverLife[f].Scalar(overLife));
            }
            particle.Speed = particle.Speed + acceleration * dt;
            if (!layer.SizeOverLife.IsNull)
            {
                var scale = layer.SizeOverLife.Vector2(overLife);
                particle.CurrSize = new DavaVector2(particle.BaseSize.x * scale.x, particle.BaseSize.y * scale.y);
            }
        }

        // ------------------------------------------------------------------
        // ParticleEffectSystem::GenerateNewParticle
        // ------------------------------------------------------------------

        private void GenerateParticle(Effect effect, Group group, float loopTime)
        {
            var layer = group.Layer;
            var particle = _pool.Count > 0 ? _pool.Pop() : new Particle();
            particle.Life = 0f;
            particle.Color = DavaColor.White;
            if (!layer.ColorRandom.IsNull) particle.Color = layer.ColorRandom.Color(RandFloat());
            if (!group.Emitter.ColorOverLife.IsNull)
            {
                var tint = group.Emitter.ColorOverLife.Color(group.Time);
                particle.Color = new DavaColor(particle.Color.r * tint.r, particle.Color.g * tint.g,
                    particle.Color.b * tint.b, particle.Color.a * tint.a);
            }

            particle.LifeTime = 0f;
            if (!layer.Life.IsNull) particle.LifeTime += layer.Life.Scalar(loopTime);
            if (!layer.LifeVariation.IsNull) particle.LifeTime += layer.LifeVariation.Scalar(loopTime) * RandFloat();

            var size = new DavaVector2(1f, 1f);
            if (!layer.Size.IsNull) size = layer.Size.Vector2(loopTime);
            if (!layer.SizeVariation.IsNull)
            {
                var variation = layer.SizeVariation.Vector2(loopTime);
                var r = RandFloat();
                size = new DavaVector2(size.x + variation.x * r, size.y + variation.y * r);
            }
            particle.BaseSize = size;
            particle.CurrSize = size;
            if (!layer.SizeOverLife.IsNull)
            {
                var scale = layer.SizeOverLife.Vector2(0f);
                particle.CurrSize = new DavaVector2(size.x * scale.x, size.y * scale.y);
            }

            particle.Angle = 0f;
            particle.Spin = 0f;
            if (!layer.Angle.IsNull) particle.Angle = layer.Angle.Scalar(loopTime) * DegToRad;
            if (!layer.AngleVariation.IsNull) particle.Angle += layer.AngleVariation.Scalar(loopTime) * RandFloat() * DegToRad;
            if (!layer.Spin.IsNull) particle.Spin = layer.Spin.Scalar(loopTime) * DegToRad;
            if (!layer.SpinVariation.IsNull) particle.Spin += layer.SpinVariation.Scalar(loopTime) * RandFloat() * DegToRad;
            if (layer.RandomSpinDirection) particle.Spin *= (NextUInt() & 1u) * 2f - 1f;

            PrepareEmitterParameters(effect, group, particle);

            var velocity = 0f;
            if (!layer.Velocity.IsNull) velocity += layer.Velocity.Scalar(loopTime);
            if (!layer.VelocityVariation.IsNull) velocity += layer.VelocityVariation.Scalar(loopTime) * RandFloat();
            particle.Speed = particle.Speed * velocity;
            if (!layer.InheritPosition) particle.Position = particle.Position + effect.Position;

            group.Particles.Add(particle);
            group.ParticlesGenerated++;
        }

        private void PrepareEmitterParameters(Effect effect, Group group, Particle particle)
        {
            var emitter = group.Emitter;
            var type = emitter.Source.type;
            var index = unchecked(group.ParticlesGenerated + group.Offset);
            var circle = type == 2 || type == 3;
            particle.Position = new DavaVector3();
            if (type == 1)
            {
                if (!emitter.Size.IsNull)
                {
                    var size = emitter.Size.Vector3(group.Time);
                    particle.Position = new DavaVector3(
                        size.x * (VanDerCorput(index, 3) - 0.5f),
                        size.y * (VanDerCorput(index, 2) - 0.5f),
                        size.z * (VanDerCorput(index, 5) - 0.5f));
                }
            }
            else if (circle)
            {
                var radius = emitter.Radius.IsNull ? 1f : emitter.Radius.Scalar(group.Time);
                var angleBase = emitter.EmissionAngle.IsNull ? 0f : emitter.EmissionAngle.Scalar(group.Time) * DegToRad;
                var angle = angleBase + Pi2 * VanDerCorput(index, 3);
                if (type == 2) radius = radius * (float)Math.Sqrt(RandFloat());
                particle.Position = new DavaVector3(radius * (float)Math.Cos(angle), radius * (float)Math.Sin(angle), 0f);
            }

            var emission = emitter.EmissionVector.IsNull ? new DavaVector3(0f, 0f, 1f) : emitter.EmissionVector.Vector3(group.Time);
            var power = emission.Length;
            if (circle && emitter.Source.shockwave != 0)
            {
                particle.Speed = particle.Position;
                var square = particle.Speed.SquareLength;
                if (square > Epsilon) particle.Speed = particle.Speed * (power / (float)Math.Sqrt(square));
            }
            else if (!emitter.EmissionRange.IsNull)
            {
                var theta = VanDerCorput(index, 3) * emitter.EmissionRange.Scalar(group.Time) * DegToRad * 0.5f;
                var phi = VanDerCorput(index, 4) * Pi2;
                var sinTheta = (float)Math.Sin(theta);
                particle.Speed = new DavaVector3(power * (float)Math.Cos(phi) * sinTheta,
                    power * (float)Math.Sin(phi) * sinTheta, power * (float)Math.Cos(theta));
            }
            else
            {
                particle.Speed = new DavaVector3(0f, 0f, power);
            }

            if (Math.Abs(emission.x) < Epsilon && Math.Abs(emission.y) < Epsilon)
            {
                if (emission.z < 0f)
                {
                    particle.Position = new DavaVector3(particle.Position.x, -particle.Position.y, -particle.Position.z);
                    particle.Speed = new DavaVector3(particle.Speed.x, -particle.Speed.y, -particle.Speed.z);
                }
            }
            else
            {
                var rotation = EmitterRotation(emission, power);
                particle.Position = particle.Position.Mul3(rotation);
                particle.Speed = particle.Speed.Mul3(rotation);
            }
            particle.Position = particle.Position + group.SpawnPosition;
            particle.Speed = TransformPreserveLength(particle.Speed, effect.Rotation);
            particle.Position = TransformPreserveLength(particle.Position, effect.Rotation);
        }

        /// <summary>
        /// ParticleEffectSystemDetails::GenerateEmitterRotationMatrix: rotate +Z
        /// onto the emission vector (Matrix3::CreateRotation, row vectors).
        /// </summary>
        public static float[] EmitterRotation(DavaVector3 vector, float power)
        {
            var axis = new DavaVector3(vector.y, -vector.x, 0f).Normalized();
            var angle = (float)Math.Acos(Math.Max(-1f, Math.Min(1f, vector.z / power)));
            return AxisRotation(axis, angle);
        }

        /// <summary>DAVA Matrix3::CreateRotation (row-major, applied as v * M).</summary>
        public static float[] AxisRotation(DavaVector3 r, float angle)
        {
            var c = (float)Math.Cos(angle);
            var s = (float)Math.Sin(angle);
            var k = 1f - c;
            return new[]
            {
                c + k * r.x * r.x, k * r.x * r.y - r.z * s, k * r.x * r.z + r.y * s,
                k * r.x * r.y + r.z * s, c + k * r.y * r.y, k * r.y * r.z - r.x * s,
                k * r.x * r.z - r.y * s, k * r.y * r.z + r.x * s, c + k * r.z * r.z,
            };
        }

        /// <summary>DAVA TransformPerserveLength: rotate by the world 3x3, keep the length.</summary>
        public static DavaVector3 TransformPreserveLength(DavaVector3 vector, float[] world)
        {
            var before = vector.SquareLength;
            var result = vector.MulRotation(world);
            var after = result.SquareLength;
            if (after > Epsilon) result = result * (float)Math.Sqrt(before / after);
            return result;
        }

        /// <summary>ParticlesRandom::VanDerCorputRnd.</summary>
        public static float VanDerCorput(uint n, uint numberBase)
        {
            var result = 0f;
            var denominator = (float)numberBase;
            while (n > 0u)
            {
                var digit = n % numberBase;
                result += digit / denominator;
                n /= numberBase;
                denominator *= numberBase;
            }
            return result;
        }

        // ------------------------------------------------------------------
        // Random numbers and compilation
        // ------------------------------------------------------------------

        private uint NextUInt()
        {
            var x = _random;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _random = x;
            return x;
        }

        /// <summary>Uniform in [0, 1], like DAVA Random::RandFloat.</summary>
        private float RandFloat()
        {
            return (NextUInt() >> 8) * (1f / 16777215f);
        }

        private DavaPropertyLine Line(Chest480Line source)
        {
            var line = new DavaPropertyLine(source);
            if (line.IsModifiable)
            {
                if (line.External != _definition.gradeVariable)
                    throw new InvalidOperationException("Unknown external particle value " + line.External);
                _modifiable.Add(line);
            }
            return line;
        }

        private Emitter Compile(Chest480Emitter source)
        {
            var emitter = new Emitter
            {
                Source = source,
                EmissionVector = Line(source.emissionVector),
                EmissionRange = Line(source.emissionRange),
                EmissionAngle = Line(source.emissionAngle),
                Radius = Line(source.radius),
                Size = Line(source.size),
                ColorOverLife = Line(source.colorOverLife),
                Layers = new Layer[source.layers.Length],
            };
            for (var i = 0; i < source.layers.Length; i++)
            {
                var l = source.layers[i];
                if (l.forces.Length > _forceValues.Length) throw new InvalidOperationException("Too many particle forces");
                var layer = new Layer
                {
                    Source = l,
                    Single = l.single != 0,
                    Looped = l.isLooped != 0,
                    InheritPosition = l.inheritPosition != 0,
                    RandomSpinDirection = l.randomSpinDirection != 0,
                    PivotX = l.pivot[0],
                    PivotY = l.pivot[1],
                    Life = Line(l.life),
                    LifeVariation = Line(l.lifeVariation),
                    Number = Line(l.number),
                    NumberVariation = Line(l.numberVariation),
                    Size = Line(l.size),
                    SizeVariation = Line(l.sizeVariation),
                    SizeOverLife = Line(l.sizeOverLife),
                    Velocity = Line(l.velocity),
                    VelocityVariation = Line(l.velocityVariation),
                    VelocityOverLife = Line(l.velocityOverLife),
                    Spin = Line(l.spin),
                    SpinVariation = Line(l.spinVariation),
                    Angle = Line(l.angle),
                    AngleVariation = Line(l.angleVariation),
                    ColorRandom = Line(l.colorRandom),
                    ColorOverLife = Line(l.colorOverLife),
                    AlphaOverLife = Line(l.alphaOverLife),
                    Forces = new DavaPropertyLine[l.forces.Length],
                    ForcesOverLife = new DavaPropertyLine[l.forces.Length],
                };
                for (var f = 0; f < l.forces.Length; f++)
                {
                    layer.Forces[f] = Line(l.forces[f].force);
                    layer.ForcesOverLife[f] = Line(l.forces[f].overLife);
                }
                emitter.Layers[i] = layer;
            }
            return emitter;
        }
    }
}
