using System.IO;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>Render the actual live 4.8 death path, never the old Godot factory.</summary>
    public static class WreckRenderCli
    {
        public const int Width=800,Height=600;
        private const string OutputDirectory="Logs/wreck-render";
        public static void Render() { RenderVehicles(new[]{"ms-1","maus"}); }
        public static void Render1120() { RenderVehicles(new[]{"ch57_bzt_70","s16_kranvagn","f119_projet_murat"}); }
        private static void RenderVehicles(string[] vehicles)
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                Directory.CreateDirectory(OutputDirectory);
                var sun=new GameObject("sun").AddComponent<Light>();sun.type=LightType.Directional;sun.intensity=2;
                sun.transform.rotation=Quaternion.Euler(50,-30,0);
                var camera=new GameObject("camera").AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;
                camera.backgroundColor=new Color(.13f,.17f,.19f);camera.nearClipPlane=.1f;camera.farClipPlane=100;
                var floor=GameObject.CreatePrimitive(PrimitiveType.Plane);floor.transform.localScale=Vector3.one*3;
                var database=TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
                foreach(var id in vehicles)foreach(var exploded in new[]{false,true})
                {
                    var host=new GameObject(id,typeof(Rigidbody));var tank=host.AddComponent<BattleTank>();tank.Initialize(database.Get(id));
                    camera.transform.position=new Vector3(10,6,-12)*Mathf.Max(.4f,tank.HullLength/7f);
                    camera.transform.LookAt(new Vector3(0,1,0));
                    if(exploded)tank.DamageModule(TankModuleIds.AmmoBay,10000);
                    else tank.ReceiveDamage(new DamageInfo{Dmg=100000,DmgBase=100000});
                    var wreck=host.GetComponentInChildren<Original480Wreck>();
                    if(!tank.Dead||wreck==null||!wreck.Playing)throw new System.InvalidOperationException("Live death path failed");
                    var previous=0f;
                    foreach(var time in new[]{.08f,.2f,.45f,.9f,1.8f,5f})
                    {
                        var left=time-previous;while(left>0){var step=Mathf.Min(1f/60,left);foreach(var fx in wreck.Effects)fx.Simulate(step);left-=step;}
                        foreach(var fx in wreck.Effects)fx.Render(camera);
                        Capture(camera,id+(exploded?"-ammo-":"-death-")+time.ToString("F2",System.Globalization.CultureInfo.InvariantCulture)+".png");previous=time;
                    }
                    Object.DestroyImmediate(host);
                }
                Debug.Log("[Blitz] original wreck render ok: "+(vehicles.Length*12)+" live-death frames");EditorApplication.Exit(0);
            }
            catch(System.Exception e){Debug.LogException(e);EditorApplication.Exit(1);}
        }
        private static void Capture(Camera camera,string file)
        {
            var target=new RenderTexture(Width,Height,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);
            var texture=new Texture2D(Width,Height,TextureFormat.RGB24,false,false);var active=RenderTexture.active;
            try{camera.targetTexture=target;for(var i=0;i<3;i++)camera.Render();RenderTexture.active=target;
                texture.ReadPixels(new Rect(0,0,Width,Height),0,0);texture.Apply();File.WriteAllBytes(Path.Combine(OutputDirectory,file),texture.EncodeToPNG());}
            finally{camera.targetTexture=null;RenderTexture.active=active;target.Release();Object.DestroyImmediate(target);Object.DestroyImmediate(texture);}
        }
    }
}
