using System;
using System.IO;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Battle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.EditorTools
{
    /// <summary>GPU captures of the original ATGM art on the real battle HUD.</summary>
    public static class AtgmRenderCli
    {
        public static void Render()
        {
            var errors = 0;
            Application.logMessageReceived += (message, stack, type) =>
            { if (type == LogType.Error || type == LogType.Exception) errors++; };
            try
            {
                AtgmTextureImport.Apply();
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Directory.CreateDirectory("Logs/atgm-render");
                var db = TanksDatabase.Parse(Resources.Load<TextAsset>("Data/tanks").text);
                ShellInfo shell = null;
                foreach (var gun in db.Get("g14_kpfpz_70r").Guns)
                    foreach (var round in gun.Shells) if (round.UsesModernAtgmVisual) shell = round;
                if (shell == null) throw new InvalidOperationException("Live modern ATGM card not wired");
                var camera = new GameObject("ATGM observer").AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.12f,.16f,.2f);
                camera.nearClipPlane = .01f;
                var hud = BattleHud.Create(null,null,false);
                var canvas = hud.Root.GetComponentInParent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 1;
                foreach (var size in new[] { new Vector2Int(1196,540), new Vector2Int(1920,1080) })
                {
                    foreach (var fov in new[] { 60f, 20f })
                    {
                        camera.fieldOfView = fov;
                        var shot = BattleProjectile.Create(shell,null,new Vector3(-2,0,15),new Vector3(-2,0,15),Vector3.right);
                        var visual = shot.AtgmVisual;
                        // Simulate the authored emitters along an actual moving path.
                        for (var i=0;i<45;i++)
                        {
                            shot.transform.position = new Vector3(-2 + i*.05f,0,15);
                            visual.RenderAt(camera,Time.time);
                            visual.FlightFx.Simulate(1f/60);
                            visual.TrailFx.Simulate(1f/60);
                        }
                        Capture(camera,canvas,visual,size,"flight-"+size.x+"-fov"+fov,Time.time);
                        visual.BeginExplosion(shot.transform.position);
                        foreach(var fx in UnityEngine.Object.FindObjectsByType<DavaFx>(FindObjectsSortMode.None))
                        { for(var i=0;i<9;i++)fx.Simulate(1f/60);fx.Render(camera); }
                        Capture(camera,canvas,visual,size,"impact-"+size.x+"-fov"+fov,Time.time+.375f);
                        visual.Cancel();
                        UnityEngine.Object.DestroyImmediate(shot.gameObject);
                        foreach(var fx in UnityEngine.Object.FindObjectsByType<DavaFx>(FindObjectsSortMode.None))
                            UnityEngine.Object.DestroyImmediate(fx.gameObject);
                    }
                }
                UnityEngine.Object.DestroyImmediate(hud.gameObject);
                UnityEngine.Object.DestroyImmediate(camera.gameObject);
                if(errors!=0)throw new InvalidOperationException("ATGM GPU errors: "+errors);
                Debug.Log("[Blitz] Original ATGM GPU captures complete; HUD, normal/scope FOV, two resolutions");
                EditorApplication.Exit(0);
            }
            catch(Exception ex){ Debug.LogException(ex);EditorApplication.Exit(1); }
        }
        private static void Capture(Camera camera,Canvas canvas,BattleAtgmPresentation visual,Vector2Int size,string name,float now)
        {
            var target = new RenderTexture(size.x,size.y,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);
            var image = new Texture2D(size.x,size.y,TextureFormat.RGB24,false,false);
            var previous = RenderTexture.active;
            try
            {
                camera.targetTexture = target;
                camera.pixelRect = new Rect(0,0,size.x,size.y);
                Canvas.ForceUpdateCanvases();
                visual.RenderAt(camera,now);
                foreach(var fx in UnityEngine.Object.FindObjectsByType<DavaFx>(FindObjectsSortMode.None))fx.Render(camera);
                for(var i=0;i<3;i++)camera.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0,0,size.x,size.y),0,0);image.Apply();
                File.WriteAllBytes("Logs/atgm-render/"+name+".png",image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture=null;RenderTexture.active=previous;
                UnityEngine.Object.DestroyImmediate(image);target.Release();UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
}
