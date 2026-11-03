// Particle effects of the hangar (ParticleEffectComponent with the client's
// 11.21 particle and mesh layers) rebuilt as Unity particle systems drawn by
// HalloweenHangar/Particle.
//
// One particle system per layer and drawing basis: the client draws every
// particle once per orientation bit (camera, emitter X, Y, Z facing), so a
// layer with several bits becomes twin systems that share the random seed and
// with it every particle. Billboards are mesh particles drawing a per layer
// quad with the client's pivot, size and corner order (ParticleRenderObject);
// mesh layers draw their polygon group baked with the layer's size, angle and
// facing. Emission windows, single particles, startFromTime (prewarm), clear
// on restart, forces and the per particle random values follow
// ParticleEffectSystem; property lines that vary over a particle's life are
// baked into a per layer ramp texture read by the shader.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using MinMaxCurve = UnityEngine.ParticleSystem.MinMaxCurve;

namespace BlitzLegacy.Runtime.Hangar.Halloween
{
    public sealed class ParticleBuildContext
    {
        public MaterialContext materials;
        public Shader shader;
        public List<MeshRecord> meshes;
        public Func<int, Transform> node;
    }

    public static class HalloweenHangarParticles
    {
        public static string Folder => HH.Generated + "/Particles";
        public static string MaterialFolder => Folder + "/Materials";
        public static string RampFolder => Folder + "/Ramps";
        public static string MeshFolder => Folder + "/Meshes";

        // ParticleLayer::eParticleOrientation
        const int CameraFacing = 1, XFacing = 2, YFacing = 4, ZFacing = 8, WorldAlign = 16;
        // ParticleEmitter::eType
        const int EmitterPoint = 0, EmitterRect = 1;
        // DAVA EPSILON
        const float Epsilon = 1e-6f;
        // ApplyWind adds power * dt * dt * 100 per frame: an acceleration of
        // power * 100 * dt, taken at the client's 60 frames per second.
        const float WindScale = 100f, WindFrameRate = 60f;
        const int MaxBursts = 50;

        // Sense of a view aligned particle's z rotation: +1 when Unity turns the
        // quad from the camera's right towards its up. Pinned for this Unity
        // version by HalloweenHangarProbeTests (measured, not assumed).
        public static float ViewSign = 1f;

        public static readonly List<ParticleSystem> Systems = new List<ParticleSystem>();
        public static int EffectCount, EmitterCount, LayerCount, SkippedLayers, RampCount, MeshCount, ForceFields, KillTriggers;
        static int meshPivots;

        static readonly List<ParticleSystemVertexStream> Streams = new List<ParticleSystemVertexStream>
        {
            ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Normal, ParticleSystemVertexStream.Color,
            ParticleSystemVertexStream.UV, ParticleSystemVertexStream.AgePercent, ParticleSystemVertexStream.InvStartLifetime,
            ParticleSystemVertexStream.StableRandomXYZW,
        };

        static readonly string[] UnportedFlags =
        {
            "enableAlphaRemap", "enableVertexAnimation", "enableVertexAnimationMask", "waterDeformation", "foregroundEffect",
            "usePerspectiveMapping", "useThreePointGradient", "randomOffsetOnStart", "applyGlobalForces",
            "alphaOverLifeBehavior", "colorOverLifeBehavior",
        };

        sealed class EffectInfo
        {
            public int index;
            public string path;
            public Transform root;
            public float duration, startFrom, startDelay, startDelayVariation;
            public bool autoplay, clearOnRestart;
        }

        // When and how long a layer's particles live, in Unity's terms.
        sealed class Timeline
        {
            public float duration, lifeMin, lifeMax, lifeScale = 1f, rate;
            public bool prewarm;
            public AnimationCurve rateCurve;
            public readonly List<ParticleSystem.Burst> bursts = new List<ParticleSystem.Burst>();
            public int maxParticles;
            public string summary;
        }

        struct Basis
        {
            public int bit;
            public string suffix;
        }

        public static void Build(ParticleBuildContext ctx)
        {
            HH.Require(ctx.shader != null, "particles: the particle shader is missing");
            Systems.Clear();
            EffectCount = EmitterCount = LayerCount = SkippedLayers = RampCount = MeshCount = ForceFields = KillTriggers = 0;
            meshPivots = 0;
            var effects = ctx.materials.scene.Req("effects").Items;
            for (int i = 0; i < effects.Count; i++)
                BuildEffect(ctx, i, effects[i]);
            if (meshPivots > 0)
                HH.Warn($"particles: {meshPivots} mesh layers keep a pivot point; their meshes are drawn around the mesh origin");
            HH.Log($"particles: {EffectCount} effects, {EmitterCount} emitters, {LayerCount} layers ({SkippedLayers} not drawn), " +
                   $"{Systems.Count} particle systems, {RampCount} ramps, {MeshCount} meshes, {ForceFields} force fields, {KillTriggers} kill triggers");
        }

        // ------------------------------------------------------------- effects
        static void BuildEffect(ParticleBuildContext ctx, int index, HalloweenJson effect)
        {
            string path = effect.Req("path").S();
            int node = effect.Req("node").I(-1);
            Transform owner = ctx.node(node);
            HH.Require(owner != null, $"{path}: effect node {node} has no game object");
            var fx = new EffectInfo
            {
                index = index,
                path = path,
                duration = effect.Req("duration").F(),
                startFrom = effect["startFromTime"].F(),
                startDelay = effect["startDelay"].F(),
                startDelayVariation = effect["startDelayVariation"].F(),
                autoplay = effect["autoplay"].B(),
                clearOnRestart = effect["clearOnRestart"].B(true),
            };
            HH.Require(fx.duration > 0f, $"{path}: effect duration {fx.duration}");
            int repeats = effect["repeats"].I(-1);
            if (repeats != -1 && repeats != 0)
                HH.Warn($"{path}: the client plays the effect {repeats} times, the port loops it");
            if (effect["stopWhenEmpty"].B())
                HH.Warn($"{path}: stop-when-empty restarts follow the effect duration");
            if (effect["inheritScale"].B())
                HH.Warn($"{path}: inherit scale is not applied to particle sizes");
            if (!fx.autoplay)
                HH.Warn($"{path}: the client does not start the effect on load, its systems do not play on awake");
            if ((owner.lossyScale - Vector3.one).sqrMagnitude > 1e-6f)
                HH.Warn($"{path}: node scale {owner.lossyScale} is ignored like the client ignores it for particles");

            fx.root = new GameObject("ParticleEffect").transform;
            fx.root.SetParent(owner, false);
            EffectCount++;

            var emitters = effect.Req("emitters").Items;
            for (int m = 0; m < emitters.Count; m++)
            {
                var emitter = emitters[m];
                string name = emitter.Req("name").S();
                var fields = new ParticleFields(emitter.Req("fields"), $"{path}/{name}");
                var t = new GameObject($"E{m:D2}_{HH.Safe(name)}").transform;
                t.SetParent(fx.root, false);
                t.localPosition = emitter["position"].V3();
                t.localRotation = emitter["rotation"].Exists ? emitter["rotation"].Q() : Quaternion.identity;
                Vector3 scale = emitter["scale"].V3(Vector3.one);
                if ((scale - Vector3.one).sqrMagnitude > 1e-6f)
                    HH.Warn($"{fields.label}: emitter scale {scale} is ignored");
                EmitterCount++;
                var layers = emitter.Req("layers").Items;
                for (int l = 0; l < layers.Count; l++)
                {
                    LayerCount++;
                    BuildLayer(ctx, fx, fields, t, m, l, layers[l]);
                }
            }
        }

