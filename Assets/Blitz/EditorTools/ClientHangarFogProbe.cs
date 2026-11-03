using System;
using System.Collections.Generic;
using BlitzLegacy.Runtime.Hangar;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.EditorTools
{
    /// <summary>Numerical GPU guard against mixing DAVA encoded fog in linear space.
    /// Uses the actual production shaders, an HDR linear target and a CPU
    /// reference of materials-fp.sl + vp-fog-math.slh, not a second test shader.</summary>
    public static class ClientHangarFogProbe
    {
        private static float Decode(float value)
        {
            return value<=.04045f?value/12.92f:Mathf.Pow((value+.055f)/1.055f,2.4f);
        }
        public static void Validate()
        {
            if(QualitySettings.activeColorSpace!=ColorSpace.Linear)
                throw new InvalidOperationException("Fog probe requires the project's Linear colour space");
            var objectRoot=new GameObject("FogProbeQuad");objectRoot.layer=30;
            var cameraRoot=new GameObject("FogProbeCamera");
            var camera=cameraRoot.AddComponent<Camera>();camera.orthographic=true;camera.orthographicSize=.09f;
            camera.nearClipPlane=.1f;camera.farClipPlane=100f;camera.cullingMask=1<<30;
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
            camera.allowHDR=true;camera.allowMSAA=false;
            var target=new RenderTexture(32,32,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);
            var image=new Texture2D(32,32,TextureFormat.RGBAFloat,false,true);
            var texture=new Texture2D(1,1,TextureFormat.RGBA32,false,false);
            texture.SetPixel(0,0,new Color(.2f,.2f,.2f,1));texture.Apply();
            var mesh=new Mesh();mesh.triangles=new int[0];
            mesh.vertices=new[]{new Vector3(-10f,-10f,0),new Vector3(10f,-10f,0),new Vector3(10f,10f,0),new Vector3(-10f,10f,0)};
            mesh.normals=new[]{Vector3.back,Vector3.back,Vector3.back,Vector3.back};
            mesh.SetUVs(0,new List<Vector3>{Vector3.zero,Vector3.right,new Vector3(1,1,0),Vector3.up});
            mesh.uv2=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up};
            mesh.colors=new[]{Color.white,Color.white,Color.white,Color.white};
            mesh.triangles=new[]{0,1,2,0,2,3};mesh.RecalculateBounds();
            objectRoot.AddComponent<MeshFilter>().sharedMesh=mesh;
            var renderer=objectRoot.AddComponent<MeshRenderer>();
            var previous=RenderTexture.active;Material material=null;
            try
            {
                Shader.SetGlobalVector("_BlitzFogColour",new Vector4(.7f,1.15f,1.2f,0));
                Shader.SetGlobalVector("_BlitzFogSky",Vector4.zero);
                Shader.SetGlobalVector("_BlitzFogDepth",new Vector4(0,0,.005f,0));
                foreach(var shader in new[]{ClientHangarScene.SurfaceShader,ClientHangarScene.ParticleShader})
                {
                    material=new Material(Shader.Find(shader));renderer.sharedMaterial=material;
                    material.SetFloat("_Cull",0);material.SetFloat("_SrcBlend",1);material.SetFloat("_DstBlend",0);
                    material.SetFloat("_SecondaryGain",1);material.SetFloat("_Fog",1);
                    if(shader==ClientHangarScene.SurfaceShader)material.SetTexture("_BaseMap",texture);
                    else material.SetTexture("_MainTex",texture);
                    foreach(var mode in new[]{0,1,2})
                    {
                        const float distance=20f;objectRoot.transform.position=new Vector3(0,0,distance);
                        Shader.SetGlobalVector("_BlitzFogRange",new Vector4(12,60,.8f,mode==1?1:2));
                        Shader.SetGlobalVector("_BlitzFogDepth",new Vector4(0,0,mode==0?0:.005f,0));
                        camera.targetTexture=target;
                        for(var frame=0;frame<4;frame++)camera.Render();
                        RenderTexture.active=target;image.ReadPixels(new Rect(0,0,32,32),0,0);image.Apply();
                        var actual=image.GetPixel(16,16);
                        // All vertices have the same radius, so interpolation
                        // has an exact constant reference at the centre pixel.
                        // The wide plane deliberately makes vertex distance
                        // differ from pixel distance: per-fragment fog fails.
                        var radius=Mathf.Sqrt(distance*distance+200f);
                        var amount=mode==1?Mathf.Clamp((radius-12)/48,0,.8f):1-Mathf.Exp(-(mode==0?0:.005f)*radius);
                        var expected=new Color(Decode(Mathf.Lerp(.2f,.7f,amount)),Decode(Mathf.Lerp(.2f,1.15f,amount)),Decode(Mathf.Lerp(.2f,1.2f,amount)));
                        var error=Mathf.Max(Mathf.Abs(expected.r-actual.r),Mathf.Abs(expected.g-actual.g),Mathf.Abs(expected.b-actual.b));
                        Debug.Log("[Blitz] fog GPU probe shader="+shader+" mode="+mode+" sourceAmount="+amount+" maxLinearError="+error);
                        if(error>.004f)throw new InvalidOperationException("DAVA fog GPU mismatch: "+shader+" mode="+mode+" expected="+expected+" actual="+actual);
                    }
                    UnityEngine.Object.DestroyImmediate(material);material=null;
                }
            }
            finally
            {
                camera.targetTexture=null;RenderTexture.active=previous;
                if(material!=null)UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(objectRoot);UnityEngine.Object.DestroyImmediate(cameraRoot);
                UnityEngine.Object.DestroyImmediate(mesh);UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(image);UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
}
