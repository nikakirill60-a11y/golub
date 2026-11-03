using System.Collections.Generic;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Battle.CombatFx;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The CPU port of the Godot combat effects: the shader random functions,
    /// the dumped manifest and its bindings, the emission clock of an
    /// explosive one-shot system, the GPUTrail3D ribbon and the shell sprite.
    /// </summary>
    public sealed class CombatFxTests
    {
        private const float Tolerance = 1e-5f;

        private bool _forced;

        [SetUp]
        public void SetUp()
        {
            _forced = GodotParticleEffect.ForceEnabled;
            GodotParticleEffect.ForceEnabled = true;
        }

        [TearDown]
        public void TearDown()
        {
            GodotParticleEffect.ForceEnabled = _forced;
        }

        [Test]
        public void HashAndRandFromSeed_MatchTheGodotShaderFunctions()
        {
            Assert.AreEqual(0u, GodotFxMath.Hash(0u));
            Assert.AreEqual(824515495u, GodotFxMath.Hash(1u));
            Assert.AreEqual(1747545881u, GodotFxMath.Hash(12345u));

            var seed = 1u;
            Assert.AreEqual(0.25645838f, GodotFxMath.Rand(ref seed), Tolerance);
            Assert.AreEqual(16807u, seed);
            Assert.AreEqual(0.23024338f, GodotFxMath.Rand(ref seed), Tolerance);
            Assert.AreEqual(282475249u, seed);
            Assert.AreEqual(0.67519646f, GodotFxMath.Rand(ref seed), Tolerance);
            Assert.AreEqual(1622650073u, seed);

            // A zero seed is replaced by the shader's own constant.
            seed = 0u;
            Assert.AreEqual(0.71642634f, GodotFxMath.Rand(ref seed), Tolerance);
            Assert.AreEqual(719435623u, seed);
        }

        [Test]
        public void Manifest_BindsEveryShotAndImpactSceneTheSourceUses()
        {
            var manifest = CombatFxManifest.Shared;
            Assert.IsNotNull(manifest);

            var shots = new[] { "shoot_auto", "shoot_small", "shoot_medium", "shoot_main", "shoot_large", "shoot_huge" };
            for (var i = 0; i < shots.Length; i++)
            {
                Assert.AreEqual(shots[i], manifest.ShotScene(i).Name);
            }

            Assert.AreEqual("armor_ap", manifest.PenetrationScene(ShellType.Ap).Name);
            Assert.AreEqual("armor_ap", manifest.PenetrationScene(ShellType.Heat).Name);
            Assert.AreEqual("armor_he", manifest.PenetrationScene(ShellType.He).Name);
            Assert.AreEqual("object_ap", manifest.ObjectScene(ShellType.Apcr).Name);
            Assert.AreEqual("object_heat", manifest.ObjectScene(ShellType.Heat).Name);
            Assert.AreEqual("armor_he", manifest.ObjectScene(ShellType.Hesh).Name);

            foreach (var scene in manifest.Scenes.Values)
            {
                Assert.Greater(scene.Emitters.Count, 0, scene.Name);
                foreach (var emitter in scene.Emitters)
                {
                    Assert.IsNotNull(emitter.Material, scene.Name + "/" + emitter.Name);
                    var path = manifest.TexturePath(emitter.Material.Texture);
                    Assert.IsNotNull(path, scene.Name + "/" + emitter.Name);
                    Assert.IsNotNull(Resources.Load<Texture2D>(path), path);
                }
            }

            Assert.AreEqual(45, manifest.Trail.Amount);
            Assert.AreEqual(60, manifest.Trail.FixedFps);
            Assert.AreEqual(0.50194556f, manifest.Trail.Curve.Sample(0.5f), 1e-4f);
            Assert.AreEqual(100 * 0.0075f, manifest.TrailScale(100), Tolerance);
            Assert.AreEqual(150 * 0.0075f, manifest.TrailScale(400), Tolerance);
            Assert.AreEqual(40 * 0.00012f, manifest.ShellPixelSize(20), Tolerance);
            Assert.AreEqual(0.05f, manifest.ImpactScaleFactor, Tolerance);
        }

        [Test]
        public void ExplosiveOneShot_EmitsEverythingOnTheFirstStepAndGoesInactive()
        {
            var data = Emitter("armor_ap", "debris");
            Assert.AreEqual(1f, data.Explosiveness);
            Assert.IsTrue(data.OneShot);

            var emitter = new GodotParticleEmitter(data);
            Assert.IsFalse(emitter.Drawn);
            emitter.Restart(1234u);
            emitter.Advance(1f / 60f, GodotXform.Identity);
            Assert.AreEqual(data.Amount, emitter.ActiveCount);
            Assert.IsTrue(emitter.Drawn);

            // Same seed, same layout: the systems are reproducible.
            var twin = new GodotParticleEmitter(data);
            twin.Restart(1234u);
            twin.Advance(1f / 60f, GodotXform.Identity);
            for (var i = 0; i < data.Amount; i++)
            {
                Assert.AreEqual(emitter.ParticleTransform(i).Origin, twin.ParticleTransform(i).Origin);
                Assert.AreEqual(emitter.ParticleVelocity(i), twin.ParticleVelocity(i));
            }

            var geometry = new GodotFxGeometry();
            var indices = new List<int>();
            var view = new GodotView { X = Vector3.right, Y = Vector3.up, Z = Vector3.forward, Origin = new Vector3(0f, 0f, 10f) };
            emitter.AppendGeometry(view, GodotXform.Identity, geometry, indices);
            Assert.AreEqual(4 * data.Amount, geometry.Positions.Count);
            Assert.AreEqual(6 * data.Amount, indices.Count);

            // One-shot: no more emission, the particles die with their
            // lifetime and the system is inactive 1.2 lifetimes later.
            for (var step = 0; step < Mathf.CeilToInt(data.Lifetime * 2.5f / 0.1f); step++)
            {
                emitter.Advance(0.1f, GodotXform.Identity);
            }

            Assert.IsFalse(emitter.Emitting);
            Assert.AreEqual(0, emitter.ActiveCount);
            Assert.IsFalse(emitter.Drawn);
        }

        [Test]
        public void FixedFps_KeepsTheRemainderBetweenFrames()
        {
            var data = Emitter("armor_ap", "debris");
            var emitter = new GodotParticleEmitter(data);
            emitter.Restart(7u);
            emitter.Advance(0.05f, GodotXform.Identity);
            // 30 fps: the clearing step runs once, the rest waits for the next frame.
            Assert.AreEqual(0.05f - 1f / 30f, emitter.FrameRemainder, 1e-4f);
        }

        [Test]
        public void ParticleEffect_TurnsItsYAxisToTheHitNormal()
        {
            var scene = CombatFxManifest.Shared.Scene("armor_ap");
            var effect = GodotParticleEffect.Create(scene, "test_effect");
            try
            {
                effect.Root = GodotXform.Identity.Scaled(2f);
                effect.Play(false, new Vector3(0f, 0f, 3f));
                Assert.AreEqual(new Vector3(0f, 0f, 1f), effect.Root.Y);
                Assert.AreEqual(new Vector3(2f, 0f, 0f), effect.Root.X);

                effect.Root = GodotXform.Identity;
                effect.Play(false, Vector3.up);
                Assert.AreEqual(Vector3.up, effect.Root.Y);

                var camera = new GameObject("test_camera").transform;
                camera.position = new Vector3(0f, 0f, -10f);
                effect.Tick(1f / 60f, camera);
                Assert.Greater(effect.Mesh.vertexCount, 0);
                Assert.Greater(effect.Mesh.subMeshCount, 0);
                Object.DestroyImmediate(camera.gameObject);
            }
            finally
            {
                Object.DestroyImmediate(effect.gameObject);
            }
        }

        [Test]
        public void TrailEmitter_CachesOneSegmentPerStep()
        {
            var curve = CombatFxManifest.Shared.Trail.Curve;
            var emitter = new GpuTrailEmitter(45, 60, GodotXform.Identity);
            emitter.Step(GodotXform.Identity);
            var moved = GodotXform.Identity;
            moved.Origin = new Vector3(0f, 0f, 1f);
            emitter.Step(moved);

            // Particle 0 spans the first and the second step.
            Assert.AreEqual(3f, emitter.Place(0));
            Assert.AreEqual(new Vector3(0f, 1f, 0f), emitter.Column(0, 0));
            Assert.AreEqual(new Vector3(0f, 1f, 1f), emitter.Column(0, 1));
            Assert.AreEqual(new Vector3(0f, -1f, 1f), emitter.Column(0, 2));
            Assert.AreEqual(new Vector3(0f, -1f, 0f), emitter.Column(0, 3));
            Assert.AreEqual(4f, emitter.Place(1));

            var geometry = new GodotFxGeometry();
            var indices = new List<int>();
            emitter.AppendGeometry(curve, geometry, indices);
            Assert.AreEqual(4 * 45, geometry.Positions.Count);
            Assert.AreEqual(6 * 45, indices.Count);

            // First vertex: UV (1, 1), the older edge at the curve's width.
            var h = curve.Sample(1f / 44f);
            Assert.AreEqual(1f - 2f * (0.5f * h + 0.5f), geometry.Positions[0].y, Tolerance);
            Assert.AreEqual(0f, geometry.Positions[0].z, Tolerance);
            Assert.AreEqual(new Vector2(1f / 44f, 1f), geometry.Uvs[0]);
            // The newer edge sits at the second step, mirrored into Unity space.
            Assert.AreEqual(-1f, geometry.Positions[1].z, Tolerance);
        }

        [Test]
        public void TrailRamp_FadesTheSourceColourToZeroAlpha()
        {
            var texels = BattleGpuTrail.RampTexels(new Vector4(1f, 1f, 1f, 0.235f));
            Assert.AreEqual(256, texels.Length);
            Assert.AreEqual(new Color32(255, 255, 255, 60), texels[0]);
            Assert.AreEqual(new Color32(255, 255, 255, 0), texels[255]);

            var heat = BattleGpuTrail.RampTexels(new Vector4(0.916f, 0.706f, 0f, 0.475f));
            Assert.AreEqual(new Color32(234, 180, 0, 121), heat[0]);
        }

        [Test]
        public void ShellTracer_RicochetDetachesThePairAndAddsASmallerOne()
        {
            var manifest = CombatFxManifest.Shared;
            var trail = BattleGpuTrail.Create(100, false, Vector3.zero, new Vector3(0f, 0f, 10f));
            try
            {
                Assert.AreEqual(2, trail.Emitters.Count);
                Assert.AreEqual(manifest.TrailScale(100), trail.Emitters[0].Local.X.magnitude, Tolerance);
                trail.Follow(new Vector3(0f, 0f, 5f));
                trail.Tick(1f / 60f, null);
                trail.Ricochet(new Vector3(0f, 0f, 5f));
                Assert.AreEqual(4, trail.Emitters.Count);
                Assert.IsTrue(trail.Emitters[0].Detached.HasValue);
                Assert.IsFalse(trail.Emitters[2].Detached.HasValue);
                Assert.AreEqual(
                    manifest.ShellPixelSize(100) * manifest.RicochetTrailPixelFactor,
                    trail.Emitters[2].Local.X.magnitude,
                    Tolerance);

                // The detached pair is freed after three seconds.
                for (var i = 0; i < 31; i++)
                {
                    trail.Tick(0.1f, null);
                }

                Assert.AreEqual(2, trail.Emitters.Count);
            }
            finally
            {
                Object.DestroyImmediate(trail.gameObject);
            }
        }

        [Test]
        public void ShellSprite_IsThirtyTwoTexelsOfPixelSize()
        {
            var shell = new GameObject("test_shell");
            try
            {
                var sprite = BattleShellSprite.Create(shell.transform, 100, true);
                Assert.IsNotNull(sprite);
                Assert.AreEqual(32 * CombatFxManifest.Shared.ShellPixelSize(100), sprite.Side, Tolerance);
                Assert.IsFalse(sprite.Shown);
                var bisque = CombatFxManifest.Shared.ShellModulateHeat;
                Assert.AreEqual((byte)Mathf.RoundToInt(bisque.x * 255f), sprite.Modulate.r);
            }
            finally
            {
                Object.DestroyImmediate(shell);
            }
        }

        private static GodotEmitterData Emitter(string scene, string name)
        {
            foreach (var emitter in CombatFxManifest.Shared.Scene(scene).Emitters)
            {
                if (emitter.Name == name)
                {
                    return emitter;
                }
            }

            Assert.Fail(scene + "/" + name + " is not in the manifest");
            return null;
        }
    }
}