        // -------------------------------------------------------------- layers
        static void BuildLayer(ParticleBuildContext ctx, EffectInfo fx, ParticleFields emitter, Transform parent, int m, int l, HalloweenJson layer)
        {
            string name = layer.Req("name").S();
            string kind = layer.Req("type").S();
            var f = new ParticleFields(layer.Req("fields"), $"{emitter.label}/{name}");
            string label = f.label;
            HH.Require(kind == "particles" || kind == "mesh", $"{label}: layer type {kind}");
            bool isMesh = kind == "mesh";
            string stem = $"P{fx.index:D2}_{m:D2}_{l:D2}_{HH.Safe(name)}";

            if (f.Flag("isDisabled"))
            {
                Skip(label, "the layer is disabled");
                return;
            }
            if (f.Has("activeLODS1") && !f.Flag("activeLODS1"))
            {
                Skip(label, "the layer is not active on the effect's LOD 1");
                return;
            }
            var textures = Textures(layer, label);
            if (!textures.ContainsKey("sprite"))
            {
                Skip(label, "the layer has no sprite and the client draws nothing");
                return;
            }
            foreach (string flag in UnportedFlags)
                if (f.Int(flag) != 0)
                    HH.Warn($"{label}: {flag} = {f.Int(flag)} is not ported");

            int orientation = f.Int("particleOrientation", CameraFacing);
            bool worldAlign = (orientation & WorldAlign) != 0;
            if (!isMesh && f.Flag("isLong"))
            {
                // A long particle replaces its basis by its speed and the camera,
                // one stretched quad per orientation bit and none without a bit.
                if ((orientation & (CameraFacing | XFacing | YFacing | ZFacing)) == 0)
                    Skip(label, "the long layer has no orientation bit and the client draws nothing");
                else
                {
                    HH.Warn($"{label}: long (speed-stretched) layers are not ported");
                    Skip(label, "long (speed-stretched) layers are not ported");
                }
                return;
            }
            var bases = Bases(orientation, isMesh, label);
            if (bases.Count == 0)
            {
                Skip(label, "the layer has no orientation bit and the client draws nothing");
                return;
            }
            var timeline = Plan(fx, f);
            if (timeline == null)
            {
                SkippedLayers++;
                return;
            }

            Vector3 ev = emitter.Vec("emissionVector", new Vector3(1f, 0f, 0f));
            float power = ev.magnitude;
            float velocity = f.Num("velocity"), velocityVariation = f.Num("velocityVariation");
            bool inherit = f.Flag("inheritPosition");
            float life = f.Num("life") + 0.5f * f.Num("lifeVariation");

            Vector3 size = f.Vec("size3d", Vector3.one);
            Vector3 sizeVariation = f.Vec("sizeVariation3d", Vector3.zero);
            PropertyLine sizeOverLife = f.Line("sizeOverLife3ds");
            bool animatedSize = sizeOverLife != null && !sizeOverLife.IsConstant;
            if (sizeOverLife != null && !animatedSize)
            {
                size.x *= sizeOverLife.Value(0, 0f);
                size.y *= sizeOverLife.Value(Ch(sizeOverLife, 1), 0f);
                size.z *= sizeOverLife.Value(Ch(sizeOverLife, 2), 0f);
            }
            Vector3 angle = f.Vec("angle3d", Vector3.zero);
            Vector3 angleVariation = f.Vec("angleVariation3d", Vector3.zero);
            Vector3 spin = f.Vec("spin3d", Vector3.zero);
            Vector3 spinVariation = f.Vec("spinVariation3d", Vector3.zero);
            PropertyLine spinOverLife = f.Line("spinOverLife3d");
            Vector3 pivot = f.Vec("pivotPoint3d", Vector3.zero);
            bool randomSpin = f.Flag("randomSpinDirection");

            Texture2D ramp = Ramp(f, timeline.lifeScale, stem, out Vector4 rampFlags);
            Material material = LayerMaterial(ctx, f, textures, isMesh, ramp, rampFlags, life, stem);

            Mesh quad = null;
            float sizeRatio = 0f;
            if (isMesh)
            {
                if (sizeVariation.sqrMagnitude > Epsilon)
                    HH.Warn($"{label}: size variation {sizeVariation} of a mesh layer is not ported");
                if (angleVariation.sqrMagnitude > Epsilon)
                    HH.Warn($"{label}: angle variation {angleVariation} of a mesh layer is not ported");
                if (spin.sqrMagnitude + spinVariation.sqrMagnitude > Epsilon)
                    HH.Warn($"{label}: spin of a mesh layer is not ported");
                if (animatedSize)
                    HH.Warn($"{label}: size over life of a mesh layer is not ported, its value at birth is used");
                if (new Vector2(pivot.x, pivot.y).sqrMagnitude > Epsilon || Mathf.Abs(pivot.z) > Epsilon)
                    meshPivots++;
                if (animatedSize)
                {
                    size.x *= sizeOverLife.Value(0, 0f);
                    size.y *= sizeOverLife.Value(Ch(sizeOverLife, 1), 0f);
                    size.z *= sizeOverLife.Value(Ch(sizeOverLife, 2), 0f);
                }
            }
            else
            {
                quad = HH.Store(Quad(size.x, size.y, pivot.x, pivot.y), $"{MeshFolder}/{stem}_quad.asset");
                MeshCount++;
                sizeRatio = SizeRatio(size, sizeVariation, label);
            }

            var systems = new List<ParticleSystem>();
            for (int b = 0; b < bases.Count; b++)
            {
                var basis = bases[b];
                var go = new GameObject($"L{l:D2}_{HH.Safe(name)}{basis.suffix}");
                go.transform.SetParent(parent, false);
                var ps = go.AddComponent<ParticleSystem>();
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.useAutoRandomSeed = false;
                ps.randomSeed = Seed($"{fx.path}|{m}|{l}");
                var renderer = go.GetComponent<ParticleSystemRenderer>();

                var main = ps.main;
                main.duration = timeline.duration;
                main.loop = true;
                main.prewarm = timeline.prewarm;
                main.startDelay = fx.startDelayVariation > 0f
                    ? new MinMaxCurve(fx.startDelay, fx.startDelay + fx.startDelayVariation)
                    : new MinMaxCurve(fx.startDelay);
                main.startLifetime = new MinMaxCurve(timeline.lifeMin, timeline.lifeMax);
                main.startSpeed = new MinMaxCurve(power * velocity, power * (velocity + velocityVariation));
                main.startColor = Color.white;
                main.gravityModifier = 0f;
                main.simulationSpace = inherit ? ParticleSystemSimulationSpace.Local : ParticleSystemSimulationSpace.World;
                main.simulationSpeed = 1f;
                main.useUnscaledTime = false;
                main.scalingMode = ParticleSystemScalingMode.Local;
                main.playOnAwake = fx.autoplay;
                main.maxParticles = timeline.maxParticles;
                main.emitterVelocityMode = ParticleSystemEmitterVelocityMode.Transform;
                main.stopAction = ParticleSystemStopAction.None;
                main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
                main.ringBufferMode = ParticleSystemRingBufferMode.Disabled;
                main.flipRotation = randomSpin ? 0.5f : 0f;
                main.startSize3D = false;
                main.startRotation3D = true;

                var emission = ps.emission;
                emission.enabled = true;
                emission.rateOverTime = timeline.rateCurve != null ? new MinMaxCurve(timeline.rate, timeline.rateCurve) : new MinMaxCurve(timeline.rate);
                emission.rateOverDistance = 0f;
                emission.SetBursts(timeline.bursts.ToArray());

                Shape(ps, emitter, ev, label);
                VelocityOverLife(ps, f, timeline.lifeScale);

                string mode;
                if (isMesh)
                {
                    var baked = HH.Store(BakeMesh(ctx, layer, basis.bit, size, angle, f.Flag("useVertexColor"), label), $"{MeshFolder}/{stem}{basis.suffix}.asset");
                    MeshCount++;
                    main.startSize = new MinMaxCurve(1f, 1f);
                    StartRotation(ps, Vector3.zero, Vector3.zero);
                    renderer.renderMode = ParticleSystemRenderMode.Mesh;
                    renderer.mesh = baked;
                    renderer.alignment = worldAlign ? ParticleSystemRenderSpace.World : ParticleSystemRenderSpace.Local;
                    mode = $"mesh {BasisName(basis.bit)}";
                }
                else
                {
                    main.startSize = new MinMaxCurve(1f, 1f + sizeRatio);
                    QuadRotation(ps, basis.bit, angle, angleVariation, label);
                    Spin(ps, basis.bit, spin, spinVariation, spinOverLife, timeline.lifeScale, label);
                    if (animatedSize)
                    {
                        var sol = ps.sizeOverLifetime;
                        sol.enabled = true;
                        sol.separateAxes = true;
                        sol.x = new MinMaxCurve(1f, sizeOverLife.Curve(0, timeline.lifeScale, 1f));
                        sol.y = new MinMaxCurve(1f, sizeOverLife.Curve(Ch(sizeOverLife, 1), timeline.lifeScale, 1f));
                        sol.z = new MinMaxCurve(1f, AnimationCurve.Constant(0f, 1f, 1f));
                    }
                    if (randomSpin)
                        RandomSpin(ps, basis.bit, angle, angleVariation, label);
                    renderer.renderMode = ParticleSystemRenderMode.Mesh;
                    renderer.mesh = quad;
                    renderer.alignment = basis.bit == CameraFacing ? ParticleSystemRenderSpace.View
                        : worldAlign ? ParticleSystemRenderSpace.World : ParticleSystemRenderSpace.Local;
                    mode = $"billboard {BasisName(basis.bit)}";
                }
                Renderer(renderer, material, f.Num("sortingFudge"));
                systems.Add(ps);
                Systems.Add(ps);
                HH.Log($"{label}{basis.suffix}: {mode}, {timeline.summary}, lifetime {timeline.lifeMin:0.###}-{timeline.lifeMax:0.###}" +
                       (timeline.lifeScale < 1f ? $" (life lines to {timeline.lifeScale:0.####})" : "") +
                       $", duration {timeline.duration:0.###}{(timeline.prewarm ? " prewarmed" : "")}, max {timeline.maxParticles}, " +
                       $"{(inherit ? "local" : "world")} space, seed {ps.randomSeed}");
            }
            Forces(fx, layer, label, stem, systems);
        }

