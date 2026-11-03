using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Locks the hull half of <c>assets/tanks/base/base_tank.gd</c>: the hull
    /// measurements <c>setup</c> takes off the model, the mass it assembles,
    /// the map border clamp of <c>_physics_process</c> and the track and wheel
    /// animation of <c>rotate_tracks</c>.
    /// </summary>
    public sealed class BattleTankTests
    {
        private const float Tolerance = 1e-4f;

        /// <summary>
        /// <c>hull_phys</c> of <c>assets/tanks/ussr1-ms-1.glb</c>, mirrored
        /// across Z like every other ported mesh. The node sits at y = 0.1,
        /// which the OBJ writer bakes in, so the minimum is the source's
        /// <c>hull_bottom_offset</c>.
        /// </summary>
        private static readonly Bounds HullBounds = MakeBounds(
            new Vector3(-0.86135f, 0.41991f, -1.63692f),
            new Vector3(0.86135f, 2.09221f, 1.78469f));

        /// <summary><c>trackR</c> of the same model, mirrored.</summary>
        private static readonly Bounds TracksBounds = MakeBounds(
            new Vector3(0.6113f, 0f, -1.55072f),
            new Vector3(0.84946f, 0.94442f, 1.84997f));

        /// <summary>
        /// <c>map_border_min</c> / <c>map_border_max</c> of Horace Sands after
        /// the Z mirror, so the minimum edge really is the minimum.
        /// </summary>
        private static readonly Bounds PlayArea = MakeBounds(
            new Vector3(54.471138f, 0f, -547.19604f),
            new Vector3(543.7946f, 0f, -57.70598f));

        [Test]
        public void MeasureHull_ReadsTheSourceHullDonor()
        {
            TankSuspension.HullGeometry hull =
                BattleTank.MeasureHull(HullBounds, TracksBounds);

            // tank_size = m_hull_phys.get_aabb().abs().size
            Assert.That(hull.Width, Is.EqualTo(1.72269f).Within(Tolerance));
            Assert.That(hull.Length, Is.EqualTo(3.42161f).Within(Tolerance));

            // hull_height = tank_size.y + hull_bottom_offset
            Assert.That(hull.Height, Is.EqualTo(2.09221f).Within(Tolerance));

            // tracks_height = m_trackR.get_aabb().size.y
            Assert.That(hull.TracksHeight, Is.EqualTo(0.94442f).Within(Tolerance));

            // dist_sides = hull_width * 0.85, and the scene default of 1.722691
            // is really the raw hull width, so keeping the export would have
            // spread the wheels a whole factor too far.
            Assert.That(
                hull.Width * SuspensionTuning.HullSideFactor,
                Is.EqualTo(1.4642865f).Within(Tolerance));
            Assert.That(
                hull.Width,
                Is.EqualTo(SuspensionTuning.DistanceSides).Within(1e-3f),
                "the exported dist_sides is the unscaled hull width");

            // dist_wheels = hull_len / wheel_count
            Assert.That(
                hull.Length / SuspensionTuning.WheelCount,
                Is.EqualTo(0.5702683f).Within(Tolerance));
        }

        /// <summary>
        /// <c>tank_info.gd</c> <c>cleanup()</c> converts a traverse above one
        /// into radians, and <c>rotate_turret</c> spends it per delta without
        /// converting again. The port reads the same degrees off the card, so
        /// it owes the same conversion.
        /// </summary>
        [Test]
        public void TraverseRadians_ConvertsTheCardDegrees()
        {
            // T-43: 35 deg/s on the card.
            Assert.That(
                TankInfo.TraverseRadians(35f),
                Is.EqualTo(0.6108652f).Within(Tolerance));

            // A resource that already ships radians is left alone.
            Assert.That(
                TankInfo.TraverseRadians(0.6806784f),
                Is.EqualTo(0.6806784f).Within(Tolerance));
        }

        [Test]
        public void MassOf_AddsTheInstalledModules()
        {
            var card = new TankInfo { HullWeight = 5.4f };
            card.Guns.Add(new GunInfo { Weight = 0.2f });
            card.Engines.Add(new EngineInfo { Weight = 0.35f, Power = 110f });
            card.Tracks.Add(new TracksInfo { Weight = 0.45f, RotationSpeed = 45f });

            Assert.That(BattleTank.MassOf(card, 0, 0, 0), Is.EqualTo(6.4f).Within(Tolerance));

            // An index the card does not carry falls back to the stock module.
            Assert.That(BattleTank.MassOf(card, 7, 7, 7), Is.EqualTo(6.4f).Within(Tolerance));

            // A card without modules weighs only its hull.
            Assert.That(
                BattleTank.MassOf(new TankInfo { HullWeight = 5.4f }, 0, 0, 0),
                Is.EqualTo(5.4f).Within(Tolerance));
        }

        [Test]
        public void ClampPosition_PinsTheHullInsideTheBorder()
        {
            Vector3 inside = BattleTank.ClampPosition(new Vector3(300f, 12f, -300f), PlayArea);
            Assert.That(inside.x, Is.EqualTo(300f).Within(Tolerance));
            Assert.That(inside.y, Is.EqualTo(12f).Within(Tolerance));
            Assert.That(inside.z, Is.EqualTo(-300f).Within(Tolerance));

            Vector3 low = BattleTank.ClampPosition(new Vector3(10f, 5000f, -20f), PlayArea);
            Assert.That(low.x, Is.EqualTo(54.471138f).Within(Tolerance));
            Assert.That(low.y, Is.EqualTo(BattleTank.HeightLimit).Within(Tolerance));
            Assert.That(low.z, Is.EqualTo(-57.70598f).Within(Tolerance));

            Vector3 high = BattleTank.ClampPosition(new Vector3(900f, -5000f, -900f), PlayArea);
            Assert.That(high.x, Is.EqualTo(543.7946f).Within(Tolerance));
            Assert.That(high.y, Is.EqualTo(-BattleTank.HeightLimit).Within(Tolerance));
            Assert.That(high.z, Is.EqualTo(-547.19604f).Within(Tolerance));
        }

        [Test]
        public void LimitBorderVelocity_OnlyAllowsInwardMotion()
        {
            // Sitting on the minimum X edge: outward motion dies, and touching
            // any edge also clips upward velocity so the hull cannot climb.
            Vector3 atMinX = BattleTank.LimitBorderVelocity(
                new Vector3(54.471138f, 8f, -300f), new Vector3(-5f, 3f, 2f), PlayArea);
            Assert.That(atMinX.x, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(atMinX.y, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(atMinX.z, Is.EqualTo(2f).Within(Tolerance));

            // The maximum Z edge is the mirror of the source's map_border_min.y.
            Vector3 atMaxZ = BattleTank.LimitBorderVelocity(
                new Vector3(300f, 8f, -57.70598f), new Vector3(1f, -4f, 6f), PlayArea);
            Assert.That(atMaxZ.x, Is.EqualTo(1f).Within(Tolerance));
            Assert.That(atMaxZ.y, Is.EqualTo(-4f).Within(Tolerance));
            Assert.That(atMaxZ.z, Is.EqualTo(0f).Within(Tolerance));

            // Inward motion along the same edge survives untouched.
            Vector3 inward = BattleTank.LimitBorderVelocity(
                new Vector3(300f, 8f, -57.70598f), new Vector3(0f, 2f, -6f), PlayArea);
            Assert.That(inward.z, Is.EqualTo(-6f).Within(Tolerance));

            // Away from every edge nothing is clipped.
            Vector3 free = BattleTank.LimitBorderVelocity(
                new Vector3(300f, 8f, -300f), new Vector3(1f, 9f, -2f), PlayArea);
            Assert.That(free.y, Is.EqualTo(9f).Within(Tolerance));
        }

        [Test]
        public void IsEqualApprox_ScalesTheToleranceLikeGodot()
        {
            Assert.That(BattleTank.IsEqualApprox(0f, 0f), Is.True);
            Assert.That(BattleTank.IsEqualApprox(0f, 5e-6f), Is.True);
            Assert.That(BattleTank.IsEqualApprox(0f, 2e-5f), Is.False);

            // CMP_EPSILON is scaled by the magnitude of the first operand, so a
            // map border a few hundred metres out tolerates a whole centimetre.
            Assert.That(BattleTank.IsEqualApprox(1000f, 1000.005f), Is.True);
            Assert.That(BattleTank.IsEqualApprox(1000f, 1000.02f), Is.False);
        }

        [Test]
        public void TrackScrollStep_MatchesRotateTrackMat()
        {
            // offset.y += (tracks_speed / hull_len) + rot_speed_add * 0.5
            Assert.That(
                BattleTank.TrackScrollStep(1.5f, 3f, 0.4f),
                Is.EqualTo(0.7f).Within(Tolerance));

            // Driving straight leaves only the speed term.
            Assert.That(
                BattleTank.TrackScrollStep(1.5f, 3f, 0f),
                Is.EqualTo(0.5f).Within(Tolerance));

            Assert.That(
                BattleTank.TrackScrollStep(1.5f, 0f, 0.4f),
                Is.EqualTo(0f).Within(Tolerance),
                "an unmeasured hull must not divide by zero");
        }

        [Test]
        public void WheelSpin_MatchesRotateWheel()
        {
            // rotate_wheel: (tracks_speed * 0.25 / radius) + rot_speed
            Assert.That(
                BattleTank.WheelSpin(2f, 0.33153f, 0.1f),
                Is.EqualTo(1.6081591f).Within(1e-3f));

            Assert.That(
                BattleTank.WheelSpin(2f, 0f, 0.1f),
                Is.EqualTo(0f).Within(Tolerance),
                "rotate_wheel returns early without a radius");
        }

        [Test]
        public void Mirror_RotatesImportedMeshAndKeepsFrontFacesOutward()
        {
            var source = new Mesh { name = "MirrorProbe" };
            source.vertices = new[]
            {
                new Vector3(0f, 0f, 3f),
                new Vector3(1f, 0f, 3f),
                new Vector3(0f, 1f, 3f),
            };
            source.normals = new[]
            {
                Vector3.forward,
                Vector3.forward,
                Vector3.forward,
            };
            source.tangents = new[]
            {
                new Vector4(1f, 0f, 0f, 1f),
                new Vector4(1f, 0f, 0f, 1f),
                new Vector4(1f, 0f, 0f, 1f),
            };
            source.triangles = new[] { 0, 1, 2 };

            Mesh mirrored = BattleTank.Mirror(source);
            Assert.That(mirrored, Is.Not.Null);

            Vector3[] vertices = mirrored.vertices;
            Assert.That(vertices[0].x, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(vertices[0].y, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(vertices[0].z, Is.EqualTo(-3f).Within(Tolerance));
            Assert.That(vertices[1].z, Is.EqualTo(-3f).Within(Tolerance));
            Assert.That(vertices[2].z, Is.EqualTo(-3f).Within(Tolerance));

            Vector3[] normals = mirrored.normals;
            Assert.That(normals[0].z, Is.EqualTo(-1f).Within(Tolerance));
            Assert.That(normals[1].z, Is.EqualTo(-1f).Within(Tolerance));

            Vector4[] tangents = mirrored.tangents;
            Assert.That(tangents[0].x, Is.EqualTo(-1f).Within(Tolerance));
            Assert.That(tangents[0].w, Is.EqualTo(1f).Within(Tolerance));

            // A proper half-turn keeps winding and tangent handedness.
            Assert.That(mirrored.triangles, Is.EqualTo(new[] { 0, 1, 2 }));
            var wound = Vector3.Cross(
                vertices[mirrored.triangles[1]] - vertices[mirrored.triangles[0]],
                vertices[mirrored.triangles[2]] - vertices[mirrored.triangles[0]]);
            Assert.Greater(Vector3.Dot(wound, normals[0]), 0f,
                "the mirrored outside would be culled and look transparent");

            // The cache hands back the very same mesh for a second tank.
            Assert.That(BattleTank.Mirror(source), Is.SameAs(mirrored));

            Object.DestroyImmediate(mirrored);
            Object.DestroyImmediate(source);
        }

        [Test]
        public void MirroredMs1_KeepsAuthoredOutsideAsItsFrontFace()
        {
            var prefab = Resources.Load<GameObject>(BattleTank.ModelResource);
            Assert.IsNotNull(prefab);
            // The model imports as one object per `g` record, so the whole
            // tree has to be walked instead of the first mesh only.
            var filters = prefab.GetComponentsInChildren<MeshFilter>(true);
            Assert.Greater(filters.Length, 0);

            var agree = 0;
            var disagree = 0;

            foreach (var filter in filters)
            {
                var mesh = BattleTank.Mirror(filter.sharedMesh);
                if (mesh == null) { continue; }

                var vertices = mesh.vertices;
                var normals = mesh.normals;
                for (var subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                {
                    var triangles = mesh.GetTriangles(subMesh);
                    for (var i = 0; i < triangles.Length; i += 3)
                    {
                        var a = triangles[i];
                        var b = triangles[i + 1];
                        var c = triangles[i + 2];
                        var wound = Vector3.Cross(
                            vertices[b] - vertices[a], vertices[c] - vertices[a]);
                        if (wound.sqrMagnitude < 1e-12f) { continue; }

                        var authored = normals[a] + normals[b] + normals[c];
                        if (Vector3.Dot(wound, authored) > 0f) { agree++; }
                        else { disagree++; }
                    }
                }
            }

            var total = agree + disagree;
            Assert.Greater(total, 4000, "the shipped MS-1 mesh did not load");
            Assert.Greater(agree / (float)total, 0.99f,
                "the opaque MS-1 is wound inside out");
        }

        private static Bounds MakeBounds(Vector3 min, Vector3 max)
        {
            var bounds = new Bounds();
            bounds.SetMinMax(min, max);
            return bounds;
        }
    }
}
