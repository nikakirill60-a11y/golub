using System;
using System.IO;
using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Ui;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.EditorTools
{
    /// <summary>Real research-card fonts/art and full-title labels in both shipped languages.</summary>
    public static class TankNameRenderCli
    {
        public static void Render()
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                Directory.CreateDirectory("Logs/tank-language-render");
                var camera=new GameObject("camera").AddComponent<Camera>();
                camera.orthographic=true;camera.transform.position=new Vector3(0,0,-100);
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.07f,.09f,.1f);
                var lang=LocalizationTable.Parse(BlitzResources.LoadText(BlitzResources.LanguageData));
                var db=TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
                var kit=new HangarUiKit(AtlasRegionTable.Parse(BlitzResources.LoadText(BlitzResources.AtlasRegionsData)),
                    lang,BlitzResources.LoadFont(BlitzResources.RegularFontName),BlitzResources.LoadFont(BlitzResources.BoldFontName),null);
                foreach(var language in new[]{"en","ru"})
                {
                    lang.Language=language;
                    var root=new GameObject("tank_names_"+language,typeof(RectTransform),typeof(Canvas));
                    var canvas=root.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera=camera;canvas.planeDistance=10;
                    ((RectTransform)root.transform).sizeDelta=new Vector2(1000,600);
                    var ids=new[]{"g14_kpfpz_70r","t49_rocket","ms-1","r109_t54s"};
                    for(var i=0;i<ids.Length;i++)
                    {
                        var tank=db.Get(ids[i]);var x=30+(i%2)*480;var y=30+(i/2)*250;
                        TechTreeCard.Build(kit,(RectTransform)root.transform,x,y,400,170,1,
                            tank,ResearchStatus.NotAvailable,new TankSaveData{Name=tank.Name},null);
                        kit.Label("full_"+tank.Name,(RectTransform)root.transform,26,TextAnchor.MiddleLeft,
                            lang.GetTankName(tank),kit.Regular,Color.white,x,y+175,460,50);
                    }
                    var target=new RenderTexture(1000,600,24);var image=new Texture2D(1000,600,TextureFormat.RGB24,false);
                    var active=RenderTexture.active;
                    try
                    {
                        camera.targetTexture=target;Canvas.ForceUpdateCanvases();camera.Render();camera.Render();
                        RenderTexture.active=target;image.ReadPixels(new Rect(0,0,1000,600),0,0);image.Apply();
                        File.WriteAllBytes("Logs/tank-language-render/"+language+".png",image.EncodeToPNG());
                    }
                    finally
                    {
                        camera.targetTexture=null;RenderTexture.active=active;target.Release();
                        UnityEngine.Object.DestroyImmediate(image);UnityEngine.Object.DestroyImmediate(target);
                        UnityEngine.Object.DestroyImmediate(root);
                    }
                }
                Debug.Log("[Blitz] tank-title GPU en/ru capture ok");EditorApplication.Exit(0);
            }
            catch(Exception e){Debug.LogException(e);EditorApplication.Exit(1);}
        }
    }
}