        static void Skip(string label, string reason)
        {
            HH.Log($"{label}: not drawn, {reason}");
            SkippedLayers++;
        }

        static int Ch(PropertyLine line, int channel) => line.Dims > channel ? channel : 0;

        static Dictionary<string, int> Textures(HalloweenJson layer, string label)
        {
            var result = new Dictionary<string, int>();
            foreach (var t in layer["textures"].Items)
            {
                string slot = t.Req("slot").S();
                int sprite = t.Req("sprite").I(-1);
                HH.Require(sprite >= 0, $"{label}: {slot} texture without a sprite");
                if (slot == "alphaRemap" || slot == "vertexAnimation")
                    continue; // warned by the layer's enabling flag
                HH.Require(slot == "sprite" || slot == "mask" || slot == "flowmap" || slot == "noise", $"{label}: texture slot {slot}");
                result[slot] = sprite;
            }
            return result;
        }

        static List<Basis> Bases(int orientation, bool isMesh, string label)
        {
            var result = new List<Basis>();
            int known = CameraFacing | XFacing | YFacing | ZFacing | WorldAlign;
            if ((orientation & ~known) != 0)
                HH.Warn($"{label}: orientation bits {orientation & ~known} are not ported");
            int[] bits = { CameraFacing, XFacing, YFacing, ZFacing };
            int count = bits.Count(bit => (orientation & bit) != 0);
            foreach (int bit in bits)
                if ((orientation & bit) != 0)
                {
                    HH.Require(!(isMesh && bit == CameraFacing), $"{label}: camera facing mesh layers are not ported");
                    result.Add(new Basis { bit = bit, suffix = count > 1 ? "_" + BasisName(bit) : "" });
                }
            return result;
        }

        static string BasisName(int bit)
        {
            switch (bit)
            {
                case CameraFacing: return "Camera";
                case XFacing: return "X";
                case YFacing: return "Y";
                case ZFacing: return "Z";
                default: return "bit " + bit;
            }
        }

        // FNV-1a of the layer's place in the scene: rebuilds keep their particles.
        static uint Seed(string key)
        {
            uint hash = 2166136261u;
            foreach (char c in key)
            {
                hash ^= c;
                hash *= 16777619u;
            }
            return hash;
        }

        // ------------------------------------------------------------ timeline
        // ParticleEffectSystem::UpdateEffect: a layer emits while the effect time
        // is past startTime and not past endTime (loopEndTime for looped layers,
        // which restart seamlessly without loop pauses); a single particle is
        // regenerated as soon as none is alive, other layers accumulate
        // number * dt and emit whole particles (the first one at 1 / number).
        // The effect restarts after its duration and clears its particles.
        static Timeline Plan(EffectInfo fx, ParticleFields f)
        {
            string label = f.label;
            float duration = fx.duration;
            bool single = f.Flag("isSingle");
            float start = f.Num("startTime"), end = f.Num("endTime", duration);
            if (f.Flag("isLooped"))
            {
                float delta = f.Num("deltaTime"), deltaVariation = f.Num("deltaVariation"), loopVariation = f.Num("loopVariation");
                if (delta > Epsilon || deltaVariation > Epsilon || loopVariation > Epsilon)
                    HH.Warn($"{label}: loop pauses (delta {delta} + {deltaVariation}, loop variation {loopVariation}) are not ported, the layer loops without pauses");
                end = f.Num("loopEndTime", end);
            }
            float windowEnd = Mathf.Min(end, duration);
            if (windowEnd <= start)
            {
                HH.Warn($"{label}: the emission window [{start}, {end}] lies outside the effect duration {duration}, nothing is emitted");
                return null;
            }
            bool continuous = start <= 1e-4f && end >= duration - 1e-4f;
            float life = f.Num("life"), lifeVariation = f.Num("lifeVariation");
            if (life + lifeVariation <= 0f)
            {
                HH.Warn($"{label}: particles without a lifetime are never drawn");
                return null;
            }
            var plan = new Timeline { prewarm = fx.startFrom > 0f && continuous, lifeMin = life, lifeMax = life + lifeVariation };
            if (fx.startFrom > 0f && !continuous)
                HH.Warn($"{label}: startFromTime {fx.startFrom} is not simulated ahead for an emission window");
            if (plan.prewarm && (fx.startDelay > 0f || fx.startDelayVariation > 0f))
                HH.Warn($"{label}: Unity does not delay a prewarmed system");
            // startFromTime: SimulateEffect runs the effect ahead; for a steady
            // layer one prewarmed cycle of that length gives the same state.
            plan.duration = plan.prewarm ? fx.startFrom : duration;
            bool ok = single
                ? Singles(plan, fx, label, start, end, continuous, life, lifeVariation)
                : Stream(plan, fx, f, start, windowEnd, continuous, life, lifeVariation);
            return ok ? plan : null;
        }

        static bool Singles(Timeline plan, EffectInfo fx, string label, float start, float end, bool continuous, float life, float lifeVariation)
        {
            if (continuous)
            {
                plan.rate = 1000f;
                plan.maxParticles = 1;
                if (fx.clearOnRestart && start + plan.lifeMax > fx.duration)
                    Clip(plan, fx.duration - start, life, lifeVariation, label);
                plan.summary = "single, regenerated when it dies";
                return true;
            }
            float span = life + 0.5f * lifeVariation;
            if (lifeVariation > 0f)
            {
                HH.Warn($"{label}: the singles of an emission window follow their mean life {span}");
                plan.lifeMin = plan.lifeMax = span;
            }
            for (int k = 0; ; k++)
            {
                float t = start + k * span;
                if (t > end + 1e-5f || t >= fx.duration)
                    break;
                HH.Require(k < 10000, $"{label}: too many singles in its emission window");
                plan.bursts.Add(new ParticleSystem.Burst(t, (short)1));
            }
            HH.Require(plan.bursts.Count > 0, $"{label}: no single in its emission window");
            plan.maxParticles = plan.bursts.Count > 1 ? 2 : 1;
            float last = plan.bursts[plan.bursts.Count - 1].time;
            if (fx.clearOnRestart && last + span > fx.duration)
            {
                HH.Require(plan.bursts.Count == 1, $"{label}: several singles and the last one outlives the effect restart");
                Clip(plan, fx.duration - last, span, 0f, label);
            }
            plan.summary = "single at " + string.Join(", ", plan.bursts.Select(b => b.time.ToString("0.###", CultureInfo.InvariantCulture)));
            return true;
        }

