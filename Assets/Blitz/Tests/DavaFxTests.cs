using System.Collections.Generic;
using System.Reflection;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Serialization;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
namespace BlitzLegacy.Tests
{
    public sealed class DavaFxTests
    {
        [TestCase(-114)][TestCase(-1)][TestCase(0)][TestCase(12)][TestCase(69)][TestCase(145)]
        public void AllocationFreeNoiseMatchesEveryOriginalChannelExactly(int index)
        {
            foreach(var age in new[]{-.5f,0,.2f,1f,10f})foreach(var frequency in new[]{0f,.5f,10f,-2f})
            {
                var legacy=DavaNoise.Sample(age,frequency,index);
                DavaNoise.Sample(age,frequency,index,out var x,out var y,out var z);
                CollectionAssert.AreEqual(legacy,new[]{x,y,z});
            }
        }

        [Test] public void ScalarLinearAndHermitePreserveSourceKeys()
        {
            var line=JsonNode.Parse("[2,[[1,2,0],[1,4,1]]]");Assert.AreEqual(3,DavaProperty.Scalar(line,.5));
            var hermite=JsonNode.Parse("[2,[[2,0,0,0,0],[2,1,1,0,0]]]");Assert.AreEqual(.15625,DavaProperty.Scalar(hermite,.25),1e-8);
            Assert.AreEqual(1,DavaProperty.Scalar(hermite,5));
        }
        [Test] public void ExternalVelocityMultipliesValueCurve()
        {
            var wrapped=JsonNode.Parse("[0,\"Velosity\",[2,[[1,0,0],[1,2,1]]],10]");
            Assert.AreEqual(5,DavaProperty.Scalar(wrapped,3,0,new Dictionary<string,double>{{"Velosity",.25}}));
            Assert.AreEqual(0,DavaProperty.Scalar(wrapped,3));
        }
        [Test] public void ForceNoiseIsFiniteAndUsesOriginalPeriodicLookup()
        {
            var a=DavaNoise.Sample(.2f,10,12);var b=DavaNoise.Sample(.2f,10,69);CollectionAssert.AreEqual(a,b);foreach(var value in a)Assert.IsTrue(float.IsFinite(value));
        }
        [Test] public void PackedColourUsesAuthoredRgbaNotAbgr()
        {var c=DavaProperty.Colour(JsonNode.Parse("4278190335"),0);CollectionAssert.AreEqual(new[]{1d,0d,0d,1d},c);}
        [Test] public void SimulatorDoesNotConsumeGameplayRandomState()
        {
            var host=new GameObject("rng-isolation");try{var fx=DavaFx.Create(host.transform,UprisingVfx.Sources["splash"]);Random.InitState(42);var previous=Random.state;fx.Simulate(.1f);var actual=Random.value;Random.state=previous;Assert.AreEqual(Random.value,actual);}finally{Object.DestroyImmediate(host);}
        }
        [Test] public void PackedWebpEmbeddedDescriptorRestoresAllOriginalAnimationFrames()
        {
            var manifest=JsonNode.Parse(Resources.Load<TextAsset>("Data/uprising_source_manifest").text);
            Assert.AreEqual(64,manifest["textures"]["~res:/Gfx/Particles/ParticlesHigh/Smoke8x8/Smoke8x8Test"]["frames"].AsInt());
            Assert.AreEqual(4,manifest["textures"]["~res:/Gfx/Particles/halloween2019/Tornado1x4/torv2"]["frames"].AsInt());
        }
        [Test] public void SustainedBerserkHonoursStartupBurstWindowsAndKeepsGrowingRing()
        {
            var host=new GameObject("sustained-berserk");var cameraHost=new GameObject("camera");
            try
            {
                var camera=cameraHost.AddComponent<Camera>();var fx=DavaFx.Create(host.transform,UprisingVfx.Sources["berserk"],15,true);
                Mesh ring=null;float small=0,grown=0,settled=0;
                for(var tick=1;tick<=300;tick++)
                {
                    fx.Simulate(1f/60);fx.Render(camera);
                    if(ring==null)foreach(var f in fx.GetComponentsInChildren<MeshFilter>())if(f.name=="Cirkle")ring=f.sharedMesh;
                    if(tick==6)small=ring.bounds.size.x;
                    if(tick==24)grown=ring.bounds.size.x;
                    if(tick==90)settled=ring.bounds.size.x;
                }
                Assert.Greater(grown,small);Assert.Greater(settled,0);Assert.AreEqual(settled,ring.bounds.size.x,.0001f);
                var groups=(System.Collections.IEnumerable)typeof(DavaFx).GetField("_groups",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(fx);var inspected=0;
                foreach(var group in groups)
                {
                    var type=group.GetType();var layer=(JsonNode)type.GetField("Layer").GetValue(group);
                    if(layer["endTime"].AsFloat()>.3f || layer["isSingle"].AsBool() || layer["isLooped"].AsBool())continue;
                    inspected++;Assert.IsTrue((bool)type.GetField("Finished").GetValue(group),"Sustain restarted a one-shot layer: "+layer["name"].AsString());
                    Assert.AreEqual(0,((System.Collections.ICollection)type.GetField("Particles").GetValue(group)).Count);
                }
                // Drained one-shot groups can also have been removed entirely.
                Assert.LessOrEqual(inspected,1);
            }
            finally{Object.DestroyImmediate(host);Object.DestroyImmediate(cameraHost);}
        }
        [TestCase("huntersMark")][TestCase("curse")][TestCase("vortex")][TestCase("splash")]
        [TestCase("returnInTime")][TestCase("moonwalk")][TestCase("moonwalk_out")][TestCase("berserk")]
        [TestCase("damageBoost")][TestCase("engineBoost")][TestCase("reloadBoost")][TestCase("lightning")]
        public void EveryOriginalEffectProducesFiniteGeometryAndStops(string id)
        {
            var host=new GameObject("source-audit");var cameraHost=new GameObject("camera");
            try {
                var camera=cameraHost.AddComponent<Camera>();camera.transform.position=new Vector3(0,5,-12);camera.transform.LookAt(Vector3.zero);
                var fx=DavaFx.Create(host.transform,UprisingVfx.Sources[id],1,false);fx.SetVariable("Velosity",1);
                if(id=="lightning")fx.SetStripeEndpoints(Vector3.zero,Vector3.right*20);
                int vertices=0;for(var i=0;i<45;i++){fx.Simulate(1f/60);fx.Render(camera);foreach(var f in fx.GetComponentsInChildren<MeshFilter>()){vertices+=f.sharedMesh.vertexCount;var uv=new System.Collections.Generic.List<Vector3>();f.sharedMesh.GetUVs(0,uv);foreach(var value in uv)Assert.IsTrue(float.IsFinite(value.x)&&float.IsFinite(value.y)&&float.IsFinite(value.z)&&value.z>0);foreach(var p in f.sharedMesh.vertices)Assert.IsTrue(float.IsFinite(p.x)&&float.IsFinite(p.y)&&float.IsFinite(p.z));}}
                Assert.Greater(fx.GetComponentsInChildren<MeshFilter>().Length,0);
                Assert.Greater(vertices,0,"Original effect has no visible geometry: "+id);
                fx.Stop();for(var i=0;i<7200 && fx.LiveParticles>0;i++)fx.Simulate(1f/60);
                Assert.AreEqual(0,fx.LiveParticles,"Original effect failed to drain: "+id);
            } finally {Object.DestroyImmediate(host);Object.DestroyImmediate(cameraHost);}
        }
    }
}
