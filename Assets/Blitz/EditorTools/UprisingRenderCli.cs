using System;
using System.IO;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Ui;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
namespace BlitzLegacy.EditorTools
{
    /// <summary>Real graphics-device snapshots of every original Uprising effect and both selection screens.</summary>
    public static class UprisingRenderCli
    {
        public static void Render()
        {
            var errors=0;Application.logMessageReceived+=(message,stack,type)=>{if(type==LogType.Error || type==LogType.Exception)errors++;};
            try {
                UprisingTextureImport.Apply();
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                Directory.CreateDirectory("Logs/uprising-render");
                var camera=new GameObject("camera").AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.12f,.16f,.2f);camera.transform.position=new Vector3(0,5,-14);camera.transform.LookAt(new Vector3(0,1,0));camera.nearClipPlane=.01f;
                var floor=GameObject.CreatePrimitive(PrimitiveType.Plane);floor.transform.position=new Vector3(0,-.05f,0);
                var light=new GameObject("sun").AddComponent<Light>();light.type=LightType.Directional;light.transform.rotation=Quaternion.Euler(50,-30,0);
                foreach(var pair in UprisingVfx.Sources){var host=new GameObject(pair.Key);var fx=DavaFx.Create(host.transform,pair.Value,3,false);fx.SetVariable("Velosity",1);if(pair.Key=="lightning")fx.SetStripeEndpoints(new Vector3(-5,1,0),new Vector3(5,1,0));float clock=0;foreach(var time in new[]{.1f,.35f,.75f,1.5f,3f}){while(clock+1f/60<=time){host.transform.position=new Vector3(Mathf.Sin(clock)*.4f,0,0);fx.Simulate(1f/60);clock+=1f/60;}fx.Render(camera);Capture(camera,pair.Key+"-"+time.ToString("F2",System.Globalization.CultureInfo.InvariantCulture));}UnityEngine.Object.DestroyImmediate(host);}
                var sustainedHost=new GameObject("sustained-berserk");var sustained=DavaFx.Create(sustainedHost.transform,UprisingVfx.Sources["berserk"],15,true);var sustainedClock=0f;
                foreach(var atTime in new[]{.1f,.25f,.5f,1f,3f,5f,10f,14f}){while(sustainedClock<atTime){sustained.Simulate(1f/60);sustainedClock+=1f/60;}sustained.Render(camera);Capture(camera,"berserk-held-"+atTime.ToString("F2",System.Globalization.CultureInfo.InvariantCulture));}UnityEngine.Object.DestroyImmediate(sustainedHost);
                UnityEngine.Object.DestroyImmediate(floor);
                var menu=BattleModeScreen.Create(null,BlitzLegacy.Core.Battle.BattleMode.Uprising,null);Canvas.ForceUpdateCanvases();CaptureUi(camera,"mode-menu");UnityEngine.Object.DestroyImmediate(menu.gameObject);
                var session=BattleSession.Create(null,false,autoStart:true,mode:BlitzLegacy.Core.Battle.BattleMode.Uprising);var player=session.Player;
                var controller=player.GetComponent<UprisingTank>();foreach(var passive in new[]{"damageBoost2","reloadBoost2","engineBoost2"})controller.State.Grant(passive);
                var mounted=player.GetComponent<UprisingVfx>();mounted.SendMessage("LateUpdate");
                foreach(var fx in player.GetComponentsInChildren<DavaFx>()){fx.SetVariable("Velosity",1);for(var tick=0;tick<90;tick++)fx.Simulate(1f/60);fx.Render(session.View.Active);}
                CaptureUi(session.View.Active,"mounted-passives");
                player.ReceiveDamage(new BlitzLegacy.Core.Tanks.DamageInfo{Dmg=player.Health.MaxHealth*10,DmgBase=player.Health.MaxHealth*10,DmgType=BlitzLegacy.Core.Tanks.DamageType.Ability});foreach(var phase in new[]{new[]{"ability-delay",".5"},new[]{"ability-fade","1.11"},new[]{"ability-offer","1.3"}}){var elapsed=double.Parse(phase[1],System.Globalization.CultureInfo.InvariantCulture);typeof(BlitzLegacy.Core.Battle.UprisingState).GetProperty("RespawnAt").SetValue(controller.State,Time.time+BlitzLegacy.Core.Battle.UprisingState.RespawnDelay-elapsed);foreach(var ui in session.GetComponentsInChildren<UprisingBattleUi>(true))ui.SendMessage("Update");Canvas.ForceUpdateCanvases();CaptureUi(session.View.Active,phase[0]);}UnityEngine.Object.DestroyImmediate(session.gameObject);
                if(errors>0 || ShaderUtil.ShaderHasError(Shader.Find("BlitzLegacy/DavaParticle1120")))throw new InvalidOperationException("Uprising render reported errors: "+errors);
                Debug.Log("[Blitz] Uprising original FX/UI captures complete");EditorApplication.Exit(0);
            } catch(Exception ex){Debug.LogException(ex);EditorApplication.Exit(1);}
        }
        private static void CaptureUi(Camera camera,string name)
        {
            var canvases=UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            foreach(var c in canvases)if(c.renderMode==RenderMode.ScreenSpaceOverlay){c.renderMode=RenderMode.ScreenSpaceCamera;c.worldCamera=camera;c.planeDistance=1;}
            Canvas.ForceUpdateCanvases();Capture(camera,name);
        }
        private static void Capture(Camera camera,string name)
        {
            var target=new RenderTexture(1196,540,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);var image=new Texture2D(1196,540,TextureFormat.RGB24,false,false);var previous=RenderTexture.active;
            try{camera.targetTexture=target;for(var i=0;i<3;i++)camera.Render();RenderTexture.active=target;image.ReadPixels(new Rect(0,0,1196,540),0,0);image.Apply();File.WriteAllBytes("Logs/uprising-render/"+name+".png",image.EncodeToPNG());}
            finally{camera.targetTexture=null;RenderTexture.active=previous;UnityEngine.Object.DestroyImmediate(image);target.Release();UnityEngine.Object.DestroyImmediate(target);}
        }
    }
}