        static bool Stream(Timeline plan, EffectInfo fx, ParticleFields f, float start, float windowEnd, bool continuous, float life, float lifeVariation)
        {
            string label = f.label;
            float number = f.Num("number");
            if (f.Has("numberVariation"))
            {
                float variation = f.Num("numberVariation");
                if (variation > 0f)
                {
                    HH.Warn($"{label}: number variation {variation} is emitted at its mean rate");
                    number += 0.5f * variation;
                }
            }
            if (number <= 0f)
            {
                HH.Warn($"{label}: emits no particles (number {number})");
                return false;
            }
            if (continuous)
            {
                plan.rate = number;
                plan.summary = $"{number:0.###} per second";
                if (fx.clearOnRestart && plan.lifeMax > fx.duration)
                    HH.Warn($"{label}: particles living {plan.lifeMax} s outlive the effect duration {fx.duration} that clears them in the client");
            }
            else
            {
                int count = Mathf.FloorToInt((windowEnd - start) * number + 1e-4f);
                if (count == 0)
                {
                    HH.Warn($"{label}: {number} per second over [{start}, {windowEnd}] never reaches a particle");
                    return false;
                }
                if (count <= MaxBursts)
                {
                    for (int k = 0; k < count; k++)
                    {
                        float t = start + (k + 1) / number;
                        if (t < fx.duration)
                            plan.bursts.Add(new ParticleSystem.Burst(t, (short)1));
                    }
                    HH.Require(plan.bursts.Count > 0, $"{label}: no particle inside the effect duration");
                    plan.summary = $"{plan.bursts.Count} particles from {plan.bursts[0].time:0.###} s";
                }
                else
                {
                    plan.rate = number;
                    plan.rateCurve = Window(start / plan.duration, windowEnd / plan.duration);
                    plan.summary = $"{number:0.###} per second in [{start:0.###}, {windowEnd:0.###}]";
                }
                float last = plan.bursts.Count > 0 ? plan.bursts[plan.bursts.Count - 1].time : windowEnd;
                if (fx.clearOnRestart && last + plan.lifeMax > fx.duration)
                {
                    if (plan.bursts.Count == 1)
                        Clip(plan, fx.duration - last, life, lifeVariation, label);
                    else
                        HH.Warn($"{label}: particles outlive the effect restart that clears them in the client");
                }
            }
            plan.maxParticles = Mathf.Clamp(Mathf.CeilToInt(plan.rate * plan.lifeMax * 1.2f) + plan.bursts.Count + 5, 1, 100000);
            return true;
        }

        // Clear on restart: a particle that would outlive the effect restart lives
        // until it; its life lines run up to the age it reaches (lifeScale).
        static void Clip(Timeline plan, float available, float life, float lifeVariation, string label)
        {
            HH.Require(available > 0f, $"{label}: no time left before the effect restart");
            float mean = life + 0.5f * lifeVariation;
            if (lifeVariation > 0f)
                HH.Warn($"{label}: life variation with a clear-on-restart clip; the mean life scales the life lines");
            plan.lifeMin = Mathf.Min(plan.lifeMin, available);
            plan.lifeMax = Mathf.Min(plan.lifeMax, available);
            plan.lifeScale = Mathf.Min(1f, available / mean);
        }

        static AnimationCurve Window(float from, float to)
        {
            var keys = new List<Keyframe>();
            if (from > 0f)
                keys.Add(Step(0f, 0f));
            keys.Add(Step(from, 1f));
            if (to < 1f)
                keys.Add(Step(to, 0f));
            return new AnimationCurve(keys.ToArray());
        }

        static Keyframe Step(float t, float value) => new Keyframe(t, value, float.PositiveInfinity, float.PositiveInfinity);

        // --------------------------------------------------------------- shape
        static Vector3 U(Vector3 dava) => new Vector3(dava.x, dava.z, dava.y);

        // GenerateEmitterRotationMatrix: the rotation taking the client's +z onto
        // the emission vector (a half turn about x for vectors pointing down z),
        // in Unity's axes where the client's +z is up.
        static Quaternion EmissionRotation(Vector3 ev)
        {
            if (Mathf.Abs(ev.x) < Epsilon && Mathf.Abs(ev.y) < Epsilon)
                return ev.z < 0f ? Quaternion.AngleAxis(180f, Vector3.right) : Quaternion.identity;
            return Quaternion.FromToRotation(Vector3.up, U(ev).normalized);
        }

        // PrepareEmitterParameters: positions (point or rect volume) and the speed
        // (+z within half the emission range) rotated onto the emission vector.
        // Unity's cone and box emit along their local +z, turned onto the
        // client's +z (Unity up) first.
        static void Shape(ParticleSystem ps, ParticleFields emitter, Vector3 ev, string label)
        {
            var shape = ps.shape;
            shape.enabled = true;
            int type = emitter.Int("type", EmitterPoint);
            float range = emitter.Num("emissionRange");
            shape.position = Vector3.zero;
            shape.rotation = (EmissionRotation(ev) * Quaternion.Euler(-90f, 0f, 0f)).eulerAngles;
            if (type == EmitterRect)
            {
                Vector3 size = emitter.Vec("size", Vector3.zero);
                shape.shapeType = ParticleSystemShapeType.Box;
                shape.scale = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
                shape.boxThickness = Vector3.zero;
                if (range > 1e-3f && range < 360f - 1e-3f)
                    HH.Warn($"{label}: a rect emitter spreading {range} degrees emits along the emission vector");
            }
            else
            {
                HH.Require(type == EmitterPoint, $"{label}: emitter type {type} is not ported");
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = Mathf.Clamp(range * 0.5f, 0f, 90f);
                shape.radius = 0.0001f;
                shape.radiusThickness = 1f;
                shape.arc = 360f;
                shape.arcMode = ParticleSystemShapeMultiModeValue.Random;
                shape.scale = Vector3.one;
                if (range > 180f + 1e-3f && range < 360f - 1e-3f)
                    HH.Warn($"{label}: an emission range of {range} degrees is capped at a 90 degree cone");
            }
            shape.randomDirectionAmount = range >= 360f - 1e-3f ? 1f : 0f;
            shape.sphericalDirectionAmount = 0f;
            shape.randomPositionAmount = 0f;
            shape.alignToDirection = false;
        }

        // velocityOverLife scales the particle's own displacement per frame.
        static void VelocityOverLife(ParticleSystem ps, ParticleFields f, float lifeScale)
        {
            var line = f.Line("velocityOverLife");
            if (line == null)
                return;
            var vol = ps.velocityOverLifetime;
            vol.enabled = true;
            vol.speedModifier = line.IsConstant ? new MinMaxCurve(line.Value(0, 0f)) : new MinMaxCurve(1f, line.Curve(0, lifeScale, 1f));
            HH.Log($"{f.label}: velocityOverLife scales the particle speed");
        }

        // ------------------------------------------------------------ rotation
        // Unity euler angles turning a quad's x / y onto the client's basis
        // (ParticleRenderObject: X facing = emitter y, z; Y facing = x, z;
        // Z facing = y, x; camera facing = the view's right and up).
        static Vector3 FacingEuler(int basis)
        {
            switch (basis)
            {
                case XFacing: return new Vector3(0f, -90f, 0f);
                case ZFacing: return new Vector3(-90f, -90f, 0f);
                default: return Vector3.zero;
            }
        }

        // The client turns a quad by -angle within its basis (SinCosFast(-angle));
        // tilted quads (x / y angles) take the 3D angle as rotations about the
        // quad's own z, y, x in that order. View aligned quads turn in the sense
        // HalloweenHangarProbeTests pin.
        static void QuadRotation(ParticleSystem ps, int basis, Vector3 angle, Vector3 variation, string label)
        {
            Vector3 facing = FacingEuler(basis);
            bool flat = Mathf.Abs(angle.x) < Epsilon && Mathf.Abs(angle.y) < Epsilon
                     && Mathf.Abs(variation.x) < Epsilon && Mathf.Abs(variation.y) < Epsilon;
            if (flat || basis == CameraFacing)
            {
                if (!flat)
                    HH.Warn($"{label}: the tilt {angle} / {variation} of a camera facing billboard is not ported");
                float sign = basis == CameraFacing ? ViewSign : 1f;
                StartRotation(ps, new Vector3(facing.x, facing.y, -angle.z * sign),
                              new Vector3(facing.x, facing.y, -(angle.z + variation.z) * sign));
                return;
            }
            Quaternion q = Quaternion.Euler(facing) * Quaternion.AngleAxis(-angle.z, Vector3.forward)
                         * Quaternion.AngleAxis(angle.y, Vector3.up) * Quaternion.AngleAxis(angle.x, Vector3.right);
            if (variation.sqrMagnitude > Epsilon)
                HH.Warn($"{label}: angle variation {variation} of a tilted billboard is not ported");
            Vector3 e = q.eulerAngles;
            StartRotation(ps, e, e);
        }

