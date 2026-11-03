using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;
namespace BlitzLegacy.Tests
{
    public sealed class TankRolloverTests
    {
        [TestCase(0f,false)][TestCase(35f,false)][TestCase(85f,false)][TestCase(100f,true)][TestCase(160f,true)][TestCase(180f,true)]
        public void OnlyOverturnedSupportedAllowedTanksStart(float angle,bool expected)
        {
            var up=Quaternion.AngleAxis(angle,Vector3.forward)*Vector3.up;
            Assert.AreEqual(expected,TankRolloverPhysics.ShouldStart(up,Vector3.up,true,true));
            Assert.IsFalse(TankRolloverPhysics.ShouldStart(up,Vector3.up,false,true));
            Assert.IsFalse(TankRolloverPhysics.ShouldStart(up,Vector3.up,true,false));
        }
        [TestCase(120f,1f)][TestCase(160f,1f)][TestCase(180f,1f)][TestCase(200f,1f)]
        [TestCase(-120f,-1f)][TestCase(-160f,-1f)][TestCase(-180f,-1f)][TestCase(-200f,-1f)]
        public void TumbleKeepsTheExistingFallDirection(float angle,float direction)
        {
            var up=Quaternion.AngleAxis(angle,Vector3.right)*Vector3.up;
            var velocity=Vector3.right*direction;
            var axis=TankRolloverPhysics.ChooseAxis(up,Vector3.up,Vector3.forward,velocity);
            Assert.Greater(Vector3.Dot(axis,velocity),.99f);
            var remaining=TankRolloverPhysics.RemainingAngle(up,Vector3.up,axis);
            Assert.AreEqual((360-Mathf.Abs(angle))*Mathf.Deg2Rad,remaining,.0001f);
        }
        [Test] public void ExactRoofRestHasANonDegenerateDeterministicAxis()
        {
            var axis=TankRolloverPhysics.ChooseAxis(Vector3.down,Vector3.up,Vector3.forward,Vector3.zero);
            Assert.AreEqual(Vector3.forward,axis);
            Assert.AreEqual(Mathf.PI,TankRolloverPhysics.RemainingAngle(Vector3.down,Vector3.up,axis),.0001f);
        }
        [Test] public void MotorIsBoundedAndSlowsNearTrackLandingWithoutTeleporting()
        {
            Assert.AreEqual(TankRolloverPhysics.AccelerationLimit,TankRolloverPhysics.MotorAcceleration(Mathf.PI,0));
            Assert.Less(TankRolloverPhysics.MotorAcceleration(.1f,0),TankRolloverPhysics.MotorAcceleration(Mathf.PI,0));
            Assert.Less(TankRolloverPhysics.MotorAcceleration(Mathf.PI,5),0);
            Assert.AreEqual(0,TankRolloverPhysics.MotorAcceleration(0,0));
            Assert.AreEqual(0,TankRolloverPhysics.MotorAcceleration(float.NaN,0));
            Assert.AreEqual(Vector3.zero,TankRolloverPhysics.ChooseAxis(new Vector3(float.NaN,0,0),Vector3.up,Vector3.forward,Vector3.zero));
        }
        [Test] public void WallAndCeilingNormalsDoNotCreateGroundSupport()
        {
            var host=new GameObject("rollover-normal-gate");
            try
            {
                var component=host.AddComponent<TankRolloverPhysics>();
                component.ObserveSupport(Vector3.right,0);component.ObserveSupport(Vector3.down,0);
                var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                Assert.AreEqual(float.NegativeInfinity,typeof(TankRolloverPhysics).GetField("_contactTick",flags).GetValue(component));
                component.ObserveSupport(Vector3.up,0);Assert.AreEqual(0f,typeof(TankRolloverPhysics).GetField("_contactTick",flags).GetValue(component));
            }
            finally{Object.DestroyImmediate(host);}
        }
        [TestCase(false)][TestCase(true)]
        public void SupportStartsOnTheNextFixedStepWithoutWritingPoseOrVelocity(bool gravityAirControl)
        {
            var root=new GameObject("rollover-immediate-start");
            try
            {
                var tank=BattleTank.Spawn(root.transform,"ms-1",TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData)).Get("ms-1"),null,new Bounds(),0);
                var body=tank.GetComponent<Rigidbody>();var rollover=tank.GetComponent<TankRolloverPhysics>();
                tank.Suspension.AirControlEnabled=gravityAirControl;
                body.rotation=Quaternion.AngleAxis(150,Vector3.right);
                body.linearVelocity=new Vector3(1,-2,3);body.angularVelocity=Vector3.right*.7f;
                var pose=body.rotation;var linear=body.linearVelocity;var angular=body.angularVelocity;
                var tick=typeof(TankRolloverPhysics).GetMethod("FixedUpdate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                rollover.ObserveSupport(Vector3.up,Time.fixedTime);tick.Invoke(rollover,null);
                Assert.IsTrue(rollover.Active);Assert.AreEqual(1,rollover.RolloverCount);
                Assert.Greater(Vector3.Dot(rollover.LastAngularAcceleration,Vector3.right),0);
                Assert.Less(Quaternion.Angle(pose,body.rotation),.001f);
                Assert.AreEqual(linear,body.linearVelocity);Assert.AreEqual(angular,body.angularVelocity);
                // A separated hull receives no torque on the next unsupported step, but keeps its momentum.
                rollover.ObserveSupport(Vector3.up,Time.fixedTime-Time.fixedDeltaTime*2);
                tick.Invoke(rollover,null);
                Assert.AreEqual(Vector3.zero,rollover.LastAngularAcceleration);
                Assert.AreEqual(linear,body.linearVelocity);Assert.AreEqual(angular,body.angularVelocity);
                Assert.IsTrue(tank.Suspension.AirControlEnabled==gravityAirControl);
            }
            finally{Object.DestroyImmediate(root);}
        }
        [TestCase(-2f)][TestCase(2f)][TestCase(100f)]
        public void ExpiredOrFutureSupportNeverStartsTheMotor(float ageInSteps)
        {
            var root=new GameObject("rollover-stale-support");
            try
            {
                var tank=BattleTank.Spawn(root.transform,"ms-1",TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData)).Get("ms-1"),null,new Bounds(),0);
                tank.GetComponent<Rigidbody>().rotation=Quaternion.AngleAxis(180,Vector3.forward);
                var rollover=tank.GetComponent<TankRolloverPhysics>();
                rollover.ObserveSupport(Vector3.up,Time.fixedTime-ageInSteps*Time.fixedDeltaTime);
                typeof(TankRolloverPhysics).GetMethod("FixedUpdate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(rollover,null);
                Assert.IsFalse(rollover.Active);Assert.AreEqual(0,rollover.RolloverCount);
                Assert.AreEqual(Vector3.zero,rollover.LastAngularAcceleration);
            }
            finally{Object.DestroyImmediate(root);}
        }
        [Test] public void RolloverUsesTheRealSupportNormalOnASlope()
        {
            var normal=Quaternion.AngleAxis(30,Vector3.forward)*Vector3.up;
            Assert.IsFalse(TankRolloverPhysics.ShouldStart(normal,normal,true,true));
            Assert.IsFalse(TankRolloverPhysics.ShouldStart(Vector3.up,normal,true,true));
            Assert.IsTrue(TankRolloverPhysics.ShouldStart(-normal,normal,true,true));
            var axis=TankRolloverPhysics.ChooseAxis(-normal,normal,Vector3.forward,Vector3.zero);
            Assert.AreEqual(0,Vector3.Dot(axis,normal),.0001f);
            Assert.AreEqual(Mathf.PI,TankRolloverPhysics.RemainingAngle(-normal,normal,axis),.0001f);
        }
        [TestCase("ms-1")][TestCase("t49_rocket")][TestCase("maus")][TestCase("m-6-y")]
        public void EveryTankGetsTheSamePolicyAndRespawnClearsIt(string id)
        {
            var root=new GameObject("rollover-card-test");
            try
            {
                var tank=BattleTank.Spawn(root.transform,id,TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData)).Get(id),null,new Bounds(),0);
                var rollover=tank.GetComponent<TankRolloverPhysics>();Assert.IsNotNull(rollover);
                var mass=tank.GetComponent<Rigidbody>().mass;rollover.ObserveSupport(Vector3.up,0);
                tank.GetComponent<TankContactPhysics>().ResetState();
                var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                Assert.AreEqual(float.NegativeInfinity,typeof(TankRolloverPhysics).GetField("_contactTick",flags).GetValue(rollover));
                Assert.IsFalse(rollover.Active);Assert.AreEqual(0,rollover.RolloverCount);
                tank.DamageModule(TankModuleIds.TrackLeft,10000);tank.ReceiveDamage(new DamageInfo{Dmg=100000,DmgBase=100000});tank.RestoreLife(root.transform);
                Assert.IsFalse(rollover.Active);Assert.AreEqual(mass,tank.GetComponent<Rigidbody>().mass);
            }
            finally{Object.DestroyImmediate(root);}
        }
    }
}
