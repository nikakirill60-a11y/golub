using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The destroyed tank: <c>tank_particles_manager.gd</c> hangs one stopped
    /// <c>explosion1.tscn</c> under the hull and
    /// <c>health_component.on_death</c> starts it. Every number is checked
    /// against Resources/Data/tank_explosion_source_manifest.json, which
    /// Tools/port_godot_particles.py dumps out of the source scene, so an
    /// emitter that is retimed or dropped fails here.
    /// </summary>
    public sealed class BattleTankWreckTests
    {
        private const float Tolerance = 1e-4f;

        /// <summary>MS-1 hull measurements, as BattleTankTests reads them.</summary>
        private const float HullHeight = 2.09221f;
        private const float HullLength = 3.42161f;
        private const float HullWidth = 1.72269f;

        private readonly List<GameObject> _spawned = new List<GameObject>();

        private JsonNode _manifest;

        [SetUp]
        public void SetUp()
        {
            var text = Resources.Load<TextAsset>(BattleTankWreck.ManifestResource);
            Assert.IsNotNull(text, "the tank explosion manifest is missing");
            _manifest = JsonNode.Parse(text.text);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawned)
            {
                if (go != null) { Object.DestroyImmediate(go); }
            }

            _spawned.Clear();
        }

        private BattleTankWreck Wreck()
        {
            var host = new GameObject("tank");
            _spawned.Add(host);
            var wreck = BattleTankWreck.Create(
                host.transform, HullHeight, HullLength, HullWidth);
            Assert.IsNotNull(wreck, "the wreck effect was not built");
            return wreck;
        }

        [Test]
        public void Placement_FollowsTheSourceScaleAndOffset()
        {
            var wreck = Wreck();
            var placement = _manifest["placement"];

            Assert.AreEqual(placement["name"].AsString(), wreck.name);

            // scale = ONE * height * len * width * 0.0005, applied on top of
            // the scale the ParticleStarter root carries in explosion1.tscn.
            var factor = placement["scaleFactor"].AsFloat();
            var root = _manifest["root"]["scale"];
            var product = HullHeight * HullLength * HullWidth * factor;
            Assert.That(
                wreck.transform.localScale.x,
                Is.EqualTo(root[0].AsFloat(1f) * product).Within(Tolerance));
            Assert.That(
                wreck.transform.localScale.y,
                Is.EqualTo(root[1].AsFloat(1f) * product).Within(Tolerance));
            Assert.That(
                wreck.transform.localScale.z,
                Is.EqualTo(root[2].AsFloat(1f) * product).Within(Tolerance));

            // position.y = height * 0.5, position.z = len * 0.2 mirrored.
            Assert.That(wreck.transform.localPosition.x, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(
                wreck.transform.localPosition.y,
                Is.EqualTo(HullHeight * 0.5f).Within(Tolerance));
            Assert.That(
                wreck.transform.localPosition.z,
                Is.EqualTo(-HullLength * 0.2f).Within(Tolerance));
        }

        [Test]
        public void Emitters_MatchTheSourceSceneOrderAndNames()
        {
            var wreck = Wreck();
            var emitters = _manifest["emitters"];

            Assert.AreEqual(emitters.Count, wreck.Emitters.Count,
                "the wreck does not carry every source emitter");
            for (var i = 0; i < emitters.Count; i++)
            {
                Assert.AreEqual(
                    emitters[i]["name"].AsString(), wreck.Emitters[i].name,
                    "emitter " + i + " is not the one the source scene has");
            }
        }

        [Test]
        public void Emitters_KeepTheSourceTimingAmountsAndBursts()
        {
            var wreck = Wreck();
            var emitters = _manifest["emitters"];

            for (var i = 0; i < emitters.Count; i++)
            {
                var source = emitters[i];
                var name = source["name"].AsString();
                var particles = wreck.Emitter(name);
                Assert.IsNotNull(particles, "missing emitter: " + name);

                var main = particles.main;
                var lifetime = source["lifetime"].AsFloat();
                Assert.That(
                    main.startLifetime.constant, Is.EqualTo(lifetime).Within(Tolerance),
                    name + " was retimed");
                Assert.That(main.duration, Is.EqualTo(lifetime).Within(Tolerance),
                    name + " has the wrong emission window");
                Assert.AreEqual(!source["oneShot"].AsBool(), main.loop,
                    name + " does not match one_shot");
                Assert.AreEqual(source["amount"].AsInt(), main.maxParticles,
                    name + " allocates a different buffer");
                Assert.AreEqual(
                    source["localCoords"].AsBool()
                        ? ParticleSystemSimulationSpace.Local
                        : ParticleSystemSimulationSpace.World,
                    main.simulationSpace,
                    name + " simulates in the wrong space");

                // explosiveness pulls amount * amount_ratio particles into one
                // burst and leaves the rest streaming over the lifetime.
                var count = Mathf.RoundToInt(
                    source["amount"].AsInt() * source["amountRatio"].AsFloat(1f));
                var burst = Mathf.RoundToInt(
                    count * Mathf.Clamp01(source["explosiveness"].AsFloat()));
                var emission = particles.emission;
                Assert.AreEqual(burst > 0 ? 1 : 0, emission.burstCount,
                    name + " has the wrong number of bursts");
                if (burst > 0)
                {
                    Assert.AreEqual(burst, emission.GetBurst(0).count.constant,
                        name + " emits a different burst");
                }

                // A trail is driven by its owner instead of by its own rate,
                // so sub_emitter_frequency wins there; Debris_Keeps... checks it.
                if (IsSubEmitterOfSomebody(name)) { continue; }

                var streamed = Mathf.Max(0, count - burst);
                Assert.That(
                    emission.rateOverTime.constant,
                    Is.EqualTo(streamed / lifetime).Within(Tolerance),
                    name + " streams at the wrong rate");
            }
        }

        /// <summary>True when another emitter names this one as its sub-emitter.</summary>
        private bool IsSubEmitterOfSomebody(string name)
        {
            var emitters = _manifest["emitters"];
            for (var i = 0; i < emitters.Count; i++)
            {
                var path = emitters[i]["subEmitter"].AsString();
                if (string.IsNullOrEmpty(path)) { continue; }
                if (path.Substring(path.LastIndexOf('/') + 1) == name) { return true; }
            }

            return false;
        }

        [Test]
        public void Emitters_KeepTheSourceShapeVelocityAndSize()
        {
            var wreck = Wreck();
            var emitters = _manifest["emitters"];

            for (var i = 0; i < emitters.Count; i++)
            {
                var source = emitters[i];
                var process = source["process"];
                var name = source["name"].AsString();
                var particles = wreck.Emitter(name);

                var shape = particles.shape;
                Assert.AreEqual(ParticleSystemShapeType.Cone, shape.shapeType,
                    name + " no longer emits into the source cone");
                Assert.That(
                    shape.angle, Is.EqualTo(process["spread"].AsFloat()).Within(Tolerance),
                    name + " has the wrong spread");

                var main = particles.main;
                Assert.That(
                    main.startSpeed.constantMin,
                    Is.EqualTo(process["initialVelocityMin"].AsFloat()).Within(Tolerance),
                    name + " launches at the wrong speed");
                Assert.That(
                    main.startSpeed.constantMax,
                    Is.EqualTo(process["initialVelocityMax"].AsFloat()).Within(Tolerance),
                    name + " launches at the wrong speed");

                // The Unity start size is the QuadMesh size times the process
                // material's scale range.
                var size = source["mesh"]["size"];
                Assert.IsTrue(main.startSize3D, name + " lost its quad proportions");
                Assert.That(
                    main.startSizeX.constantMin,
                    Is.EqualTo(size[0].AsFloat() * process["scaleMin"].AsFloat()).Within(Tolerance),
                    name + " is the wrong size");
                Assert.That(
                    main.startSizeY.constantMax,
                    Is.EqualTo(size[1].AsFloat() * process["scaleMax"].AsFloat()).Within(Tolerance),
                    name + " is the wrong size");

                // gravity lands on force over lifetime, mirrored across Z.
                var gravity = process["gravity"];
                var force = particles.forceOverLifetime;
                var hasGravity = gravity[0].AsFloat() != 0f
                    || gravity[1].AsFloat() != 0f || gravity[2].AsFloat() != 0f;
                Assert.AreEqual(hasGravity, force.enabled,
                    name + " does not match the source gravity");
                if (hasGravity)
                {
                    Assert.That(
                        force.y.constant,
                        Is.EqualTo(gravity[1].AsFloat()).Within(Tolerance),
                        name + " falls at the wrong rate");
                    Assert.That(
                        force.z.constant,
                        Is.EqualTo(-gravity[2].AsFloat()).Within(Tolerance),
                        name + " was not mirrored across Z");
                }

                var radialMin = process["radialVelocityMin"].AsFloat();
                var radialMax = process["radialVelocityMax"].AsFloat();
                var velocity = particles.velocityOverLifetime;
                if (radialMin != 0f || radialMax != 0f)
                {
                    Assert.IsTrue(velocity.enabled, name + " lost its radial velocity");
                    Assert.That(
                        velocity.radial.constantMax,
                        Is.EqualTo(radialMax).Within(Tolerance),
                        name + " flies outwards at the wrong speed");
                }
            }
        }

        [Test]
        public void Emitters_LoadTheirSourceSpriteSheets()
        {
            var wreck = Wreck();
            var emitters = _manifest["emitters"];

            for (var i = 0; i < emitters.Count; i++)
            {
                var source = emitters[i];
                var name = source["name"].AsString();
                var material = source["mesh"]["material"];
                var textureName = material["texture"].AsString();

                var texture = Resources.Load<Texture2D>(
                    BattleTankWreck.TextureFolder + textureName);
                Assert.IsNotNull(texture,
                    "the porter did not copy the sprite sheet: " + textureName);

                var renderer = wreck.Emitter(name)
                    .GetComponent<ParticleSystemRenderer>();
                Assert.IsNotNull(renderer.sharedMaterial, name + " has no material");

                // align_y and transform_align stretch the quad along the
                // particle's own velocity.
                var stretched = source["process"]["alignY"].AsBool()
                    || source["transformAlign"].AsInt()
                        >= BattleTankWreck.TransformAlignYToVelocity;
                Assert.AreEqual(
                    stretched
                        ? ParticleSystemRenderMode.Stretch
                        : ParticleSystemRenderMode.Billboard,
                    renderer.renderMode,
                    name + " is drawn the wrong way round");

                // anim_h_frames plays the sheet once across the lifetime.
                var columns = material["animHFrames"].AsInt(1);
                var sheet = wreck.Emitter(name).textureSheetAnimation;
                Assert.AreEqual(columns > 1, sheet.enabled,
                    name + " does not match the source flip-book");
                if (columns > 1)
                {
                    Assert.AreEqual(columns, sheet.numTilesX,
                        name + " plays the wrong number of frames");
                }
            }
        }

        [Test]
        public void Debris_KeepsTheSourceSubEmitterAndCollision()
        {
            var wreck = Wreck();
            var emitters = _manifest["emitters"];

            for (var i = 0; i < emitters.Count; i++)
            {
                var source = emitters[i];
                var path = source["subEmitter"].AsString();
                if (string.IsNullOrEmpty(path)) { continue; }

                var parent = wreck.Emitter(source["name"].AsString());
                var leaf = path.Substring(path.LastIndexOf('/') + 1);
                var child = wreck.Emitter(leaf);
                Assert.IsNotNull(child, "the sub-emitter is missing: " + leaf);

                var sub = parent.subEmitters;
                Assert.IsTrue(sub.enabled, "the sub-emitter was not bound");
                Assert.AreEqual(1, sub.subEmittersCount);
                Assert.AreSame(child, sub.GetSubEmitterSystem(0));
                Assert.AreSame(parent.transform, child.transform.parent,
                    "Unity only drives a sub-emitter parented to its owner");
                Assert.That(
                    child.emission.rateOverTime.constant,
                    Is.EqualTo(source["process"]["subEmitterFrequency"].AsFloat())
                        .Within(Tolerance),
                    "the trail does not spawn at the source frequency");

                // collision_mode = COLLISION_HIDE_ON_CONTACT
                if (source["process"]["collisionMode"].AsInt()
                    == BattleTankWreck.CollisionHideOnContact)
                {
                    Assert.IsTrue(parent.collision.enabled,
                        "the debris no longer collides with the map");
                    Assert.That(
                        parent.collision.lifetimeLoss.constant,
                        Is.EqualTo(1f).Within(Tolerance),
                        "the debris survives its first contact");
                }
            }
        }

        [Test]
        public void Wreck_StartsHiddenAndStoppedAndOnlyDeathPlaysIt()
        {
            var wreck = Wreck();
            Assert.IsTrue(_manifest["placement"]["startsHidden"].AsBool(),
                "the source hides the wreck until the tank dies");
            Assert.IsFalse(wreck.Visible, "the wreck is visible on a healthy tank");
            for (var i = 0; i < wreck.Emitters.Count; i++)
            {
                Assert.IsFalse(wreck.Emitters[i].isEmitting,
                    wreck.Emitters[i].name + " emits on a healthy tank");
            }

            // _start_wreck_effect: show() then start().
            wreck.Play();
            Assert.IsTrue(wreck.Visible, "the wreck stayed hidden after the death");
            Assert.IsTrue(wreck.Emitter("smoke_loop").isPlaying,
                "the burning wreck is not running");

            // _reset_wreck_effect hides the tree, because clearing emitting
            // alone would leave the already spawned particles alive.
            wreck.Stop();
            Assert.IsFalse(wreck.Visible, "the reset left the wreck on screen");
            Assert.IsFalse(wreck.Emitter("smoke_loop").isEmitting,
                "the reset left an emitter running");
        }

        [Test]
        public void Rotation_MirrorsTheSourceEulerAcrossZ()
        {
            var degrees = JsonNode.Parse("[10, 90, 20]");
            var mirrored = BattleTankWreck.Rotation(degrees);
            Assert.That(
                Quaternion.Angle(mirrored, Quaternion.Euler(-10f, -90f, 20f)),
                Is.LessThan(0.001f),
                "a mirrored rotation flips its X and Y angles");
        }
    }
}
