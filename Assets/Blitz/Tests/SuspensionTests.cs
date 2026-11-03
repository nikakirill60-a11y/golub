using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// Locks the drivetrain math against the Godot source in
    /// <c>assets/tanks/base/suspension_component.gd</c> and the torque curve
    /// stored in <c>assets/tanks/base/base_tank.tscn</c>.
    /// </summary>
    public sealed class SuspensionTests
    {
        private const float Tolerance = 1e-4f;

        [Test]
        public void TorqueCurve_MatchesTheSourceCurve()
        {
            TorqueCurve curve = TorqueCurve.CreateDefault();

            Assert.That(curve.Count, Is.EqualTo(5), "Curve_pgggk holds five points");

            Assert.That(curve.Sample(0f), Is.EqualTo(0.84435797f).Within(Tolerance));
            Assert.That(curve.Sample(0.14210527f), Is.EqualTo(0.6264592f).Within(Tolerance));
            Assert.That(curve.Sample(0.37368423f), Is.EqualTo(0.14396894f).Within(Tolerance));
            Assert.That(curve.Sample(0.7626811f), Is.EqualTo(0.10296488f).Within(Tolerance));
            Assert.That(curve.Sample(1f), Is.EqualTo(0.14501333f).Within(Tolerance));

            // Outside the domain Godot clamps to the end points.
            Assert.That(curve.Sample(-1f), Is.EqualTo(0.84435797f).Within(Tolerance));
            Assert.That(curve.Sample(2f), Is.EqualTo(0.14501333f).Within(Tolerance));

            // Halfway through the first span, evaluated by hand from the cubic
            // Bezier that Godot builds out of the two tangents.
            Assert.That(curve.Sample(0.071052635f), Is.EqualTo(0.7473037f).Within(Tolerance));
        }

        [Test]
        public void GodotEuler_ReadsTheHullAnglesOfTheTeamASpawn()
        {
            // First team-A TankSpawner in _rudniki.tscn.
            float[] basis = new float[]
            {
                0.9556441f, 0f, 0.29452404f,
                0f, 1f, 0f,
                -0.29452404f, 0f, 0.9556441f,
            };

            Vec3 right;
            Vec3 up;
            Vec3 forward;
            GodotSpace.Basis(basis, out right, out up, out forward);

            Vec3 sourceX;
            Vec3 sourceY;
            Vec3 sourceZ;
            GodotEuler.SourceBasis(right, up, forward, out sourceX, out sourceY, out sourceZ);

            // The mirror is its own inverse, so the round trip has to return
            // the source columns untouched. Godot serialises rows, so column X
            // is (basis[0], basis[3], basis[6]) and column Z is basis[2, 5, 8].
            Assert.That(sourceX.X, Is.EqualTo(basis[0]).Within(Tolerance));
            Assert.That(sourceX.Z, Is.EqualTo(basis[6]).Within(Tolerance));
            Assert.That(sourceZ.X, Is.EqualTo(basis[2]).Within(Tolerance));
            Assert.That(sourceZ.Z, Is.EqualTo(basis[8]).Within(Tolerance));

            float pitch;
            float yaw;
            float roll;
            GodotEuler.Yxz(sourceX, sourceY, sourceZ, out pitch, out yaw, out roll);

            Assert.That(pitch, Is.EqualTo(0f).Within(Tolerance), "the spawn is level");
            Assert.That(yaw, Is.EqualTo(17.129f).Within(1e-3f),
                "the spawn keeps its authored heading");
            Assert.That(roll, Is.EqualTo(0f).Within(Tolerance), "the spawn does not bank");
        }

        [Test]
        public void GodotEuler_ReportsPitchWithoutYawOnAPureSlope()
        {
            // A hull that only pitches must not report the numerically
            // equivalent pair of large angles that a naive decomposition gives.
            // Godot serialises rows, so a 30 degree pitch reads -sin on
            // rows[1][2] and +sin on rows[2][1].
            float[] basis = new float[]
            {
                1f, 0f, 0f,
                0f, 0.8660254f, -0.5f,
                0f, 0.5f, 0.8660254f,
            };

            Vec3 right;
            Vec3 up;
            Vec3 forward;
            GodotSpace.Basis(basis, out right, out up, out forward);

            Vec3 sourceX;
            Vec3 sourceY;
            Vec3 sourceZ;
            GodotEuler.SourceBasis(right, up, forward, out sourceX, out sourceY, out sourceZ);

            float pitch;
            float yaw;
            float roll;
            GodotEuler.Yxz(sourceX, sourceY, sourceZ, out pitch, out yaw, out roll);

            Assert.That(pitch, Is.EqualTo(30f).Within(1e-3f));
            Assert.That(yaw, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(roll, Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void SpeedReachedMul_KeepsTheSourceQuirks()
        {
            // Both branches divide by the same threshold; max_speed_back is
            // never used as a divisor.
            Assert.That(SuspensionTuning.SpeedReachedMul(19f, 38f, 0.85f, 0.5f), Is.EqualTo(0.425f).Within(Tolerance));
            Assert.That(SuspensionTuning.SpeedReachedMul(-19f, 38f, 0.85f, 0.5f), Is.EqualTo(0.25f).Within(Tolerance));

            // sign(0) is not greater than zero, so a standing tank takes the
            // backward branch and the multiplier collapses to zero.
            Assert.That(SuspensionTuning.SpeedReachedMul(0f, 38f, 0.85f, 0.5f), Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void GetBaseAngle_KeepsTheSignedPitch()
        {
            // Only the pitch is optionally absolute, so a nose down hull can
            // lose to the roll.
            Assert.That(SuspensionTuning.GetBaseAngle(-10f, 5f, false, false, false), Is.EqualTo(5f).Within(Tolerance));
            Assert.That(SuspensionTuning.GetBaseAngle(-10f, 5f, false, false, true), Is.EqualTo(10f).Within(Tolerance));
            Assert.That(SuspensionTuning.GetBaseAngle(-10f, 5f, true, false, true), Is.EqualTo(5f).Within(Tolerance));
            Assert.That(SuspensionTuning.GetBaseAngle(-10f, 5f, false, true, true), Is.EqualTo(10f).Within(Tolerance));
        }

        [Test]
        public void TorqueMul_FallsOffOnASteepSlope()
        {
            TorqueCurve curve = TorqueCurve.CreateDefault();

            Assert.That(SuspensionTuning.TorqueMul(curve, 0f, 38f, 0f, true), Is.EqualTo(1f).Within(Tolerance),
                "driving on top of another tank disables the curve");

            Assert.That(SuspensionTuning.TorqueMul(curve, 0f, 38f, 0f, false), Is.EqualTo(85.435797f).Within(1e-3f),
                "a standing tank on level ground gets the whole starting torque");

            Assert.That(SuspensionTuning.TorqueMul(curve, 0f, 38f, 40f, false), Is.EqualTo(1f).Within(Tolerance),
                "past twice the limit the slope cancels the torque completely");
        }

        [Test]
        public void SpringMagnitude_PushesTheHullUp()
        {
            float comprCoeff = SuspensionTuning.ComprCoeffFor(20f);
            float dampCoeff = SuspensionTuning.DampCoeffFor(20f);

            Assert.That(comprCoeff, Is.EqualTo(200f).Within(Tolerance));
            Assert.That(dampCoeff, Is.EqualTo(16f).Within(Tolerance));

            // Fully extended wheel: nothing to push against.
            Assert.That(SuspensionTuning.SpringMagnitude(0f, 0f, dampCoeff, comprCoeff), Is.EqualTo(0f).Within(Tolerance));

            // Fully compressed wheel at rest.
            Assert.That(SuspensionTuning.SpringMagnitude(0f, 0.45f, dampCoeff, comprCoeff), Is.EqualTo(99f).Within(Tolerance));

            // Wheel still dropping: the damper adds to the spring.
            Assert.That(SuspensionTuning.SpringMagnitude(-1f, 0.45f, dampCoeff, comprCoeff), Is.EqualTo(130.2f).Within(1e-3f));

            // Never pulls the hull down, and never exceeds the compression coefficient.
            Assert.That(SuspensionTuning.SpringMagnitude(5f, 0f, dampCoeff, comprCoeff), Is.EqualTo(0f).Within(Tolerance));
            Assert.That(SuspensionTuning.SpringMagnitude(-100f, 0.45f, dampCoeff, comprCoeff), Is.EqualTo(200f).Within(Tolerance));
        }

        [Test]
        public void Compression_StopsAtTheSourceLimit()
        {
            Assert.That(SuspensionTuning.Compression(0f, 0.98f), Is.EqualTo(0.45f).Within(Tolerance));
            Assert.That(SuspensionTuning.Compression(0.98f * 0.98f, 0.98f), Is.EqualTo(0f).Within(Tolerance));
            Assert.That(SuspensionTuning.Compression(0.98f * 0.98f * 0.9f, 0.98f), Is.EqualTo(0.1f).Within(Tolerance));
        }

        [Test]
        public void DriveCommandQueue_DelaysEveryInputChange()
        {
            DriveCommandQueue queue = new DriveCommandQueue();

            // Nothing to do: all four values are already zero.
            queue.Request(0f, 0f, 0f);
            Assert.That(queue.PendingCount, Is.EqualTo(0));

            queue.Request(1f, 0f, 0f);
            Assert.That(queue.PendingCount, Is.EqualTo(1));

            // An identical command is already pending, so this one is dropped.
            queue.Request(1f, 0f, 0.05f);
            Assert.That(queue.PendingCount, Is.EqualTo(1));

            Assert.That(queue.Process(0.1f), Is.False, "the delay has not elapsed yet");
            Assert.That(queue.ForwardAxis, Is.EqualTo(0f));

            Assert.That(queue.Process(0.15f), Is.True);
            Assert.That(queue.ForwardAxis, Is.EqualTo(1f));
            Assert.That(queue.PendingCount, Is.EqualTo(0));

            // The axes already hold the requested values.
            queue.Request(1f, 0f, 0.2f);
            Assert.That(queue.PendingCount, Is.EqualTo(0));

            queue.Stop();
            Assert.That(queue.ForwardAxis, Is.EqualTo(0f));
            Assert.That(queue.RightAxis, Is.EqualTo(0f));
        }

        [Test]
        public void SteerAxis_InvertsWhileReversing()
        {
            Assert.That(SuspensionTuning.SteerAxis(1f, 0f), Is.EqualTo(1f).Within(Tolerance), "standing still steers forward");
            Assert.That(SuspensionTuning.SteerAxis(1f, 1f), Is.EqualTo(1f).Within(Tolerance));
            Assert.That(SuspensionTuning.SteerAxis(1f, -1f), Is.EqualTo(-1f).Within(Tolerance), "reversing mirrors the stick");
        }

        [Test]
        public void WheelLayout_MatchesTheSourceRows()
        {
            const int wheelCount = 6;
            float distanceWheels = SuspensionTuning.DistanceWheelsFor(SuspensionTuning.HullLength, wheelCount);
            float distanceSides = 1f * SuspensionTuning.HullSideFactor;

            Assert.That(distanceWheels, Is.EqualTo(0.625f).Within(Tolerance));

            Assert.That(SuspensionTuning.WheelIndex(0, -1, wheelCount), Is.EqualTo(0));
            Assert.That(SuspensionTuning.WheelIndex(5, -1, wheelCount), Is.EqualTo(5));
            Assert.That(SuspensionTuning.WheelIndex(0, 1, wheelCount), Is.EqualTo(6));
            Assert.That(SuspensionTuning.WheelIndex(5, 1, wheelCount), Is.EqualTo(11));

            Vec3 first = SuspensionTuning.WheelPosition(0, -1, wheelCount, distanceWheels, distanceSides, SuspensionTuning.WheelOffsetY);
            Vec3 last = SuspensionTuning.WheelPosition(5, 1, wheelCount, distanceWheels, distanceSides, SuspensionTuning.WheelOffsetY);

            Assert.That(first.X, Is.EqualTo(-0.425f).Within(Tolerance));
            Assert.That(first.Y, Is.EqualTo(0.925f).Within(Tolerance));
            Assert.That(first.Z, Is.EqualTo(1.5625f).Within(Tolerance));

            Assert.That(last.X, Is.EqualTo(0.425f).Within(Tolerance));
            Assert.That(last.Z, Is.EqualTo(-1.5625f).Within(Tolerance));

            // The row is mirrored across Z on the way into Unity, so the wheel
            // that sits in front of the hull in Godot must sit behind it here.
            Vec3 mirrored = GodotSpace.Point(first.X, first.Y, first.Z);
            Assert.That(mirrored.Z, Is.EqualTo(-1.5625f).Within(Tolerance));
        }

        [Test]
        public void BrakeAndSpeedLimits_FollowTheSource()
        {
            Assert.That(SuspensionTuning.MaxSpeedReached(40f, 38f, 20f, 1f), Is.True);
            Assert.That(SuspensionTuning.MaxSpeedReached(-21f, 38f, 20f, 1f), Is.True);
            Assert.That(SuspensionTuning.MaxSpeedReached(-19f, 38f, 20f, 1f), Is.False);
            Assert.That(SuspensionTuning.MaxSpeedReached(40f, 38f, 20f, 1.25f), Is.False);

            // Rolling forward while reverse is held.
            Assert.That(SuspensionTuning.ChangingDir(10f, -1f, 38f), Is.True);
            Assert.That(SuspensionTuning.ChangingDir(1f, -1f, 38f), Is.False, "below a tenth of the top speed it does not count");

            // No throttle brakes, throttle on level ground does not.
            Assert.That(SuspensionTuning.OnBrake(0f, 0f, 38f, 20f, 0f, 30f, false, 6, 6), Is.True);
            Assert.That(SuspensionTuning.OnBrake(1f, 10f, 38f, 20f, 0f, 30f, false, 6, 6), Is.False);
            Assert.That(SuspensionTuning.OnBrake(1f, 10f, 38f, 20f, 0f, 30f, true, 6, 6), Is.True, "a blocked hull always brakes");

            // The angle term also needs more wheel contacts than the counter can
            // ever reach, so a steep slope alone does not brake.
            Assert.That(SuspensionTuning.OnBrake(1f, 10f, 38f, 20f, 45f, 30f, false, 12, 6), Is.False);
        }

        [Test]
        public void WheelMask_IsTheGroundAndTheHullShellOnly()
        {
            // suspension_component.gd: rc.collision_mask = 1 + 2.
            int mask = BattleLayers.WheelMask;

            Assert.That((mask & (1 << BattleLayers.Static)) != 0, Is.True, "the map holds the tank up");
            Assert.That((mask & (1 << BattleLayers.Physics)) != 0, Is.True, "a tank can stand on another tank");

            // A hull carries its own armour around; a ray that could see it
            // would prop the tank up on its own plates, which is what left the
            // T-43 sitting nose down with its tail in the air.
            Assert.That((mask & (1 << BattleLayers.Armor)) == 0, Is.True);
            Assert.That((mask & (1 << BattleLayers.Screen)) == 0, Is.True);
            Assert.That((mask & (1 << BattleLayers.Modules)) == 0, Is.True);
            Assert.That((mask & (1 << BattleLayers.Foliage)) == 0, Is.True);
            Assert.That((mask & (1 << BattleLayers.Destructibles)) == 0, Is.True);
        }
    }
}
