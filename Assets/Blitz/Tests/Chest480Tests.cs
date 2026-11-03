using System;
using System.Linq;
using BlitzLegacy.Runtime.Chest;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>The ported 4.8.0 chest scene: data, box animation and particle system.</summary>
    public sealed class Chest480Tests
    {
        private static Chest480Definition Definition { get { return Chest480Definition.Load(); } }

        [Test]
        public void Definition_LoadsTheCompleteExport()
        {
            var d = Definition;
            Assert.AreEqual("3d/Maps/chest/Box_anim.sc2", d.scene);
            Assert.AreEqual(Chest480Definition.PartCount, d.parts.Length);
            Assert.AreEqual(Chest480Definition.SampleCount, d.samples.Length);
            Assert.AreEqual(Chest480Definition.SpriteCount, d.sprites.Length);
            Assert.AreEqual(Chest480Definition.EmitterCount, d.emitters.Length);
            Assert.AreEqual(Chest480Definition.EffectCount, d.effects.Length);
            Assert.AreEqual(70f, d.camera.fovX, 1e-4f);
            Assert.AreEqual(2.5f, d.boxDuration, 1e-4f);
            Assert.AreEqual("grade_box", d.gradeVariable);
            Assert.AreEqual(1.6f, d.effects.First(e => e.name == "fireworks").delay, 1e-5f);
            foreach (var sprite in d.sprites)
                Assert.IsNotNull(Resources.Load<Texture2D>(sprite.frames[0]), sprite.name);
            Assert.IsNotNull(Resources.Load<Texture2D>(d.background.texture.resource));
            Assert.IsNotNull(Resources.Load<Texture2D>(d.box.albedo.resource));
            Assert.IsNotNull(Resources.Load<Texture2D>(d.box.normal.resource));
            Assert.IsNotNull(Resources.Load<AudioClip>("Audio/box_prize"));
            Assert.IsNotNull(Resources.Load<AudioClip>("Audio/whoosh_prize"));
        }

        [Test]
        public void Meshes_ReadAllSevenWithBinormals()
        {
            var meshes = Chest480Definition.ReadMeshes(Resources.Load<TextAsset>(Definition.meshResource).bytes);
            try
            {
                Assert.AreEqual(Chest480Definition.MeshCount, meshes.Length);
                Assert.AreEqual(4, meshes[0].vertexCount);
                for (var i = 1; i < meshes.Length; i++) Assert.Greater(meshes[i].vertexCount, 20);
            }
            finally
            {
                foreach (var mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void Meshes_RejectForeignOrTruncatedData()
        {
            Assert.Throws<System.IO.InvalidDataException>(() => Chest480Definition.ReadMeshes(new byte[4]));
            Assert.Throws<System.IO.InvalidDataException>(() => Chest480Definition.ReadMeshes(new byte[64]));
            var bytes = Resources.Load<TextAsset>(Definition.meshResource).bytes;
            var truncated = new byte[bytes.Length / 2];
            Array.Copy(bytes, truncated, truncated.Length);
            Assert.Throws<System.IO.InvalidDataException>(() => Chest480Definition.ReadMeshes(truncated));
        }

        [Test]
        public void Definition_RejectsBrokenJson()
        {
            Assert.Throws<System.IO.InvalidDataException>(() => Chest480Definition.Parse("{}"));
            Assert.Throws<System.IO.InvalidDataException>(() => Chest480Definition.Parse(string.Empty));
        }

        [Test]
        public void Animation_MatchesTheFloat64ReferencePoses()
        {
            var d = Definition;
            foreach (var sample in d.samples)
            {
                var world = Chest480Animation.World(d.parts[sample.part], sample.time);
                for (var k = 0; k < 16; k++)
                    Assert.AreEqual(sample.world[k], world[k], 1e-3f,
                        d.parts[sample.part].name + " t=" + sample.time + " m" + k);
            }
        }

        [Test]
        public void Animation_HoldsTheLastPoseAfterTheBoxDuration()
        {
            var part = Definition.parts[0];
            var end = Chest480Animation.World(part, 2.5f);
            var later = Chest480Animation.World(part, 7f);
            for (var k = 0; k < 16; k++) Assert.AreEqual(end[k], later[k], 1e-6f);
        }

        [Test]
        public void UnityConversion_SwapsYAndZ()
        {
            var world = new float[16];
            world[0] = 1f; world[5] = 1f; world[10] = 1f; world[15] = 1f;
            world[12] = 1f; world[13] = 2f; world[14] = 3f;
            var unity = Chest480Animation.ToUnity(world);
            Assert.AreEqual(new Vector3(1f, 3f, 2f), unity.MultiplyPoint3x4(Vector3.zero));
            // DAVA +Y (forward) is Unity +Z.
            world[12] = world[13] = world[14] = 0f;
            world[4] = 0f; world[5] = 0f; world[6] = 1f; world[8] = 0f; world[9] = 1f; world[10] = 0f;
            unity = Chest480Animation.ToUnity(world);
            Assert.AreEqual(new Vector3(0f, 0f, 1f), unity.MultiplyVector(new Vector3(0f, 1f, 0f)));
        }

        [Test]
        public void PropertyLine_FollowsDavaKeyframeRules()
        {
            var line = new DavaPropertyLine(new Chest480Line { dim = 1, t = new[] { 0f, 1f, 2f, 4f }, v = new[] { 0f, 10f, 20f, 0f }, external = string.Empty });
            Assert.AreEqual(0f, line.Scalar(-1f));
            Assert.AreEqual(0f, line.Scalar(0f));
            Assert.AreEqual(5f, line.Scalar(0.5f), 1e-5f);
            Assert.AreEqual(10f, line.Scalar(1f), 1e-5f);
            Assert.AreEqual(15f, line.Scalar(1.5f), 1e-5f);
            Assert.AreEqual(10f, line.Scalar(3f), 1e-5f);
            Assert.AreEqual(0f, line.Scalar(9f));
            var two = new DavaPropertyLine(new Chest480Line { dim = 2, t = new[] { 1f, 3f }, v = new[] { 0f, 2f, 4f, 6f }, external = string.Empty });
            Assert.AreEqual(2f, two.Vector2(2f).x, 1e-5f);
            Assert.AreEqual(4f, two.Vector2(2f).y, 1e-5f);
            Assert.AreEqual(4f, two.Vector2(3f).x, 1e-5f);
            var single = new DavaPropertyLine(new Chest480Line { dim = 1, t = new[] { 1.6f }, v = new[] { 25f }, external = string.Empty });
            Assert.AreEqual(25f, single.Scalar(0f));
            Assert.AreEqual(25f, single.Scalar(100f));
            Assert.IsTrue(new DavaPropertyLine(new Chest480Line { dim = 3, t = new float[0], v = new float[0] }).IsNull);
            Assert.Throws<ArgumentException>(() => new DavaPropertyLine(new Chest480Line { dim = 2, t = new[] { 0f }, v = new[] { 1f } }));
        }

        [TestCase(0f, 1f, 0.8196f, 0.0039f)]
        [TestCase(0.3f, 0.3059f, 0.5373f, 1f)]
        [TestCase(0.6f, 1f, 0.251f, 0.8118f)]
        [TestCase(1f, 1f, 1f, 1f)]
        public void ModifiableLine_PicksTheGradeColour(float grade, float r, float g, float b)
        {
            var confetti = Definition.emitters.First(e => e.name == "fireworks1").layers.First(l => l.name == "confetti1");
            var line = new DavaPropertyLine(confetti.colorRandom);
            Assert.IsTrue(line.IsModifiable);
            line.SetExternalValue(grade);
            var color = line.Color(0.37f);
            Assert.AreEqual(r, color.r, 2e-3f);
            Assert.AreEqual(g, color.g, 2e-3f);
            Assert.AreEqual(b, color.b, 2e-3f);
        }

        [Test]
        public void EmitterRotation_TurnsZOntoTheEmissionVector()
        {
            var vectors = new[] { new DavaVector3(1f, 0f, 0f), new DavaVector3(0f, -2f, 0f), new DavaVector3(0.3f, 0.4f, 0.5f) };
            foreach (var vector in vectors)
            {
                var rotated = new DavaVector3(0f, 0f, 1f).Mul3(Chest480Particles.EmitterRotation(vector, vector.Length));
                var expected = vector.Normalized();
                Assert.AreEqual(expected.x, rotated.x, 1e-5f);
                Assert.AreEqual(expected.y, rotated.y, 1e-5f);
                Assert.AreEqual(expected.z, rotated.z, 1e-5f);
            }
        }

        [Test]
        public void VanDerCorput_MatchesTheReferenceSequence()
        {
            Assert.AreEqual(0f, Chest480Particles.VanDerCorput(0, 2));
            Assert.AreEqual(0.5f, Chest480Particles.VanDerCorput(1, 2), 1e-6f);
            Assert.AreEqual(0.25f, Chest480Particles.VanDerCorput(2, 2), 1e-6f);
            Assert.AreEqual(0.75f, Chest480Particles.VanDerCorput(3, 2), 1e-6f);
            Assert.AreEqual(1f / 9f, Chest480Particles.VanDerCorput(3, 3), 1e-6f);
        }

        [Test]
        public void Particles_AreDeterministicPerSeed()
        {
            var a = new Chest480Particles(Definition, 77u, 1f);
            var b = new Chest480Particles(Definition, 77u, 1f);
            var c = new Chest480Particles(Definition, 78u, 1f);
            a.Advance(3.6f);
            // DAVA consumes one shared random stream, so only an identical
            // frame sequence replays identically.
            b.Advance(3.6f);
            c.Advance(3.6f);
            Assert.Greater(a.TotalParticles, 0);
            Assert.AreEqual(a.TotalParticles, b.TotalParticles);
            Assert.AreEqual(a.SceneTime, b.SceneTime, 1e-4f);
            var pa = a.Effects.SelectMany(e => e.Groups).SelectMany(g => g.Particles).Select(p => p.Position).ToArray();
            var pb = b.Effects.SelectMany(e => e.Groups).SelectMany(g => g.Particles).Select(p => p.Position).ToArray();
            var pc = c.Effects.SelectMany(e => e.Groups).SelectMany(g => g.Particles).Select(p => p.Position).ToArray();
            for (var i = 0; i < pa.Length; i++)
            {
                Assert.AreEqual(pa[i].x, pb[i].x, 1e-5f);
                Assert.AreEqual(pa[i].z, pb[i].z, 1e-5f);
            }
            Assert.IsFalse(pa.Length == pc.Length && pa.Zip(pc, (x, y) => Math.Abs(x.x - y.x) < 1e-6f).All(same => same),
                "another seed must give another run");
            a.Restart(77u, 1f);
            Assert.AreEqual(0, a.TotalParticles);
            Assert.AreEqual(0f, a.SceneTime);
        }

        [Test]
        public void Particles_FollowTheAuthoredTimeline()
        {
            var sim = new Chest480Particles(Definition, 5u, 1f);
            sim.Advance(0.25f);
            Assert.AreEqual(0, sim.ParticleCount("dust"), "dust starts with the lid at 0.3-0.6 s");
            Assert.AreEqual(0, sim.ParticleCount("glow_flow"));
            Assert.Greater(sim.ParticleCount("pul"), 0, "ambient dust motes run from the start");
            sim.Advance(0.4f);
            Assert.Greater(sim.ParticleCount("dust"), 0);
            sim.Advance(1.2f);
            Assert.Greater(sim.ParticleCount("sparks") + sim.ParticleCount("sparks1"), 0, "sparks burst at 1.6 s");
            Assert.AreEqual(0, sim.ParticleCount("glow_flow"), "3 per second from 1.6 s: none before 1.93 s");
            sim.Advance(1.25f);
            Assert.Greater(sim.ParticleCount("glow_flow"), 0);
            Assert.AreEqual(0, sim.ParticleCount("fireworks"), "fireworks: action delay 1.6 s + layer start 1.6 s");
            sim.Advance(0.3f);
            Assert.Greater(sim.ParticleCount("fireworks"), 0);
            foreach (var particle in sim.Effects.SelectMany(e => e.Groups).SelectMany(g => g.Particles))
            {
                Assert.IsFalse(float.IsNaN(particle.Position.x) || float.IsInfinity(particle.Position.z));
                Assert.Less(particle.Life, particle.LifeTime);
            }
        }

        [Test]
        public void Particles_UseTheGradeForConfetti()
        {
            var sim = new Chest480Particles(Definition, 9u, 0.3f);
            sim.Advance(3.6f);
            var confetti = sim.Effects.Where(e => e.Source.name == "fireworks").SelectMany(e => e.Groups)
                .Where(g => g.Layer.Source.name == "confetti1").SelectMany(g => g.Particles).ToArray();
            Assert.Greater(confetti.Length, 0);
            foreach (var particle in confetti)
            {
                Assert.AreEqual(0.3059f, particle.Color.r, 2e-3f);
                Assert.AreEqual(1f, particle.Color.b, 2e-3f);
            }
        }

        [Test]
        public void ParticleMesh_BuildsSortedQuadBatches()
        {
            var sim = new Chest480Particles(Definition, 3u, 0.6f);
            sim.Advance(3.5f);
            var builder = new Chest480ParticleMesh();
            builder.Build(sim, Definition.camera);
            Assert.Greater(builder.QuadCount, 0);
            Assert.Greater(builder.BatchCount, 0);
            var indices = 0;
            for (var i = 0; i < builder.BatchCount; i++)
            {
                var batch = builder.Batch(i);
                Assert.IsTrue(batch.Blending == 1 || batch.Blending == 3);
                Assert.AreEqual(indices, batch.FirstIndex);
                indices += batch.IndexCount;
                if (i > 0)
                    Assert.IsFalse(batch.Sprite == builder.Batch(i - 1).Sprite && batch.Blending == builder.Batch(i - 1).Blending,
                        "equal neighbouring batches are merged");
            }
            Assert.AreEqual(builder.QuadCount * 6, indices);
            foreach (var vertex in builder.Vertices) Assert.IsFalse(float.IsNaN(vertex.x) || float.IsInfinity(vertex.y));
            var mesh = new Mesh();
            try
            {
                builder.Apply(mesh);
                Assert.AreEqual(builder.BatchCount, mesh.subMeshCount);
                Assert.AreEqual(builder.QuadCount * 4, mesh.vertexCount);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void Camera_ProjectsTheRewardLocatorIntoTheImage()
        {
            var d = Definition;
            Assert.AreEqual(70f, Chest480Projection.VerticalFov(d.camera, 1f), 1e-3f);
            Assert.Less(Chest480Projection.VerticalFov(d.camera, 16f / 9f), 70f);
            var view = Chest480Projection.View(d.camera);
            var target = DavaVector3.From(d.camera.target).ToUnity();
            var local = view.MultiplyPoint3x4(target);
            Assert.Less(local.z, 0f, "the camera looks down -Z at its target");
            Assert.AreEqual(0f, local.x, 1e-3f);
            var clip = Chest480Projection.Projection(d.camera, 16f / 9f) * view
                * new Vector4(d.hardpoint[0], d.hardpoint[2], d.hardpoint[1], 1f);
            var x = clip.x / clip.w * 0.5f + 0.5f;
            var y = clip.y / clip.w * 0.5f + 0.5f;
            Assert.That(x, Is.InRange(0.3f, 0.7f));
            Assert.That(y, Is.InRange(0.2f, 0.8f));
        }
    }
}
