using System;
using System.Collections.Generic;
using System.IO;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Renders the battle minimap of every ported map off screen with tanks
    /// standing on the map's spawns: the player on the first ally spawn
    /// looking north with the camera turned 45 degrees right, the other
    /// allies, spotted enemies, one enemy last seen elsewhere (ghost) and one
    /// destroyed ally halfway through its fade.
    /// </summary>
    public static class BattleMinimapRenderCli
    {
        public const int Width = 1280;
        public const int Height = 720;

        private const string OutputDirectory = "Logs/minimap-render";

        private static readonly string[] MapIds = { "rudniki", "ordeal", "karieri", "lake", "asia", "moon" };

        private static readonly string[] TankIds = { "t-34", "ms-1", "is-7", "su-100" };

        public static void Render()
        {
            try
            {
                Directory.CreateDirectory(OutputDirectory);
                var db = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
                foreach (var id in MapIds)
                {
                    RenderMap(db, id);
                }
                Debug.Log("[Blitz] minimap render ok: out=" + Path.GetFullPath(OutputDirectory));
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private static void RenderMap(TanksDatabase db, string mapId)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cameraObject = new GameObject("render_camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.25f, 0.3f, 0.25f, 1f);
            camera.orthographic = true;
            camera.transform.position = new Vector3(0f, 0f, -100f);

            var map = BattleWorld.LoadDefinition(mapId);
            var root = new GameObject("tanks");
            var tanks = new List<BattleTank>();
            BattleTank player = null;
            for (var team = 0; team < 2; team++)
            {
                var spawns = team == 0 ? map.TeamA : map.TeamB;
                for (var i = 0; i < spawns.Count; i++)
                {
                    var key = TankIds[(i + team) % TankIds.Length];
                    var tank = BattleTank.Spawn(root.transform, "t" + team + "_" + i, db.Get(key), root.transform,
                        new Bounds(Vector3.zero, new Vector3(2000f, 200f, 2000f)), team);
                    var origin = spawns[i].Origin;
                    tank.transform.position = new Vector3(origin[0], origin[1], -origin[2]);
                    tank.transform.rotation = Quaternion.identity;
                    tanks.Add(tank);
                    if (team == 0 && i == 0) { player = tank; }
                }
            }

            var hud = BattleHud.Create(null, null, false, 7);
            foreach (Transform child in hud.Root)
            {
                child.gameObject.SetActive(false);
            }
            var minimap = BattleMinimap.Create(hud.Root, map);
            minimap.Playhead = () => 0f;
            minimap.ViewForward = () => new Vector3(1f, 0f, 1f);
            minimap.FollowScoreboard();

            // Every enemy but the last is spotted; the last one was seen at
            // the map centre and has gone dark since.
            var enemies = tanks.FindAll(t => t.Team == 1);
            for (var i = 0; i < enemies.Count; i++) { enemies[i].SetSpotted(true); }
            var lost = enemies[enemies.Count - 1];
            var centreX = (map.MinimapArea.Min[0] + map.MinimapArea.Max[0]) * 0.5f;
            var centreZ = (map.MinimapArea.Min[1] + map.MinimapArea.Max[1]) * 0.5f;
            lost.transform.position = new Vector3(centreX, 0f, -centreZ);
            minimap.Bind(player, 0, tanks);
            lost.SetSpotted(false);
            minimap.Tick(0f);

            var fallen = tanks.Find(t => t.Team == 0 && t != player);
            if (fallen != null)
            {
                fallen.Health.ReceiveDamageBasic(1000000);
                minimap.Tick(0f);
                minimap.Tick(BattleMinimap.DeathFadeSeconds * 0.5f);
            }

            var canvas = hud.Root.GetComponentInParent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = 10f;
            Capture(camera, mapId + ".png");
            UnityEngine.Object.DestroyImmediate(hud.gameObject);
            UnityEngine.Object.DestroyImmediate(root);
        }

        private static void Capture(Camera camera, string fileName)
        {
            var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var texture = new Texture2D(Width, Height, TextureFormat.RGB24, false, false);
            var previous = RenderTexture.active;
            try
            {
                camera.targetTexture = target;
                Canvas.ForceUpdateCanvases();
                camera.Render();
                camera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0f, 0f, Width, Height), 0, 0);
                texture.Apply(false, false);
                File.WriteAllBytes(Path.Combine(OutputDirectory, fileName), texture.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(texture);
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
}
