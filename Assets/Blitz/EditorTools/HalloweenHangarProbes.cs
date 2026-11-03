// Conventions of this Unity version the Halloween hangar runtime pins, measured
// instead of assumed: which texel of a cube face the GPU samples for a
// direction (HalloweenHangarAssets.CubeRowsFlipped), and how a mesh particle's
// 3D start rotation, rotation over lifetime and flipRotation lay the mesh in
// Local, World and View alignment (HalloweenHangarParticles.ViewSign,
// FlipMirrorsFacing, FlipTurnsView). The player cannot measure them, so the
// editor checks that the pinned values still hold: HalloweenHangarProbeTests
// on every test run, ClientHangarRenderCli on the GPU before it renders.
using System;
using System.Collections.Generic;
using BlitzLegacy.Runtime.Hangar.Halloween;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace BlitzLegacy.EditorTools
{
    public static class HalloweenHangarProbes
    {
        public const string CubeShader = "Hidden/HalloweenHangar/CubeProbe";
        const float Tolerance = 2e-3f, SpinTolerance = 0.02f;

        /// <summary>The cube probe reads back a render; -nographics has none.</summary>
        public static bool CanProbeGpu
        {
            get { return SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null; }
        }

        /// <summary>Checks the cube face row order on the GPU; returns what it found.</summary>
        public static string CubeRows()
        {
            return WithTrash(CubeRows);
        }

        /// <summary>Checks the particle rotation conventions; returns what it found.</summary>
        public static string Particles()
        {
            return WithTrash(Particles);
        }

        static string WithTrash(Func<List<Object>, string> probe)
        {
            var trash = new List<Object>();
            try
            {
                return probe(trash);
            }
            finally
            {
                foreach (var o in trash)
                    if (o != null)
                        Object.DestroyImmediate(o);
            }
        }

        static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        // ---------------------------------------------------------- cube maps
        static Color32 Code(int x, int y, int face) =>
            new Color32((byte)(20 + 60 * x), (byte)(20 + 60 * y), (byte)(20 + 40 * face), 255);

        static bool Same(Color32 a, Color32 b) =>
            Mathf.Abs(a.r - b.r) <= 1 && Mathf.Abs(a.g - b.g) <= 1 && Mathf.Abs(a.b - b.b) <= 1;

        // A 4 x 4 cube map with every texel coded by its face, column and row is
        // sampled through the centre of each texel along the GL table's
        // direction; the rows come back either as written or flipped.
        static string CubeRows(List<Object> trash)
        {
            var shader = Shader.Find(CubeShader);
            Require(shader != null && shader.isSupported, $"probe: shader {CubeShader} is missing or unsupported");
            const int n = 4;
            var cube = new Cubemap(n, GraphicsFormat.R8G8B8A8_UNorm, TextureCreationFlags.None) { hideFlags = HideFlags.HideAndDontSave };
            trash.Add(cube);
            var faces = new Color[6][];
            for (int f = 0; f < 6; f++)
            {
                faces[f] = new Color[n * n];
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                        faces[f][y * n + x] = Code(x, y, f);
                cube.SetPixels(faces[f], (CubemapFace)f, 0);
            }
            cube.filterMode = FilterMode.Point;
            cube.wrapMode = TextureWrapMode.Clamp;
            cube.Apply(false, false);

            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            trash.Add(material);
            material.SetTexture("_Cube", cube);
            var target = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { hideFlags = HideFlags.HideAndDontSave };
            trash.Add(target);
            target.Create();
            var read = new Texture2D(1, 1, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave };
            trash.Add(read);

            int straight = 0, flipped = 0;
            var misses = new List<string>();
            var previous = RenderTexture.active;
            try
            {
                for (int f = 0; f < 6; f++)
                    for (int y = 0; y < n; y++)
                        for (int x = 0; x < n; x++)
                        {
                            Vector3 d = HalloweenHangarAssets.GLDirection((CubemapFace)f, (x + 0.5f) / n, (y + 0.5f) / n);
                            material.SetVector("_Direction", d);
                            Graphics.Blit(null, target, material);
                            RenderTexture.active = target;
                            read.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
                            read.Apply(false);
                            Color32 c = read.GetPixels32()[0];
                            if (Same(c, Code(x, y, f)))
                                straight++;
                            else if (Same(c, Code(x, n - 1 - y, f)))
                                flipped++;
                            else if (misses.Count < 8)
                                misses.Add($"face {f} texel ({x}, {y}) along {d} reads {c}");
                        }
            }
            finally
            {
                RenderTexture.active = previous;
            }
            int total = 6 * n * n;
            Require(straight == total || flipped == total,
                $"probe: cube texels follow neither the GL table nor its row flip ({straight} straight, {flipped} flipped of {total}): " +
                string.Join("; ", misses));
            Require((flipped == total) == HalloweenHangarAssets.CubeRowsFlipped,
                $"probe: cube face rows {(flipped == total ? "run against" : "follow")} the GL t coordinate on " +
                $"{SystemInfo.graphicsDeviceType}, HalloweenHangarAssets.CubeRowsFlipped pins {HalloweenHangarAssets.CubeRowsFlipped}");

            // The CPU lookup the editor uses must agree with the GPU on every texel.
            for (int f = 0; f < 6; f++)
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        Vector3 d = HalloweenHangarAssets.TexelDirection((CubemapFace)f, x, y, n);
                        Color c = HalloweenHangarAssets.SampleCube(faces, n, d);
                        Require(Same(c, faces[f][y * n + x]),
                            $"probe: SampleCube({d}) = {c}, texel ({x}, {y}) of face {f} is {faces[f][y * n + x]}");
                    }
            return $"probe: cube face rows {(HalloweenHangarAssets.CubeRowsFlipped ? "run against" : "follow")} the GL t coordinate " +
                   $"({SystemInfo.graphicsDeviceType}); the CPU lookup agrees on all {total} texels";
        }

        // ---------------------------------------------------------- particles
        sealed class Frame
        {
            public Vector3 x, y, rotation;
            public float age;
            public override string ToString() => $"x {x:F3}, y {y:F3}, rotation3D {rotation:F2}, age {age:F3}";
        }

        static Mesh probeMesh;

        static string Particles(List<Object> trash)
        {
            probeMesh = new Mesh { name = "HalloweenHangarProbe", hideFlags = HideFlags.HideAndDontSave };
            trash.Add(probeMesh);
            probeMesh.SetVertices(new[] { Vector3.zero, Vector3.right, Vector3.up });
            probeMesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back });
            probeMesh.SetUVs(0, new[] { Vector2.zero, Vector2.right, Vector2.up });
            probeMesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0, true);

            var cameraObject = new GameObject("HalloweenHangarProbeCamera") { hideFlags = HideFlags.HideAndDontSave };
            trash.Add(cameraObject);
            cameraObject.transform.SetPositionAndRotation(new Vector3(3f, 2f, -10f), Quaternion.Euler(20f, 30f, 0f));
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            Quaternion view = cameraObject.transform.rotation;

            // Local and World alignment: the start rotation is Quaternion.Euler of
            // the three angles, under the transform's rotation in Local alignment.
            Quaternion parent = Quaternion.Euler(10f, 20f, 30f);
            var start = new Vector3(25f, 40f, 60f);
            foreach (var space in new[] { ParticleSystemSimulationSpace.World, ParticleSystemSimulationSpace.Local })
                Expect(Measure(trash, camera, ParticleSystemRenderSpace.Local, space, parent, start, 0f, 0f, 0.5f),
                       parent * Quaternion.Euler(start), Tolerance, $"local alignment in {space} space");
            Expect(Measure(trash, camera, ParticleSystemRenderSpace.World, ParticleSystemSimulationSpace.World, parent, start, 0f, 0f, 0.5f),
                   Quaternion.Euler(start), Tolerance, "world alignment");

            // Rotation over lifetime adds to the z angle under the x / y facing.
            var facing = new Vector3(-90f, -90f, 30f);
            const float spin = 45f;
            var spun = Measure(trash, camera, ParticleSystemRenderSpace.Local, ParticleSystemSimulationSpace.World, Quaternion.identity, facing, spin, 0f, 2f);
            float z = facing.z + spin * spun.age;
            Require(Mathf.Abs(Mathf.DeltaAngle(spun.rotation.z, z)) < 1f, $"probe: rotation over lifetime: {spun}, expected z {z:F2}");
            Expect(spun, Quaternion.Euler(facing.x, facing.y, z), SpinTolerance, "rotation over lifetime");

            // flipRotation mirrors the z angle (and the spin); does it reach x / y?
            var flipStill = Measure(trash, camera, ParticleSystemRenderSpace.Local, ParticleSystemSimulationSpace.World, Quaternion.identity, facing, 0f, 1f, 0.5f);
            bool zOnly = Matches(flipStill, Quaternion.Euler(facing.x, facing.y, -facing.z), Tolerance);
            bool all = Matches(flipStill, Quaternion.Euler(-facing.x, -facing.y, -facing.z), Tolerance);
            Require(zOnly != all, $"probe: flipRotation lays a quad turned by {facing} as {flipStill}");
            Require(all == HalloweenHangarParticles.FlipMirrorsFacing,
                $"probe: flipRotation mirrors {(all ? "all three angles" : "z only")}, HalloweenHangarParticles.FlipMirrorsFacing pins {HalloweenHangarParticles.FlipMirrorsFacing}");
            var flipSpun = Measure(trash, camera, ParticleSystemRenderSpace.Local, ParticleSystemSimulationSpace.World, Quaternion.identity, facing, spin, 1f, 2f);
            z = facing.z + spin * flipSpun.age;
            Expect(flipSpun, all ? Quaternion.Euler(-facing.x, -facing.y, -z) : Quaternion.Euler(facing.x, facing.y, -z), SpinTolerance,
                   "flipped rotation over lifetime");

            // View alignment: quad x / y on the camera's right / up, then the sense
            // in which a z rotation turns it.
            Expect(Measure(trash, camera, ParticleSystemRenderSpace.View, ParticleSystemSimulationSpace.World, Quaternion.identity, Vector3.zero, 0f, 0f, 0.5f),
                   view, Tolerance, "view alignment");
            var turned = Measure(trash, camera, ParticleSystemRenderSpace.View, ParticleSystemSimulationSpace.World, Quaternion.identity, new Vector3(0f, 0f, 30f), 0f, 0f, 0.5f);
            float phi = ViewAngle(turned, view);
            Require(Mathf.Abs(Mathf.Abs(phi) - 30f) < 0.5f, $"probe: a view aligned quad turned by 30 degrees lies at {phi:F2} degrees ({turned})");
            float sign = Mathf.Sign(phi);
            Require(sign == HalloweenHangarParticles.ViewSign,
                $"probe: view aligned quads turn with sign {sign}, HalloweenHangarParticles.ViewSign pins {HalloweenHangarParticles.ViewSign}");
            var viewSpun = Measure(trash, camera, ParticleSystemRenderSpace.View, ParticleSystemSimulationSpace.World, Quaternion.identity, new Vector3(0f, 0f, 30f), spin, 0f, 2f);
            float expected = sign * (30f + spin * viewSpun.age);
            Require(Mathf.Abs(Mathf.DeltaAngle(ViewAngle(viewSpun, view), expected)) < 1f,
                $"probe: a spinning view aligned quad lies at {ViewAngle(viewSpun, view):F2} degrees, expected {expected:F2} ({viewSpun})");
            var viewFlip = Measure(trash, camera, ParticleSystemRenderSpace.View, ParticleSystemSimulationSpace.World, Quaternion.identity, new Vector3(0f, 0f, 30f), spin, 1f, 2f);
            float flippedAngle = ViewAngle(viewFlip, view);
            float back = -sign * (30f + spin * viewFlip.age), forth = sign * (30f + spin * viewFlip.age);
            bool turnsBack = Mathf.Abs(Mathf.DeltaAngle(flippedAngle, back)) < 1f;
            Require(turnsBack || Mathf.Abs(Mathf.DeltaAngle(flippedAngle, forth)) < 1f,
                $"probe: a flipped view aligned quad lies at {flippedAngle:F2} degrees, neither {back:F2} nor {forth:F2} ({viewFlip})");
            Require(turnsBack == HalloweenHangarParticles.FlipTurnsView,
                $"probe: flipRotation {(turnsBack ? "turns view aligned quads back" : "leaves view aligned quads")}, HalloweenHangarParticles.FlipTurnsView pins {HalloweenHangarParticles.FlipTurnsView}");

            return ($"probe: particle start rotation = Quaternion.Euler (under the transform in Local alignment); rotation over lifetime adds to z; " +
                   $"flipRotation mirrors {(all ? "all three angles" : "z only")}; view aligned quads lie on the camera's right / up and turn " +
                   $"{(sign > 0 ? "from right towards up" : "from up towards right")} for a positive angle; flipRotation " +
                   $"{(turnsBack ? "turns them back" : "leaves them")}");
        }

        static float ViewAngle(Frame f, Quaternion view)
        {
            Vector3 right = view * Vector3.right, up = view * Vector3.up, forward = view * Vector3.forward;
            Require(Mathf.Abs(Vector3.Dot(f.x.normalized, forward)) < Tolerance, $"probe: a view aligned quad leaves the camera plane ({f})");
            return Mathf.Atan2(Vector3.Dot(f.x, up), Vector3.Dot(f.x, right)) * Mathf.Rad2Deg;
        }

        static bool Matches(Frame f, Quaternion q, float tolerance) =>
            (f.x - q * Vector3.right).magnitude < tolerance && (f.y - q * Vector3.up).magnitude < tolerance;

        static void Expect(Frame f, Quaternion q, float tolerance, string what) =>
            Require(Matches(f, q, tolerance),
                $"probe: {what}: the quad lies as {f}, expected x {q * Vector3.right:F3}, y {q * Vector3.up:F3}");

        // One particle of a burst, unit size, no speed, baked in world space.
        static Frame Measure(List<Object> trash, Camera camera, ParticleSystemRenderSpace alignment, ParticleSystemSimulationSpace space,
                             Quaternion rotation, Vector3 start, float spin, float flip, float time)
        {
            var go = new GameObject("HalloweenHangarProbe") { hideFlags = HideFlags.HideAndDontSave };
            trash.Add(go);
            go.transform.SetPositionAndRotation(Vector3.zero, rotation);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.useAutoRandomSeed = false;
            ps.randomSeed = 7;
            var main = ps.main;
            main.duration = 10f;
            main.loop = false;
            main.playOnAwake = false;
            main.startDelay = 0f;
            main.startLifetime = 100f;
            main.startSpeed = 0f;
            main.startSize3D = false;
            main.startSize = 1f;
            main.startRotation3D = true;
            main.startRotationX = start.x * Mathf.Deg2Rad;
            main.startRotationY = start.y * Mathf.Deg2Rad;
            main.startRotationZ = start.z * Mathf.Deg2Rad;
            main.flipRotation = flip;
            main.gravityModifier = 0f;
            main.simulationSpace = space;
            main.scalingMode = ParticleSystemScalingMode.Local;
            main.maxParticles = 1;
            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 1) });
            var shape = ps.shape;
            shape.enabled = false;
            if (spin != 0f)
            {
                var rol = ps.rotationOverLifetime;
                rol.enabled = true;
                rol.separateAxes = true;
                rol.x = 0f;
                rol.y = 0f;
                rol.z = spin * Mathf.Deg2Rad;
            }
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Mesh;
            renderer.mesh = probeMesh;
            renderer.alignment = alignment;
            ps.Simulate(time, true, true, true);
            var particles = new ParticleSystem.Particle[2];
            int count = ps.GetParticles(particles);
            Require(count == 1, $"probe: {count} particles instead of one");
            var baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            trash.Add(baked);
            renderer.BakeMesh(baked, camera, ParticleSystemBakeMeshOptions.BakeRotationAndScale | ParticleSystemBakeMeshOptions.BakePosition);
            var v = baked.vertices;
            Require(v.Length == 3, $"probe: the baked particle has {v.Length} vertices instead of three");
            var p = particles[0];
            return new Frame { x = v[1] - v[0], y = v[2] - v[0], rotation = p.rotation3D, age = p.startLifetime - p.remainingLifetime };
        }
    }
}
