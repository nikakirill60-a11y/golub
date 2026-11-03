using System;
using System.Collections.Generic;
using System.IO;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Ui;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Renders the hit direction markers and the sixth-sense lamp off screen:
    /// the player at the origin looking north, a penetration from straight
    /// ahead, a blocked shell from the right and a penetration from behind on
    /// the left, with the lamp at the first peak of its shine.
    /// </summary>
    public static class BattleHitMarkersRenderCli
    {
        public const int Width = 1280;
        public const int Height = 720;

        private const string OutputDirectory = "Logs/hit-markers-render";

        public static void Render()
        {
            try
            {
                Directory.CreateDirectory(OutputDirectory);
                RenderFrame();
                Debug.Log("[Blitz] hit markers render ok: out=" + Path.GetFullPath(OutputDirectory));
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private static void RenderFrame()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cameraObject = new GameObject("render_camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.45f, 0.5f, 0.42f, 1f);
            camera.transform.position = new Vector3(0f, 4f, -8f);
            camera.transform.rotation = Quaternion.LookRotation(Vector3.forward);

            var db = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            var root = new GameObject("tanks");
            var bounds = new Bounds(Vector3.zero, new Vector3(2000f, 200f, 2000f));
            var player = BattleTank.Spawn(root.transform, "player", db.Get("t-34"), root.transform, bounds, 0);
            player.transform.position = Vector3.zero;
            var spots = new List<Vector3>
            {
                new Vector3(0f, 0f, 120f),
                new Vector3(120f, 0f, -8f),
                new Vector3(-90f, 0f, -98f),
            };
            var attackers = new List<BattleTank>();
            for (var i = 0; i < spots.Count; i++)
            {
                var tank = BattleTank.Spawn(root.transform, "enemy" + i, db.Get("is-7"), root.transform, bounds, 1);
                tank.transform.position = spots[i];
                attackers.Add(tank);
            }

            var hud = BattleHud.Create(player, null, false, 7);
            foreach (Transform child in hud.Root)
            {
                var keep = child == hud.DamageMarkers.Handle || child == hud.StaticCrosshair;
                child.gameObject.SetActive(keep);
            }

            var markers = hud.DamageMarkers;
            markers.Receive(Hit(attackers[0], 150), 0f);
            markers.Receive(Hit(attackers[1], 0), 0f);
            markers.Receive(Hit(attackers[2], 90), 0f);
            markers.Tick(1f);

            var lamp = BattleSpottedLamp.Create(hud.Root);
            lamp.Schedule(0f);
            lamp.Tick(BattleSpottedLamp.Delay + 1f);

            var canvas = hud.Root.GetComponentInParent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = 1f;
            Capture(camera, "markers.png");
            UnityEngine.Object.DestroyImmediate(hud.gameObject);
            UnityEngine.Object.DestroyImmediate(root);
        }

        private static DamageInfo Hit(BattleTank attacker, int dmg)
        {
            return new DamageInfo
            {
                AttackerTank = attacker, Dmg = dmg, DmgBase = 150, DmgType = DamageType.Shell,
            };
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
