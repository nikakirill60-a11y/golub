using System;
using System.IO;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Renders a battle-rigged tank through its baked gun clips (the
    /// Annihilator's Gatling spin, barrel recoil and reload drum) so the motion
    /// can be checked frame by frame without a device: BLITZ_CLIP_TANK picks
    /// the card, frames land in Logs/gun-clip-render.
    /// </summary>
    public static class GunClipRenderCli
    {
        private const string OutputDirectory = "Logs/gun-clip-render";
        private const int Width = 960;
        private const int Height = 540;

        public static void Render()
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var id = Environment.GetEnvironmentVariable("BLITZ_CLIP_TANK");
                if (string.IsNullOrEmpty(id)) { id = "oth25_hwt20_falconfire"; }
                var database = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
                var card = database.Get(id);
                var root = new GameObject("clip-render");
                var spawn = new GameObject("Spawn").transform;
                spawn.SetParent(root.transform, false);
                var tank = BattleTank.Spawn(root.transform, card.Name, card, spawn,
                    new Bounds(Vector3.zero, new Vector3(1000f, 100f, 1000f)));
                var body = tank.GetComponent<Rigidbody>();
                if (body != null) { body.isKinematic = true; }
                var turret = tank.Turret;

                var light = new GameObject("Sun").AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.3f;
                light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.6f);

                var camera = new GameObject("Camera").AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.35f, 0.4f, 0.45f);
                camera.fieldOfView = 30f;
                var gun = turret.GunPivot.position;
                var muzzle = turret.Muzzle.position;
                // Front quarter, slightly above: the cylinder, the levers under
                // the gun and the drum on the mantlet side all face the camera.
                var bore = (muzzle - gun).normalized;
                var focus = Vector3.Lerp(gun, muzzle, 0.35f);
                var side = Vector3.Cross(Vector3.up, bore);
                var sign = Environment.GetEnvironmentVariable("BLITZ_CLIP_SIDE") == "left" ? -1f : 1f;
                camera.transform.position = focus + bore * 4.5f + side * (3.2f * sign) + Vector3.up * 1.2f;
                camera.transform.LookAt(focus);

                var target = new RenderTexture(Width, Height, 24);
                camera.targetTexture = target;
                Directory.CreateDirectory(OutputDirectory);

                Shoot(camera, target, "rest");
                turret.PlayGunClip(GunClip.BurstTrigger);
                var last = 0f;
                foreach (var time in new[] { 0.1f, 0.2f, 0.43f, 0.77f, 1.0f })
                {
                    turret.StepGunClips(time - last);
                    last = time;
                    Shoot(camera, target, "burst_" + time.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                }

                turret.StepGunClips(5f);
                turret.PlayGunClip(GunClip.ReloadedTrigger);
                turret.StepGunClips(0.6f);
                Shoot(camera, target, "reload_0.60");
                Debug.Log("[Blitz] gun clip render: " + turret.GunClips.Count + " clips");
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        }

        private static void Shoot(Camera camera, RenderTexture target, string name)
        {
            for (var i = 0; i < 3; i++) { camera.Render(); }
            var previous = RenderTexture.active;
            RenderTexture.active = target;
            var image = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            image.Apply();
            RenderTexture.active = previous;
            File.WriteAllBytes(Path.Combine(OutputDirectory, name + ".png"), image.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(image);
        }
    }
}
