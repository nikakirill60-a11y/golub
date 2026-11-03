using System.Collections.Generic;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;
namespace BlitzLegacy.Tests
{
    public sealed class DavaFxEquivalenceTests
    {
        [TestCase("huntersMark", 11, 76)]
        [TestCase("curse", 50, 200)]
        [TestCase("vortex", 12, 48)]
        [TestCase("splash", 32, 128)]
        [TestCase("returnInTime", 0, 0)]
        [TestCase("moonwalk", 11, 44)]
        [TestCase("moonwalk_out", 0, 0)]
        [TestCase("berserk", 195, 1152)]
        [TestCase("damageBoost", 8, 32)]
        [TestCase("engineBoost", 36, 144)]
        [TestCase("reloadBoost", 15, 64)]
        [TestCase("lightning", 0, 0)]
        public void OriginalParticleAndGeometryBudgetsMatchReferenceAndAllStreamsStayFinite(string id,int particles,int vertices)
        {
            var root=new GameObject("original-fx-reference");var cameraHost=new GameObject("camera");
            try
            {
                var camera=cameraHost.AddComponent<Camera>();camera.transform.position=new Vector3(0,5,-14);camera.transform.LookAt(new Vector3(0,1,0));camera.nearClipPlane=.01f;
                var copy=0;var mount=new GameObject("mount");mount.transform.SetParent(root.transform,false);mount.transform.localPosition=new Vector3((copy%7-3)*.6f,0,(copy/7)*.6f);
                var fx=DavaFx.Create(mount.transform,UprisingVfx.Sources[id],20,true);fx.SetVariable("Velosity",1);
                if(id=="lightning")fx.SetStripeEndpoints(new Vector3(-5,1,0),new Vector3(5,1,0));
                for(var step=0;step<120;step++){fx.Simulate(1f/60);fx.Render(camera);}
                Assert.AreEqual(particles,fx.LiveParticles,id);
                var filters=root.GetComponentsInChildren<MeshFilter>(true);var count=0;
                foreach(var filter in filters)
                {
                    var mesh=filter.sharedMesh;count+=mesh.vertexCount;
                    foreach(var v in mesh.vertices)Assert.IsTrue(float.IsFinite(v.x)&&float.IsFinite(v.y)&&float.IsFinite(v.z));
                    foreach(var v in mesh.normals)Assert.IsTrue(float.IsFinite(v.x)&&float.IsFinite(v.y)&&float.IsFinite(v.z));
                    foreach(var c in mesh.colors)Assert.IsTrue(float.IsFinite(c.r)&&float.IsFinite(c.g)&&float.IsFinite(c.b)&&float.IsFinite(c.a));
                    var uv=new List<Vector3>();for(var channel=0;channel<3;channel++){mesh.GetUVs(channel,uv);foreach(var v in uv)Assert.IsTrue(float.IsFinite(v.x)&&float.IsFinite(v.y)&&float.IsFinite(v.z));}
                    foreach(var index in mesh.triangles)Assert.That(index,Is.InRange(0,Mathf.Max(0,mesh.vertexCount-1)));
                }
                Assert.AreEqual(vertices,count,id);
            }
            finally{Object.DestroyImmediate(root);Object.DestroyImmediate(cameraHost);}
        }
    }
}
