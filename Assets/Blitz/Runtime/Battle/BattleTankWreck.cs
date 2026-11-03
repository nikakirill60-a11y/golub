using System.Collections.Generic;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Lighting;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// <c>tank_particles_manager.gd</c> plus <c>particle_starter.gd</c>: the
    /// one <c>explosion1.tscn</c> instance every tank owns, parked under the
    /// hull as <c>wreck_effect</c>, hidden and stopped until
    /// <c>health_component.on_death</c> fires.
    ///
    /// Every number below is read from
    /// <c>Resources/Data/tank_explosion_source_manifest.json</c>, which
    /// <c>Tools/port_godot_particles.py</c> writes from a headless dump of the
    /// source scene. Nothing here is retimed or eyeballed.
    ///
    /// Where Godot and Unity genuinely disagree the mapping is spelled out on
    /// the spot rather than hidden: Godot's process material is a velocity
    /// field over a point or sphere emitter, Unity's is a shape plus a set of
    /// over-lifetime modules.
    /// </summary>
    public sealed class BattleTankWreck : MonoBehaviour
    {
        /// <summary>Manifest written by <c>Tools/port_godot_particles.py</c>.</summary>
        public const string ManifestResource = "Data/tank_explosion_source_manifest";

        /// <summary>Resources folder the porter copies the sprite sheets into.</summary>
        public const string TextureFolder = "Particles/Tanks/";

        /// <summary>
        /// Serialized particle materials, for the same reason the hangar smoke
        /// has one: Unity's player variant stripper only keeps the keyword
        /// combinations some material asset uses, so a material assembled purely
        /// at runtime renders as an opaque white quad on a device.
        /// <c>ProjectBootstrap.EnsureRuntimeMaterials</c> generates both.
        /// </summary>
        public const string AdditiveMaterialResource = "Materials/BattleParticleAdd";
        public const string MixMaterialResource = "Materials/BattleParticleMix";

        /// <summary>Shader both materials use, as the hangar smoke does.</summary>
        public const string ParticleShaderName =
            "Universal Render Pipeline/Particles/Unlit";

        /// <summary><c>BaseMaterial3D.BLEND_MODE_MIX</c> / <c>_ADD</c>.</summary>
        public const int BlendModeMix = 0;
        public const int BlendModeAdd = 1;

        /// <summary><c>ParticleProcessMaterial.EMISSION_SHAPE_SPHERE</c>.</summary>
        public const int EmissionShapePoint = 0;
        public const int EmissionShapeSphere = 1;

        /// <summary><c>ParticleProcessMaterial.COLLISION_HIDE_ON_CONTACT</c>.</summary>
        public const int CollisionHideOnContact = 2;

        /// <summary><c>GPUParticles3D.TRANSFORM_ALIGN_Y_TO_VELOCITY</c> and up.</summary>
        public const int TransformAlignYToVelocity = 2;

        /// <summary>The dump samples every Godot curve at this many points.</summary>
        public const int CurveSamples = 17;

        /// <summary>A Gradient cannot hold more than eight keys of either kind.</summary>
        public const int MaxGradientKeys = 8;

        private readonly List<ParticleSystem> _emitters = new List<ParticleSystem>();
        private readonly List<Material> _materials = new List<Material>();

        /// <summary>The ten emitters of <c>explosion1.tscn</c>, in scene order.</summary>
        public IReadOnlyList<ParticleSystem> Emitters { get { return _emitters; } }

        /// <summary>The tank this wreck belongs to.</summary>
        public BattleTank Tank { get; private set; }

        /// <summary><c>wreck_effect.visible</c>.</summary>
        public bool Visible { get { return gameObject.activeSelf; } }

        /// <summary>
        /// <c>tank_particles_manager.setup</c>: the wreck is built once per
        /// tank, scaled by the hull box, pushed behind and above its centre,
        /// and left stopped and hidden.
        /// </summary>
        public static BattleTankWreck Attach(BattleTank tank)
        {
            if (tank == null) { return null; }

            var host = new GameObject("Original480Wreck");
            host.transform.SetParent(tank.transform,false);
            var wreck = host.AddComponent<BattleTankWreck>();
            wreck._original = host.AddComponent<Original480Wreck>();
            wreck._original.Initialize(tank);
            if (wreck != null) { wreck.Tank = tank; }
            return wreck;
        }

        /// <summary>
        /// The same build against bare hull measurements, which is what the
        /// tests hand it: a whole tank is not needed to place the effect.
        /// </summary>
        public static BattleTankWreck Create(
            Transform parent, float hullHeight, float hullLength, float hullWidth)
        {
            var manifest = JsonResources.Load(ManifestResource);
            if (manifest == null)
            {
                Debug.LogError("[Blitz] missing explosion manifest: " + ManifestResource);
                return null;
            }

            var placement = manifest["placement"];

            var host = new GameObject(placement["name"].AsString("wreck_effect"));
            host.transform.SetParent(parent, false);

            var wreck = host.AddComponent<BattleTankWreck>();
            wreck.Build(manifest, placement, hullHeight, hullLength, hullWidth);
            return wreck;
        }

        /// <summary><c>_start_wreck_effect</c>: show the tree and restart it.</summary>
        private Original480Wreck _original;

        /// <summary>The 4.8 wreck scene player, or null for the fallback wreck.</summary>
        public Original480Wreck Original { get { return _original; } }
        public void Play()
        {
            if (_original != null) { _original.Play(); return; }
            gameObject.SetActive(true);
            for (var i = 0; i < _emitters.Count; i++)
            {
                var emitter = _emitters[i];
                if (emitter == null) { continue; }

                // particle_starter.start() restarts every buffer, and a
                // sub-emitter is driven by its parent instead of by itself.
                if (IsSubEmitter(emitter)) { continue; }

                emitter.Clear(true);
                emitter.Play(true);
            }
        }

        /// <summary>
        /// <c>_reset_wreck_effect</c>. Clearing emitting alone leaves the
        /// already spawned particles alive, so the source hides the tree as
        /// well and lets the next death restart the buffers.
        /// </summary>
        public void Stop()
        {
            if (_original != null) { _original.Stop(); return; }
            for (var i = 0; i < _emitters.Count; i++)
            {
                var emitter = _emitters[i];
                if (emitter == null) { continue; }

                emitter.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }

            gameObject.SetActive(false);
        }

        /// <summary><c>set_fake_wreck</c>, which the debug tools drive.</summary>
        public void SetFakeWreck(bool on)
        {
            if (on) { Play(); }
            else if (Tank == null || !Tank.Dead) { Stop(); }
        }

        /// <summary>The emitter named by the source scene, or null.</summary>
        public ParticleSystem Emitter(string emitterName)
        {
            for (var i = 0; i < _emitters.Count; i++)
            {
                if (_emitters[i] != null && _emitters[i].name == emitterName)
                {
                    return _emitters[i];
                }
            }

            return null;
        }

        private void OnDestroy()
        {
            for (var i = 0; i < _materials.Count; i++)
            {
                if (_materials[i] != null) { Destroy(_materials[i]); }
            }

            _materials.Clear();
        }

        private void Build(
            JsonNode manifest, JsonNode placement,
            float hullHeight, float hullLength, float hullWidth)
        {
            // scale = ONE * height * len * width * 0.0005, on top of the
            // scale the ParticleStarter root already carries in the scene.
            var factor = placement["scaleFactor"].AsFloat();
            var root = Scale(manifest["root"]["scale"]);
            transform.localScale =
                root * (hullHeight * hullLength * hullWidth * factor);

            // position.z = len * 0.2 and position.y = height * 0.5, mirrored
            // across Z with the rest of the port: the source drives along -Z,
            // so the offset lands behind the hull in both engines.
            var offset = GodotSpace.Point(0f, hullHeight * 0.5f, hullLength * 0.2f);
            transform.localPosition = new Vector3(offset.X, offset.Y, offset.Z);

            var emitters = manifest["emitters"];
            var byName = new Dictionary<string, ParticleSystem>();
            for (var i = 0; i < emitters.Count; i++)
            {
                var built = BuildEmitter(emitters[i]);
                if (built == null) { continue; }

                _emitters.Add(built);
                byName[built.name] = built;
            }

            // sub_emitter is a node path inside the same scene, so it can only
            // be resolved once every emitter exists.
            for (var i = 0; i < emitters.Count; i++)
            {
                BindSubEmitter(emitters[i], byName);
            }

            if (placement["startsHidden"].AsBool(true)) { Stop(); }
        }

        private ParticleSystem BuildEmitter(JsonNode emitter)
        {
            var process = emitter["process"];
            var mesh = emitter["mesh"];
            var material = mesh["material"];

            var host = new GameObject(emitter["name"].AsString());
            host.transform.SetParent(transform, false);

            var position = Point(emitter["position"]);
            host.transform.localPosition = position;
            host.transform.localRotation = Rotation(emitter["rotationDegrees"]);
            host.transform.localScale = Scale(emitter["scale"]);

            var particles = host.AddComponent<ParticleSystem>();
            // A freshly added system may already be playing in Play Mode, and
            // duration cannot be written until it is fully cleared.
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var lifetime = emitter["lifetime"].AsFloat();
            var oneShot = emitter["oneShot"].AsBool();
            // amount is the buffer Godot allocates, amount_ratio the share of
            // it that is actually emitted.
            var amount = Mathf.Max(1, emitter["amount"].AsInt(1));
            var count = Mathf.Max(
                1,
                Mathf.RoundToInt(amount * emitter["amountRatio"].AsFloat(1f)));

            var main = particles.main;
            main.playOnAwake = false;
            main.loop = !oneShot;
            main.duration = Mathf.Max(lifetime, 0.01f);
            main.maxParticles = amount;
            main.simulationSpeed = emitter["speedScale"].AsFloat(1f);
            main.simulationSpace = emitter["localCoords"].AsBool()
                ? ParticleSystemSimulationSpace.Local
                : ParticleSystemSimulationSpace.World;
            // Godot scales a quad by the whole node chain, while the Unity
            // default (Local) only looks at the emitter transform and so
            // drops the wreck scale factor, blowing the quads up ~160x.
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;

            // lifetime_randomness cuts into the lifetime from below.
            var lifetimeRandomness = process["lifetimeRandomness"].AsFloat();
            main.startLifetime = lifetimeRandomness > 0f
                ? new ParticleSystem.MinMaxCurve(
                    lifetime * (1f - lifetimeRandomness), lifetime)
                : new ParticleSystem.MinMaxCurve(lifetime);

            main.startSpeed = Range(
                process["initialVelocityMin"], process["initialVelocityMax"]);
            main.startColor = Colour(process["color"]);
            main.startRotation = new ParticleSystem.MinMaxCurve(
                Mathf.Deg2Rad * process["angleMin"].AsFloat(),
                Mathf.Deg2Rad * process["angleMax"].AsFloat());

            // QuadMesh.size is the quad the process material scales, so the
            // Unity start size is the product of the two.
            var size = mesh["size"];
            var scaleMin = process["scaleMin"].AsFloat(1f);
            var scaleMax = process["scaleMax"].AsFloat(1f);
            main.startSize3D = true;
            main.startSizeX = new ParticleSystem.MinMaxCurve(
                size[0].AsFloat() * scaleMin, size[0].AsFloat() * scaleMax);
            main.startSizeY = new ParticleSystem.MinMaxCurve(
                size[1].AsFloat() * scaleMin, size[1].AsFloat() * scaleMax);
            main.startSizeZ = new ParticleSystem.MinMaxCurve(1f);

            ApplyEmission(particles, emitter, count, lifetime, oneShot);
            ApplyShape(particles, process);
            ApplyVelocity(particles, process);
            ApplyRotation(particles, process);
            ApplySize(particles, process);
            ApplyColour(particles, process);
            ApplyCollision(particles, process);
            ApplyRenderer(particles, emitter, mesh, material, process);

            return particles;
        }

        /// <summary>
        /// Godot spreads <c>amount</c> particles over one lifetime and
        /// <c>explosiveness</c> pulls them towards a single instant, which is
        /// exactly a Unity burst plus whatever rate is left over.
        /// </summary>
        private static void ApplyEmission(
            ParticleSystem particles, JsonNode emitter, int count, float lifetime,
            bool oneShot)
        {
            var explosiveness = Mathf.Clamp01(emitter["explosiveness"].AsFloat());
            var burst = Mathf.RoundToInt(count * explosiveness);
            var streamed = Mathf.Max(0, count - burst);

            var emission = particles.emission;
            emission.enabled = true;
            emission.rateOverTime = lifetime > 0f ? streamed / lifetime : 0f;
            if (burst <= 0)
            {
                emission.SetBursts(new ParticleSystem.Burst[0]);
                return;
            }

            var interval = Mathf.Max(lifetime, 0.01f);
            emission.SetBursts(new[]
            {
                oneShot
                    ? new ParticleSystem.Burst(0f, (short)burst)
                    : new ParticleSystem.Burst(0f, (short)burst, 0, interval),
            });
        }

        /// <summary>
        /// The process material emits along <c>direction</c> inside
        /// <c>spread</c>, which is a Unity cone. A sphere emission shape keeps
        /// that cone and only widens the base: Unity's cone base is a disc of
        /// the sphere's radius, so the spawn volume is flattened while every
        /// launch direction stays the one the source authored. Feeding the
        /// sphere shape instead would make velocity radial and blow the smoke
        /// downwards as well.
        /// </summary>
        private static void ApplyShape(ParticleSystem particles, JsonNode process)
        {
            var shape = particles.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = Mathf.Clamp(process["spread"].AsFloat(), 0f, 90f);
            shape.radius = process["emissionShape"].AsInt() == EmissionShapeSphere
                ? Mathf.Max(process["emissionSphereRadius"].AsFloat(), 0.0001f)
                : 0.0001f;
            shape.radiusThickness = 1f;
            shape.arc = 360f;
            shape.length = 0f;

            // A Unity cone fires along its own +Z, so the node is turned until
            // that axis lands on the mirrored source direction.
            var direction = Point(process["direction"]);
            shape.rotation = direction.sqrMagnitude > 0f
                ? Quaternion.LookRotation(direction.normalized).eulerAngles
                : Vector3.zero;
        }

        private static void ApplyVelocity(ParticleSystem particles, JsonNode process)
        {
            var radialMin = process["radialVelocityMin"].AsFloat();
            var radialMax = process["radialVelocityMax"].AsFloat();
            if (!Mathf.Approximately(radialMin, 0f) || !Mathf.Approximately(radialMax, 0f))
            {
                var velocity = particles.velocityOverLifetime;
                velocity.enabled = true;
                velocity.space = ParticleSystemSimulationSpace.World;
                velocity.radial = new ParticleSystem.MinMaxCurve(radialMin, radialMax);
            }

            var gravity = Point(process["gravity"]);
            if (gravity.sqrMagnitude > 0f)
            {
                var force = particles.forceOverLifetime;
                force.enabled = true;
                force.space = ParticleSystemSimulationSpace.World;
                force.x = new ParticleSystem.MinMaxCurve(gravity.x);
                force.y = new ParticleSystem.MinMaxCurve(gravity.y);
                force.z = new ParticleSystem.MinMaxCurve(gravity.z);
            }

            var damping = Mathf.Max(
                process["dampingMin"].AsFloat(), process["dampingMax"].AsFloat());
            if (damping > 0f)
            {
                // Godot subtracts damping units of speed per second, Unity
                // applies a drag proportional to the current speed. The two
                // agree at the authored launch speed and Unity's tail is
                // softer; there is no exact counterpart to bind them to.
                var limit = particles.limitVelocityOverLifetime;
                limit.enabled = true;
                limit.separateAxes = false;
                limit.drag = new ParticleSystem.MinMaxCurve(damping);
                limit.multiplyDragByParticleSize = false;
                limit.multiplyDragByParticleVelocity = false;
            }
        }

        private static void ApplyRotation(ParticleSystem particles, JsonNode process)
        {
            var min = process["angularVelocityMin"].AsFloat();
            var max = process["angularVelocityMax"].AsFloat();
            if (Mathf.Approximately(min, 0f) && Mathf.Approximately(max, 0f)) { return; }

            var rotation = particles.rotationOverLifetime;
            rotation.enabled = true;
            rotation.separateAxes = false;
            rotation.z = new ParticleSystem.MinMaxCurve(
                Mathf.Deg2Rad * min, Mathf.Deg2Rad * max);
        }

        private static void ApplySize(ParticleSystem particles, JsonNode process)
        {
            var samples = process["scaleCurve"];
            if (samples == null || samples.Count < 2) { return; }

            var size = particles.sizeOverLifetime;
            size.enabled = true;
            size.separateAxes = false;
            size.size = new ParticleSystem.MinMaxCurve(1f, Curve(samples));
        }

        /// <summary>
        /// <c>color_ramp</c> tinted by <c>alpha_curve</c>. Both are sampled by
        /// the dump; a Gradient holds eight keys of each kind, so a longer ramp
        /// is resampled evenly instead of being truncated.
        /// </summary>
        private static void ApplyColour(ParticleSystem particles, JsonNode process)
        {
            var ramp = process["colorRamp"];
            var alphaCurve = process["alphaCurve"];
            var hasRamp = ramp != null && ramp.Count > 0;
            var hasAlpha = alphaCurve != null && alphaCurve.Count > 1;
            if (!hasRamp && !hasAlpha) { return; }

            var colourKeys = new List<GradientColorKey>();
            var alphaKeys = new List<GradientAlphaKey>();
            if (hasRamp)
            {
                for (var i = 0; i < ramp.Count; i++)
                {
                    var point = ramp[i];
                    var offset = Mathf.Clamp01(point["offset"].AsFloat());
                    var colour = Colour(point["color"]);
                    colourKeys.Add(new GradientColorKey(colour, offset));
                    alphaKeys.Add(new GradientAlphaKey(
                        colour.a * Sample(alphaCurve, offset), offset));
                }
            }
            else
            {
                colourKeys.Add(new GradientColorKey(Color.white, 0f));
                colourKeys.Add(new GradientColorKey(Color.white, 1f));
                for (var i = 0; i < MaxGradientKeys; i++)
                {
                    var offset = i / (float)(MaxGradientKeys - 1);
                    alphaKeys.Add(new GradientAlphaKey(Sample(alphaCurve, offset), offset));
                }
            }

            var gradient = new Gradient();
            gradient.SetKeys(Trim(colourKeys), Trim(alphaKeys));

            var colour2 = particles.colorOverLifetime;
            colour2.enabled = true;
            colour2.color = new ParticleSystem.MinMaxGradient(gradient);
        }

        private static void ApplyCollision(ParticleSystem particles, JsonNode process)
        {
            if (process["collisionMode"].AsInt() != CollisionHideOnContact) { return; }

            var collision = particles.collision;
            collision.enabled = true;
            collision.type = ParticleSystemCollisionType.World;
            collision.mode = ParticleSystemCollisionMode.Collision3D;
            collision.bounce = 0f;
            // COLLISION_HIDE_ON_CONTACT removes the particle on the first hit.
            collision.lifetimeLoss = 1f;
            collision.collidesWith = ~0;
        }

        private void ApplyRenderer(
            ParticleSystem particles, JsonNode emitter, JsonNode mesh, JsonNode material,
            JsonNode process)
        {
            var renderer = particles.GetComponent<ParticleSystemRenderer>();

            // particle_flag_align_y and transform_align stretch the quad along
            // the particle's own velocity, which is Unity's stretched billboard.
            var stretched = process["alignY"].AsBool()
                || emitter["transformAlign"].AsInt() >= TransformAlignYToVelocity;
            renderer.renderMode = stretched
                ? ParticleSystemRenderMode.Stretch
                : ParticleSystemRenderMode.Billboard;
            if (stretched)
            {
                renderer.lengthScale = 1f;
                renderer.velocityScale = 0f;
                renderer.cameraVelocityScale = 0f;
            }
            else
            {
                renderer.alignment = ParticleSystemRenderSpace.View;
            }

            // QuadMesh.center_offset moves the quad inside its own frame, which
            // Unity expresses as a pivot in units of the particle size.
            var size = mesh["size"];
            var offset = mesh["centerOffset"];
            renderer.pivot = new Vector3(
                Ratio(offset[0].AsFloat(), size[0].AsFloat()),
                Ratio(offset[1].AsFloat(), size[1].AsFloat()),
                0f);

            // draw_order = DRAW_ORDER_INDEX draws in emission order.
            renderer.sortMode = ParticleSystemSortMode.OldestInFront;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sharedMaterial = CreateMaterial(material);

            ApplySheet(particles, material);
        }

        /// <summary>
        /// <c>StandardMaterial3D.anim_h_frames</c>: the sprite sheet plays once
        /// across the particle lifetime, backwards when anim_speed is negative.
        /// </summary>
        private static void ApplySheet(ParticleSystem particles, JsonNode material)
        {
            var columns = Mathf.Max(1, material["animHFrames"].AsInt(1));
            var rows = Mathf.Max(1, material["animVFrames"].AsInt(1));
            if (columns <= 1 && rows <= 1) { return; }

            var sheet = particles.textureSheetAnimation;
            sheet.enabled = true;
            sheet.mode = ParticleSystemAnimationMode.Grid;
            sheet.numTilesX = columns;
            sheet.numTilesY = rows;
            sheet.animation = ParticleSystemAnimationType.WholeSheet;
            sheet.timeMode = ParticleSystemAnimationTimeMode.Lifetime;
            sheet.cycleCount = 1;
            sheet.frameOverTime = new ParticleSystem.MinMaxCurve(
                1f, AnimationCurve.Linear(0f, 0f, 1f, 1f));
        }

        private void BindSubEmitter(
            JsonNode emitter, Dictionary<string, ParticleSystem> byName)
        {
            var path = emitter["subEmitter"].AsString();
            if (string.IsNullOrEmpty(path)) { return; }

            var leaf = path.Substring(path.LastIndexOf('/') + 1);
            ParticleSystem child;
            if (!byName.TryGetValue(leaf, out child) || child == null) { return; }

            ParticleSystem parent;
            if (!byName.TryGetValue(emitter["name"].AsString(), out parent)
                || parent == null)
            {
                return;
            }

            // Unity only accepts a sub-emitter that is a child of its owner, so
            // the trail moves under the debris and keeps the world position the
            // scene gave it.
            var world = child.transform.localPosition;
            child.transform.SetParent(parent.transform, false);
            child.transform.localPosition = world - parent.transform.localPosition;

            var childMain = child.main;
            childMain.playOnAwake = false;

            // SUB_EMITTER_CONSTANT spawns sub_emitter_frequency particles per
            // second for as long as the parent particle lives, which is what a
            // birth sub-emitter with its own rate does here.
            var childEmission = child.emission;
            childEmission.enabled = true;
            childEmission.rateOverTime =
                emitter["process"]["subEmitterFrequency"].AsFloat();
            childEmission.SetBursts(new ParticleSystem.Burst[0]);

            var sub = parent.subEmitters;
            sub.enabled = true;
            sub.AddSubEmitter(
                child,
                ParticleSystemSubEmitterType.Birth,
                ParticleSystemSubEmitterProperties.InheritNothing);
        }

        private bool IsSubEmitter(ParticleSystem emitter)
        {
            return emitter.transform.parent != transform;
        }

        private Material CreateMaterial(JsonNode source)
        {
            var additive = source["blendMode"].AsInt() == BlendModeAdd;
            var textureName = source["texture"].AsString();
            var texture = string.IsNullOrEmpty(textureName)
                ? null
                : Resources.Load<Texture2D>(TextureFolder + textureName);
            if (texture == null && !string.IsNullOrEmpty(textureName))
            {
                Debug.LogWarning(
                    "[Blitz] missing particle sprite sheet: " + TextureFolder + textureName);
            }

            var resource = additive ? AdditiveMaterialResource : MixMaterialResource;
            var preset = Resources.Load<Material>(resource);
            Material material;
            if (preset != null && preset.shader != null)
            {
                material = new Material(preset);
            }
            else
            {
                var shader = Shader.Find(ParticleShaderName);
                if (shader == null) { shader = Shader.Find("Particles/Standard Unlit"); }
                if (shader == null) { shader = Shader.Find("Unlit/Transparent"); }
                if (shader == null) { return null; }

                material = new Material(shader);
                Debug.LogWarning(
                    "[Blitz] missing pinned particle material: " + resource);
            }

            material.name = textureName;
            ConfigureParticleMaterial(material, texture, additive, Colour(source["albedoColor"]));
            _materials.Add(material);
            return material;
        }

        /// <summary>
        /// The blend state of the source StandardMaterial3D: unshaded, no fog,
        /// no ambient, alpha transparency, and either ADD or MIX. Shared by the
        /// runtime and by the edit-time material generator so the two cannot
        /// drift apart, exactly as the hangar smoke does.
        /// </summary>
        public static void ConfigureParticleMaterial(
            Material material, Texture2D texture, bool additive)
        {
            ConfigureParticleMaterial(material, texture, additive, Color.white);
        }

        /// <summary>
        /// The same blend state with the source <c>albedo_color</c> bound to the
        /// material tint. Godot multiplies the sheet by that colour, and three
        /// emitters rely on it: <c>smoke_loop</c> is a dark grey (0.122), while
        /// <c>smoke2</c> and <c>fire_loop</c> carry an alpha well below one and
        /// <c>fire2_loop</c> an orange tint. Dropping it rendered them as white
        /// quads.
        /// </summary>
        public static void ConfigureParticleMaterial(
            Material material, Texture2D texture, bool additive, Color albedo)
        {
            if (material == null) { return; }

            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
            if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", albedo);
            if (material.HasProperty("_Color")) material.SetColor("_Color", albedo);

            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_Blend")) material.SetFloat("_Blend", additive ? 2f : 0f);
            if (material.HasProperty("_BlendOp"))
            {
                material.SetFloat("_BlendOp", (float)BlendOp.Add);
            }
            if (material.HasProperty("_SrcBlend"))
            {
                material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            }
            if (material.HasProperty("_DstBlend"))
            {
                material.SetFloat(
                    "_DstBlend",
                    (float)(additive
                        ? UnityEngine.Rendering.BlendMode.One
                        : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
            }
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
            if (material.HasProperty("_Cull"))
            {
                material.SetFloat("_Cull", (float)CullMode.Off);
            }
            if (material.HasProperty("_AlphaClip")) material.SetFloat("_AlphaClip", 0f);
            if (material.HasProperty("_ReceiveShadows")) material.SetFloat("_ReceiveShadows", 0f);
            if (material.HasProperty("_ColorMode")) material.SetFloat("_ColorMode", 0f);
            if (material.HasProperty("_SoftParticlesEnabled"))
            {
                material.SetFloat("_SoftParticlesEnabled", 0f);
            }

            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.DisableKeyword("_ALPHAMODULATE_ON");
            material.DisableKeyword("_SURFACE_TYPE_OPAQUE");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.SetShaderPassEnabled("ShadowCaster", false);
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)RenderQueue.Transparent;
        }

        private static GradientColorKey[] Trim(List<GradientColorKey> keys)
        {
            if (keys.Count <= MaxGradientKeys) { return keys.ToArray(); }

            var trimmed = new GradientColorKey[MaxGradientKeys];
            for (var i = 0; i < MaxGradientKeys; i++)
            {
                var index = Mathf.RoundToInt(
                    i * (keys.Count - 1) / (float)(MaxGradientKeys - 1));
                trimmed[i] = keys[index];
            }

            return trimmed;
        }

        private static GradientAlphaKey[] Trim(List<GradientAlphaKey> keys)
        {
            if (keys.Count <= MaxGradientKeys) { return keys.ToArray(); }

            var trimmed = new GradientAlphaKey[MaxGradientKeys];
            for (var i = 0; i < MaxGradientKeys; i++)
            {
                var index = Mathf.RoundToInt(
                    i * (keys.Count - 1) / (float)(MaxGradientKeys - 1));
                trimmed[i] = keys[index];
            }

            return trimmed;
        }

        /// <summary>Rebuilds a sampled Godot curve as an AnimationCurve.</summary>
        public static AnimationCurve Curve(JsonNode samples)
        {
            var curve = new AnimationCurve();
            var last = Mathf.Max(1, samples.Count - 1);
            for (var i = 0; i < samples.Count; i++)
            {
                curve.AddKey(i / (float)last, samples[i].AsFloat());
            }

            for (var i = 0; i < curve.length; i++)
            {
                curve.SmoothTangents(i, 0f);
            }

            return curve;
        }

        /// <summary>Reads a sampled curve at a normalised offset.</summary>
        private static float Sample(JsonNode samples, float offset)
        {
            if (samples == null || samples.Count == 0) { return 1f; }

            var position = Mathf.Clamp01(offset) * (samples.Count - 1);
            var low = Mathf.FloorToInt(position);
            var high = Mathf.Min(low + 1, samples.Count - 1);
            return Mathf.Lerp(
                samples[low].AsFloat(), samples[high].AsFloat(), position - low);
        }

        private static float Ratio(float offset, float size)
        {
            return Mathf.Approximately(size, 0f) ? 0f : offset / size;
        }

        private static ParticleSystem.MinMaxCurve Range(JsonNode min, JsonNode max)
        {
            // Always a range, even when the source authored both ends the
            // same: a constant curve reports nothing in constantMin, and the
            // manifest tests read both ends back.
            return new ParticleSystem.MinMaxCurve(min.AsFloat(), max.AsFloat());
        }

        private static Color Colour(JsonNode node)
        {
            if (node == null || node.Count < 4) { return Color.white; }

            // The dump carries Godot's linear channels, and the particle
            // colour lands in a field Unity encodes itself, so it has to be
            // handed over encoded like every other ported colour. Feeding the
            // linear numbers straight in washes the smoke out to white.
            var colour = SourceEnvironment.SourceColour(new Color(
                node[0].AsFloat(), node[1].AsFloat(), node[2].AsFloat(), 1f));
            colour.a = node[3].AsFloat();
            return colour;
        }

        /// <summary>A source position or direction, mirrored across Z.</summary>
        private static Vector3 Point(JsonNode node)
        {
            if (node == null || node.Count < 3) { return Vector3.zero; }

            var point = GodotSpace.Point(
                node[0].AsFloat(), node[1].AsFloat(), node[2].AsFloat());
            return new Vector3(point.X, point.Y, point.Z);
        }

        private static Vector3 Scale(JsonNode node)
        {
            if (node == null || node.Count < 3) { return Vector3.one; }

            return new Vector3(
                node[0].AsFloat(1f), node[1].AsFloat(1f), node[2].AsFloat(1f));
        }

        /// <summary>
        /// Mirroring a rotation across Z conjugates it with diag(1, 1, -1),
        /// which turns a rotation about an axis into the same rotation about
        /// the mirrored axis: the X and Y Euler angles flip sign and Z keeps
        /// its own. Godot composes Y * X * Z, which is exactly the order
        /// Quaternion.Euler applies, so the triple maps straight across.
        /// </summary>
        public static Quaternion Rotation(JsonNode degrees)
        {
            if (degrees == null || degrees.Count < 3) { return Quaternion.identity; }

            return Quaternion.Euler(
                -degrees[0].AsFloat(), -degrees[1].AsFloat(), degrees[2].AsFloat());
        }
    }
}
