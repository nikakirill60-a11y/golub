using System;
using System.IO;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Runtime.Ui;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
namespace BlitzLegacy.EditorTools
{
    public static class AccountStatisticsRenderCli
    {
        public static void Render()
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                Directory.CreateDirectory("Logs/profile-render");
                var go=new GameObject("camera");var camera=go.AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.07f,.09f,.1f,1);camera.orthographic=true;camera.transform.position=new Vector3(0,0,-100);
                var account=new AccountData();
                for(var i=0;i<23;i++)
                {
                    var result=new BattleResultData{Id="history_"+i,TankId="is-7",MapId="rudniki",FinishedAt=1452931361+i,Outcome=(BattleOutcome)(i%3),CreditsEarned=10000+i,Credits=100,Gold=10,Xp=1000};
                    result.Participants.Add(new BattleParticipantResult{IsPlayer=true,Name="Amway921",TankId="is-7",TankName="IS-7",Damage=100+i,Shots=i%2==0?10:2,Hits=i%2==0?1:2,XpRaw=200+i,Xp=1000,Survived=i%2==0,Kills=i%7});account.BattleResults.Add(result);
                }
                account.Rewards["first_victory"]=1;account.Rewards["warrior"]=2;TeamKillPolicy.ApplyBattle(account.TeamKill,4200,TeamKillPolicy.Now());
                var screen=AccountStatisticsScreen.Create(null,account,()=>{},r=>{});
                Configure(screen.GetComponentInChildren<Canvas>(),camera);
                Capture(camera,"summary.png");screen.ShowTab(1);Capture(camera,"history.png");screen.ShowPage(3);Capture(camera,"history_last.png");
                var popup=MedalInfoPopup.Open(screen.transform,"warrior");Configure(popup.GetComponentInChildren<Canvas>(),camera);Capture(camera,"medal_popup.png");
                UnityEngine.Object.DestroyImmediate(screen.gameObject);
                Debug.Log("[Blitz] profile render ok: summary/history/history_last/medal_popup 1387x733");EditorApplication.Exit(0);
            }
            catch(Exception ex){Debug.LogException(ex);EditorApplication.Exit(1);}
        }
        private static void Configure(Canvas canvas,Camera camera){canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=10;}
        private static void Capture(Camera camera,string name)
        {
            var target=new RenderTexture(1387,733,24);var texture=new Texture2D(1387,733,TextureFormat.RGB24,false);var previous=RenderTexture.active;
            try {camera.targetTexture=target;Canvas.ForceUpdateCanvases();camera.Render();camera.Render();RenderTexture.active=target;texture.ReadPixels(new Rect(0,0,1387,733),0,0);texture.Apply();File.WriteAllBytes("Logs/profile-render/"+name,texture.EncodeToPNG());}
            finally{camera.targetTexture=null;RenderTexture.active=previous;target.Release();UnityEngine.Object.DestroyImmediate(texture);UnityEngine.Object.DestroyImmediate(target);}
        }
    }
}