        // Degrees in, Unity's radians out; every axis in the same mode so twin
        // systems draw the same random numbers.
        static void StartRotation(ParticleSystem ps, Vector3 from, Vector3 to)
        {
            var main = ps.main;
            main.startRotation3D = true;
            main.startRotationX = new MinMaxCurve(from.x * Mathf.Deg2Rad, to.x * Mathf.Deg2Rad);
            main.startRotationY = new MinMaxCurve(from.y * Mathf.Deg2Rad, to.y * Mathf.Deg2Rad);
            main.startRotationZ = new MinMaxCurve(from.z * Mathf.Deg2Rad, to.z * Mathf.Deg2Rad);
        }

        // angle += spin * spinOverLife(t) * dt, drawn as -angle like the start angle.
        static void Spin(ParticleSystem ps, int basis, Vector3 spin, Vector3 variation, PropertyLine spinOverLife, float lifeScale, string label)
        {
            if (Mathf.Abs(spin.x) + Mathf.Abs(spin.y) + Mathf.Abs(variation.x) + Mathf.Abs(variation.y) > Epsilon)
                HH.Warn($"{label}: spin about a billboard's x and y axes is not ported");
            if (Mathf.Abs(spin.z) < Epsilon && Mathf.Abs(variation.z) < Epsilon)
                return;
            var rol = ps.rotationOverLifetime;
            rol.enabled = true;
            rol.separateAxes = true;
            float sign = basis == CameraFacing ? ViewSign : 1f;
            float from = -spin.z * sign * Mathf.Deg2Rad, to = -(spin.z + variation.z) * sign * Mathf.Deg2Rad;
            if (spinOverLife == null || spinOverLife.IsConstant)
            {
                float k = spinOverLife != null ? spinOverLife.Value(Ch(spinOverLife, 2), 0f) : 1f;
                rol.x = new MinMaxCurve(0f, 0f);
                rol.y = new MinMaxCurve(0f, 0f);
                rol.z = new MinMaxCurve(from * k, to * k);
                return;
            }
            int c = Ch(spinOverLife, 2);
            rol.x = new MinMaxCurve(1f, AnimationCurve.Constant(0f, 1f, 0f), AnimationCurve.Constant(0f, 1f, 0f));
            rol.y = new MinMaxCurve(1f, AnimationCurve.Constant(0f, 1f, 0f), AnimationCurve.Constant(0f, 1f, 0f));
            rol.z = new MinMaxCurve(1f, spinOverLife.Curve(c, lifeScale, from), spinOverLife.Curve(c, lifeScale, to));
        }

        // randomSpinDirection turns half the particles the other way; Unity's
        // flipRotation mirrors their start angle as well, which only a full turn
        // of start angles hides. Pinned by HalloweenHangarProbeTests: whether the
        // flip reaches the x / y rotations that lay a quad onto its basis, and
        // whether it turns view aligned quads the other way.
        public static bool FlipMirrorsFacing = true, FlipTurnsView = true;

        static void RandomSpin(ParticleSystem ps, int basis, Vector3 angle, Vector3 variation, string label)
        {
            var main = ps.main;
            if (basis == CameraFacing ? !FlipTurnsView : FlipMirrorsFacing)
            {
                main.flipRotation = 0f;
                HH.Warn($"{label}: random spin direction of a {BasisName(basis)} billboard is not ported, every particle spins one way");
                return;
            }
            bool fullTurn = Mathf.Abs(variation.z) >= 360f - 1e-3f;
            bool symmetric = Mathf.Abs(variation.z) < Epsilon
                          && (Mathf.Abs(Mathf.DeltaAngle(0f, angle.z)) < Epsilon || Mathf.Abs(Mathf.DeltaAngle(180f, angle.z)) < Epsilon);
            if (!fullTurn && !symmetric)
                HH.Warn($"{label}: random spin direction mirrors the start angle {angle.z} + {variation.z} of half the particles as well");
        }

        // ------------------------------------------------------------ geometry
        // The client's billboard: corners left + bot, right + bot, left + top,
        // right + top with left = +ex, top = -ey, scaled by half the size and the
        // pivot; quad x / y are the basis' ex / ey, uv in Unity's orientation of
        // the sheet (v up), the client's (0, 0) being the frame's top-left.
        static Mesh Quad(float sx, float sy, float px, float py)
        {
            float left = 0.5f * sx * (1f + px), right = 0.5f * sx * (1f - px);
            float top = 0.5f * sy * (1f + py), bottom = 0.5f * sy * (1f - py);
            var mesh = new Mesh();
            mesh.SetVertices(new[]
            {
                new Vector3(left, bottom, 0f), new Vector3(-right, bottom, 0f),
                new Vector3(left, -top, 0f), new Vector3(-right, -top, 0f),
            });
            mesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
            mesh.SetUVs(0, new[] { new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 0f), new Vector2(1f, 0f) });
            mesh.SetIndices(new[] { 0, 1, 2, 2, 1, 3 }, MeshTopology.Triangles, 0, true);
            mesh.RecalculateBounds();
            return mesh;
        }

        // Size variation adds variation * one random number to both axes; Unity
        // scales the quad by 1 + ratio * random, exact while the variation is
        // proportional to the size.
        static float SizeRatio(Vector3 size, Vector3 variation, string label)
        {
            var ratios = new List<float>();
            if (Mathf.Abs(size.x) > Epsilon)
                ratios.Add(variation.x / size.x);
            else if (Mathf.Abs(variation.x) > Epsilon)
                HH.Warn($"{label}: size variation {variation.x} of a zero width is not ported");
            if (Mathf.Abs(size.y) > Epsilon)
                ratios.Add(variation.y / size.y);
            else if (Mathf.Abs(variation.y) > Epsilon)
                HH.Warn($"{label}: size variation {variation.y} of a zero height is not ported");
            if (ratios.Count == 0)
                return 0f;
            float k = ratios.Average();
            if (ratios.Any(r => Mathf.Abs(r - k) > 1e-3f))
                HH.Warn($"{label}: size variation {variation} is not proportional to size {size}; both axes vary by the mean ratio {k:0.###}");
            return k;
        }

