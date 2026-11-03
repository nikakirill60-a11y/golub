using System;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Runtime.Hangar;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    public sealed class ClientHangarTests
    {
        [TestCase(0, 100, false)]
        [TestCase(99, 100, false)]
        [TestCase(100, 100, false)]
        [TestCase(101, 100, true)]
        public void PremiumUsesAccountExpirationNotTankKind(double expiration, double now, bool active)
        {
            Assert.AreEqual(active, new AccountData { PremAccountExpiration=expiration }.HasPremium(now));
        }
        [Test]
        public void MalformedPremiumDateDoesNotUnlockHangar()
        {
            Assert.IsFalse(new AccountData { PremAccountExpiration=double.NaN }.HasPremium(100));
            Assert.IsFalse(new AccountData { PremAccountExpiration=double.PositiveInfinity }.HasPremium(100));
        }
        [TestCase(false,28,66,0,4)]
        [TestCase(true,237,53,4,2)]
        public void BothScenesLoadOriginalGeometryAndEffects(bool premium,int batches,int layers,int motions,int smoke)
        {
            var scene=ClientHangarScene.Create(null,premium);
            try
            {
                Assert.AreEqual(batches,scene.BatchCount);
                Assert.AreEqual(layers,scene.ParticleLayerCount);
                Assert.AreEqual(motions,scene.AnimationCount);
                Assert.AreEqual(smoke,scene.SmokeEmitterCount);
                Assert.AreEqual(1,scene.RuntimeLightCount);
                Assert.AreEqual(premium?649131:96869,scene.TriangleCount);
                foreach(var filter in scene.GetComponentsInChildren<MeshFilter>(true))
                    Assert.AreEqual(filter.sharedMesh.vertexCount,filter.sharedMesh.uv2.Length);
                foreach(var ps in scene.GetComponentsInChildren<ParticleSystem>(true))
                {
                    Assert.AreEqual(ClientHangarScene.ParticleShader,ps.GetComponent<ParticleSystemRenderer>().sharedMaterial.shader.name);
                    Assert.IsFalse(ps.main.playOnAwake);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(scene.gameObject); }
        }
        [Test]
        public void SourceEffectsSelectOnlyActiveMaterialProperties()
        {
            var regular=ClientHangarDefinition.Load(false);
            foreach(var material in regular.materials)
                CollectionAssert.AreEqual(new float[]{1,1,1,1},material.tint,
                    "Unused red flatColor must not tint the ordinary scene");
            var premium=ClientHangarDefinition.Load(true);
            Assert.AreEqual(6.20486f,premium.standHeight,.00001f);
            Assert.IsFalse(premium.environment.linear);
            Assert.AreEqual(.005f,premium.environment.density,.000001f);
            var floor=Array.Find(premium.materials,m=>m.name.StartsWith("404786198_"));
            Assert.IsNotNull(floor);
            StringAssert.EndsWith("env_hg_floorLM",floor.secondary,
                "Decal.Opaque must not sample its inactive texture0 lightmap");
            CollectionAssert.AreEqual(new float[]{1,1},floor.uvScale);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SmokeRetainsAuthoredForceReversals(bool premium)
        {
            var definition=ClientHangarDefinition.Load(premium);
            var found=false;
            foreach(var effect in definition.effects)
                foreach(var layer in effect.layers)
                    if(layer.source.EndsWith("/smoke.yaml"))
                    {
                        found=true;
                        Assert.AreEqual(5,layer.forceKeys.Length);
                        Assert.Greater(layer.forceKeys[0].value[0]*layer.forceKeys[2].value[0],0);
                        Assert.Less(layer.forceKeys[0].value[0]*layer.forceKeys[1].value[0],0);
                    }
            Assert.IsTrue(found);
        }

        [Test]
        public void WeldingSparksHaveGravityDragAndSourceCollisionPlane()
        {
            var scene=ClientHangarScene.Create(null,true);
            try
            {
                var collisions=0;
                foreach(var ps in scene.GetComponentsInChildren<ParticleSystem>(true))
                    if(ps.collision.enabled)
                    {
                        collisions++;
                        Assert.AreEqual(ParticleSystemCollisionType.Planes,ps.collision.type);
                        Assert.IsNotNull(ps.collision.GetPlane(0));
                        Assert.Greater(ps.limitVelocityOverLifetime.drag.constant,0);
                        Assert.Less(ps.forceOverLifetime.y.curve.Evaluate(0),0);
                    }
                Assert.AreEqual(2,collisions);
            }
            finally { UnityEngine.Object.DestroyImmediate(scene.gameObject); }
        }

        [Test]
        public void FogFixKeepsOriginalDavaDensityRangeAndHdrColours()
        {
            var regular=ClientHangarDefinition.Load(false).environment;
            Assert.IsTrue(regular.linear);Assert.AreEqual(12,regular.start);Assert.AreEqual(60,regular.end);
            Assert.AreEqual(.7f,regular.limit,.00001f);
            var premium=ClientHangarDefinition.Load(true).environment;
            Assert.IsFalse(premium.linear);Assert.AreEqual(.005f,premium.density,.000001f);
            Assert.AreEqual(.8f,premium.limit,.00001f);Assert.IsTrue(premium.atmosphere);
            Assert.AreEqual(500,premium.distance);Assert.AreEqual(4,premium.scattering);
            Assert.AreEqual(1.15f,premium.color[1],.00001f);
            Assert.AreEqual(1.2f,premium.color[2],.00001f);
        }

        [Test]
        public void SwitchingPremiumRetainsTankCameraAndCurrentOrbit()
        {
            var world=HangarWorld.Create(null,new SettingsData());
            try
            {
                var tank=world.TankModel;var camera=world.HangarCamera;var orbit=world.Orbit;
                orbit.BeginDrag();orbit.Drag(new Vector2(123,-100));orbit.EndDrag();
                var yaw=orbit.Yaw;var pitch=orbit.Pitch;
                Assert.IsTrue(world.SetPremium(true));
                Assert.IsTrue(world.IsPremium);Assert.AreEqual("hangar_prem_2018",world.ClientScene.Definition.id);
                Assert.AreSame(tank,world.TankModel);Assert.AreSame(camera,world.HangarCamera);Assert.AreSame(orbit,world.Orbit);
                Assert.AreEqual(yaw,orbit.Yaw);Assert.AreEqual(pitch,orbit.Pitch);
                var current=world.ClientScene;Assert.IsFalse(world.SetPremium(true));Assert.AreSame(current,world.ClientScene);
                Assert.IsTrue(world.SetPremium(false));Assert.IsFalse(world.IsPremium);
                Assert.AreEqual(1,world.GetComponentsInChildren<ClientHangarScene>(true).Length);
            }
            finally { UnityEngine.Object.DestroyImmediate(world.gameObject); }
        }
        [Test]
        public void AmbientSkeletonsEvaluateTheOriginalAnimation()
        {
            var scene=ClientHangarScene.Create(null,true);
            try
            {
                var motions=scene.GetComponentsInChildren<ClientHangarMotion>(true);
                Assert.AreEqual(4,motions.Length);
                foreach(var motion in motions) { Assert.Greater(motion.Duration,0);motion.Sample(1);motion.Sample(3); }
                foreach(var mesh in scene.GetComponentsInChildren<MeshFilter>(true))
                    foreach(var p in mesh.sharedMesh.vertices)
                        Assert.IsFalse(float.IsNaN(p.x)||float.IsInfinity(p.x));
            }
            finally { UnityEngine.Object.DestroyImmediate(scene.gameObject); }
        }
    }
}