        // Mesh layers: the polygon group (exported in Unity's axes) taken back to
        // the client's axes, turned by -angle about x, y, z, scaled by size3d,
        // laid onto the basis (X facing: x, y, z -> y, z, x; Y facing:
        // x, y, z -> z, x, y; Z facing as is) and brought to Unity's axes again.
        // Normals go through the same matrix like the client's shader.
        static Mesh BakeMesh(ParticleBuildContext ctx, HalloweenJson layer, int basis, Vector3 size, Vector3 angle, bool vertexColor, string label)
        {
            int index = layer.Req("mesh").I(-1);
            HH.Require(index >= 0 && index < ctx.meshes.Count, $"{label}: particle mesh {index} is missing");
            var record = ctx.meshes[index];
            HH.Require(record.positions != null && record.uv0 != null, $"{label}: particle mesh {record.name} without positions or uv");
            Matrix4x4 m = SwapYZ * FacingMatrix(basis) * Matrix4x4.Scale(size)
                        * Rotation(2, -angle.z) * Rotation(1, -angle.y) * Rotation(0, -angle.x) * SwapYZ;
            Matrix4x4 cofactor = Cofactor(m);
            var positions = new Vector3[record.vertexCount];
            Vector3[] normals = record.normals != null ? new Vector3[record.vertexCount] : null;
            for (int v = 0; v < record.vertexCount; v++)
            {
                positions[v] = m.MultiplyPoint3x4(record.positions[v]);
                if (normals == null)
                    continue;
                Vector3 n = m.MultiplyVector(record.normals[v]);
                if (n.sqrMagnitude < 1e-12f)
                    n = cofactor.MultiplyVector(record.normals[v]);
                normals[v] = n.sqrMagnitude < 1e-12f ? record.normals[v] : n.normalized;
            }
            var indices = new List<int>();
            foreach (var submesh in record.submeshes)
                indices.AddRange(submesh);
            if (Determinant(m) < 0f)
                for (int i = 0; i + 2 < indices.Count; i += 3)
                    (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
            var mesh = new Mesh { indexFormat = record.vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(positions);
            if (normals != null)
                mesh.SetNormals(normals);
            mesh.SetUVs(0, record.uv0);
            if (vertexColor)
            {
                if (record.colors != null)
                    mesh.SetColors(record.colors);
                else
                    HH.Warn($"{label}: vertex colours are enabled but {record.name} has none");
            }
            mesh.SetIndices(indices, MeshTopology.Triangles, 0, true);
            if (normals == null)
                mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static readonly Matrix4x4 SwapYZ = Rows(new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector3(0f, 1f, 0f));

        static Matrix4x4 FacingMatrix(int basis)
        {
            switch (basis)
            {
                case XFacing: return Rows(new Vector3(0f, 0f, 1f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f));
                case YFacing: return Rows(new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f), new Vector3(1f, 0f, 0f));
                case ZFacing: return Matrix4x4.identity;
            }
            throw new InvalidOperationException("mesh layer basis " + basis);
        }

        static Matrix4x4 Rows(Vector3 r0, Vector3 r1, Vector3 r2)
        {
            var m = Matrix4x4.identity;
            m.SetRow(0, new Vector4(r0.x, r0.y, r0.z, 0f));
            m.SetRow(1, new Vector4(r1.x, r1.y, r1.z, 0f));
            m.SetRow(2, new Vector4(r2.x, r2.y, r2.z, 0f));
            return m;
        }

        // Right-handed rotation about the client's x (0), y (1) or z (2) axis.
        static Matrix4x4 Rotation(int axis, float degrees)
        {
            float r = degrees * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
            var m = Matrix4x4.identity;
            int a = (axis + 1) % 3, b = (axis + 2) % 3;
            m[a, a] = c;
            m[a, b] = -s;
            m[b, a] = s;
            m[b, b] = c;
            return m;
        }

        static float Determinant(Matrix4x4 m) =>
            m.m00 * (m.m11 * m.m22 - m.m12 * m.m21) - m.m01 * (m.m10 * m.m22 - m.m12 * m.m20) + m.m02 * (m.m10 * m.m21 - m.m11 * m.m20);

        // det(M) * inverse transpose: the normal transform of a flattening matrix.
        static Matrix4x4 Cofactor(Matrix4x4 m)
        {
            var c = Matrix4x4.identity;
            c.m00 = m.m11 * m.m22 - m.m12 * m.m21;
            c.m01 = -(m.m10 * m.m22 - m.m12 * m.m20);
            c.m02 = m.m10 * m.m21 - m.m11 * m.m20;
            c.m10 = -(m.m01 * m.m22 - m.m02 * m.m21);
            c.m11 = m.m00 * m.m22 - m.m02 * m.m20;
            c.m12 = -(m.m00 * m.m21 - m.m01 * m.m20);
            c.m20 = m.m01 * m.m12 - m.m02 * m.m11;
            c.m21 = -(m.m00 * m.m12 - m.m02 * m.m10);
            c.m22 = m.m00 * m.m11 - m.m01 * m.m10;
            return c;
        }

        // ------------------------------------------------------------ material
        static Vector4 Sheet(HalloweenJson sprite) =>
            new Vector4(sprite.Req("columns").I(1), sprite.Req("rows").I(1), sprite.Req("frames").I(1), 0f);

        static void Toggle(Material material, string property, string keyword, bool on)
        {
            material.SetFloat(property, on ? 1f : 0f);
            if (on)
                material.EnableKeyword(keyword);
            else
                material.DisableKeyword(keyword);
        }

        static Material LayerMaterial(ParticleBuildContext ctx, ParticleFields f, Dictionary<string, int> textures, bool isMesh,
                                      Texture2D ramp, Vector4 rampFlags, float life, string stem)
        {
            string label = f.label;
            var scene = ctx.materials.scene;
            var sprites = scene.Req("sprites");
            var material = new Material(ctx.shader);

            int sprite = textures["sprite"];
            var spriteRecord = sprites[sprite];
            HH.Require(spriteRecord.Exists, $"{label}: sprite {sprite} is missing");
            material.SetTexture("_Albedo", HalloweenHangarAssets.Sprite(scene, sprite));
            material.SetVector("_AlbedoSheet", Sheet(spriteRecord));
            int frames = spriteRecord.Req("frames").I(1);

            bool flow = f.Flag("enableFlow"), flowAnimation = f.Flag("enableFlowAnimation");
            bool hasFlowmap = textures.TryGetValue("flowmap", out int flowmap);
            if ((flow || flowAnimation) && !hasFlowmap)
                HH.Warn($"{label}: flow is enabled without a flowmap and is not drawn");
            if (hasFlowmap && (flow || flowAnimation))
            {
                material.SetTexture("_Flowmap", HalloweenHangarAssets.Sprite(scene, flowmap));
                material.SetVector("_FlowmapSheet", Sheet(sprites[flowmap]));
            }
            Toggle(material, "_HHFlowmap", "_HH_FLOWMAP", hasFlowmap && flow);
            Toggle(material, "_HHFlowmapAnimation", "_HH_FLOWMAP_ANIMATION", hasFlowmap && flowAnimation);

            bool noise = f.Flag("enableNoise") && textures.TryGetValue("noise", out int noiseIndex) && Assign(material, "_Noise", "_NoiseSheet", scene, noiseIndex);
            if (f.Flag("enableNoise") && !noise)
                HH.Warn($"{label}: noise is enabled without a noise texture and is not drawn");
            Toggle(material, "_HHNoise", "_HH_NOISE", noise);

            bool mask = f.Flag("enableMask") && textures.TryGetValue("mask", out int maskIndex) && Assign(material, "_Mask", "_MaskSheet", scene, maskIndex);
            if (f.Flag("enableMask") && !mask)
                HH.Warn($"{label}: the mask is enabled without a mask texture and is not drawn");
            Toggle(material, "_HHMask", "_HH_MASK", mask);

            Toggle(material, "_HHFrameBlend", "_HH_FRAME_BLEND", f.Flag("enableFrameBlend"));
            Toggle(material, "_HHSoft", "_HH_SOFT", f.Flag("softParticles"));

            int blending = f.Int("blending", 1);
            HalloweenHangarMaterials.Blend(blending, true, out BlendMode src, out BlendMode dst);
            material.SetFloat("_SrcBlend", (float)src);
            material.SetFloat("_DstBlend", (float)dst);
            Toggle(material, "_HHFuse", "_HH_FUSE", blending == 8);
            Vector3 fuse = f.Vec("fuseBlendAddEdges", new Vector3(0.45f, 0.55f, 0f));
            material.SetVector("_FuseEdges", new Vector4(fuse.x, fuse.y, 0f, 0f));
            material.SetFloat("_AdditiveSoft", blending == 2 ? 1f : 0f);
            material.SetFloat("_ZTest", (float)CompareFunction.Less);
            int cull = f.Int("cullMode");
            HH.Require(cull >= 0 && cull <= 2, $"{label}: cull mode {cull}");
            material.SetFloat("_Cull", cull); // rhi::CullMode NONE, CCW, CW = Unity Off, Front, Back

            float fps = f.Num("frameOverLifeFPS");
            PropertyLine animSpeed = f.Line("animSpeedOverLife");
            if (f.Flag("autoFPS"))
            {
                // The animation spans the particle's life: frames / (life * mean animSpeedOverLife).
                float mean = animSpeed != null ? Integral(animSpeed, 0f, 1f, 256) : 1f;
                HH.Require(life > 0f && mean > 0f, $"{label}: auto FPS without a life or animation speed");
                float auto = frames / (life * mean);
                HH.Log($"{label}: auto FPS {auto:0.###} (stored {fps:0.###})");
                fps = auto;
            }
            material.SetVector("_FrameParams", new Vector4(fps, f.Flag("loopSpriteAnimation") ? 1f : 0f,
                f.Flag("randomFrameOnStart") ? 1f : 0f, f.Flag("frameOverLifeEnabled") ? 1f : 0f));
            if (ramp != null)
                material.SetTexture("_Ramps", ramp);
            material.SetVector("_RampFlags", rampFlags);

            Vector3 uvScale = f.Vec("uvScale", Vector3.one), uvScaleVariation = f.Vec("uvScaleVariation", Vector3.zero);
            Vector3 uvScroll = f.Vec("uvScrollSpeed", Vector3.zero), uvScrollVariation = f.Vec("uvScrollSpeedVariation", Vector3.zero);
            material.SetVector("_UvScale", new Vector4(uvScale.x, uvScale.y, uvScaleVariation.x, uvScaleVariation.y));
            material.SetVector("_UvScroll", new Vector4(uvScroll.x, uvScroll.y, uvScrollVariation.x, uvScrollVariation.y));
            material.SetVector("_UvFlags", new Vector4(f.Flag("enableUvScroll") ? 1f : 0f, f.Flag("enableUvScale") ? 1f : 0f, 0f, 0f));
            Vector3 maskScale = f.Flag("enableMaskUvScale") ? f.Vec("maskUvScale", Vector3.one) : Vector3.one;
            Vector3 maskScroll = f.Flag("enableMaskUvScroll") ? f.Vec("maskUvScrollSpeed", Vector3.zero) : Vector3.zero;
            material.SetVector("_MaskUv", new Vector4(maskScale.x, maskScale.y, maskScroll.x, maskScroll.y));
            material.SetVector("_FlowParams", new Vector4(f.Num("flowSpeed"), f.Num("flowSpeedVariation"), f.Num("flowOffset"), f.Num("flowOffsetVariation")));
            material.SetVector("_NoiseParams", new Vector4(f.Num("noiseScale"), f.Num("noiseScaleVariation"), f.Num("noiseUScrollSpeed"), f.Num("noiseVScrollSpeed")));
            material.SetVector("_NoiseScroll", new Vector4(f.Num("noiseUScrollSpeedVariation"), f.Num("noiseVScrollSpeedVariation"),
                f.Flag("enableNoiseScroll") ? 1f : 0f, 0f));
            material.SetVector("_FresnelParams", new Vector4(f.Num("fresToAlphaBias"), f.Num("fresToAlphaPower", 1f), isMesh ? 1f : 0f,
                f.Flag("useFresToAlpha") ? 1f : 0f));
            material.SetFloat("_DepthDifferenceSlope", f.Num("depthDifferenceSlope", 2f));
            material.SetFloat("_UseVertexColor", isMesh && f.Flag("useVertexColor") ? 1f : 0f);
            material.SetFloat("_FogEnabled", f.Flag("enableFog") ? 1f : 0f);
            material.SetVector("_FogColor", ctx.materials.fog);
            material.SetFloat("_FogLimit", ctx.materials.fogLimit);
            material.renderQueue = (int)RenderQueue.Transparent;
            material.SetOverrideTag("RenderType", "Transparent");
            material.enableInstancing = false;
            return HH.Store(material, $"{MaterialFolder}/{stem}.mat");
        }

        static bool Assign(Material material, string texture, string sheet, HalloweenJson scene, int sprite)
        {
            var record = scene.Req("sprites")[sprite];
            HH.Require(record.Exists, $"sprite {sprite} is missing");
            material.SetTexture(texture, HalloweenHangarAssets.Sprite(scene, sprite));
            material.SetVector(sheet, Sheet(record));
            return true;
        }

        // ---------------------------------------------------------------- ramps
        // 256 x 4 linear half floats, texel i at t = i / 255 of the particle's
        // Unity life (scaled by lifeScale into the client's life): row 0
        // colorRandom at the random number t, row 1 colorOverLife, row 2
        // alphaOverLife, row 3 the integral of animSpeedOverLife over t.
        static Texture2D Ramp(ParticleFields f, float lifeScale, string stem, out Vector4 flags)
        {
            var colorRandom = f.Line("colorRandom");
            var colorOverLife = f.Line("colorOverLife");
            var alphaOverLife = f.Line("alphaOverLife");
            var animSpeed = f.Line("animSpeedOverLife");
            flags = new Vector4(colorRandom != null ? 1f : 0f, colorOverLife != null ? 1f : 0f, alphaOverLife != null ? 1f : 0f, animSpeed != null ? 1f : 0f);
            if (flags == Vector4.zero)
                return null;
            const int width = 256;
            var pixels = new Color[width * 4];
            float integral = 0f;
            for (int i = 0; i < width; i++)
            {
                float t = i / (width - 1f);
                pixels[i] = colorRandom != null ? colorRandom.Color(t) : Color.white;
                pixels[width + i] = colorOverLife != null ? colorOverLife.Color(t * lifeScale) : Color.white;
                float alpha = alphaOverLife != null ? alphaOverLife.Value(0, t * lifeScale) : 1f;
                pixels[2 * width + i] = new Color(alpha, alpha, alpha, 1f);
                if (animSpeed != null && i > 0)
                    integral += Integral(animSpeed, (i - 1) / (width - 1f) * lifeScale, t * lifeScale, 16) / lifeScale;
                pixels[3 * width + i] = new Color(integral, integral, integral, 1f);
            }
            var texture = new Texture2D(width, 4, TextureFormat.RGBAHalf, false, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 0,
            };
            texture.SetPixels(pixels);
            texture.Apply(false, false);
            RampCount++;
            return HH.Store(texture, $"{RampFolder}/{stem}.asset");
        }

        // Trapezoid integral of channel 0 over [a, b].
        static float Integral(PropertyLine line, float a, float b, int steps)
        {
            float sum = 0f, h = (b - a) / steps;
            for (int k = 0; k < steps; k++)
                sum += 0.5f * (line.Value(0, a + k * h) + line.Value(0, a + (k + 1) * h)) * h;
            return sum;
        }

        // ---------------------------------------------------------------- forces
        // ParticleForces: gravity (down * power.x), wind (direction * power.x * 100
        // * dt per frame, turbulence moving the particle by noise * turbulence
        // * dt) and point gravity (power towards the force position, killing or
        // holding particles within its radius). World aligned forces sit at the
        // effect's position plus their offset, unrotated.
        static void Forces(EffectInfo fx, HalloweenJson layer, string label, string stem, List<ParticleSystem> systems)
        {
            var records = layer["forces"].Items;
            if (records.Count == 0)
                return;
            Vector3 acceleration = Vector3.zero;
            bool accelerates = false;
            Vector3 driftMin = Vector3.zero, driftMax = Vector3.zero;
            bool drifts = false;
            var drift = ParticleSystemSimulationSpace.World;
            float drag = 0f;
            var fields = new List<ParticleSystemForceField>();
            var kills = new List<Collider>();
            for (int k = 0; k < records.Count; k++)
            {
                var force = new ParticleFields(records[k], $"{label}/force {k}");
                string type = force.Str("forceType");
                string name = force.Str("forceName", type);
                if (force.Has("forceIsActive") && !force.Flag("forceIsActive"))
                {
                    HH.Log($"{force.label}: {type} force '{name}' is inactive");
                    continue;
                }
                if (force.Flag("isGlobal"))
                {
                    HH.Warn($"{force.label}: global {type} force '{name}' is not ported");
                    continue;
                }
                bool world = force.Flag("worldAlign");
                string timing = force.Str("dragForceTimingType", "const");
                if (timing != "const" && timing != "ovr_layer" && timing != "ovr_prt" && timing != "sec")
                    HH.Warn($"{force.label}: timing {timing}");
                Vector3 power = force.Vec("dragForceLine", Vector3.zero);
                if (!force.Flag("dragForceInfinityRange") && type != "grav")
                    HH.Warn($"{force.label}: the {force.Str("dragForceShape")} range of {type} force '{name}' is not ported, it acts everywhere");
                Quaternion toWorld = world ? Quaternion.identity : fx.root.rotation;
                switch (type)
                {
                    case "grav":
                        acceleration += toWorld * (Vector3.down * power.x);
                        accelerates = true;
                        HH.Log($"{force.label}: gravity {power.x}");
                        break;
                    case "wind":
                    {
                        Vector3 direction = U(force.Vec("forceDirection", new Vector3(1f, 0f, 0f)));
                        float multiplier = 1f;
                        if (Mathf.Abs(force.Num("forceWindFreq")) > Epsilon)
                        {
                            multiplier = force.Num("forceWindBias", 1f);
                            HH.Warn($"{force.label}: wind gusts follow their bias {multiplier}");
                        }
                        acceleration += toWorld * (direction * (power.x * multiplier * WindScale / WindFrameRate));
                        accelerates = true;
                        float turbulence = force.Num("turbulenceLine");
                        if (Mathf.Abs(turbulence) > Epsilon)
                        {
                            if (Mathf.Abs(force.Num("windTurbulenceFrequency")) > Epsilon)
                                HH.Warn($"{force.label}: turbulence changing over life drifts at a constant random speed");
                            if (force.Flag("isVelocityTurbulence"))
                                HH.Warn($"{force.label}: velocity turbulence drifts the position");
                            // A random speed per particle in [-t, t] per axis; unless
                            // backwardTurbulenceProbability allows it the noise never
                            // points against the wind.
                            Vector3 lo = -Vector3.one * Mathf.Abs(turbulence), hi = Vector3.one * Mathf.Abs(turbulence);
                            if (force.Int("backwardTurbulenceProbability") < 100)
                            {
                                int axis = Dominant(direction);
                                if (axis >= 0)
                                {
                                    if (direction[axis] > 0f) lo[axis] = 0f;
                                    else hi[axis] = 0f;
                                }
                                else
                                    HH.Warn($"{force.label}: turbulence of a diagonal wind is drifted both ways");
                            }
                            var space = world ? ParticleSystemSimulationSpace.World : ParticleSystemSimulationSpace.Local;
                            if (drifts && space != drift)
                                HH.Warn($"{force.label}: turbulence in two spaces");
                            driftMin += lo;
                            driftMax += hi;
                            drift = space;
                            drifts = true;
                        }
                        HH.Log($"{force.label}: wind {power.x} along {direction}, turbulence {turbulence}");
                        break;
                    }
                    case "drag":
                    {
                        float mean = (power.x + power.y + power.z) / 3f;
                        if (Mathf.Abs(power.x - mean) + Mathf.Abs(power.y - mean) + Mathf.Abs(power.z - mean) > 1e-4f)
                            HH.Warn($"{force.label}: drag {power} acts with its mean {mean}");
                        drag += mean;
                        break;
                    }
                    case "pointgr":
                    {
                        var field = PointGravity(fx, force, world, power, stem, k, kills);
                        fields.Add(field);
                        break;
                    }
                    default:
                        HH.Warn($"{force.label}: {type} force '{name}' is not ported");
                        break;
                }
            }
            foreach (var ps in systems)
            {
                if (accelerates)
                {
                    var fol = ps.forceOverLifetime;
                    fol.enabled = true;
                    fol.space = ParticleSystemSimulationSpace.World;
                    fol.randomized = false;
                    fol.x = new MinMaxCurve(acceleration.x);
                    fol.y = new MinMaxCurve(acceleration.y);
                    fol.z = new MinMaxCurve(acceleration.z);
                }
                if (drifts)
                {
                    var vol = ps.velocityOverLifetime;
                    vol.enabled = true;
                    vol.space = drift;
                    vol.x = new MinMaxCurve(driftMin.x, driftMax.x);
                    vol.y = new MinMaxCurve(driftMin.y, driftMax.y);
                    vol.z = new MinMaxCurve(driftMin.z, driftMax.z);
                }
                if (drag > 0f)
                {
                    var limit = ps.limitVelocityOverLifetime;
                    limit.enabled = true;
                    limit.limit = new MinMaxCurve(float.MaxValue);
                    limit.drag = new MinMaxCurve(drag);
                    limit.multiplyDragByParticleSize = false;
                    limit.multiplyDragByParticleVelocity = false;
                }
                if (fields.Count > 0)
                {
                    var external = ps.externalForces;
                    external.enabled = true;
                    external.multiplier = 1f;
                    external.influenceFilter = ParticleSystemGameObjectFilter.List;
                    foreach (var field in fields)
                        external.AddInfluence(field);
                }
                if (kills.Count > 0)
                {
                    var trigger = ps.trigger;
                    trigger.enabled = true;
                    for (int i = 0; i < kills.Count; i++)
                        trigger.SetCollider(i, kills[i]);
                    trigger.inside = ParticleSystemOverlapAction.Kill;
                    trigger.enter = ParticleSystemOverlapAction.Kill;
                    trigger.exit = ParticleSystemOverlapAction.Ignore;
                    trigger.outside = ParticleSystemOverlapAction.Ignore;
                    trigger.radiusScale = 0.001f;
                }
            }
        }

        static int Dominant(Vector3 v)
        {
            for (int axis = 0; axis < 3; axis++)
                if (Mathf.Abs(Mathf.Abs(v[axis]) - v.magnitude) < 1e-5f)
                    return axis;
            return -1;
        }

        // Point gravity: a constant pull of the force's power towards its
        // position; inside the radius particles are killed (kill particles) or
        // held on the sphere. Unity's force field pulls from beyond the radius;
        // the kill uses a trigger sphere.
        static ParticleSystemForceField PointGravity(EffectInfo fx, ParticleFields force, bool world, Vector3 power, string stem, int k, List<Collider> kills)
        {
            float radius = force.Num("pointGravityRadius");
            bool kill = force.Flag("killParticles");
            Vector3 offset = U(force.Vec("dragForcePosition", Vector3.zero));
            float strength = (power.x + power.y + power.z) / 3f;
            if (Mathf.Abs(power.x - strength) + Mathf.Abs(power.y - strength) + Mathf.Abs(power.z - strength) > 1e-4f)
                HH.Warn($"{force.label}: point gravity {power} pulls with its mean {strength:0.###}");
            if (force.Flag("pointGravityUseRandomPointsOnSphere"))
                HH.Warn($"{force.label}: point gravity aims at the centre instead of random points on its sphere");
            if (!kill)
                HH.Warn($"{force.label}: particles reaching the point gravity sphere pass through instead of resting on it");
            var go = new GameObject($"{stem}_F{k}_PointGravity");
            go.transform.SetParent(fx.root, false);
            if (world)
            {
                go.transform.position = fx.root.position + offset;
                go.transform.rotation = Quaternion.identity;
            }
            else
                go.transform.localPosition = offset;
            var field = go.AddComponent<ParticleSystemForceField>();
            field.shape = ParticleSystemForceFieldShape.Sphere;
            field.startRange = kill ? 0f : radius;
            field.endRange = 1000f;
            field.directionX = new MinMaxCurve(0f);
            field.directionY = new MinMaxCurve(0f);
            field.directionZ = new MinMaxCurve(0f);
            field.gravity = new MinMaxCurve(strength);
            field.gravityFocus = 0f;
            field.rotationSpeed = new MinMaxCurve(0f);
            field.rotationAttraction = new MinMaxCurve(0f);
            field.drag = new MinMaxCurve(0f);
            ForceFields++;
            if (kill)
            {
                go.layer = 2; // Ignore Raycast
                var sphere = go.AddComponent<SphereCollider>();
                sphere.isTrigger = true;
                sphere.radius = radius;
                kills.Add(sphere);
                KillTriggers++;
            }
            HH.Log($"{force.label}: point gravity {strength:0.###} at {go.transform.position}, radius {radius}{(kill ? ", kills" : "")}");
            return field;
        }

        static void Renderer(ParticleSystemRenderer renderer, Material material, float sortingFudge)
        {
            renderer.sharedMaterial = material;
            renderer.sortMode = ParticleSystemSortMode.OldestInFront;
            renderer.sortingFudge = sortingFudge;
            renderer.minParticleSize = 0f;
            renderer.maxParticleSize = 10f;
            renderer.allowRoll = true;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            renderer.enableGPUInstancing = false;
            renderer.SetActiveVertexStreams(Streams);
        }
    }
}
